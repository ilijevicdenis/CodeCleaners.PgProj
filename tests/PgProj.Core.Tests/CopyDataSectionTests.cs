using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using PgProj.Core.Data;
using PgProj.Core.Introspection;
using PgProj.Core.Model;
using PgProj.Core.Packaging;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Issue #151 — the <c>.pgpkg</c>-embedded <c>data/</c> COPY section (the BACPAC-analogue variant). A DB-free
/// test proves the data section write/read round-trips and stays outside the source checksum; a live test
/// proves an export → pack → unpack → load round-trip reproduces rows, ALWAYS-identity values, and the
/// next-id sequence state (COPY relaxes the identity column to BY DEFAULT around the load).
/// </summary>
public sealed class CopyDataSectionTests
{
    private static string? Admin => Environment.GetEnvironmentVariable("PGPROJ_TEST_CONNECTION");

    // ---- DB-free: package format round-trip ----------------------------------------------------

    [Fact]
    public void Data_section_round_trips_and_is_outside_the_source_checksum()
    {
        var sources = new[] { new PgPkgSource("a.sql", "CREATE TABLE app.t (id int);") };
        var checksum = SourceChecksum.Compute(sources.Select(s => (s.RelativePath, s.Content)));
        var manifest = new PgPkgManifest("App", "18", "test", "1980-01-01T00:00:00Z", checksum);
        var data = new[]
        {
            new PgPkgDataTable("app", "parent", new[] { "id", "name" }, HasAlwaysIdentity: true, "1\tp1\n2\tp2\n"),
            new PgPkgDataTable("app", "child", new[] { "id", "parent_id" }, HasAlwaysIdentity: false, "10\t1\n"),
        };
        var pkg = PgPkg.Create(manifest, new DatabaseModel(), sources, data: data);

        using var ms = new MemoryStream();
        pkg.Write(ms);
        ms.Position = 0;
        var read = PgPkg.Read(ms);   // Read also verifies the source checksum — data must not perturb it

        Assert.True(read.HasData);
        Assert.Equal(2, read.Data.Count);
        // Order is preserved (parent before child — FK-safe).
        Assert.Equal("app.parent", read.Data[0].QualifiedName);
        Assert.True(read.Data[0].HasAlwaysIdentity);
        Assert.Equal(new[] { "id", "name" }, read.Data[0].Columns);
        Assert.Equal("1\tp1\n2\tp2\n", read.Data[0].CopyText);
        Assert.Equal("app.child", read.Data[1].QualifiedName);
        Assert.False(read.Data[1].HasAlwaysIdentity);
    }

    [Fact]
    public void Schema_only_package_has_no_data()
    {
        var sources = new[] { new PgPkgSource("a.sql", "CREATE TABLE app.t (id int);") };
        var checksum = SourceChecksum.Compute(sources.Select(s => (s.RelativePath, s.Content)));
        var pkg = PgPkg.Create(new PgPkgManifest("App", "18", "test", "1980-01-01T00:00:00Z", checksum),
            new DatabaseModel(), sources);

        using var ms = new MemoryStream();
        pkg.Write(ms);
        ms.Position = 0;
        Assert.False(PgPkg.Read(ms).HasData);
    }

    // ---- live: export → pack → unpack → load round-trip ----------------------------------------

    private const string SchemaSql =
        "CREATE SCHEMA app;" +
        "CREATE TABLE app.parent (id int GENERATED ALWAYS AS IDENTITY PRIMARY KEY, name text NOT NULL);" +
        "CREATE TABLE app.child (id int PRIMARY KEY, parent_id int REFERENCES app.parent (id), note text);";

    [Fact]
    public async Task Export_pack_unpack_load_reproduces_rows_identity_and_sequence()
    {
        if (string.IsNullOrWhiteSpace(Admin)) return;   // skip — no live DB

        var source = await CreateDbAsync(SchemaSql +
            "INSERT INTO app.parent (name) VALUES ('p1'),('p2');" +     // ALWAYS identity → ids 1,2
            "INSERT INTO app.child VALUES (10,1,'c1'),(20,2,'c2');");
        var target = await CreateDbAsync(SchemaSql);                    // same schema, empty
        try
        {
            var model = await new LiveDatabaseReader().ReadAsync(source);
            var data = await DataExporter.ExportCopyAsync(source, model);

            // Pack into a real .pgpkg and read it back so the load exercises the on-disk format.
            var sources = new[] { new PgPkgSource("schema.sql", SchemaSql) };
            var manifest = new PgPkgManifest("App", "18", "test", "1980-01-01T00:00:00Z",
                SourceChecksum.Compute(sources.Select(s => (s.RelativePath, s.Content))));
            var pkg = PgPkg.Create(manifest, model, sources, data: data);

            using var ms = new MemoryStream();
            pkg.Write(ms);
            ms.Position = 0;
            var loaded = PgPkg.Read(ms);

            // parent must precede child in the data section (FK-safe load order).
            var names = loaded.Data.Select(d => d.QualifiedName).ToList();
            Assert.True(names.IndexOf("app.parent") < names.IndexOf("app.child"));

            await CopyDataLoader.LoadAsync(target, loaded);

            Assert.Equal(2, await ScalarAsync(target, "SELECT count(*) FROM app.parent"));
            Assert.Equal(2, await ScalarAsync(target, "SELECT count(*) FROM app.child"));
            // ALWAYS-identity ids preserved exactly (1,2), not regenerated.
            Assert.Equal(2, await ScalarAsync(target, "SELECT max(id) FROM app.parent"));
            // The identity column is restored to GENERATED ALWAYS and its sequence advanced past the loaded rows.
            Assert.Equal(3, await ScalarAsync(target,
                "INSERT INTO app.parent (name) VALUES ('p3') RETURNING id"));
        }
        finally
        {
            await DropAsync(source);
            await DropAsync(target);
        }
    }

    // ---- helpers (mirror DataExporterTests) ----------------------------------------------------

    private static async Task<string> CreateDbAsync(string? sql = null)
    {
        var admin = new NpgsqlConnectionStringBuilder(Admin!) { Pooling = false }.ConnectionString;
        var db = "pgproj_copy_" + Guid.NewGuid().ToString("N")[..12];
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
}
