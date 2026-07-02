using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PgProj.Core.Packaging;
using PgProj.Core.Project;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// EP-PKG (#138): package equivalence (the DacpacVerify analogue). DB-free — packages are built
/// from temp projects and verified in memory and across a write→read round-trip. Stamps
/// (CreatedUtc/ToolVersion) must never affect the verdict, or the command is useless as a
/// build-determinism gate.
/// </summary>
public sealed class PackageVerifierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pgproj-verify-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private async Task<PgPkg> BuildPackageAsync(string name, string widgetsSql,
        string targetVersion = "18", string created = "2026-01-01T00:00:00Z", string tool = "1.0.0-test")
    {
        var dir = Path.Combine(_root, name + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(dir, "app", "Tables"));
        File.WriteAllText(Path.Combine(dir, "Db.pgproj"), $"""
            <Project DefaultTargets="Build">
              <PropertyGroup>
                <Name>{name}</Name>
                <DefaultSchema>public</DefaultSchema>
                <TargetPostgresVersion>{targetVersion}</TargetPostgresVersion>
              </PropertyGroup>
              <ItemGroup>
                <Build Include="**/*.sql" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir, "app", "Tables", "widgets.sql"), widgetsSql);

        var project = DatabaseProject.Load(Path.Combine(dir, "Db.pgproj"));
        var result = await project.BuildAsync();
        return PgPkgBuilder.FromBuild(project, result.Model, result.Files, tool, created);
    }

    private const string BaseSql = "CREATE TABLE app.widgets (id integer NOT NULL, label text);\n";

    [Fact]
    public async Task Identical_packages_verify_equivalent_even_with_different_stamps()
    {
        // build determinism: same sources, different CreatedUtc/ToolVersion → PASS
        var a = await BuildPackageAsync("Db", BaseSql, created: "2026-01-01T00:00:00Z", tool: "1.0.0");
        var b = await BuildPackageAsync("Db", BaseSql, created: "2026-06-12T12:00:00Z", tool: "9.9.9");

        var report = PackageVerifier.Verify(a, b);
        Assert.True(report.Equivalent);
        Assert.Empty(report.ModelDrift);
        Assert.Empty(report.SourceDrift);
        Assert.Empty(report.OptionDrift);
    }

    [Fact]
    public async Task Model_drift_names_the_drifting_object()
    {
        var a = await BuildPackageAsync("Db", BaseSql);
        var b = await BuildPackageAsync("Db", "CREATE TABLE app.widgets (id integer NOT NULL, label text, extra integer);\n");

        var report = PackageVerifier.Verify(a, b);
        Assert.False(report.Equivalent);
        Assert.Contains(report.ModelDrift, d => d.Contains("extra"));
        Assert.NotEmpty(report.SourceDrift); // the source differs too, naturally
    }

    [Fact]
    public async Task Source_only_drift_is_reported_even_when_the_model_is_identical()
    {
        // a comment-only change: same model, different embedded source — DacpacVerify semantics
        // say that still fails equivalence (the artifact is not the same thing).
        var a = await BuildPackageAsync("Db", BaseSql);
        var b = await BuildPackageAsync("Db", "-- reviewed 2026-06-12\n" + BaseSql);

        var report = PackageVerifier.Verify(a, b);
        Assert.False(report.Equivalent);
        var drift = Assert.Single(report.SourceDrift);
        Assert.Equal("changed", drift.Kind);
        Assert.Contains("widgets.sql", drift.Path);
        Assert.Empty(report.ModelDrift); // the comparer confirms the MODELS are the same
    }

    [Fact]
    public async Task Option_drift_reports_the_setting_and_both_values()
    {
        var a = await BuildPackageAsync("Db", BaseSql, targetVersion: "18");
        var b = await BuildPackageAsync("Db", BaseSql, targetVersion: "16");

        var report = PackageVerifier.Verify(a, b);
        Assert.False(report.Equivalent);
        var drift = Assert.Single(report.OptionDrift);
        Assert.Equal("pgVersion", drift.Option);
        Assert.Equal("18", drift.ValueA);
        Assert.Equal("16", drift.ValueB);
    }

    [Fact]
    public async Task Missing_source_files_are_reported_per_side()
    {
        var a = await BuildPackageAsync("Db", BaseSql);
        var bDir = Path.Combine(_root, "twofile-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(bDir, "app", "Tables"));
        File.WriteAllText(Path.Combine(bDir, "Db.pgproj"), """
            <Project DefaultTargets="Build">
              <PropertyGroup><Name>Db</Name><DefaultSchema>public</DefaultSchema><TargetPostgresVersion>18</TargetPostgresVersion></PropertyGroup>
              <ItemGroup><Build Include="**/*.sql" /></ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(bDir, "app", "Tables", "widgets.sql"), BaseSql);
        File.WriteAllText(Path.Combine(bDir, "app", "Tables", "gadgets.sql"),
            "CREATE TABLE app.gadgets (id integer NOT NULL);\n");
        var projB = DatabaseProject.Load(Path.Combine(bDir, "Db.pgproj"));
        var resB = await projB.BuildAsync();
        var b = PgPkgBuilder.FromBuild(projB, resB.Model, resB.Files, "1.0.0-test", "2026-01-01T00:00:00Z");

        var report = PackageVerifier.Verify(a, b);
        Assert.False(report.Equivalent);
        Assert.Contains(report.SourceDrift, d => d.Kind == "onlyInB" && d.Path.Contains("gadgets"));
        Assert.Contains(report.ModelDrift, d => d.Contains("gadgets"));
    }

    [Fact]
    public async Task Write_read_round_trip_verifies_equivalent()
    {
        var pkg = await BuildPackageAsync("Db", BaseSql);
        var path = Path.Combine(_root, "roundtrip.pgpkg");
        Directory.CreateDirectory(_root);
        pkg.Write(path);
        var reread = PgPkg.Read(path);

        var report = PackageVerifier.Verify(pkg, reread);
        Assert.True(report.Equivalent);
    }

    [Fact]
    public async Task Extract_round_trip_verifies_equivalent()
    {
        // sync/extract round-trip: introspect the live DB twice -> two packages from the two
        // extracts must verify equivalent (introspection + canonical rendering are deterministic).
        var admin = Environment.GetEnvironmentVariable("PGPROJ_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(admin)) return;   // no live DB - treated as a skip

        // A PRIVATE throwaway database: reading the shared admin DB twice raced the other live tests
        // mutating the server in parallel (objects appearing between the two reads → flaky inequality).
        var adminNoPool = new Npgsql.NpgsqlConnectionStringBuilder(admin) { Pooling = false }.ConnectionString;
        var db = "pgproj_pkgver_" + Guid.NewGuid().ToString("N")[..12];
        await ExecAsync(adminNoPool, $"CREATE DATABASE \"{db}\"");
        var conn = new Npgsql.NpgsqlConnectionStringBuilder(admin) { Database = db, Pooling = false }.ConnectionString;
        try
        {
            await ExecAsync(conn, "CREATE TABLE public.t (id int PRIMARY KEY, name text DEFAULT 'X'); CREATE VIEW public.v AS SELECT id FROM public.t;");

            var live1 = await new PgProj.Core.Introspection.LiveDatabaseReader().ReadAsync(conn);
            var live2 = await new PgProj.Core.Introspection.LiveDatabaseReader().ReadAsync(conn);

            PgPkg FromModel(PgProj.Core.Model.DatabaseModel m, string created)
            {
                var files = PgProj.Core.Comparison.DdlExporter.ExportFiles(m)
                    .Select(kv => new PgPkgSource(kv.Key.Replace('\\', '/'), kv.Value)).ToList();
                var checksum = SourceChecksum.Compute(files.ConvertAll(s => (s.RelativePath, s.Content)));
                return PgPkg.Create(new PgPkgManifest("Extracted", "18", "1.0.0-test", created, checksum), m, files);
            }

            var report = PackageVerifier.Verify(
                FromModel(live1, "2026-01-01T00:00:00Z"),
                FromModel(live2, "2026-06-12T00:00:00Z"));
            Assert.True(report.Equivalent,
                "two extracts of the same database must package equivalently:\n" + PackageVerifier.RenderText(report));
        }
        finally
        {
            Npgsql.NpgsqlConnection.ClearAllPools();
            await ExecAsync(adminNoPool, $"DROP DATABASE IF EXISTS \"{db}\" WITH (FORCE)");
        }
    }

    private static async Task ExecAsync(string conn, string sql)
    {
        await using var c = new Npgsql.NpgsqlConnection(conn);
        await c.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }
    [Fact]
    public async Task Text_rendering_carries_the_verdict_and_each_difference()
    {
        var a = await BuildPackageAsync("Db", BaseSql, targetVersion: "18");
        var b = await BuildPackageAsync("Db", "-- changed\n" + BaseSql, targetVersion: "16");

        var report = PackageVerifier.Verify(a, b, "a.pgpkg", "b.pgpkg");
        var text = PackageVerifier.RenderText(report);
        Assert.StartsWith("FAIL", text);
        Assert.Contains("option pgVersion", text);
        Assert.Contains("source app/Tables/widgets.sql: changed", text);

        var json = PackageVerifier.Serialize(report);
        Assert.Contains("\"equivalent\": false", json);
        Assert.Contains("\"pgVersion\"", json);
    }
}
