using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PgProj.Core.Comparison;
using PgProj.Core.Introspection;
using PgProj.Core.Model;
using PgProj.Core.Project;
using PgProj.Core.Syntax;

namespace PgProj.Core.Tests;

/// <summary>
/// End-to-end "read from database" check, driven entirely from C# (ADO.NET via Npgsql — no shelling to
/// the CLI). Greenfield-deploys the AllFeaturesDb sample to a live server, reads it back with the
/// parallel <see cref="LiveDatabaseReader"/>, and re-parses every exported object so the catalog DDL is
/// proven to round-trip through the parser. Skipped unless PGPROJ_TEST_CONNECTION points at a throwaway DB.
/// </summary>
public sealed class LiveReaderIntegrationTests
{
    private static string? Conn => Environment.GetEnvironmentVariable("PGPROJ_TEST_CONNECTION");

    [Fact]
    public async Task Deploy_read_back_and_reparse_round_trips()
    {
        var conn = Conn;
        if (string.IsNullOrWhiteSpace(conn)) return;   // no live DB available — treated as a skip

        var project = DatabaseProject.Load(FindSampleProject());
        var built = project.Build();
        Assert.False(built.HasErrors, "sample project build: " + string.Join("\n", built.Diagnostics));

        // Scenario 1 (greenfield): full create script, deployed in one transaction.
        var create = new SchemaComparer().Compare(built.Model, new DatabaseModel());
        var script = new DeployScriptGenerator().Generate(create, new DeployOptions { WrapInTransaction = true });

        var deployer = new DatabaseDeployer();
        await deployer.ExecuteAsync(conn, "DROP SCHEMA IF EXISTS afd CASCADE; DROP SCHEMA IF EXISTS reporting CASCADE;");
        await deployer.ExecuteAsync(conn, script);

        // Read it back with the parallel reader and sanity-check the shape.
        var live = await new LiveDatabaseReader().ReadAsync(conn);
        Assert.Contains(live.Schemas, s => DatabaseModel.NameEquals(s.Name, "afd"));
        Assert.NotEmpty(live.Tables);
        Assert.NotEmpty(live.Functions);

        // Every exported object's DDL must re-parse cleanly — this is the "complete the parser" check.
        var unparseable = DdlExporter.ExportFiles(live)
            .Select(kv => (kv.Key, Diags: new PgParser().Parse(kv.Value).Diagnostics))
            .Where(x => x.Diags.Count > 0)
            .Select(x => $"{x.Key}: {string.Join(" | ", x.Diags)}")
            .ToList();
        Assert.True(unparseable.Count == 0, "extracted DDL did not re-parse:\n" + string.Join("\n", unparseable));

        // Idempotence: re-comparing the live model to itself yields no changes (stable read).
        Assert.Empty(new SchemaComparer().Compare(live, live));

        // Gold-standard round-trip: the extracted model must itself re-deploy cleanly — this proves
        // every reconstructed raw-object DDL (aggregates, FDW/server/foreign table, collation, …) is
        // valid and correctly ordered, not just parseable.
        var recreate = new SchemaComparer().Compare(live, new DatabaseModel());
        var script2 = new DeployScriptGenerator().Generate(recreate, new DeployOptions { WrapInTransaction = true });
        await deployer.ExecuteAsync(conn, "DROP SCHEMA IF EXISTS afd CASCADE; DROP SCHEMA IF EXISTS reporting CASCADE;");
        await deployer.ExecuteAsync(conn, script2);
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
