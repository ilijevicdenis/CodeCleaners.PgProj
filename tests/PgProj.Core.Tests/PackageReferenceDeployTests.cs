using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using PgProj.Core.Comparison;
using PgProj.Core.Introspection;
using PgProj.Core.Model;
using PgProj.Core.Packaging;
using PgProj.Core.Project;
using PgProj.Core.Project.References;
using PgProj.Core.Publishing;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// EP-REF #149 — consumer build/deploy parity vs the inlined baseline (live shadow DB). A project that
/// references a packed "common schema" package (reference-only) deploys its OWN objects identically to a
/// baseline that inlines those objects: deploy common then the consumer reproduces the same end state as a
/// single project containing both, and the consumer's cross-schema view resolves and is queryable.
/// </summary>
public sealed class PackageReferenceDeployTests : IDisposable
{
    private static string? Admin => Environment.GetEnvironmentVariable("PGPROJ_TEST_CONNECTION");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pgrefdeploy_" + Guid.NewGuid().ToString("N"));

    public PackageReferenceDeployTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string Write(string rel, string content)
    {
        var path = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private const string CommonTable = "CREATE TABLE common.customer (id int PRIMARY KEY, name text NOT NULL);";
    private const string SalesView = "CREATE VIEW app.cust AS SELECT c.id, c.name FROM common.customer c;";

    [Fact]
    public async Task Consumer_referencing_a_packed_common_deploys_equivalent_to_inlined_baseline()
    {
        if (string.IsNullOrWhiteSpace(Admin)) return;   // skip — no live DB

        // --- common project, packed into a fake restored feed ---
        Write("common/Common.pgproj", "<Project><PropertyGroup><Name>Common</Name><DefaultSchema>common</DefaultSchema></PropertyGroup><ItemGroup><Build Include=\"**/*.sql\" /></ItemGroup></Project>");
        Write("common/customer.sql", CommonTable);
        var common = DatabaseProject.Load(Path.Combine(_root, "common", "Common.pgproj"));
        var commonBuilt = common.Build();
        Assert.False(commonBuilt.HasErrors);
        var feed = Path.Combine(_root, "nuget");
        var feedDir = Path.Combine(feed, "acme.common", "1.0.0", "pgpkg");
        Directory.CreateDirectory(feedDir);
        PgPkgBuilder.FromBuild(common, commonBuilt.Model, commonBuilt.Files, "t", "2026-01-01T00:00:00Z")
            .Write(Path.Combine(feedDir, "Common.pgpkg"));

        // --- consumer B references the package and adds a view over common.customer ---
        Write("b/B.pgproj", "<Project><PropertyGroup><Name>B</Name><DefaultSchema>app</DefaultSchema></PropertyGroup><ItemGroup><Build Include=\"**/*.sql\" /></ItemGroup><ItemGroup><PackageReference Include=\"Acme.Common\" Version=\"1.0.0\" /></ItemGroup></Project>");
        Write("b/v.sql", SalesView);
        var b = DatabaseProject.Load(Path.Combine(_root, "b", "B.pgproj"));
        var bBuilt = b.Build();
        Assert.False(bBuilt.HasErrors);
        // The package reference resolves (B's view binds to common.customer).
        var resolution = new ReferenceResolver(feed).Resolve(b);
        Assert.False(resolution.HasErrors, string.Join("\n", resolution.Diagnostics));
        Assert.Empty(ReferenceValidator.Validate(b, resolution));

        // --- inlined baseline: a single project containing BOTH common.customer and the view ---
        Write("inlined/Inlined.pgproj", "<Project><PropertyGroup><Name>Inlined</Name><DefaultSchema>app</DefaultSchema></PropertyGroup><ItemGroup><Build Include=\"**/*.sql\" /></ItemGroup></Project>");
        Write("inlined/customer.sql", CommonTable);
        Write("inlined/v.sql", SalesView);
        var inlined = DatabaseProject.Load(Path.Combine(_root, "inlined", "Inlined.pgproj"));
        var inlinedBuilt = inlined.Build();
        Assert.False(inlinedBuilt.HasErrors);

        var dbInlined = await CreateDbAsync();
        var dbReferenced = await CreateDbAsync();
        try
        {
            // Baseline: deploy the inlined project in one shot.
            await DeployAsync(dbInlined, inlinedBuilt.Model);

            // Referenced: deploy common first (the package is reference-only — B doesn't create it), then B.
            await DeployAsync(dbReferenced, commonBuilt.Model);
            await DeployAsync(dbReferenced, bBuilt.Model);

            // Parity: the two databases introspect to the same schema (no diff either direction).
            var mInlined = await new LiveDatabaseReader().ReadAsync(dbInlined);
            var mReferenced = await new LiveDatabaseReader().ReadAsync(dbReferenced);
            Assert.Empty(new SchemaComparer().Compare(mInlined, mReferenced));
            Assert.Empty(new SchemaComparer().Compare(mReferenced, mInlined));

            // The consumer's cross-schema view is real and queryable in the referenced deployment.
            await ExecAsync(dbReferenced, "INSERT INTO common.customer VALUES (1,'a')");
            Assert.Equal(1, await ScalarAsync(dbReferenced, "SELECT count(*) FROM app.cust"));
        }
        finally
        {
            await DropAsync(dbInlined);
            await DropAsync(dbReferenced);
        }
    }

    private async Task DeployAsync(string conn, DatabaseModel model)
    {
        var script = new DeployScriptGenerator().Generate(
            new SchemaComparer().Compare(model, new DatabaseModel()), new DeployOptions { WrapInTransaction = true });
        await new DatabaseDeployer().ExecuteAsync(conn, script);
    }

    private static async Task<string> CreateDbAsync()
    {
        var admin = new NpgsqlConnectionStringBuilder(Admin!) { Pooling = false }.ConnectionString;
        var db = "pgproj_refdep_" + Guid.NewGuid().ToString("N")[..12];
        await ExecAsync(admin, $"CREATE DATABASE \"{db}\"");
        return new NpgsqlConnectionStringBuilder(Admin!) { Database = db, Pooling = false }.ConnectionString;
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
