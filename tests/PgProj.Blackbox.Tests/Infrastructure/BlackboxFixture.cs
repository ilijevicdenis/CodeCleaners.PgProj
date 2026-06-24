using Npgsql;

namespace PgProj.Blackbox.Tests.Infrastructure;

/// <summary>
/// Shared state for the blackbox suite: the SOURCE and TARGET admin connection strings (from the
/// Docker harness env vars), the seeded sample database on the source, and a factory for isolated
/// throwaway databases. One instance is shared across the whole "blackbox" collection.
/// </summary>
public sealed class BlackboxFixture
{
    public string? SourceAdmin { get; } = Environment.GetEnvironmentVariable("PGPROJ_SOURCE_CONNECTION");
    public string? TargetAdmin { get; } = Environment.GetEnvironmentVariable("PGPROJ_TARGET_CONNECTION");

    /// <summary>The seeded sample database living on the SOURCE server (schemas sales/inventory/audit).</summary>
    public string? SourceSampleDb => SourceAdmin is null
        ? null
        : new NpgsqlConnectionStringBuilder(SourceAdmin) { Database = "sampledb" }.ConnectionString;

    public bool Ready => SourceAdmin is not null && TargetAdmin is not null && PgProjCli.Available;

    /// <summary>Human-readable reason the suite is skipping, or null when everything is in place.</summary>
    public string? SkipReason
    {
        get
        {
            if (!PgProjCli.Available)
                return "pgproj CLI not built (expected src/PgProj.Cli/bin/<cfg>/net10.0/PgProj.Cli.dll).";
            if (SourceAdmin is null || TargetAdmin is null)
                return "PGPROJ_SOURCE_CONNECTION / PGPROJ_TARGET_CONNECTION not set — run tests/blackbox-db/blackbox-db.ps1 -Export.";
            return null;
        }
    }

    /// <summary>Create an isolated empty database on the TARGET server (dropped on dispose).</summary>
    public Task<ThrowawayDb> NewTargetDbAsync(string? seedSql = null) =>
        ThrowawayDb.CreateAsync(TargetAdmin!, "pgproj_bb_tgt_", seedSql);

    /// <summary>Create an isolated database on the SOURCE server (for source→target two-DB scenarios).</summary>
    public Task<ThrowawayDb> NewSourceDbAsync(string? seedSql = null) =>
        ThrowawayDb.CreateAsync(SourceAdmin!, "pgproj_bb_src_", seedSql);
}

/// <summary>A uniquely-named database created for one test and dropped (WITH FORCE) when disposed.</summary>
public sealed class ThrowawayDb : IAsyncDisposable
{
    private readonly string _admin;
    public string Name { get; }
    public string ConnectionString { get; }
    public Db Db { get; }

    private ThrowawayDb(string admin, string name, string conn)
    {
        _admin = admin;
        Name = name;
        ConnectionString = conn;
        Db = new Db(conn);
    }

    public static async Task<ThrowawayDb> CreateAsync(string adminConn, string prefix, string? seedSql)
    {
        var admin = new NpgsqlConnectionStringBuilder(adminConn) { Pooling = false }.ConnectionString;
        var name = prefix + Guid.NewGuid().ToString("N")[..12];
        await ExecAsync(admin, $"CREATE DATABASE \"{name}\"");
        var conn = new NpgsqlConnectionStringBuilder(adminConn) { Database = name, Pooling = false }.ConnectionString;
        if (!string.IsNullOrWhiteSpace(seedSql))
            await ExecAsync(conn, seedSql);
        return new ThrowawayDb(admin, name, conn);
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        try { await ExecAsync(_admin, $"DROP DATABASE IF EXISTS \"{Name}\" WITH (FORCE)"); }
        catch { /* best effort — a leaked test DB is harmless on a throwaway container */ }
    }

    private static async Task ExecAsync(string conn, string sql)
    {
        await using var c = new NpgsqlConnection(conn);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition("blackbox")]
public sealed class BlackboxCollection : ICollectionFixture<BlackboxFixture> { }
