using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PgProj.Core.Comparison;
using PgProj.Core.Introspection;
using PgProj.Core.Model;
using PgProj.Core.Packaging;
using PgProj.Core.Project;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// End-to-end "build once, deploy many" check for the <c>.pgpkg</c> artifact, driven from C# (ADO.NET via
/// Npgsql). Builds the AllFeaturesDb sample into a package, publishes the package's embedded model to a
/// fresh server, reads it back, and asserts the live catalog matches the package. Mirrors
/// <see cref="LiveReaderIntegrationTests"/>; skipped unless PGPROJ_TEST_CONNECTION points at a throwaway DB.
/// </summary>
public sealed class PgPkgIntegrationTests
{
    private static string? Conn => Environment.GetEnvironmentVariable("PGPROJ_TEST_CONNECTION");

    private const string DropAll =
        "DROP PUBLICATION IF EXISTS customer_pub; DROP SCHEMA IF EXISTS afd CASCADE; " +
        "DROP SCHEMA IF EXISTS reporting CASCADE; DROP FOREIGN DATA WRAPPER IF EXISTS dummy_fdw CASCADE;";

    [Fact]
    public async Task Build_pgpkg_then_publish_from_package_matches_publish_from_source()
    {
        var conn = Conn;
        if (string.IsNullOrWhiteSpace(conn)) return;   // no live DB → treated as a skip

        var project = DatabaseProject.Load(FindSampleProject());
        var built = project.Build();
        Assert.False(built.HasErrors, "sample build: " + string.Join("\n", built.Diagnostics));

        // Build the .pgpkg with an injected (deterministic) stamp.
        var pkg = PgPkgBuilder.FromBuild(project, built.Model, built.Files, "test", "2026-01-01T00:00:00Z");
        var tmp = Path.Combine(Path.GetTempPath(), "pgpkg_int_" + Guid.NewGuid().ToString("N") + ".pgpkg");
        pkg.Write(tmp);
        try
        {
            // Reload purely from the package (no re-parse) — this is the deploy unit CI would ship.
            var fromPkg = PgPkg.Read(tmp);

            // The script generated from the package must be byte-identical to the one from source.
            string Create(DatabaseModel m) => new DeployScriptGenerator().Generate(
                new SchemaComparer().Compare(m, new DatabaseModel()), new DeployOptions { WrapInTransaction = true });
            Assert.Equal(Create(built.Model), Create(fromPkg.Model));

            // Publish the package model to a fresh DB.
            var deployer = new DatabaseDeployer();
            await deployer.ExecuteAsync(conn, DropAll);
            await deployer.ExecuteAsync(conn, Create(fromPkg.Model));

            // Introspect and confirm the live catalog matches the package model (no residual diff).
            var live = await new LiveDatabaseReader().ReadAsync(conn);
            Assert.Contains(live.Schemas, s => DatabaseModel.NameEquals(s.Name, "afd"));
            Assert.NotEmpty(live.Tables);

            // Every table/function in the package is present in the live catalog.
            foreach (var t in fromPkg.Model.Tables)
                Assert.Contains(live.Tables, lt => DatabaseModel.NameEquals(lt.Schema, t.Schema) && DatabaseModel.NameEquals(lt.Name, t.Name));

            // Comparing the package model to the freshly-introspected live model yields no *additions*
            // (the publish brought the server fully in line with the package).
            var residual = new SchemaComparer().Compare(fromPkg.Model, live);
            Assert.DoesNotContain(residual, c => !c.IsDestructive);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    private static string FindSampleProject()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
        {
            var candidate = Path.Combine(dir, "sample", "AllFeaturesDb", "AllFeaturesDb.pgproj");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Could not locate sample/AllFeaturesDb/AllFeaturesDb.pgproj from " + AppContext.BaseDirectory);
    }
}
