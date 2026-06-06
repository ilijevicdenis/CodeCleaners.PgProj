using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using PgProj.Core.Cli;
using PgProj.Core.Comparison;
using PgProj.Core.Model;
using PgProj.Core.Packaging;
using PgProj.Core.Project;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Exercises EP-SCHEMACOMPARE: the unified two-way <see cref="SchemaCompare"/> API, the structured
/// selectable <see cref="SchemaChangeSet"/>, object-type filters, selection round-trips, the
/// <c>--output diff.json</c> report shape (camelCase + deterministic order), the empty/in-sync case, and
/// direction. Endpoint resolution reuses <see cref="EndpointResolver"/>, so project↔project and
/// package↔project run with no live database; live-DB endpoints gate behind <see cref="DbFactAttribute"/>.
/// </summary>
public sealed class SchemaCompareTests
{
    private static DatabaseModel M(string sql) => TestModel.Build(sql);

    // ---- SchemaChangeSet: build, ids, basics ---------------------------------------------------

    [Fact]
    public void Build_assigns_a_stable_deterministic_id_per_change()
    {
        var source = M("CREATE TABLE app.t (id int PRIMARY KEY, name text NOT NULL);");
        var target = new DatabaseModel();

        var a = SchemaChangeSet.Build(source, target);
        var b = SchemaChangeSet.Build(source, target);

        Assert.False(a.InSync);
        // Ids are content-derived → identical across two independent builds, in the same order.
        Assert.Equal(a.Changes.Select(c => c.Id), b.Changes.Select(c => c.Id));
        // Every id is unique within the set.
        Assert.Equal(a.Changes.Count, a.Changes.Select(c => c.Id).Distinct().Count());
    }

