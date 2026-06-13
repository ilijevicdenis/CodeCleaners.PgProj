using System;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using PgProj.Core.Comparison;
using PgProj.Core.Data;
using PgProj.Core.Introspection;
using PgProj.Core.Model;
using PgProj.Core.Publishing;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Issue #134 — table-data extract round-trip against real PostgreSQL: extracting a populated database's
/// rows and loading them into a freshly-created empty copy reproduces both the data (FK-ordered, identity
/// columns overridden) and the next-id sequence state.
/// </summary>
public sealed class DataExporterTests
{
    private static string? Admin => Environment.GetEnvironmentVariable("PGPROJ_TEST_CONNECTION");

    private static async Task<string> CreateDbAsync(string? sql = null)
    {
        var admin = new NpgsqlConnectionStringBuilder(Admin!) { Pooling = false }.ConnectionString;
        var db = "pgproj_dexp_" + Guid.NewGuid().ToString("N")[..12];
        await ExecAsync(admin, $"CREATE DATABASE \"{db}\"");
        var conn = new NpgsqlConnectionStringBuilder(Admin!) { Database = db, Pooling = false }.ConnectionString;
        if (sql is not null) await ExecAsync(conn, sql);
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

    private static async Task<long> ScalarAsync(string conn, string sql)
    {
        await using var c = new NpgsqlConnection(conn);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private const string SchemaSql =
        "CREATE SCHEMA app;" +
        "CREATE TABLE app.parent (id int GENERATED ALWAYS AS IDENTITY PRIMARY KEY, name text NOT NULL);" +
        "CREATE TABLE app.child (id int PRIMARY KEY, parent_id int REFERENCES app.parent (id), note text);";

    [Fact]
    public async Task Extract_then_load_reproduces_rows_identity_and_sequence_state()
    {
        if (string.IsNullOrWhiteSpace(Admin)) return;   // skip — no live DB
        var source = await CreateDbAsync(SchemaSql +
            "INSERT INTO app.parent (name) VALUES ('p1'),('p2');" +     // identity → ids 1,2
            "INSERT INTO app.child VALUES (10,1,'c1'),(20,2,'c2');");
        var target = await CreateDbAsync(SchemaSql);                    // same schema, empty
        try
        {
            var model = await new LiveDatabaseReader().ReadAsync(source);
            var dataSql = await DataExporter.ExportAsync(source, model);

            // The child INSERT must come after the parent INSERT (FK order).
            Assert.True(dataSql.IndexOf("INTO \"app\".\"parent\"", StringComparison.Ordinal)
                      < dataSql.IndexOf("INTO \"app\".\"child\"", StringComparison.Ordinal));
            Assert.Contains("OVERRIDING SYSTEM VALUE", dataSql);   // identity column written explicitly
            Assert.Contains("setval(pg_get_serial_sequence", dataSql);

            // Load into the empty copy: the rows arrive and the data matches the source.
            await new DatabaseDeployer().ExecuteAsync(target, dataSql);
            Assert.Equal(2, await ScalarAsync(target, "SELECT count(*) FROM app.parent"));
            Assert.Equal(2, await ScalarAsync(target, "SELECT count(*) FROM app.child"));
            Assert.True((await DataCompare.CompareAsync(source, target)).InSync);

            // The identity sequence was advanced past the loaded rows: the next insert gets id 3, not 1.
            await ExecAsync(target, "INSERT INTO app.parent (name) VALUES ('p3');");
            Assert.Equal(3, await ScalarAsync(target, "SELECT max(id) FROM app.parent"));
        }
        finally { await DropAsync(source); await DropAsync(target); }
    }

    [Fact]
    public async Task Table_data_selection_limits_the_export()
    {
        if (string.IsNullOrWhiteSpace(Admin)) return;   // skip — no live DB
        var source = await CreateDbAsync(SchemaSql +
            "INSERT INTO app.parent (name) VALUES ('p1');INSERT INTO app.child VALUES (10,1,'c1');");
        try
        {
            var model = await new LiveDatabaseReader().ReadAsync(source);
            var only = await DataExporter.ExportAsync(source, model, new[] { "app.parent" });
            Assert.Contains("INTO \"app\".\"parent\"", only);
            Assert.DoesNotContain("INTO \"app\".\"child\"", only);
        }
        finally { await DropAsync(source); }
    }
}
