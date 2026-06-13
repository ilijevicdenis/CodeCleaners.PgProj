using System;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using PgProj.Core.Data;
using PgProj.Core.Publishing;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Issue #132 — row-level data compare + sync between two databases, end-to-end against real PostgreSQL
/// (reuses PGPROJ_TEST_CONNECTION; each test provisions two throwaway databases and drops them). Covers all
/// four diff categories, the keyless-table skip, and the compare → apply → re-compare round-trip.
/// </summary>
public sealed class DataCompareTests
{
    private static string? Admin => Environment.GetEnvironmentVariable("PGPROJ_TEST_CONNECTION");

    private static async Task<string> CreateDbAsync(string sql)
    {
        var admin = new NpgsqlConnectionStringBuilder(Admin!) { Pooling = false }.ConnectionString;
        var db = "pgproj_dcmp_" + Guid.NewGuid().ToString("N")[..12];
        await ExecAsync(admin, $"CREATE DATABASE \"{db}\"");
        var conn = new NpgsqlConnectionStringBuilder(Admin!) { Database = db, Pooling = false }.ConnectionString;
        await ExecAsync(conn, sql);
        return conn;
    }

    private static async Task DropAsync(string conn)
    {
        NpgsqlConnection.ClearAllPools();
        var db = new NpgsqlConnectionStringBuilder(conn).Database!;
        var admin = new NpgsqlConnectionStringBuilder(Admin!) { Pooling = false }.ConnectionString;
        await ExecAsync(admin, $"DROP DATABASE IF EXISTS \"{db}\" WITH (FORCE)");
    }

    private static async Task ExecAsync(string conn, string sql)
    {
        await using var c = new NpgsqlConnection(conn);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    private const string Schema =
        "CREATE SCHEMA app; CREATE TABLE app.t (id int PRIMARY KEY, name text, qty int);";

    [Fact]
    public async Task Diff_buckets_rows_into_all_four_categories()
    {
        if (string.IsNullOrWhiteSpace(Admin)) return;   // skip — no live DB
        var source = await CreateDbAsync(Schema + "INSERT INTO app.t VALUES (1,'a',10),(2,'b',20),(3,'c',30);");
        var target = await CreateDbAsync(Schema + "INSERT INTO app.t VALUES (1,'a',10),(2,'b',99),(4,'d',40);");
        try
        {
            var result = await DataCompare.CompareAsync(source, target);
            var t = Assert.Single(result.Tables, x => x.Table == "t");

            Assert.Equal(1, t.IdenticalCount);       // id=1
            Assert.Equal(1, t.DifferentCount);       // id=2 (qty 20≠99)
            Assert.Equal(1, t.OnlyInSourceCount);    // id=3
            Assert.Equal(1, t.OnlyInTargetCount);    // id=4

            var diff = t.Rows.Single(r => r.Category == RowDiffCategory.Different);
            Assert.Contains(diff.ColumnDiffs, c => c.Column == "qty" && c.Source == "20" && c.Target == "99");
        }
        finally { await DropAsync(source); await DropAsync(target); }
    }

    [Fact]
    public async Task Apply_makes_the_target_match_the_source_and_a_recompare_is_in_sync()
    {
        if (string.IsNullOrWhiteSpace(Admin)) return;   // skip — no live DB
        var source = await CreateDbAsync(Schema + "INSERT INTO app.t VALUES (1,'a',10),(2,'b',20),(3,'c',30);");
        var target = await CreateDbAsync(Schema + "INSERT INTO app.t VALUES (1,'a',10),(2,'b',99),(4,'d',40);");
        try
        {
            var before = await DataCompare.CompareAsync(source, target);
            Assert.False(before.InSync);

            var script = DataCompare.GenerateSyncScript(before, wrapInTransaction: true);
            await new DatabaseDeployer().ExecuteAsync(target, script);

            var after = await DataCompare.CompareAsync(source, target);
            Assert.True(after.InSync, "after applying the sync script the data must match");
        }
        finally { await DropAsync(source); await DropAsync(target); }
    }

    [Fact]
    public async Task Keyless_tables_are_reported_as_skipped()
    {
        if (string.IsNullOrWhiteSpace(Admin)) return;   // skip — no live DB
        const string keyless = "CREATE SCHEMA app; CREATE TABLE app.log (msg text);";
        var source = await CreateDbAsync(keyless);
        var target = await CreateDbAsync(keyless);
        try
        {
            var result = await DataCompare.CompareAsync(source, target);
            Assert.DoesNotContain(result.Tables, t => t.Table == "log");
            Assert.Contains(result.Skipped, s => s.Qualified == "app.log");
        }
        finally { await DropAsync(source); await DropAsync(target); }
    }

    [Fact]
    public async Task Identical_databases_compare_in_sync()
    {
        if (string.IsNullOrWhiteSpace(Admin)) return;   // skip — no live DB
        var data = Schema + "INSERT INTO app.t VALUES (1,'a',10),(2,'b',20);";
        var source = await CreateDbAsync(data);
        var target = await CreateDbAsync(data);
        try
        {
            Assert.True((await DataCompare.CompareAsync(source, target)).InSync);
        }
        finally { await DropAsync(source); await DropAsync(target); }
    }

    [Fact]
    public void Literal_formats_common_types_for_sql()
    {
        Assert.Equal("NULL", DataCompare.Literal(null));
        Assert.Equal("true", DataCompare.Literal(true));
        Assert.Equal("42", DataCompare.Literal(42));
        Assert.Equal("'O''Brien'", DataCompare.Literal("O'Brien"));
    }
}
