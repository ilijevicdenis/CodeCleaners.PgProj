using System;
using System.Collections.Generic;
using System.Linq;
using PgProj.Core.Analysis;
using PgProj.Core.Model;
using PgProj.Core.Model.Identity;
using PgProj.Core.Semantics;
using PgProj.Core.Semantics.Dependencies;
using PgProj.Core.Semantics.Incremental;
using PgProj.Core.Syntax;
using Xunit;
using Diagnostic = PgProj.Core.Diagnostics.Diagnostic;

namespace PgProj.Core.Tests;

/// <summary>
/// Phase 15 (issue #57): incremental analysis &amp; object cache. All DB-free — snapshots + dependency graph
/// are built straight from PgParser output, and the per-object "analyze" delegate is a counting stub so the
/// tests can assert exactly which objects were re-analyzed (the recompute count is the acceptance metric).
/// </summary>
public sealed class IncrementalAnalysisTests
{
    // ---- harness: build (snapshots, graph) for a model, with per-object CanonicalHash -----------------

    private const string Schema = "app";

    // Compute, from DDL, the snapshot list (graph-key + CanonicalHash per table/view/function) AND the
    // dependency graph — the same production inputs DatabaseProject would feed the incremental layer.
    private static (List<ObjectSnapshot> snapshots, DependencyGraph graph) Build(string sql)
    {
        var model = ModelOf(sql);
        var identity = new ObjectIdentityComputer();

        var snapshots = new List<ObjectSnapshot>();
        foreach (var t in model.Tables)
            snapshots.Add(new ObjectSnapshot(Key(t.Schema, t.Name), identity.CanonicalHashOf(t)));
        foreach (var v in model.Views)
            snapshots.Add(new ObjectSnapshot(Key(v.Schema, v.Name), identity.CanonicalHashOf(v)));

        var catalog = CatalogBuilder.Build(sql, Schema);
        ReferenceCollector.Collect(catalog, new PgParser().Parse(sql), "schema.sql");
        var graph = DependencyGraphBuilder.Build(catalog.Symbols);
        return (snapshots, graph);
    }

    private static string Key(string schema, string name) => $"{schema}.{name}".ToLowerInvariant();

    private static DatabaseModel ModelOf(string sql) =>
        new ModelBuilder(Schema).Build(new PgParser().Parse(sql));

    // A counting analyze delegate: records every key it is asked to (re)analyze, so a test can assert the
    // recompute set. Produces a trivial diagnostic tagged with the object key so reuse can be observed too.
    private static Func<ObjectSnapshot, AnalyzedObject> CountingAnalyzer(List<string> log) => snap =>
    {
        log.Add(snap.Key);
        return new AnalyzedObject
        {
            Key = snap.Key,
            CanonicalHash = snap.CanonicalHash,
            Diagnostics = new[]
            {
                new Diagnostic { Severity = DiagnosticSeverity.Info, Code = "TEST", Message = $"analyzed {snap.Key}", Target = snap.Key },
            },
        };
    };

    // ==============================================================================================
    //  ACCEPTANCE 1 — editing ONE object invalidates that object + its dependents only.
    // ==============================================================================================

