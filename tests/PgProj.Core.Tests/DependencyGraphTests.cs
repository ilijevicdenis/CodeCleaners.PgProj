using System.Linq;
using PgProj.Core.Analysis;
using PgProj.Core.Semantics;
using PgProj.Core.Semantics.Dependencies;
using PgProj.Core.Syntax;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Phases 6-7 (issue #50): the schema-object dependency graph + circular-dependency detection. All DB-free —
/// built straight from a <see cref="Catalog"/>/<see cref="SymbolTable"/> populated by PgParser output.
/// </summary>
public sealed class DependencyGraphTests
{
    // Build a catalog from DDL, collect references, then derive the graph — the production path.
    private static (DependencyGraph graph, Catalog catalog) BuildGraph(string sql, string schema = "app")
    {
        var catalog = CatalogBuilder.Build(sql, schema);
        ReferenceCollector.Collect(catalog, new PgParser().Parse(sql), "schema.sql");
        var graph = DependencyGraphBuilder.Build(catalog.Symbols);
        return (graph, catalog);
    }

    // ---- ACCEPTANCE: View A ↔ View B mutual reference reported with the FULL cycle path ----------

    [Fact]
    public void Mutual_view_reference_is_reported_with_full_cycle_path()
    {
        // app.a selects from app.b and vice-versa: a true hard ordering cycle (Postgres can't create either
        // first). The diagnostic must name the WHOLE loop a → b → a, not just one culprit.
        var (graph, _) = BuildGraph(
            "CREATE VIEW app.a AS SELECT 1 FROM app.b;\n" +
            "CREATE VIEW app.b AS SELECT 1 FROM app.a;");

        var cycles = CycleDetector.Detect(graph);
        Assert.Single(cycles);

        var cycle = cycles[0];
        Assert.True(cycle.IsHard);                                  // view→view via SELECT is a Hard edge
        // Full path closes on itself: a → b → a (start node repeated at the end).
        Assert.Equal(cycle.Path[0], cycle.Path[^1]);
        Assert.Contains("app.a", cycle.Path);
        Assert.Contains("app.b", cycle.Path);
        Assert.Equal("app.a → app.b → app.a", cycle.Describe());

        // Lifted to a unified diagnostic it is an ERROR carrying the full path in the message.
        var diags = CycleDetector.DetectDiagnostics(graph);
        var d = Assert.Single(diags);
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.Equal(CycleDetector.HardCycleCode, d.Code);
        Assert.Contains("app.a → app.b → app.a", d.Message);
    }

    // ---- ACCEPTANCE: reverse-dependency query returns the correct closure (Phase 15) ------------

    [Fact]
    public void Reverse_dependency_query_returns_the_correct_closure()
    {
        // t ← v1 ← v2 (v2 selects v1, v1 selects t). Changing t must dirty v1 AND v2 (transitive closure).
        var (graph, _) = BuildGraph(
            "CREATE TABLE app.t (id int);\n" +
            "CREATE VIEW app.v1 AS SELECT id FROM app.t;\n" +
            "CREATE VIEW app.v2 AS SELECT id FROM app.v1;");

        var closure = graph.ReverseDependencies("app.t");
        Assert.Equal(2, closure.Count);
        Assert.Contains("app.v1", closure);
        Assert.Contains("app.v2", closure);

        // A leaf dependent has an empty reverse closure; forward deps point the other way.
        Assert.Empty(graph.ReverseDependencies("app.v2"));
        Assert.Equal(new[] { "app.t" }, graph.ForwardDependencies("app.v1").OrderBy(x => x).ToArray());
        Assert.Contains("app.t", graph.ForwardDependencies("app.v2"));   // v2 → v1 → t (transitive)
        Assert.Contains("app.v1", graph.ForwardDependencies("app.v2"));
    }

    // ---- ACCEPTANCE: edge classification — Hard (view→table) vs Runtime (dynamic SQL) -----------

    [Fact]
    public void Edge_classification_distinguishes_hard_view_to_table_from_runtime_dynamic_sql()
    {
        const string sql =
            "CREATE TABLE app.t (id int);\n" +
            "CREATE TABLE app.logs (msg text);\n" +
            "CREATE VIEW app.v AS SELECT id FROM app.t;\n" +
            "CREATE FUNCTION app.f() RETURNS void AS $$ BEGIN EXECUTE 'SELECT msg FROM app.logs'; END $$ LANGUAGE plpgsql;";

        var catalog = CatalogBuilder.Build(sql, "app");
        ReferenceCollector.Collect(catalog, new PgParser().Parse(sql), "schema.sql");
        var graph = DependencyGraphBuilder.Build(catalog.Symbols);
        // Runtime edges are NOT derived by reference inversion — they come from the dynamic-SQL scan.
        DependencyGraphBuilder.AddRuntimeEdges(graph, catalog.Symbols, new PgParser().Parse(sql),
            catalog.SearchPath, "app");

        // The view→table edge is Hard (Postgres-enforced: the table must exist to create the view).
        var hard = graph.OutgoingEdges("app.v").Single();
        Assert.Equal("app.t", hard.ToKey);
        Assert.Equal(DependencyKind.Hard, hard.Kind);

        // The function's reference to app.logs lives only in dynamic SQL ⇒ a Runtime edge, never Hard.
        // The function node key carries its (empty) overload signature: "app.f()".
        var fnKey = catalog.Symbols.ResolveFunction("app", "f", new FunctionSignature(""))!.Key;
        var fnEdge = graph.OutgoingEdges(fnKey).Single();
        Assert.Equal("app.logs", fnEdge.ToKey);
        Assert.Equal(DependencyKind.Runtime, fnEdge.Kind);

        // Runtime edges are excluded from ordering traversal (default) but visible when asked for.
        Assert.Empty(graph.ForwardDependencies(fnKey));                        // ordering: nothing
        Assert.Contains("app.logs", graph.ForwardDependencies(fnKey, includeRuntime: true));

        // …and excluded from cycle detection (a runtime back-edge can't make a deploy cycle).
        Assert.Empty(CycleDetector.Detect(graph));
    }

    // ---- Forward/reverse traversal over a small known graph --------------------------------------

    [Fact]
    public void Forward_and_reverse_traversal_over_a_small_known_graph()
    {
        // Diamond: d depends on b and c; b and c each depend on a.  d → {b,c} → a
        var graph = new DependencyGraph();
        foreach (var k in new[] { "a", "b", "c", "d" })
            graph.AddNode(SymbolEntry.ForRelation("s", k));
        graph.AddEdge("s.b", "s.a", DependencyKind.Hard, "test");
        graph.AddEdge("s.c", "s.a", DependencyKind.Hard, "test");
        graph.AddEdge("s.d", "s.b", DependencyKind.Hard, "test");
        graph.AddEdge("s.d", "s.c", DependencyKind.Hard, "test");

        // Forward closure of d = everything it transitively needs.
        Assert.Equal(new[] { "s.a", "s.b", "s.c" },
            graph.ForwardDependencies("s.d").OrderBy(x => x).ToArray());

        // Reverse closure of a = everything that transitively needs a.
        Assert.Equal(new[] { "s.b", "s.c", "s.d" },
            graph.ReverseDependencies("s.a").OrderBy(x => x).ToArray());

        // A leaf has no forward deps; the root has no reverse deps.
        Assert.Empty(graph.ForwardDependencies("s.a"));
        Assert.Empty(graph.ReverseDependencies("s.d"));

        // Direct edges are queryable both ways.
        Assert.Equal(2, graph.IncomingEdges("s.a").Count);
        Assert.Equal(2, graph.OutgoingEdges("s.d").Count);
    }

    // ---- Soft-only cycle is a warning, hard cycle is an error ------------------------------------

    [Fact]
    public void Soft_only_cycle_is_a_warning_hard_cycle_is_an_error()
    {
        var graph = new DependencyGraph();
        foreach (var k in new[] { "x", "y" }) graph.AddNode(SymbolEntry.ForRelation("s", k));
        graph.AddEdge("s.x", "s.y", DependencyKind.Soft, "preferred");
        graph.AddEdge("s.y", "s.x", DependencyKind.Soft, "preferred");

        var cycle = Assert.Single(CycleDetector.Detect(graph));
        Assert.False(cycle.IsHard);                                  // all-soft loop

        var d = Assert.Single(CycleDetector.DetectDiagnostics(graph));
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);       // preference-only ⇒ warning, not error
        Assert.Equal(CycleDetector.SoftCycleCode, d.Code);
        Assert.Contains("s.x → s.y → s.x", d.Message);
    }

    // ---- Legal, non-cyclic graph & self-reference produce no diagnostics -------------------------

    [Fact]
    public void Acyclic_graph_and_self_reference_report_no_cycle()
    {
        // A normal layered model has no cycle.
        var (graph, _) = BuildGraph(
            "CREATE TABLE app.t (id int);\n" +
            "CREATE VIEW app.v AS SELECT id FROM app.t;");
        Assert.Empty(CycleDetector.Detect(graph));

        // A self-reference (recursive view) is NOT a cycle: the graph drops self-edges, matching Postgres
        // which permits WITH RECURSIVE / self-recursive functions.
        var g2 = new DependencyGraph();
        g2.AddNode(SymbolEntry.ForRelation("s", "r"));
        g2.AddEdge("s.r", "s.r", DependencyKind.Hard, "self");
        Assert.Empty(g2.OutgoingEdges("s.r"));                       // self-edge dropped
        Assert.Empty(CycleDetector.Detect(g2));
    }

    // ---- A 3-node cycle names every object on the loop ------------------------------------------

    [Fact]
    public void Three_node_cycle_names_the_full_path()
    {
        var graph = new DependencyGraph();
        foreach (var k in new[] { "a", "b", "c" }) graph.AddNode(SymbolEntry.ForRelation("s", k));
        graph.AddEdge("s.a", "s.b", DependencyKind.Hard, "t");
        graph.AddEdge("s.b", "s.c", DependencyKind.Hard, "t");
        graph.AddEdge("s.c", "s.a", DependencyKind.Hard, "t");

        var cycle = Assert.Single(CycleDetector.Detect(graph));
        Assert.Equal("s.a → s.b → s.c → s.a", cycle.Describe());     // full loop, every node named
        Assert.True(cycle.IsHard);
    }
}
