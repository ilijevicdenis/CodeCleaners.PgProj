using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using PgProj.Core.Comparison;

namespace PgProj.Core.Introspection;

/// <summary>
/// Executes an ordered set of <see cref="SchemaChange"/>s with intra-phase parallelism and
/// hard phase barriers. Changes within one <see cref="SchemaChange.Phase"/> are independent and
/// run concurrently (connection-per-worker, one transaction each); phases run strictly in order.
/// On any failure the phase fails fast — a linked cancellation aborts every sibling worker so none
/// commit, giving phase-level atomicity. Earlier committed phases persist (the changes are already
/// dependency-ordered, so a stop is always at a consistent inter-phase boundary).
///
/// Tradeoff vs <see cref="DatabaseDeployer"/>: that runs the whole script in ONE transaction
/// (strict all-or-nothing, no parallelism). Use this when you want parallel throughput and accept
/// phase-level atomicity. (Design: concurrency-orchestrator agent, 2026-06-03.)
/// </summary>
public sealed class PhasedDeployer
{
    private readonly string _connectionString;
    private readonly int _maxConnections;

    public PhasedDeployer(string connectionString, int maxConnections = 8)
    {
        _connectionString = connectionString;
        _maxConnections = Math.Max(1, maxConnections);
    }

    public async Task ExecuteAsync(IReadOnlyList<SchemaChange> changes, CancellationToken ct = default)
    {
        // Phase numbers are sparse (10, 20, 21, 30, …) — group by distinct value, ascending.
        var phases = changes
            .GroupBy(c => c.Phase)
            .OrderBy(g => g.Key)
            .Select(g => (Phase: g.Key, Items: g.ToArray()));

        foreach (var (phase, items) in phases)
        {
            ct.ThrowIfCancellationRequested();
            await RunPhaseAsync(phase, items, ct); // awaited fully → hard barrier
        }
    }

    private async Task RunPhaseAsync(int phase, SchemaChange[] items, CancellationToken outerCt)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        Exception? rootCause = null;
        SchemaChange? failedChange = null;

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Min(items.Length, _maxConnections),
            CancellationToken = linked.Token,
        };

        try
        {
            await Parallel.ForEachAsync(items, options, async (change, token) =>
            {
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync(token);

                // CREATE INDEX CONCURRENTLY cannot run inside a transaction block; run autocommit.
                var sql = change.ToSql();
                if (sql.Contains("CONCURRENTLY", StringComparison.OrdinalIgnoreCase))
                {
                    await using var raw = new NpgsqlCommand(sql, conn);
                    await raw.ExecuteNonQueryAsync(token);
                    return;
                }

                await using var tx = await conn.BeginTransactionAsync(token);
                try
                {
                    await using var cmd = new NpgsqlCommand(sql, conn, tx);
                    await cmd.ExecuteNonQueryAsync(token);
                    await tx.CommitAsync(token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await SafeRollback(tx);
                    if (Interlocked.CompareExchange(ref rootCause, ex, null) is null)
                        failedChange = change;
                    linked.Cancel();   // trip siblings → fail-fast, none commit
                    throw;
                }
                catch (OperationCanceledException)
                {
                    await SafeRollback(tx); // a sibling failed; abort this one cleanly
                    throw;
                }
            });
        }
        catch (Exception) when (rootCause is not null)
        {
            throw new DeploymentException($"Phase {phase} failed on: {failedChange?.Describe()}", rootCause);
        }
    }

    private static async Task SafeRollback(NpgsqlTransaction tx)
    {
        try { await tx.RollbackAsync(CancellationToken.None); } catch { /* connection already gone */ }
    }
}

public sealed class DeploymentException : Exception
{
    public DeploymentException(string message, Exception inner) : base(message, inner) { }
}