    [Fact]
    public void Editing_one_object_recomputes_only_it_and_its_dependents()
    {
        // A wide, mostly-independent model: one base table feeding a small chain, plus many unrelated tables.
        var ddl =
            "CREATE TABLE app.t (id int);\n" +
            "CREATE VIEW app.v1 AS SELECT id FROM app.t;\n" +
            "CREATE VIEW app.v2 AS SELECT id FROM app.v1;\n" +
            "CREATE TABLE app.u1 (id int);\n" +
            "CREATE TABLE app.u2 (id int);\n" +
            "CREATE TABLE app.u3 (id int);\n" +
            "CREATE TABLE app.u4 (id int);\n" +
            "CREATE TABLE app.u5 (id int);\n";

        var (snaps0, graph0) = Build(ddl);
        var analyzer = new IncrementalAnalyzer();

        var full = analyzer.AnalyzeFull(snaps0, graph0, CountingAnalyzer(new List<string>()));
        Assert.Equal(8, full.TotalObjects);                       // t, v1, v2, u1..u5

        // Edit ONLY app.t (add a column → its CanonicalHash flips; everything else is byte-identical).
        var edited =
            "CREATE TABLE app.t (id int, name text);\n" +         // <-- changed
            "CREATE VIEW app.v1 AS SELECT id FROM app.t;\n" +
            "CREATE VIEW app.v2 AS SELECT id FROM app.v1;\n" +
            "CREATE TABLE app.u1 (id int);\n" +
            "CREATE TABLE app.u2 (id int);\n" +
            "CREATE TABLE app.u3 (id int);\n" +
            "CREATE TABLE app.u4 (id int);\n" +
            "CREATE TABLE app.u5 (id int);\n";

        var (snaps1, graph1) = Build(edited);
        var log = new List<string>();
        var inc = analyzer.Update(full, snaps1, graph1, CountingAnalyzer(log));

        // Recompute set = app.t (changed) + app.v1, app.v2 (reverse-dependency closure). NOTHING else.
        Assert.Equal(new[] { "app.t", "app.v1", "app.v2" }, inc.Recomputed.OrderBy(x => x).ToArray());
        Assert.Equal(new[] { "app.t", "app.v1", "app.v2" }, log.OrderBy(x => x).ToArray());

        // The metric the issue asks for: re-analysis count ≪ project object count.
        Assert.Equal(3, inc.RecomputedCount);
        Assert.Equal(8, inc.TotalObjects);
        Assert.True(inc.RecomputedCount < inc.TotalObjects);
        Assert.Equal(5, inc.ReusedCount);                        // the 5 unrelated tables were reused
        Assert.DoesNotContain("app.u1", inc.Recomputed);
    }

    // ==============================================================================================
    //  ACCEPTANCE 2 — a cache HIT on an unchanged object returns its prior diagnostics, no re-bind.
    // ==============================================================================================

    [Fact]
    public void Cache_hit_on_unchanged_object_reuses_prior_diagnostics_without_recompute()
    {
        var ddl =
            "CREATE TABLE app.a (id int);\n" +
            "CREATE TABLE app.b (id int);\n";

        var (snaps0, graph0) = Build(ddl);
        var analyzer = new IncrementalAnalyzer();
        var full = analyzer.AnalyzeFull(snaps0, graph0, CountingAnalyzer(new List<string>()));

        var priorA = full.Cache.Get("app.a")!;                   // capture the exact prior entry for app.a

        // Re-run with an IDENTICAL model: nothing changed, so nothing should recompute.
        var (snaps1, graph1) = Build(ddl);
        var log = new List<string>();
        var inc = analyzer.Update(full, snaps1, graph1, CountingAnalyzer(log));

        Assert.Empty(log);                                       // analyze delegate was NEVER invoked
        Assert.Equal(0, inc.RecomputedCount);
        Assert.Equal(2, inc.ReusedCount);

        // The reused entry is the very same prior object (same reference) — prior diagnostics preserved verbatim.
        var reusedA = inc.Cache.Get("app.a")!;
        Assert.Same(priorA, reusedA);
        Assert.Equal(priorA.Diagnostics, reusedA.Diagnostics);
        Assert.Equal("analyzed app.a", reusedA.Diagnostics.Single().Message);
    }

    // ==============================================================================================
    //  ACCEPTANCE 3 — reverse-closure invalidation: changing a base TABLE invalidates the VIEW on it.
    // ==============================================================================================

    [Fact]
    public void Changing_a_base_table_invalidates_the_view_that_depends_on_it()
    {
        var ddl =
            "CREATE TABLE app.t (id int);\n" +
            "CREATE VIEW app.v AS SELECT id FROM app.t;\n";

        var (snaps0, graph0) = Build(ddl);
        var analyzer = new IncrementalAnalyzer();
        var full = analyzer.AnalyzeFull(snaps0, graph0, CountingAnalyzer(new List<string>()));

        // Sanity: the graph really does carry the reverse edge (view depends on table).
        Assert.Contains("app.v", graph0.ReverseDependencies("app.t"));

        // Change the base table; the view body is untextually unchanged but is invalidated by the closure.
        var edited =
            "CREATE TABLE app.t (id bigint);\n" +                 // type change → table CanonicalHash flips
            "CREATE VIEW app.v AS SELECT id FROM app.t;\n";

        var (snaps1, graph1) = Build(edited);
        var log = new List<string>();
        var inc = analyzer.Update(full, snaps1, graph1, CountingAnalyzer(log));

        Assert.Contains("app.t", inc.Recomputed);                // the directly-changed table
        Assert.Contains("app.v", inc.Recomputed);                // the dependent view (reverse closure)
        Assert.Equal(2, inc.RecomputedCount);
        Assert.Empty(inc.Reused);
    }

