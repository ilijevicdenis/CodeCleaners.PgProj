using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using PgProj.Core.Analysis;
using PgProj.Core.Contracts;
using PgProj.Core.Model;
using PgProj.Core.Project;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Conformance tests for the EP-RPC JSON contract (issue #17). These are the editor-backend guard rails:
/// every payload must carry a <c>schemaVersion</c>; diagnostics must map to <c>file:line:col</c>; the
/// model-tree must enumerate every object kind with positions; and the JSON shape must not drift
/// unintentionally (golden field-set assertions). No database required.
/// </summary>
public class JsonContractTests
{
    private static DatabaseProject SampleProject() => DatabaseProject.Load(FindSampleProject());

    // ---- schemaVersion on every payload ---------------------------------------------------------

    [Fact]
    public async Task Build_payload_has_schema_version()
    {
        var report = await ContractBuilder.BuildAsync(SampleProject());
        Assert.Equal(JsonContract.SchemaVersion, report.SchemaVersion);
        Assert.True(report.Success);
        var root = JsonDocument.Parse(JsonContract.Serialize(report)).RootElement;
        Assert.Equal("1.0", root.GetProperty("schemaVersion").GetString());
    }

    [Fact]
    public void Analyze_payload_has_schema_version()
    {
        var report = ContractBuilder.Analyze(SampleProject(), strict: false);
        Assert.Equal(JsonContract.SchemaVersion, report.SchemaVersion);
        Assert.Equal(PgAnalyzer.RuleCount + ModelAnalyzer.RuleCount, report.RuleCount);
    }

    [Fact]
    public async Task Compare_and_publish_payloads_have_schema_version()
    {
        var project = SampleProject();
        var source = (await project.BuildAsync()).Model;
        // Compare against an empty target — equivalent to a greenfield create plan.
        var compare = ContractBuilder.Compare(source, new DatabaseModel(), project.Name, allowDrops: false);
        var publish = ContractBuilder.PublishPlan(source, new DatabaseModel(), project.Name, allowDrops: false, wrapInTransaction: true);

        Assert.Equal(JsonContract.SchemaVersion, compare.SchemaVersion);
        Assert.Equal(JsonContract.SchemaVersion, publish.SchemaVersion);
        Assert.False(compare.InSync);
        Assert.True(publish.ChangeCount > 0);
        Assert.Contains("CREATE", publish.Script, StringComparison.OrdinalIgnoreCase);
    }

    // ---- diagnostic mapping: known-bad SQL → file:line:col --------------------------------------

    [Fact]
    public void BuildDiagnostic_maps_file_line_col_from_parser_string()
    {
        // The exact shape DatabaseProject.Build emits: "rel/file.sql: line:col: message".
        var dto = ContractMappers.ToBuildDto("Tables/bad.sql: 12:5: unexpected token");
        Assert.Equal("BUILD", dto.RuleId);
        Assert.Equal(ContractSeverity.Error, dto.Severity);
        Assert.Equal("Tables/bad.sql", dto.File);
        Assert.Equal(12, dto.Line);
        Assert.Equal(5, dto.Col);
        Assert.Equal("unexpected token", dto.Message);
    }

    [Fact]
    public void BuildDiagnostic_without_position_still_carries_file()
    {
        var dto = ContractMappers.ToBuildDto("Tables/dup.sql: Duplicate table definition: afd.x (defined 2 times).");
        Assert.Equal("Tables/dup.sql", dto.File);
        Assert.Equal(0, dto.Line);
        Assert.Contains("Duplicate table", dto.Message);
    }

    [Fact]
    public async Task BadProject_build_surfaces_diagnostic_with_position()
    {
        using var tmp = new TempProject(
            ("Tables/broken.sql", "CREATE TABLE afd.broken (id int,;"));
        var report = await ContractBuilder.BuildAsync(tmp.Project, includeTree: false);

        Assert.False(report.Success);
        Assert.NotEmpty(report.Diagnostics);
        var d = report.Diagnostics[0];
        Assert.Equal("BUILD", d.RuleId);
        Assert.Equal(ContractSeverity.Error, d.Severity);
        Assert.Equal("Tables/broken.sql", d.File);
        Assert.True(d.Line >= 1, "a parser diagnostic must carry a 1-based line");
    }

    [Fact]
    public void AnalyzerDiagnostic_maps_to_dto_with_resolved_position()
    {
        using var tmp = new TempProject(
            ("Funcs/f.sql", "CREATE FUNCTION afd.f() RETURNS int LANGUAGE sql STABLE SECURITY DEFINER AS $$ SELECT 1 $$;"));
        var report = ContractBuilder.Analyze(tmp.Project, strict: false);

        var pg001 = report.Diagnostics.Single(x => x.RuleId == "PG001");
        Assert.Equal(ContractSeverity.Warning, pg001.Severity);
        Assert.Equal("afd.f", pg001.Target);
        Assert.Equal("Funcs/f.sql", pg001.File);
        Assert.True(pg001.Line >= 1);
    }

    // ---- model-tree: enumerates every object kind with positions --------------------------------

    [Fact]
    public async Task ModelTree_enumerates_all_object_kinds_with_positions()
    {
        var tree = await ContractBuilder.ModelTreeAsync(SampleProject());

        // Node count must equal the model's total object count (schemas + tables + indexes + views +
        // sequences + functions + raw objects) — proves every object is enumerated.
        var s = tree.Summary;
        var expected = s.Schemas + s.Tables + s.Indexes + s.Views + s.Sequences + s.Functions + s.Objects;
        Assert.Equal(expected, tree.Nodes.Count);

        // Spot-check the finely-modelled kinds and a few raw kinds are present.
        foreach (var kind in new[] { "schema", "table", "view", "sequence", "function", "index" })
            Assert.Contains(tree.Nodes, n => n.Kind == kind);
        foreach (var raw in new[] { "type", "domain", "trigger", "policy" })
            Assert.Contains(tree.Nodes, n => n.Kind == raw);

        // A table node exposes its columns as children.
        var table = tree.Nodes.First(n => n.Kind == "table");
        Assert.NotEmpty(table.Children);
        Assert.All(table.Children, c => Assert.Equal("column", c.Kind));

        // Finely-modelled objects resolve a source position (file + 1-based line).
        var view = tree.Nodes.First(n => n.Kind == "view");
        Assert.False(string.IsNullOrEmpty(view.File));
        Assert.True(view.Line >= 1);
    }

    // ---- golden field-set snapshot: fail on unintended schema drift -----------------------------

    [Fact]
    public async Task Build_json_field_set_is_stable()
    {
        var report = await ContractBuilder.BuildAsync(SampleProject());
        var root = JsonDocument.Parse(JsonContract.Serialize(report)).RootElement;

        AssertFields(root, "schemaVersion", "verb", "project", "success", "fileCount", "model", "summary", "diagnostics", "modelTree");
        AssertFields(root.GetProperty("model"), "schemas", "tables", "indexes", "views", "sequences", "functions", "objects");
        AssertFields(root.GetProperty("summary"), "errors", "warnings", "infos", "total");
    }

    [Fact]
    public void Compare_change_field_set_is_stable()
    {
        var project = SampleProject();
        var source = project.Build().Model;
        var report = ContractBuilder.Compare(source, new DatabaseModel(), project.Name, allowDrops: false);
        var root = JsonDocument.Parse(JsonContract.Serialize(report)).RootElement;

        AssertFields(root, "schemaVersion", "verb", "project", "inSync", "changeCount", "destructiveCount", "changes");
        var change = root.GetProperty("changes")[0];
        AssertFields(change, "kind", "description", "destructive", "phase");
    }

    [Fact]
    public async Task ModelTree_node_field_set_is_stable()
    {
        var tree = await ContractBuilder.ModelTreeAsync(SampleProject());
        var root = JsonDocument.Parse(JsonContract.Serialize(tree)).RootElement;
        AssertFields(root, "schemaVersion", "verb", "project", "summary", "nodes");
        var node = root.GetProperty("nodes")[0];
        // file is omit-null, so it may be absent; assert the always-present fields.
        var names = node.EnumerateObject().Select(p => p.Name).ToHashSet();
        foreach (var f in new[] { "kind", "schema", "name", "qualifiedName", "line", "col", "children" })
            Assert.Contains(f, names);
    }

    // ---- camelCase + string-enum wire format ----------------------------------------------------

    [Fact]
    public void Severity_serializes_as_string_not_int()
    {
        var dto = new DiagnosticDto { RuleId = "X", Severity = ContractSeverity.Warning, Message = "m" };
        var json = JsonContract.Serialize(dto);
        Assert.Contains("\"severity\": \"Warning\"", json);
        Assert.DoesNotContain("\"severity\": 1", json);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static void AssertFields(JsonElement obj, params string[] expected)
    {
        var actual = obj.EnumerateObject().Select(p => p.Name).ToHashSet();
        // omit-null may drop optional fields (modelTree, file); assert expected ⊆ actual for required,
        // and no unexpected extra fields beyond the declared set.
        foreach (var f in expected)
            if (f is not ("modelTree" or "file"))
                Assert.True(actual.Contains(f), $"missing field '{f}' in {obj}");
        Assert.True(actual.IsSubsetOf(expected.ToHashSet()),
            $"unexpected field(s): {string.Join(",", actual.Except(expected))}");
    }

    internal static string FindSampleProject()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
        {
            var candidate = Path.Combine(dir, "sample", "AllFeaturesDb", "AllFeaturesDb.pgproj");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Could not locate sample/AllFeaturesDb from " + AppContext.BaseDirectory);
    }

    /// <summary>A throwaway on-disk project with a default schema of <c>afd</c>, cleaned up on dispose.</summary>
    private sealed class TempProject : IDisposable
    {
        private readonly string _dir;
        public DatabaseProject Project { get; }

        public TempProject(params (string Rel, string Sql)[] files)
        {
            _dir = Path.Combine(Path.GetTempPath(), "pgproj_json_" + Guid.NewGuid().ToString("N")[..10]);
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, "Temp.pgproj"),
                "<Project Sdk=\"PgProj.Sdk/0.1.0\"><PropertyGroup><Name>Temp</Name><DefaultSchema>afd</DefaultSchema></PropertyGroup>" +
                "<ItemGroup><Build Include=\"**/*.sql\" /></ItemGroup></Project>");
            foreach (var (rel, sql) in files)
            {
                var path = Path.Combine(_dir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, sql);
            }
            Project = DatabaseProject.Load(Path.Combine(_dir, "Temp.pgproj"));
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
