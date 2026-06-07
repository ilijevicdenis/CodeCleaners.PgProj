using System;
using System.Collections.Generic;
using System.Linq;
using PgProj.Core.Comparison;
using PgProj.Core.Model;
using PgProj.Core.Project;
using PgProj.Core.Semantics;
using PgProj.Core.Semantics.Dependencies;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Phase 13 (issue #55) — the <see cref="DeploymentPlanner"/>: topo-sort against the #50 dependency graph,
/// edge-class handling, and the hard-cycle → skeleton-then-alter split. All DB-free.
/// </summary>
public sealed class DeploymentPlannerTests
{
    private static SchemaChange View(string schema, string name, string body = "SELECT 1") =>
        new CreateOrReplaceViewChange(new ViewDefinition(schema, name, body));

    private static SchemaChange Func(string schema, string name, string argTypes = "") =>
        new CreateOrReplaceFunctionChange(new FunctionDefinition(
            schema, name, $"{schema}.{name}({argTypes})",
            $"CREATE OR REPLACE FUNCTION {SqlEmitter.Qualified(schema, name)}() RETURNS void AS $$ BEGIN END $$ LANGUAGE plpgsql;",
            argTypes));

    private static DependencyGraph GraphOf(params SymbolEntry[] nodes)
    {
        var g = new DependencyGraph();
        foreach (var n in nodes) g.AddNode(n);
        return g;
    }

    // ---- ACCEPTANCE: two mutually-referencing views deploy via a skeleton pass ------------------

    [Fact]
    public void Hard_cycle_of_two_views_is_split_into_a_skeleton_pass()
    {
        var a = View("app", "a");
        var b = View("app", "b");

        // app.a depends on app.b and vice-versa: a hard ordering cycle.
        var g = GraphOf(SymbolEntry.ForRelation("app", "a"), SymbolEntry.ForRelation("app", "b"));
        g.AddEdge("app.a", "app.b", DependencyKind.Hard, "view selects view");
        g.AddEdge("app.b", "app.a", DependencyKind.Hard, "view selects view");

        var plan = new DeploymentPlanner().Plan(new[] { a, b }, g);

        Assert.True(plan.HasSkeletonPass);
        Assert.Single(plan.BrokenCycles);
        // The seed is the lexicographically-smallest member (app.a); its stub runs first.
        var stub = Assert.Single(plan.SkeletonPass);
        Assert.IsType<SkeletonChange>(stub);
        Assert.Contains("app", stub.Describe());
        Assert.Contains("a", stub.Describe());
        // Both real definitions still appear in the ordered pass.
        Assert.Contains(a, plan.Ordered);
        Assert.Contains(b, plan.Ordered);
        // The skeleton sorts ahead of the real changes in the full step list.
        var steps = plan.AllSteps.ToList();
        Assert.True(steps.IndexOf(stub) < steps.IndexOf(a));
        Assert.True(steps.IndexOf(stub) < steps.IndexOf(b));
    }

    [Fact]
    public void Hard_cycle_of_two_functions_is_split_into_a_skeleton_pass()
    {
        var f = Func("app", "f");
        var g2 = Func("app", "g");

        var g = GraphOf(
            SymbolEntry.ForFunction("app", "f", new FunctionSignature("")),
            SymbolEntry.ForFunction("app", "g", new FunctionSignature("")));
        g.AddEdge("app.f()", "app.g()", DependencyKind.Hard, "function calls function");
        g.AddEdge("app.g()", "app.f()", DependencyKind.Hard, "function calls function");

        var plan = new DeploymentPlanner().Plan(new[] { f, g2 }, g);

        Assert.True(plan.HasSkeletonPass);
        var stub = Assert.Single(plan.SkeletonPass);
        // A function skeleton is a CREATE OR REPLACE FUNCTION stub completed by the real body later.
        Assert.Contains("CREATE OR REPLACE FUNCTION", stub.ToSql());
        Assert.Contains("function app.f", stub.Describe());   // app.f() is the lex-smallest seed
    }

    // ---- ACCEPTANCE: topo-sort orders a same-phase A→B dependency correctly ----------------------

    [Fact]
    public void Topo_sort_orders_a_same_phase_dependency_correctly()
    {
        // Two views at the SAME phase (75). app.a depends on app.b ⇒ b must be created before a, even though
        // by stable phase order (input order) a comes first.
        var a = View("app", "a");
        var b = View("app", "b");

        var g = GraphOf(SymbolEntry.ForRelation("app", "a"), SymbolEntry.ForRelation("app", "b"));
        g.AddEdge("app.a", "app.b", DependencyKind.Hard, "view selects view");

        var plan = new DeploymentPlanner().Plan(new[] { a, b }, g);

        Assert.False(plan.HasSkeletonPass);                 // acyclic — no skeleton
        Assert.Equal(new[] { b, a }, plan.Ordered.ToArray()); // b before a despite input order a, b
    }

    [Fact]
    public void Runtime_edges_do_not_constrain_order()
    {
        // A runtime edge (dynamic SQL) must NOT reorder: it fires at call time, after everything exists.
        var a = View("app", "a");
        var b = View("app", "b");

        var g = GraphOf(SymbolEntry.ForRelation("app", "a"), SymbolEntry.ForRelation("app", "b"));
        g.AddEdge("app.a", "app.b", DependencyKind.Runtime, "dynamic SQL");

        var plan = new DeploymentPlanner().Plan(new[] { a, b }, g);
        // No constraint ⇒ stable phase/input order preserved: a then b.
        Assert.Equal(new[] { a, b }, plan.Ordered.ToArray());
    }

    [Fact]
    public void Soft_only_cycle_is_not_broken_and_deploy_still_succeeds()
    {
        var a = View("app", "a");
        var b = View("app", "b");

        var g = GraphOf(SymbolEntry.ForRelation("app", "a"), SymbolEntry.ForRelation("app", "b"));
        g.AddEdge("app.a", "app.b", DependencyKind.Soft, "preferred");
        g.AddEdge("app.b", "app.a", DependencyKind.Soft, "preferred");

        var plan = new DeploymentPlanner().Plan(new[] { a, b }, g);

        Assert.False(plan.HasSkeletonPass);                 // soft cycle ⇒ no skeleton
        Assert.Single(plan.SoftCycles);
        Assert.Equal(2, plan.Ordered.Count);                // both still emitted (any order is safe)
        Assert.Contains(a, plan.Ordered);
        Assert.Contains(b, plan.Ordered);
    }

    [Fact]
    public void No_graph_yields_the_stable_phase_order()
    {
        var a = View("app", "a");
        var b = View("app", "b");
        var t = new CreateTableChange(new TableDefinition { Schema = "app", Name = "t" });

        var plan = new DeploymentPlanner().Plan(new SchemaChange[] { a, b, t });

        Assert.False(plan.HasSkeletonPass);
        // Table (phase 40) precedes the views (phase 75); within the views the input order is kept.
        Assert.Equal(new SchemaChange[] { t, a, b }, plan.Ordered.ToArray());
    }

    // ---- ACCEPTANCE: deterministic plan order for AllFeaturesDb --------------------------------

    [Fact]
    public void AllFeaturesDb_plan_is_deterministic_and_equivalent_to_the_static_order()
    {
        var projectFile = FindSampleProject();
        var project = DatabaseProject.Load(projectFile);
        var built = project.Build();
        Assert.False(built.HasErrors);

        var changes = new SchemaComparer().Compare(built.Model, new DatabaseModel());

        // Build the dependency graph from the project's combined source so the planner has real edges (the
        // production path mirrors DependencyGraphTests: CatalogBuilder + ReferenceCollector → graph).
        var graph = BuildGraph(projectFile);

        var planner = new DeploymentPlanner();
        var plan1 = planner.Plan(changes, graph);
        var plan2 = planner.Plan(changes, graph);

        // Deterministic: two runs produce the identical ordered sequence.
        Assert.Equal(
            plan1.AllSteps.Select(c => c.Describe()).ToArray(),
            plan2.AllSteps.Select(c => c.Describe()).ToArray());

        // Equivalent to the static order for this acyclic project: the generated script from the plan equals
        // the script from the plain change list with default options (golden-equivalence).
        var gen = new DeployScriptGenerator();
        var fromPlan = gen.Generate(plan1, new DeployOptions { WrapInTransaction = true });
        var fromChanges = gen.Generate(changes, new DeployOptions { WrapInTransaction = true });
        Assert.Equal(Normalise(fromChanges), Normalise(fromPlan));
        Assert.False(plan1.HasSkeletonPass);   // AllFeaturesDb is acyclic
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static DependencyGraph BuildGraph(string projectFile)
    {
        // The sample project ships a single concatenated create script; build the catalog/symbols from it and
        // derive the graph the production way. Default schema "afd" matches the sample's objects.
        var dir = System.IO.Path.GetDirectoryName(projectFile)!;
        var full = System.IO.Path.Combine(dir, "_full_create.sql");
        var sql = System.IO.File.Exists(full) ? System.IO.File.ReadAllText(full) : "";
        var catalog = PgProj.Core.Semantics.CatalogBuilder.Build(sql, "afd");
        PgProj.Core.Semantics.ReferenceCollector.Collect(catalog, new PgProj.Core.Syntax.PgParser().Parse(sql), "_full_create.sql");
        return DependencyGraphBuilder.Build(catalog.Symbols);
    }

    private static string Normalise(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n");

    private static string FindSampleProject()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && dir is not null; i++, dir = System.IO.Path.GetDirectoryName(dir))
        {
            var candidate = System.IO.Path.Combine(dir, "sample", "AllFeaturesDb", "AllFeaturesDb.pgproj");
            if (System.IO.File.Exists(candidate)) return candidate;
        }
        throw new System.IO.FileNotFoundException("Could not locate sample/AllFeaturesDb/AllFeaturesDb.pgproj");
    }
}