    // ==============================================================================================
    //  Staleness detection — CanonicalHash mismatch ⇒ stale; equal ⇒ current (the ObjectCache rule).
    // ==============================================================================================

    [Fact]
    public void ObjectCache_staleness_is_a_canonical_hash_comparison()
    {
        var (snaps, _) = Build("CREATE TABLE app.t (id int);");
        var t = snaps.Single();

        var cache = new ObjectCache();
        cache.Put(new AnalyzedObject { Key = t.Key, CanonicalHash = t.CanonicalHash });

        // Same hash ⇒ current (a cache hit); a different hash ⇒ stale; a missing key ⇒ stale.
        Assert.True(cache.IsCurrent(t.Key, t.CanonicalHash));
        Assert.False(cache.IsStale(t.Key, t.CanonicalHash));

        var (snaps2, _) = Build("CREATE TABLE app.t (id int, x text);");
        var changedHash = snaps2.Single().CanonicalHash;
        Assert.NotEqual(t.CanonicalHash, changedHash);
        Assert.True(cache.IsStale(t.Key, changedHash));

        Assert.True(cache.IsStale("app.missing", t.CanonicalHash));
    }

    // ==============================================================================================
    //  Removal invalidation — deleting a base table re-analyzes (now-dangling) dependents and drops it.
    // ==============================================================================================

    [Fact]
    public void Deleting_an_object_drops_it_and_reanalyzes_its_former_dependents()
    {
        var ddl =
            "CREATE TABLE app.t (id int);\n" +
            "CREATE VIEW app.v AS SELECT id FROM app.t;\n" +
            "CREATE TABLE app.other (id int);\n";

        var (snaps0, graph0) = Build(ddl);
        var analyzer = new IncrementalAnalyzer();
        var full = analyzer.AnalyzeFull(snaps0, graph0, CountingAnalyzer(new List<string>()));
        Assert.Equal(3, full.TotalObjects);

        // Drop app.t (and necessarily its view, since the view can't compile without it). Keep app.other.
        // Model the realistic edit: the view text stays but its base is gone — the view must be re-evaluated.
        var edited =
            "CREATE VIEW app.v AS SELECT id FROM app.t;\n" +
            "CREATE TABLE app.other (id int);\n";

        var (snaps1, graph1) = Build(edited);
        var log = new List<string>();
        var inc = analyzer.Update(full, snaps1, graph1, CountingAnalyzer(log));

        Assert.Contains("app.t", inc.Removed);                   // the deleted table is dropped from the cache
        Assert.False(inc.Cache.Contains("app.t"));
        Assert.Contains("app.v", inc.Recomputed);                // its former dependent is re-analyzed
        Assert.DoesNotContain("app.other", inc.Recomputed);      // the unrelated table is reused
        Assert.Contains("app.other", inc.Reused);
    }

    // ==============================================================================================
    //  Adding a new object analyzes only the new object (and any existing dependents — none here).
    // ==============================================================================================

    [Fact]
    public void Adding_a_new_object_recomputes_only_the_new_object()
    {
        var (snaps0, graph0) = Build("CREATE TABLE app.t (id int);");
        var analyzer = new IncrementalAnalyzer();
        var full = analyzer.AnalyzeFull(snaps0, graph0, CountingAnalyzer(new List<string>()));

        var (snaps1, graph1) = Build(
            "CREATE TABLE app.t (id int);\n" +
            "CREATE TABLE app.fresh (id int);\n");
        var log = new List<string>();
        var inc = analyzer.Update(full, snaps1, graph1, CountingAnalyzer(log));

        Assert.Equal(new[] { "app.fresh" }, log.ToArray());      // only the new object was analyzed
        Assert.Equal(new[] { "app.fresh" }, inc.Recomputed.ToArray());
        Assert.Contains("app.t", inc.Reused);
        Assert.Equal(2, inc.TotalObjects);
    }
}
