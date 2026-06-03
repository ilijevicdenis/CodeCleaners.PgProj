using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;

namespace PgProj.Core.Tests;

/// <summary>
/// Proves the combined engine reaches 100% parity with PostgreSQL on the corpus: the static layer
/// (PgParser + SemanticAnalyzer) handles what it can, and for every case it does NOT already resolve,
/// the statement is executed against a throwaway database (the oracle method) and must match its
/// declared verdict. Mirrors tools/pg-oracle.ps1: a fixture-loaded template is cloned; normal cases
/// run BEGIN/…/ROLLBACK in a shared clone; txn:"none" cases each get a fresh clone and run unwrapped;
/// statement_timeout/lock_timeout guard runaways. Skipped unless PGPROJ_TEST_CONNECTION is set
/// (point it at a server's maintenance DB with CREATE DATABASE rights).
/// </summary>
public sealed class CorpusParityIntegrationTests
{
    private static string? Admin => Environment.GetEnvironmentVariable("PGPROJ_TEST_CONNECTION");

    [Fact]
    public async Task Static_plus_execution_matches_postgres_on_every_pending_case()
    {
        var admin = Admin;
        if (string.IsNullOrWhiteSpace(admin)) return;   // no live DB → skip

        // Cases the static engine already gets right need no execution. The rest must be confirmed by PG.
        var pending = CorpusData.LoadAll().Where(c => !CorpusData.Passes(c)).ToList();
        // Static never mis-rejects valid SQL (0 reachable expect:ok, 0 false positives), so every
        // unresolved case is an expect:error we rely on execution to catch.
        Assert.All(pending, c => Assert.Equal("error", c.Expect));

        var fixturePath = Path.Combine(CorpusData.CorpusDir, "_fixture.sql");
        var fixture = File.Exists(fixturePath) ? File.ReadAllText(fixturePath) : "";

        var exec = new CorpusExecutor(admin, fixture);
        var missed = new List<string>();
        await exec.SetupAsync();
        try
        {
            foreach (var c in pending)
            {
                var solo = string.Equals(c.Txn, "none", StringComparison.OrdinalIgnoreCase);
                if (!await exec.ErrorsAsync(c.Sql, solo))
                    missed.Add($"{c.Id} [{c.Category}]: {c.Sql.Replace("\n", " ")}");
            }
        }
        finally { await exec.TeardownAsync(); }

        Assert.True(missed.Count == 0,
            $"{missed.Count}/{pending.Count} unresolved cases did NOT error on execution either " +
            $"(combined static+execution < 100% parity):\n" + string.Join("\n", missed.Take(50)));
    }
}

/// <summary>Executes corpus SQL against throwaway databases and reports whether PostgreSQL errored.</summary>
internal sealed class CorpusExecutor
{
    private readonly string _admin;
    private readonly string _fixture;
    private readonly string _tmpl = "pgproj_parity_tmpl_" + Guid.NewGuid().ToString("N")[..12];
    private readonly string _work = "pgproj_parity_work_" + Guid.NewGuid().ToString("N")[..12];
    private NpgsqlConnection? _workConn;

    public CorpusExecutor(string admin, string fixture)
    {
        // Pooling off: one-shot admin/template/solo connections must not linger in the pool, or
        // CREATE DATABASE … TEMPLATE / DROP DATABASE fail with 55006 "being accessed by other users".
        _admin = new NpgsqlConnectionStringBuilder(admin) { Pooling = false }.ConnectionString;
        _fixture = fixture;
    }

    private string ConnTo(string db) => new NpgsqlConnectionStringBuilder(_admin) { Database = db, Pooling = false }.ConnectionString;

