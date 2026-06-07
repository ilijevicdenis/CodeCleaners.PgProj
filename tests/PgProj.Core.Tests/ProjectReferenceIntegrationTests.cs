using System;
using System.IO;
using System.Threading.Tasks;
using PgProj.Core.Comparison;
using PgProj.Core.Introspection;
using PgProj.Core.Model;
using PgProj.Core.Project;
using PgProj.Core.Project.References;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// EP-REF end-to-end check against real Postgres (ADO.NET via the existing DatabaseDeployer / LiveReader).
/// Publishes A then B to the SAME database: B's objects deploy and resolve against A's already-present
/// objects, while B's deploy script never re-creates A's objects. Then proves the failure mode: B alone
/// against an EMPTY database fails predictably because A's table is absent.
///
/// Mirrors <see cref="PgPkgIntegrationTests"/> / <see cref="LiveReaderIntegrationTests"/>: skipped (no-op)
/// unless PGPROJ_TEST_CONNECTION points at a live DB.
///
/// Each run gets its OWN throwaway database (via <see cref="ThrowawayDatabaseFixture"/>) so no
/// cross-class state leaks. Both tests in this class share the same fixture DB; each test resets it via
/// a schema-level DROP at the start of its work (equivalent to a per-test clean slate within the class).
/// </summary>
public sealed class ProjectReferenceIntegrationTests : IClassFixture<ThrowawayDatabaseFixture>, IDisposable
{
    private readonly ThrowawayDatabaseFixture _fixture;
    private readonly string _root;

    private const string DropAll = "DROP SCHEMA IF EXISTS common CASCADE; DROP SCHEMA IF EXISTS app CASCADE;";

    public ProjectReferenceIntegrationTests(ThrowawayDatabaseFixture fixture)
    {
        _fixture = fixture;
        _root = Path.Combine(Path.GetTempPath(), "pgref_int_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static string FullCreate(DatabaseModel m) =>
        new DeployScriptGenerator().Generate(
            new SchemaComparer().Compare(m, new DatabaseModel()),
            new DeployOptions { WrapInTransaction = true });

    private (DatabaseProject A, DatabaseProject B) MakeAB()
    {
        Write("A/A.pgproj", """
            <Project><PropertyGroup><Name>A</Name><DefaultSchema>common</DefaultSchema></PropertyGroup>
              <ItemGroup><Build Include="**/*.sql" /></ItemGroup></Project>
            """);
        Write("A/customer.sql", "CREATE SCHEMA common; CREATE TABLE common.customer (id int PRIMARY KEY, name text NOT NULL);");

        var bProj = Write("B/B.pgproj", """
            <Project><PropertyGroup><Name>B</Name><DefaultSchema>app</DefaultSchema></PropertyGroup>
              <ItemGroup><Build Include="**/*.sql" /></ItemGroup>
              <ItemGroup><ProjectReference Include="../A/A.pgproj" /></ItemGroup></Project>
            """);
        Write("B/v.sql", "CREATE SCHEMA app; CREATE VIEW app.customer_names AS SELECT c.id, c.name FROM common.customer c;");

        return (DatabaseProject.Load(Path.Combine(_root, "A", "A.pgproj")), DatabaseProject.Load(bProj));
    }

    [Fact]
    public async Task Publish_A_then_B_to_same_database_resolves_against_present_objects()
    {
        var conn = _fixture.ConnectionString;
        if (string.IsNullOrWhiteSpace(conn)) return;   // no live DB → treated as a skip

        var (a, b) = MakeAB();

        // B builds clean with the reference (A's objects resolvable, never emitted).
        var builtA = a.Build();
        var builtB = b.Build();
        Assert.False(builtA.HasErrors, string.Join("\n", builtA.Diagnostics));
        Assert.False(builtB.HasErrors, string.Join("\n", builtB.Diagnostics));

        var resolution = new ReferenceResolver().Resolve(b);
        Assert.False(resolution.HasErrors, string.Join("\n", resolution.Diagnostics));
        Assert.Empty(ReferenceValidator.Validate(b, resolution));

        // B's deploy script must NOT create A's table (it belongs to A) — B owns only the view.
        // (The view body legitimately *references* common.customer, so assert no table is CREATEd,
        // not that the substring is absent.)
        var bScript = FullCreate(builtB.Model);
        Assert.DoesNotContain("CREATE TABLE", bScript, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("customer_names", bScript, StringComparison.OrdinalIgnoreCase);

        var deployer = new DatabaseDeployer();
        // Reset this class's throwaway DB to a clean state before this test's scenario.
        await deployer.ExecuteAsync(conn, DropAll);
        await deployer.ExecuteAsync(conn, FullCreate(builtA.Model));   // A first
        await deployer.ExecuteAsync(conn, bScript);                    // then B — resolves against A

        var live = await new LiveDatabaseReader().ReadAsync(conn);
        Assert.Contains(live.Tables, t => DatabaseModel.NameEquals(t.Schema, "common") && DatabaseModel.NameEquals(t.Name, "customer"));
        Assert.Contains(live.Views, v => DatabaseModel.NameEquals(v.Schema, "app") && DatabaseModel.NameEquals(v.Name, "customer_names"));
        // No trailing DropAll needed — the throwaway DB is dropped by the fixture on dispose.
    }

    [Fact]
    public async Task Publish_B_alone_to_empty_database_fails_because_A_is_absent()
    {
        var conn = _fixture.ConnectionString;
        if (string.IsNullOrWhiteSpace(conn)) return;   // no live DB → treated as a skip

        var (_, b) = MakeAB();
        var builtB = b.Build();
        Assert.False(builtB.HasErrors);

        var deployer = new DatabaseDeployer();
        // Reset this class's throwaway DB to a clean state before this test's scenario.
        await deployer.ExecuteAsync(conn, DropAll);

        // B's script references common.customer (only via the view body) but never creates it; against an
        // empty DB the CREATE VIEW must fail because the referenced relation does not exist.
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await deployer.ExecuteAsync(conn, FullCreate(builtB.Model)));
        // No trailing DropAll needed — the throwaway DB is dropped by the fixture on dispose.
    }
}