    [Fact]
    public void Build_disambiguates_duplicate_signatures_with_a_suffix()
    {
        // Two identical raw objects (same kind + body) collapse to the same signature; ids must still differ.
        var source = M("""
            COMMENT ON SCHEMA public IS 'x';
            COMMENT ON SCHEMA public IS 'x';
            """);
        var set = SchemaChangeSet.Build(source, new DatabaseModel());
        var ids = set.Changes.Select(c => c.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Find_returns_the_change_for_a_known_id_and_null_otherwise()
    {
        var set = SchemaChangeSet.Build(M("CREATE TABLE app.t (id int);"), new DatabaseModel());
        var first = set.Changes[0];
        Assert.Same(first, set.Find(first.Id));
        Assert.Null(set.Find("deadbeef"));
    }

    // ---- in-sync (empty diff) ------------------------------------------------------------------

    [Fact]
    public void Identical_models_are_in_sync_with_no_changes()
    {
        var sql = "CREATE TABLE app.t (id int PRIMARY KEY);";
        var set = SchemaChangeSet.Build(M(sql), M(sql));
        Assert.True(set.InSync);
        Assert.Empty(set.Changes);
        Assert.Equal(0, set.IncludedCount);
        Assert.Empty(set.SelectedIds);
    }

    // ---- direction (source→target vs target→source) -------------------------------------------

    [Fact]
    public void Direction_matters_create_one_way_drop_the_other()
    {
        var withTable = M("CREATE TABLE app.t (id int PRIMARY KEY);");
        var without = new DatabaseModel();
        var drops = new ComparerOptions { DropObjectsNotInSource = true };

        // source has the table, target doesn't → CREATE.
        var forward = SchemaChangeSet.Build(withTable, without, drops);
        Assert.Contains(forward.Changes, c => c.Kind == nameof(CreateTableChange));

        // swap: source lacks the table, target has it → DROP (with drops allowed).
        var reverse = SchemaChangeSet.Build(without, withTable, drops);
        Assert.Contains(reverse.Changes, c => c.Kind == nameof(DropTableChange));
        Assert.Contains(reverse.Changes, c => c.IsDestructive);
    }

    // ---- selection: include / exclude by id ----------------------------------------------------

    [Fact]
    public void IncludeAll_then_exclude_by_id_drops_only_that_change()
    {
        var set = SchemaChangeSet.Build(
            M("CREATE TABLE app.a (id int); CREATE TABLE app.b (id int);"), new DatabaseModel());

        var target = set.Changes.First(c => c.Kind == nameof(CreateTableChange));
        Assert.True(set.ExcludeById(target.Id));
        Assert.False(target.Included);
        Assert.DoesNotContain(target, set.Included);
        Assert.False(set.ExcludeById("nope"));   // unknown id is a no-op
    }

    [Fact]
    public void ExcludeAll_then_include_by_id_keeps_only_that_change()
    {
        var set = SchemaChangeSet.Build(
            M("CREATE TABLE app.a (id int); CREATE TABLE app.b (id int);"), new DatabaseModel());
        set.ExcludeAll();
        Assert.Equal(0, set.IncludedCount);

        var keep = set.Changes.First(c => c.Kind == nameof(CreateTableChange));
        Assert.True(set.IncludeById(keep.Id));
        var only = Assert.Single(set.Included);
        Assert.Equal(keep.Id, only.Id);
    }

    // ---- selection round-trip (SelectedIds <-> ApplySelection) --------------------------------

    [Fact]
    public void Selection_round_trips_through_SelectedIds_and_ApplySelection()
    {
        var build = M("CREATE TABLE app.a (id int); CREATE TABLE app.b (id int); CREATE SCHEMA app;");
        var first = SchemaChangeSet.Build(build, new DatabaseModel());

        // Pick an arbitrary subset.
        first.ExcludeAll();
        var picks = first.Changes.Take(2).ToList();
        foreach (var p in picks) p.Included = true;
        var snapshot = first.SelectedIds;
        Assert.Equal(2, snapshot.Count);

        // A fresh build (same models) + ApplySelection reproduces exactly that subset.
        var second = SchemaChangeSet.Build(build, new DatabaseModel());
        second.ApplySelection(snapshot);
        Assert.Equal(snapshot.OrderBy(x => x), second.SelectedIds.OrderBy(x => x));
    }

    [Fact]
    public void ApplySelection_ignores_ids_that_are_not_present()
    {
        var set = SchemaChangeSet.Build(M("CREATE TABLE app.t (id int);"), new DatabaseModel());
        var keep = set.Changes[0].Id;
        set.ApplySelection(new[] { keep, "ffffffff", "00000000" });
        var only = Assert.Single(set.Included);
        Assert.Equal(keep, only.Id);
    }

    // ---- object-type classification ------------------------------------------------------------

    [Theory]
    [InlineData("CREATE TABLE app.t (id int);", "table")]
    [InlineData("CREATE SCHEMA app;", "schema")]
    [InlineData("CREATE SEQUENCE app.s;", "sequence")]
    [InlineData("CREATE EXTENSION pgcrypto;", "extension")]
    public void ObjectType_is_classified_for_each_change(string sql, string expectedType)
    {
        var set = SchemaChangeSet.Build(M(sql), new DatabaseModel());
        Assert.Contains(set.Changes, c => c.ObjectType == expectedType);
    }

    [Fact]
    public void ObjectTypes_lists_distinct_sorted_types_present()
    {
        var set = SchemaChangeSet.Build(
            M("CREATE SCHEMA app; CREATE TABLE app.t (id int); CREATE INDEX ix ON app.t (id);"),
            new DatabaseModel());
        var types = set.ObjectTypes;
        Assert.Equal(types.OrderBy(t => t, StringComparer.Ordinal), types);   // sorted
        Assert.Equal(types.Distinct(), types);                                // distinct
        Assert.Contains("table", types);
        Assert.Contains("index", types);
    }

    // ---- object-type filters -------------------------------------------------------------------

    [Fact]
    public void Exclude_at_build_time_marks_those_object_types_excluded()
    {
        var set = SchemaChangeSet.Build(
            M("CREATE EXTENSION pgcrypto; CREATE TABLE app.t (id int);"),
            new DatabaseModel(),
            exclude: new[] { "extension" });

        Assert.DoesNotContain(set.Included, c => c.ObjectType == "extension");
        Assert.Contains(set.Included, c => c.ObjectType == "table");
        // Excluded changes are still in the set (just not included), so a UI can re-check them.
        Assert.Contains(set.Changes, c => c.ObjectType == "extension" && !c.Included);
    }

    [Fact]
    public void ExcludeObjectTypes_accepts_friendly_aliases()
    {
        var set = SchemaChangeSet.Build(
            M("CREATE EXTENSION pgcrypto; CREATE TABLE app.t (id int);"), new DatabaseModel());
        // plural alias "extensions" canonicalizes to "extension".
        set.ExcludeObjectTypes(new[] { "extensions" });
        Assert.DoesNotContain(set.Included, c => c.ObjectType == "extension");
    }

    [Fact]
    public void IncludeOnlyObjectTypes_restricts_to_exactly_those_types()
    {
        var set = SchemaChangeSet.Build(
            M("CREATE SCHEMA app; CREATE TABLE app.t (id int); CREATE INDEX ix ON app.t (id);"),
            new DatabaseModel());
        set.IncludeOnlyObjectTypes(new[] { "table" });
        Assert.All(set.Included, c => Assert.Equal("table", c.ObjectType));
        Assert.True(set.IncludedCount >= 1);
    }

    [Fact]
    public void Parse_rejects_empty_and_canonicalizes_unknown_as_is()
    {
        Assert.Throws<ArgumentException>(() => SchemaCompareObjectType.Parse("   "));
        Assert.Equal("primarykey", SchemaCompareObjectType.Parse("PK"));
        Assert.Equal("primarykey", SchemaCompareObjectType.Parse("pkey"));
        Assert.Equal("foreignkey", SchemaCompareObjectType.Parse("FK"));
        Assert.Equal("permission", SchemaCompareObjectType.Parse("permissions"));
        Assert.Equal("table", SchemaCompareObjectType.Parse("Tables"));
        Assert.Equal("widget", SchemaCompareObjectType.Parse("WIDGET"));   // unknown → lower-cased as-is
    }

    // ---- subset scripting ----------------------------------------------------------------------

    [Fact]
    public void ScriptIncluded_emits_only_the_included_subset()
    {
        var set = SchemaChangeSet.Build(
            M("CREATE TABLE app.keep (id int); CREATE TABLE app.skip (id int);"), new DatabaseModel());

        var skip = set.Changes.First(c => c.Description.Contains("skip"));
        set.ExcludeById(skip.Id);

        var script = set.ScriptIncluded(new DeployOptions { WrapInTransaction = false, IncludeHeader = false });
        Assert.Contains("app.keep", script);
        Assert.DoesNotContain("app.skip", script);
    }

    // ---- SchemaCompare.Of (pure two-model API) -------------------------------------------------

    [Fact]
    public void Of_produces_the_same_set_as_a_direct_build()
    {
        var s = M("CREATE TABLE app.t (id int);");
        var t = new DatabaseModel();
        var viaApi = SchemaCompare.Of(s, t);
        var viaBuild = SchemaChangeSet.Build(s, t);
        Assert.Equal(viaBuild.Changes.Select(c => c.Id), viaApi.Changes.Select(c => c.Id));
    }

    // ---- RunAsync: project ↔ project (no live DB) ---------------------------------------------

    [Fact]
    public async Task RunAsync_diffs_project_against_project()
    {
        using var src = new ProjectDir("Src", "CREATE TABLE public.t (id int PRIMARY KEY, name text NOT NULL);");
        using var tgt = new ProjectDir("Tgt", "CREATE TABLE public.t (id int PRIMARY KEY);");

        var result = await SchemaCompare.RunAsync(src.ProjectFile, tgt.ProjectFile);

        Assert.Equal(EndpointKind.Project, result.Source.Kind);
        Assert.Equal(EndpointKind.Project, result.Target.Kind);
        Assert.Equal("Src", result.Source.DisplayName);
        Assert.Equal("Tgt", result.Target.DisplayName);
        // Source adds `name` (NOT NULL) the target lacks.
        Assert.Contains(result.ChangeSet.Changes, c => c.Kind == nameof(AddColumnChange) && c.ObjectType == "column");
    }

    [Fact]
    public async Task RunAsync_in_sync_when_two_projects_match()
    {
        const string sql = "CREATE TABLE public.t (id int PRIMARY KEY);";
        using var a = new ProjectDir("A", sql);
        using var b = new ProjectDir("B", sql);

        var result = await SchemaCompare.RunAsync(a.ProjectFile, b.ProjectFile);
        Assert.True(result.ChangeSet.InSync);
    }

    // ---- RunAsync: package ↔ project (no live DB) --------------------------------------------

    [Fact]
    public async Task RunAsync_diffs_package_against_project()
    {
        // Build a .pgpkg from one project, diff it against a different project — both via EndpointResolver.
        using var pkgDir = new ProjectDir("Pkg", "CREATE TABLE public.t (id int PRIMARY KEY, name text);");
        var project = DatabaseProject.Load(pkgDir.ProjectFile);
        var build = await project.BuildAsync();
        var pkgPath = Path.Combine(pkgDir.Path, "Pkg.pgpkg");
        PgPkgBuilder.FromBuild(project, build.Model, build.Files, "0.0.0-test", "2026-01-01T00:00:00Z").Write(pkgPath);

        using var tgt = new ProjectDir("Tgt", "CREATE TABLE public.t (id int PRIMARY KEY);");

        var result = await SchemaCompare.RunAsync(pkgPath, tgt.ProjectFile);

        Assert.Equal(EndpointKind.Package, result.Source.Kind);
        Assert.Equal("Pkg", result.Source.DisplayName);
        Assert.Equal(EndpointKind.Project, result.Target.Kind);
        Assert.Contains(result.ChangeSet.Changes, c => c.Kind == nameof(AddColumnChange));
    }

    [Fact]
    public async Task RunAsync_honours_exclude_object_types()
    {
        using var src = new ProjectDir("Src",
            "CREATE EXTENSION IF NOT EXISTS pgcrypto; CREATE TABLE public.t (id int);");
        using var tgt = new ProjectDir("Tgt", "-- empty\nCREATE SCHEMA placeholder;");

        var result = await SchemaCompare.RunAsync(src.ProjectFile, tgt.ProjectFile,
            excludeObjectTypes: new[] { "extension" });

        Assert.DoesNotContain(result.ChangeSet.Included, c => c.ObjectType == "extension");
        Assert.Contains(result.ChangeSet.Included, c => c.ObjectType == "table");
    }

    // ---- diff.json report shape ----------------------------------------------------------------

    [Fact]
    public async Task Report_json_is_camelCase_with_a_stable_shape()
    {
        using var src = new ProjectDir("Src", "CREATE TABLE public.t (id int PRIMARY KEY, name text NOT NULL);");
        using var tgt = new ProjectDir("Tgt", "CREATE TABLE public.t (id int PRIMARY KEY);");

        var result = await SchemaCompare.RunAsync(src.ProjectFile, tgt.ProjectFile);
        var json = SchemaCompareReport.Serialize(SchemaCompareReport.Build(result));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("1.0", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("compare", root.GetProperty("verb").GetString());
        Assert.Equal("project", root.GetProperty("source").GetProperty("kind").GetString());
        Assert.Equal("Src", root.GetProperty("source").GetProperty("displayName").GetString());
        Assert.Equal("project", root.GetProperty("target").GetProperty("kind").GetString());
        Assert.False(root.GetProperty("inSync").GetBoolean());
        Assert.True(root.GetProperty("changeCount").GetInt32() >= 1);

        var change = root.GetProperty("changes")[0];
        // camelCase field names present.
        foreach (var field in new[] { "id", "kind", "objectType", "description", "included", "destructive", "phase", "sql" })
            Assert.True(change.TryGetProperty(field, out _), $"missing field '{field}'");
        Assert.True(change.GetProperty("included").GetBoolean());
    }

    [Fact]
    public async Task Report_json_changes_are_ordered_by_phase()
    {
        using var src = new ProjectDir("Src",
            "CREATE SCHEMA app; CREATE TABLE app.t (id int PRIMARY KEY); CREATE INDEX ix ON app.t (id);");
        using var tgt = new ProjectDir("Tgt", "CREATE SCHEMA placeholder;");

        var result = await SchemaCompare.RunAsync(src.ProjectFile, tgt.ProjectFile);
        var json = SchemaCompareReport.Serialize(SchemaCompareReport.Build(result));

        using var doc = JsonDocument.Parse(json);
        var phases = doc.RootElement.GetProperty("changes").EnumerateArray()
            .Select(c => c.GetProperty("phase").GetInt32()).ToList();
        Assert.True(phases.Count >= 2);
        Assert.Equal(phases.OrderBy(p => p), phases);   // deterministic, phase-ordered
    }

    [Fact]
    public async Task Report_json_reflects_excluded_changes_in_included_flags_and_counts()
    {
        using var src = new ProjectDir("Src",
            "CREATE EXTENSION IF NOT EXISTS pgcrypto; CREATE TABLE public.t (id int);");
        using var tgt = new ProjectDir("Tgt", "CREATE SCHEMA placeholder;");

        var result = await SchemaCompare.RunAsync(src.ProjectFile, tgt.ProjectFile,
            excludeObjectTypes: new[] { "extension" });
        var report = SchemaCompareReport.Build(result);

        Assert.True(report.IncludedCount < report.ChangeCount);
        var extChange = report.Changes.Single(c => c.ObjectType == "extension");
        Assert.False(extChange.Included);
    }

    [Fact]
    public async Task Report_in_sync_serializes_empty_changes()
    {
        const string sql = "CREATE TABLE public.t (id int PRIMARY KEY);";
        using var a = new ProjectDir("A", sql);
        using var b = new ProjectDir("B", sql);

        var result = await SchemaCompare.RunAsync(a.ProjectFile, b.ProjectFile);
        var json = SchemaCompareReport.Serialize(SchemaCompareReport.Build(result));

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("inSync").GetBoolean());
        Assert.Equal(0, doc.RootElement.GetProperty("changeCount").GetInt32());
        Assert.Empty(doc.RootElement.GetProperty("changes").EnumerateArray());
    }

    // ---- live-DB endpoints (gated) -------------------------------------------------------------

    [DbFact]
    public async Task RunAsync_diffs_project_against_live_database()
    {
        var conn = Environment.GetEnvironmentVariable("PGPROJ_TEST_CONNECTION")!;
        using var src = new ProjectDir("Live", "CREATE TABLE public.sc_probe (id int PRIMARY KEY);");

        // project (source) vs live DB (target) — the live target is resolved via EndpointResolver too.
        var result = await SchemaCompare.RunAsync(src.ProjectFile, conn);
        Assert.Equal(EndpointKind.Project, result.Source.Kind);
        Assert.Equal(EndpointKind.LiveDatabase, result.Target.Kind);
        Assert.NotNull(result.ChangeSet);
    }

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>A throwaway on-disk .pgproj with one .sql file, for resolver-backed compares.</summary>
    private sealed class ProjectDir : IDisposable
    {
        public string Path { get; }
        public string ProjectFile { get; }

        public ProjectDir(string name, string sql)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pgproj_sc_{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            ProjectFile = System.IO.Path.Combine(Path, $"{name}.pgproj");
            File.WriteAllText(ProjectFile,
                $"""
                <Project Sdk="PgProj.Sdk/0.1.0">
                  <PropertyGroup><Name>{name}</Name><DefaultSchema>public</DefaultSchema></PropertyGroup>
                  <ItemGroup><Build Include="**/*.sql" /></ItemGroup>
                </Project>
                """);
            File.WriteAllText(System.IO.Path.Combine(Path, "objects.sql"), sql);
        }

        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ } }
    }
}