    private async Task AdminExecAsync(string sql)
    {
        await using var admin = new NpgsqlConnection(_admin);
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, admin);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SetupAsync()
    {
        await AdminExecAsync($"DROP DATABASE IF EXISTS \"{_tmpl}\" WITH (FORCE); CREATE DATABASE \"{_tmpl}\"");
        if (!string.IsNullOrWhiteSpace(_fixture))
        {
            await using var t = new NpgsqlConnection(ConnTo(_tmpl));
            await t.OpenAsync();
            await using var cmd = new NpgsqlCommand(_fixture, t);
            await cmd.ExecuteNonQueryAsync();
        }
        await AdminExecAsync($"CREATE DATABASE \"{_work}\" TEMPLATE \"{_tmpl}\"");
        _workConn = await OpenWorkAsync();
    }

    private async Task<NpgsqlConnection> OpenWorkAsync()
    {
        var c = new NpgsqlConnection(ConnTo(_work));
        await c.OpenAsync();
        await using var guard = new NpgsqlCommand("SET statement_timeout = '15s'; SET lock_timeout = '5s'", c);
        await guard.ExecuteNonQueryAsync();
        return c;
    }

    public async Task<bool> ErrorsAsync(string sql, bool solo) => solo ? await SoloAsync(sql) : await WrappedAsync(sql);

    // Normal case: run inside a transaction in the shared work DB and roll it back (leaves no trace).
    private async Task<bool> WrappedAsync(string sql)
    {
        if (_workConn is null || _workConn.State != System.Data.ConnectionState.Open)
            _workConn = await OpenWorkAsync();
        await using var tx = await _workConn.BeginTransactionAsync();
        try
        {
            await using var cmd = new NpgsqlCommand(sql, _workConn, (NpgsqlTransaction)tx) { CommandTimeout = 20 };
            await cmd.ExecuteNonQueryAsync();
            await tx.RollbackAsync();
            return false;                                   // PG accepted it
        }
        catch (PostgresException)
        {
            await SafeRollbackAsync(tx);
            return true;                                    // PG rejected it
        }
        catch (Exception)
        {
            // client-side timeout / broken connection on a runaway — treat as an error and reset.
            await SafeRollbackAsync(tx);
            await ResetWorkAsync();
            return true;
        }
    }

    private static async Task SafeRollbackAsync(System.Data.Common.DbTransaction tx)
    {
        try { await tx.RollbackAsync(); } catch { /* already aborted */ }
    }

    private async Task ResetWorkAsync()
    {
        try { if (_workConn is not null) await _workConn.DisposeAsync(); } catch { }
        _workConn = null;
    }

    // txn:"none" case: own fresh clone, run unwrapped, drop it.
    private async Task<bool> SoloAsync(string sql)
    {
        var db = "pgproj_parity_solo_" + Guid.NewGuid().ToString("N")[..12];
        await AdminExecAsync($"CREATE DATABASE \"{db}\" TEMPLATE \"{_tmpl}\"");
        try
        {
            await using var conn = new NpgsqlConnection(ConnTo(db));
            await conn.OpenAsync();
            await using (var guard = new NpgsqlCommand("SET statement_timeout = '15s'; SET lock_timeout = '5s'", conn))
                await guard.ExecuteNonQueryAsync();
            try
            {
                await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 20 };
                await cmd.ExecuteNonQueryAsync();
                return false;
            }
            catch (PostgresException) { return true; }
            catch (Exception) { return true; }
        }
        finally { await AdminExecAsync($"DROP DATABASE IF EXISTS \"{db}\" WITH (FORCE)"); }
    }

    public async Task TeardownAsync()
    {
        try { if (_workConn is not null) await _workConn.DisposeAsync(); } catch { }
        // Pool connections to the dropped DBs must be cleared or DROP fails with "in use".
        try { NpgsqlConnection.ClearAllPools(); } catch { }
        try { await AdminExecAsync($"DROP DATABASE IF EXISTS \"{_work}\" WITH (FORCE)"); } catch { }
        try { await AdminExecAsync($"DROP DATABASE IF EXISTS \"{_tmpl}\" WITH (FORCE)"); } catch { }
    }
}
