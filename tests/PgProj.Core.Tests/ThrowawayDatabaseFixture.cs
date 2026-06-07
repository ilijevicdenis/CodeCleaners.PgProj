using System;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// xUnit class-fixture that creates a uniquely-named throwaway PostgreSQL database on init and
/// drops it (WITH FORCE) on dispose.  Each test class that applies
/// <c>IClassFixture&lt;ThrowawayDatabaseFixture&gt;</c> gets its own isolated database, so no
/// cross-test state leaks even when tests run in parallel.
///
/// When <c>PGPROJ_TEST_CONNECTION</c> is unset the fixture is a no-op: <see cref="ConnectionString"/>
/// returns <c>null</c> and all DB-backed tests that check for <c>null</c> skip cleanly.
/// </summary>
public sealed class ThrowawayDatabaseFixture : IAsyncLifetime
{
    private static readonly string? AdminConn =
        Environment.GetEnvironmentVariable("PGPROJ_TEST_CONNECTION");

    private string? _dbName;
    private string? _adminConn;

    /// <summary>
    /// The connection string that targets the throwaway database, or <c>null</c> when
    /// <c>PGPROJ_TEST_CONNECTION</c> is unset. Tests should early-return (skip) when this is null.
    /// </summary>
    public string? ConnectionString { get; private set; }

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(AdminConn))
            return;  // No DB configured — remain a no-op; tests will skip.

        _adminConn = new NpgsqlConnectionStringBuilder(AdminConn) { Pooling = false }.ConnectionString;
        _dbName    = "pgproj_test_" + Guid.NewGuid().ToString("N")[..16];

        await ExecAsync(_adminConn, $"CREATE DATABASE \"{_dbName}\"");

        ConnectionString = new NpgsqlConnectionStringBuilder(AdminConn)
        {
            Database = _dbName,
            Pooling  = false,
        }.ConnectionString;
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        if (_adminConn is null || _dbName is null)
            return;  // Was never initialised (no PGPROJ_TEST_CONNECTION).

        NpgsqlConnection.ClearAllPools();
        await ExecAsync(_adminConn, $"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static async Task ExecAsync(string conn, string sql)
    {
        await using var c = new NpgsqlConnection(conn);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }
}
