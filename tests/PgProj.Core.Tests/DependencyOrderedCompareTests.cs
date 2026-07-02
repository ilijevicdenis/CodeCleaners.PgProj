using System;
using System.IO;
using System.Linq;
using PgProj.Core.Comparison;
using PgProj.Core.Model;
using PgProj.Core.Project;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Wiring of the DeploymentPlanner into the production compare path (P0 audit finding, 2026-07-02 —
/// the planner shipped with #55 but had ZERO production callers, so issue #160 stayed live: a view
/// calling a function was scripted BEFORE the function, failing greenfield deploy with 42883).
/// </summary>
public class DependencyOrderedCompareTests
{
    private const string ViewCallsFunction = @"
        CREATE FUNCTION public.total() RETURNS int LANGUAGE sql AS $$ SELECT 1 $$;
        CREATE VIEW public.v_total AS SELECT public.total() AS t;";

    [Fact]
    public void Without_a_graph_the_view_precedes_the_function_the_160_bug()
    {
        var changes = new SchemaComparer().Compare(TestModel.Build(ViewCallsFunction), new DatabaseModel());
        var view = IndexOf(changes, c => c is CreateOrReplaceViewChange);
        var fn = IndexOf(changes, c => c is CreateOrReplaceFunctionChange);
        Assert.True(view < fn, "baseline (phase order): view phase 75 < function phase 80");
    }

    [Fact]
    public void With_a_graph_the_function_deploys_before_the_view_that_calls_it()
    {
        using var proj = MakeProject(ViewCallsFunction);
        var project = DatabaseProject.Load(proj.ProjectFile);
        var graph = DeploymentGraphFactory.TryBuild(project);
        Assert.NotNull(graph);

        var changes = new SchemaComparer().Compare(
            project.Build().Model, new DatabaseModel(), new ComparerOptions { DependencyGraph = graph });

        var view = IndexOf(changes, c => c is CreateOrReplaceViewChange);
        var fn = IndexOf(changes, c => c is CreateOrReplaceFunctionChange);
        Assert.True(fn < view, $"function must precede the view that calls it (function at {fn}, view at {view})");
    }

    [Fact]
    public void Generator_preserves_the_refined_order_when_asked()
    {
        using var proj = MakeProject(ViewCallsFunction);
        var project = DatabaseProject.Load(proj.ProjectFile);
        var graph = DeploymentGraphFactory.TryBuild(project);
        var changes = new SchemaComparer().Compare(
            project.Build().Model, new DatabaseModel(), new ComparerOptions { DependencyGraph = graph });

        var script = new DeployScriptGenerator().Generate(changes, new DeployOptions { PreserveChangeOrder = true });
        var fnAt = script.IndexOf("CREATE OR REPLACE FUNCTION", StringComparison.OrdinalIgnoreCase);
        var viewAt = script.IndexOf("CREATE OR REPLACE VIEW", StringComparison.OrdinalIgnoreCase);
        Assert.True(fnAt >= 0 && viewAt >= 0);
        Assert.True(fnAt < viewAt, "the emitted script must keep the dependency-refined order");
    }

    [Fact]
    public void With_a_graph_but_no_binding_edges_the_order_equals_the_phase_sort()
    {
        // The golden-script equivalence guarantee: no edge ⇒ byte-identical to OrderBy(Phase).
        const string independent = @"
            CREATE TABLE public.a (id int);
            CREATE FUNCTION public.f() RETURNS int LANGUAGE sql AS $$ SELECT 1 $$;
            CREATE VIEW public.v AS SELECT 1 AS one;";
        using var proj = MakeProject(independent);
        var project = DatabaseProject.Load(proj.ProjectFile);
        var model = project.Build().Model;

        var plain = new SchemaComparer().Compare(model, new DatabaseModel());
        var refined = new SchemaComparer().Compare(model, new DatabaseModel(),
            new ComparerOptions { DependencyGraph = DeploymentGraphFactory.TryBuild(project) });

        Assert.Equal(plain.Select(c => c.Describe()), refined.Select(c => c.Describe()));
    }

    private static int IndexOf(System.Collections.Generic.IReadOnlyList<SchemaChange> changes, Func<SchemaChange, bool> match)
    {
        for (var i = 0; i < changes.Count; i++)
            if (match(changes[i])) return i;
        return -1;
    }

    // ---- a minimal on-disk project (DeploymentGraphFactory reads files via the project) --------------

    private sealed class TempProject : IDisposable
    {
        public string ProjectFile { get; }
        private readonly string _dir;

        public TempProject(string dir, string projectFile) { _dir = dir; ProjectFile = projectFile; }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static TempProject MakeProject(string sql)
    {
        var dir = Path.Combine(Path.GetTempPath(), "pgproj_deporder_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "objects.sql"), sql);
        var proj = Path.Combine(dir, "t.pgproj");
        File.WriteAllText(proj,
            "<Project DefaultTargets=\"Build\"><PropertyGroup><Name>t</Name><DefaultSchema>public</DefaultSchema></PropertyGroup></Project>");
        return new TempProject(dir, proj);
    }
}
