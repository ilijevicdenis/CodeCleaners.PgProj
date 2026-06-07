using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PgProj.Core.Comparison;
using PgProj.Core.Introspection;
using PgProj.Core.Model;
using PgProj.Core.Project;
using PgProj.Core.Syntax;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// End-to-end "read from database" check, driven entirely from C# (ADO.NET via Npgsql — no shelling to
/// the CLI). Greenfield-deploys the AllFeaturesDb sample to a live server, reads it back with the
/// parallel <see cref="LiveDatabaseReader"/>, and re-parses every exported object so the catalog DDL is
/// proven to round-trip through the parser. Skipped unless PGPROJ_TEST_CONNECTION points at a live DB.
///
/// Each run gets its OWN throwaway database (via <see cref="ThrowawayDatabaseFixture"/>) so there is no
/// shared-state pollution between test classes and no manual DROP-cleanup required.
/// </summary>
public sealed class LiveReaderIntegrationTests : IClassFixture<ThrowawayDatabaseFixture>
{
    private readonly ThrowawayDatabaseFixture _fixture;

    public LiveReaderIntegrationTests(ThrowawayDatabaseFixture fixture)
        => _fixture = fixture;

    [Fact]
    public async Task Deploy_read_back_and_reparse_round_trips()
    {
        var conn = _fixture.ConnectionString;
        if (string.IsNullOrWhiteSpace(conn)) return;   // no live DB available — treated as a skip

        var project = DatabaseProject.Load(FindSampleProject());
        var built = project.Build();
        Assert.False(built.HasErrors, "sample project build: " + string.Join("\n", built.Diagnostics));

        // Scenario 1 (greenfield): full create script, deployed in one transaction.
        var create = new SchemaComparer().Compare(built.Model, new DatabaseModel());
        var script = new DeployScriptGenerator().Generate(create, new DeployOptions { WrapInTransaction = true });

        var deployer = new DatabaseDeployer();
        await deployer.ExecuteAsync(conn, script);

        // Read it back with the parallel reader and sanity-check the shape.
        var live = await new LiveDatabaseReader().ReadAsync(conn);
        Assert.Contains(live.Schemas, s => DatabaseModel.NameEquals(s.Name, "afd"));
        Assert.NotEmpty(live.Tables);
        Assert.NotEmpty(live.Functions);

        // Foreign-data wrapper / server / conversion are reconstructed as real DDL (not existence-only),
        // so they carry a body and participate in extract + re-deploy below.
        Assert.Contains(live.Objects, o => o.Kind == ObjectKind.ForeignDataWrapper && o.Body.Contains("FOREIGN DATA WRAPPER"));
        Assert.Contains(live.Objects, o => o.Kind == ObjectKind.Server && o.Body.Contains("CREATE SERVER"));
        Assert.Contains(live.Objects, o => o.Kind == ObjectKind.Conversion && o.Body.Contains("CONVERSION"));
        Assert.Contains(live.Objects, o => o.Kind == ObjectKind.Statistics && o.Body.Contains("CREATE STATISTICS"));
        Assert.Contains(live.Objects, o => o.Kind == ObjectKind.Cast && o.Body.Contains("CREATE CAST"));
        Assert.Contains(live.Objects, o => o.Kind == ObjectKind.ForeignTable && o.Body.Contains("CREATE FOREIGN TABLE"));
        Assert.Contains(live.Objects, o => o.Kind == ObjectKind.Operator && o.Body.Contains("CREATE OPERATOR"));
        Assert.Contains(live.Objects, o => o.Kind == ObjectKind.TextSearchDictionary && o.Body.Contains("CREATE TEXT SEARCH DICTIONARY"));
        Assert.Contains(live.Objects, o => o.Kind == ObjectKind.TextSearchConfiguration && o.Body.Contains("ADD MAPPING"));
        Assert.Contains(live.Objects, o => o.Kind == ObjectKind.OperatorClass && o.Body.Contains("CREATE OPERATOR CLASS"));
        Assert.Contains(live.Objects, o => o.Kind == ObjectKind.Publication && o.Body.Contains("CREATE PUBLICATION") && o.Body.Contains("FOR TABLE"));
        // #104: event-trigger reconstruction now carries the WHEN TAG IN (...) filter (was omitted).
        Assert.Contains(live.Objects, o => o.Kind == ObjectKind.EventTrigger
            && o.Body.Contains("WHEN TAG IN") && o.Body.Contains("'CREATE TABLE'"));
        // #103: policy reconstruction now carries the TO roles clause (was omitted).
        Assert.Contains(live.Objects, o => o.Kind == ObjectKind.Policy && o.Body.Contains(" TO PUBLIC"));
        // #98: EXCLUDE constraints are introspected into TableDefinition.OtherConstraints.
        Assert.Contains(live.Tables, t => t.OtherConstraints.Any(c => c.Contains("EXCLUDE")));
        // #108: user mappings are introspected (FOR <user> SERVER <server> [OPTIONS …]).
        Assert.Contains(live.Objects, o => o.Kind == ObjectKind.UserMapping
            && o.Body.Contains("CREATE USER MAPPING") && o.Body.Contains("SERVER dummy_server"));
        // #99: partitioning + inheritance round-trip. Parent carries PARTITION BY; children are raw
        // PARTITION OF objects (not flattened to standalone tables); an INHERITS child carries INHERITS.
        Assert.Contains(live.Tables, t => DatabaseModel.NameEquals(t.Name, "events")
            && (t.TrailingOptions ?? "").Contains("PARTITION BY"));
        Assert.Contains(live.Objects, o => o.Kind == ObjectKind.Table
            && DatabaseModel.NameEquals(o.Name, "events_2024") && o.Body.Contains("PARTITION OF"));
        Assert.DoesNotContain(live.Tables, t => DatabaseModel.NameEquals(t.Name, "events_2024")); // not double-modelled
        Assert.Contains(live.Tables, t => DatabaseModel.NameEquals(t.Name, "document")
            && (t.TrailingOptions ?? "").Contains("INHERITS"));

        // Every exported object's DDL must re-parse cleanly — this is the "complete the parser" check.
        var unparseable = DdlExporter.ExportFiles(live)
            .Select(kv => (kv.Key, Diags: new PgParser().Parse(kv.Value).Diagnostics))
            .Where(x => x.Diags.Count > 0)
            .Select(x => $"{x.Key}: {string.Join(" | ", x.Diags)}")
            .ToList();
        Assert.True(unparseable.Count == 0, "extracted DDL did not re-parse:\n" + string.Join("\n", unparseable));

        // Idempotence: re-comparing the live model to itself yields no changes (stable read).
        Assert.Empty(new SchemaComparer().Compare(live, live));

        // Round-trip idempotency: the *project* model compared against the freshly-read live model must be
        // free of phantom non-destructive changes. #36 scoped this to extension, text-search dict/config,
        // FDW/server, typed table, statistics and aggregates. M4 #61/#64 close most remaining gaps —
        // cast / operator / operator-class / operator-family (raw) and the finely-modelled functions /
        // generated columns / BETWEEN / EXCLUDE — so the guard now covers nearly all raw kinds in
        // AllFeaturesDb. Cast/operator/operator-class are reconciled by the kind-canonical comparison key.
        // #61 closed the last two gaps verified against PG18: Trigger (event order is canonicalized — the
        // catalog renders insert/delete/update in a fixed order) and function/procedure Comment (the
        // reconstruction now uses a types-only signature from proargtypes, matching a hand-written
        // COMMENT ON FUNCTION name(type,type)). The guard now covers ALL raw kinds AllFeaturesDb exercises.
        var scoped = new HashSet<ObjectKind>
        {
            ObjectKind.Extension, ObjectKind.TextSearchDictionary, ObjectKind.TextSearchConfiguration,
            ObjectKind.ForeignDataWrapper, ObjectKind.Server, ObjectKind.Statistics,
            ObjectKind.Aggregate, ObjectKind.Table,
            ObjectKind.Cast, ObjectKind.Operator, ObjectKind.OperatorClass, ObjectKind.OperatorFamily,
            ObjectKind.Trigger, ObjectKind.Comment,
            ObjectKind.Type, ObjectKind.Domain, ObjectKind.Collation, ObjectKind.Conversion,
            ObjectKind.Rule, ObjectKind.Policy, ObjectKind.EventTrigger, ObjectKind.ForeignTable,
            ObjectKind.Publication, ObjectKind.Language,
        };
        var roundTrip = new SchemaComparer().Compare(built.Model, live);
        var rawChurn = roundTrip
            .Select(ch => ch switch
            {
                CreateRawObjectChange c => (Kind: (ObjectKind?)c.Def.Kind, Sql: (string?)ch.ToSql()),
                RecreateRawObjectChange r => (Kind: (ObjectKind?)r.Def.Kind, Sql: (string?)ch.ToSql()),
                _ => (Kind: (ObjectKind?)null, Sql: (string?)null),
            })
            .Where(x => x.Kind is ObjectKind k && scoped.Contains(k))
            .Select(x => x.Sql!)
            .ToList();
        Assert.True(rawChurn.Count == 0, "phantom raw-object diffs on project→live round-trip (scoped kinds):\n" + string.Join("\n", rawChurn));

        // #98: with EXCLUDE constraints introspected, a project→live round-trip no longer reports a phantom
        // "add EXCLUDE" table-constraint change (these are AddRawTableConstraintChange, not a raw object).
        var exChurn = roundTrip.OfType<AddRawTableConstraintChange>().Where(c => c.Clause.Contains("EXCLUDE")).ToList();
        Assert.True(exChurn.Count == 0, "phantom EXCLUDE-constraint diffs on project→live round-trip:\n"
            + string.Join("\n", exChurn.Select(c => c.Clause)));

        // #101: opclass/expression/ordering indexes (e.g. `lower(full_name) text_pattern_ops ASC NULLS LAST`)
        // must not churn a phantom drop+recreate just because pg_get_indexdef omits the redundant defaults.
        var idxChurn = roundTrip.Where(c => c is CreateIndexChange or DropIndexChange).Select(c => c.ToSql()).ToList();
        Assert.True(idxChurn.Count == 0, "phantom index diffs on project→live round-trip:\n" + string.Join("\n", idxChurn));

        // Gold-standard round-trip: the extracted model must itself re-deploy cleanly — this proves
        // every reconstructed raw-object DDL (aggregates, FDW/server/foreign table, collation, …) is
        // valid and correctly ordered, not just parseable.
        //
        // The throwaway DB already has the full AllFeaturesDb deployed from the greenfield step above;
        // drop project-owned objects before re-deploying so the second deploy starts from a clean slate
        // within the same throwaway DB.
        var recreate = new SchemaComparer().Compare(live, new DatabaseModel());
        var script2 = new DeployScriptGenerator().Generate(recreate, new DeployOptions { WrapInTransaction = true });
        await deployer.ExecuteAsync(conn, "DROP PUBLICATION IF EXISTS customer_pub; DROP SCHEMA IF EXISTS afd CASCADE; DROP SCHEMA IF EXISTS reporting CASCADE; DROP FOREIGN DATA WRAPPER IF EXISTS dummy_fdw CASCADE;");
        await deployer.ExecuteAsync(conn, script2);
    }

    [Fact]
    public async Task ShadowValidate_accepts_valid_sql_and_catches_broken_sql()
    {
        var conn = _fixture.ConnectionString;
        if (string.IsNullOrWhiteSpace(conn)) return;
        var validator = new ShadowValidator();

        // Valid SQL applies (then rolls back + drops the scratch DB).
        var ok = await validator.ValidateAsync(conn, "CREATE TABLE public.zzz_valid (id int PRIMARY KEY, name text);");
        Assert.True(ok.Ok, ok.Error);

        // A semantic error only PostgreSQL can catch (a column of a non-existent type) is reported.
        var bad = await validator.ValidateAsync(conn, "CREATE TABLE public.zzz_bad (id nonexistent_type_xyz);");
        Assert.False(bad.Ok);
        Assert.Equal("42704", bad.SqlState);   // undefined_object

        // The whole AllFeaturesDb project validates against a real server.
        var built = DatabaseProject.Load(FindSampleProject()).Build();
        var script = new DeployScriptGenerator().Generate(
            new SchemaComparer().Compare(built.Model, new DatabaseModel()), new DeployOptions { WrapInTransaction = false });
        var full = await validator.ValidateAsync(conn, script);
        Assert.True(full.Ok, full.Error);
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
