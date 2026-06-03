using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Npgsql;

namespace PgProj.Core.Tests;

/// <summary>
/// Shared, lazily-initialised PostgreSQL execution backend for the generated corpus tests. The static
/// engine resolves most cases offline; the rest (runtime-only errors it can't decide without false
/// positives) are verified by actually executing them here — mirroring tools/pg-oracle.ps1.
///
/// One fixture-loaded template DB is created once per test run and cloned into a work DB; wrapped cases
/// run BEGIN/…/ROLLBACK on a pooled connection (so the many parallel per-case tests are isolated and
/// safe), and txn:"none" cases each get their own fresh clone. Stale DBs from a prior run are dropped
/// on init, so nothing accumulates. No-ops entirely when PGPROJ_TEST_CONNECTION is unset.
/// </summary>
public static class CorpusDb
{
    private static readonly string? Admin = Environment.GetEnvironmentVariable("PGPROJ_TEST_CONNECTION");
    private static readonly Lazy<Task<Endpoints>> _init = new(InitAsync);

    private sealed record Endpoints(string AdminConn, string WorkConn, string Template);

    /// <summary>True iff PostgreSQL rejected the statement (actual verdict = "error").</summary>
    public static async Task<bool> ErrorsAsync(string sql, bool solo)
    {
        var e = await _init.Value;
        return solo ? await SoloAsync(e, sql) : await WrappedAsync(e, sql);
    }

    private static async Task<Endpoints> InitAsync()
    {
        if (string.IsNullOrWhiteSpace(Admin))
            throw new InvalidOperationException("CorpusDb used without PGPROJ_TEST_CONNECTION");

        var admin = new NpgsqlConnectionStringBuilder(Admin) { Pooling = false }.ConnectionString;
        var id = Guid.NewGuid().ToString("N")[..12];
        var tmpl = "pgproj_corpus_tmpl_" + id;
        var work = "pgproj_corpus_work_" + id;

        await DropStaleAsync(admin);
        await ExecAsync(admin, $"CREATE DATABASE \"{tmpl}\"");

        var fixturePath = Path.Combine(CorpusData.CorpusDir, "_fixture.sql");
        if (File.Exists(fixturePath))
        {
            var fixture = File.ReadAllText(fixturePath);
            if (!string.IsNullOrWhiteSpace(fixture))
                await ExecAsync(ConnTo(admin, tmpl, pooling: false), fixture);
        }
        await ExecAsync(admin, $"CREATE DATABASE \"{work}\" TEMPLATE \"{tmpl}\"");

        // Work connections are pooled (many parallel tests) with session guards baked in via Options so
        // a runaway statement fails fast as an error rather than hanging the suite.
        var workConn = new NpgsqlConnectionStringBuilder(Admin)
        {
            Database = work,
            Pooling = true,
            Options = "-c statement_timeout=15000 -c lock_timeout=5000",
        }.ConnectionString;

        return new Endpoints(admin, workConn, tmpl);
    }

    private static async Task<bool> WrappedAsync(Endpoints e, string sql)
    {
        await using var conn = new NpgsqlConnection(e.WorkConn);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            await using var cmd = new NpgsqlCommand(sql, conn, tx) { CommandTimeout = 20 };
            await cmd.ExecuteNonQueryAsync();
            await tx.RollbackAsync();
            return false;                                  // PostgreSQL accepted it
        }
        catch (PostgresException) { return true; }         // PostgreSQL rejected it
        catch (Exception) { return true; }                 // client-side timeout on a runaway → error
    }

    private static async Task<bool> SoloAsync(Endpoints e, string sql)
    {
        var db = "pgproj_corpus_solo_" + Guid.NewGuid().ToString("N")[..12];
        await ExecAsync(e.AdminConn, $"CREATE DATABASE \"{db}\" TEMPLATE \"{e.Template}\"");
        try
        {
            await using var conn = new NpgsqlConnection(ConnTo(e.AdminConn, db, pooling: false,
                options: "-c statement_timeout=15000 -c lock_timeout=5000"));
            await conn.OpenAsync();
            try
            {
                await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 20 };
                await cmd.ExecuteNonQueryAsync();
                return false;
            }
            catch (PostgresException) { return true; }
            catch (Exception) { return true; }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecAsync(e.AdminConn, $"DROP DATABASE IF EXISTS \"{db}\" WITH (FORCE)");
        }
    }

    private static async Task DropStaleAsync(string admin)
    {
        var stale = new List<string>();
        await using (var conn = new NpgsqlConnection(admin))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT datname FROM pg_database WHERE datname LIKE 'pgproj_corpus_%'", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) stale.Add(r.GetString(0));
        }
        foreach (var db in stale)
            await ExecAsync(admin, $"DROP DATABASE IF EXISTS \"{db}\" WITH (FORCE)");
    }

    private static string ConnTo(string baseConn, string db, bool pooling, string? options = null)
    {
        var b = new NpgsqlConnectionStringBuilder(baseConn) { Database = db, Pooling = pooling };
        if (options is not null) b.Options = options;
        return b.ConnectionString;
    }

    private static async Task ExecAsync(string conn, string sql)
    {
        await using var c = new NpgsqlConnection(conn);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }
}
