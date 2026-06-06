using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PgProj.Core.Analysis;
using PgProj.Core.Contracts;
using PgProj.Core.Project;
using PgProj.Core.Syntax;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// EP-ANALYSIS+: <c>analyze --format sarif</c> emits a structurally valid SARIF 2.1.0 document — schema +
/// version fields, a tool driver advertising every rule, and <c>results[]</c> carrying ruleId, mapped
/// level, message, and (where resolvable) a file:line physical location. Output must be deterministic.
/// </summary>
public sealed class SarifWriterTests
{
    private static IReadOnlyList<Diagnostic> Analyze(string sql)
        => new PgAnalyzer().Analyze(new PgParser().Parse(sql));

    private static JsonElement Root(string sarif) => JsonDocument.Parse(sarif).RootElement;

    private const string DirtySql =
        "CREATE FUNCTION s.f() RETURNS int LANGUAGE sql SECURITY DEFINER AS $$ SELECT 1 $$;";

    // ---- top-level document shape --------------------------------------------------------------

    [Fact]
    public void Emits_schema_and_version_fields()
    {
        var sarif = new SarifWriter().Write(Analyze(DirtySql), positions: null);
        var root = Root(sarif);
        Assert.Equal(SarifWriter.SchemaUri, root.GetProperty("$schema").GetString());
        Assert.Equal("2.1.0", root.GetProperty("version").GetString());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("runs").ValueKind);
        Assert.Single(root.GetProperty("runs").EnumerateArray());
    }

    [Fact]
    public void Tool_driver_names_pgproj_and_lists_every_rule()
    {
        var sarif = new SarifWriter().Write(Analyze(DirtySql), positions: null);
        var driver = Root(sarif).GetProperty("runs")[0].GetProperty("tool").GetProperty("driver");
        Assert.Equal("pgproj", driver.GetProperty("name").GetString());

        var ruleIds = driver.GetProperty("rules").EnumerateArray().Select(r => r.GetProperty("id").GetString()).ToList();
        Assert.Equal(PgAnalyzer.RuleCount, ruleIds.Count);
        foreach (var id in PgAnalyzer.RuleIds)
            Assert.Contains(id, ruleIds);
    }

    [Fact]
    public void Rule_descriptors_carry_short_description_and_default_level()
    {
        var sarif = new SarifWriter().Write(Array.Empty<Diagnostic>(), positions: null);
        var rules = Root(sarif).GetProperty("runs")[0].GetProperty("tool").GetProperty("driver").GetProperty("rules");
        var pg001 = rules.EnumerateArray().Single(r => r.GetProperty("id").GetString() == "PG001");
        Assert.False(string.IsNullOrWhiteSpace(pg001.GetProperty("shortDescription").GetProperty("text").GetString()));
        Assert.Equal("warning", pg001.GetProperty("defaultConfiguration").GetProperty("level").GetString());
        var pg005 = rules.EnumerateArray().Single(r => r.GetProperty("id").GetString() == "PG005");
        Assert.Equal("note", pg005.GetProperty("defaultConfiguration").GetProperty("level").GetString());
    }

    // ---- results -------------------------------------------------------------------------------

    [Fact]
    public void Results_carry_ruleid_level_and_message()
    {
        var findings = Analyze(DirtySql);
        var sarif = new SarifWriter().Write(findings, positions: null);
        var results = Root(sarif).GetProperty("runs")[0].GetProperty("results");

        Assert.Equal(findings.Count, results.GetArrayLength());
        var pg001 = results.EnumerateArray().Single(r => r.GetProperty("ruleId").GetString() == "PG001");
        Assert.Equal("warning", pg001.GetProperty("level").GetString());
        Assert.False(string.IsNullOrWhiteSpace(pg001.GetProperty("message").GetProperty("text").GetString()));
    }

    [Fact]
    public void Empty_findings_produce_zero_results_but_a_valid_document()
    {
        var sarif = new SarifWriter().Write(Array.Empty<Diagnostic>(), positions: null);
        var run = Root(sarif).GetProperty("runs")[0];
        Assert.Empty(run.GetProperty("results").EnumerateArray());
        Assert.NotEmpty(run.GetProperty("tool").GetProperty("driver").GetProperty("rules").EnumerateArray());
    }

    // ---- severity → SARIF level mapping --------------------------------------------------------

    [Theory]
    [InlineData(DiagnosticSeverity.Info, "note")]
    [InlineData(DiagnosticSeverity.Warning, "warning")]
    [InlineData(DiagnosticSeverity.Error, "error")]
    public void Severity_maps_to_sarif_level(DiagnosticSeverity severity, string expected)
    {
        Assert.Equal(expected, SarifWriter.ToLevel(severity));
        var diag = new Diagnostic("PG003", severity, "msg", "s.t");
        var sarif = new SarifWriter().Write(new[] { diag }, positions: null);
        var result = Root(sarif).GetProperty("runs")[0].GetProperty("results")[0];
        Assert.Equal(expected, result.GetProperty("level").GetString());
    }

    [Fact]
    public void Severity_override_flows_into_sarif_level()
    {
        // PG005 default is note; override to error via config → SARIF level must be error.
        var config = AnalysisConfig.FromOverrides(new Dictionary<string, RuleOverride>
        {
            ["PG005"] = new RuleOverride(Severity: DiagnosticSeverity.Error),
        });
        var findings = new PgAnalyzer(config).Analyze(new PgParser().Parse(DirtySql));
        var sarif = new SarifWriter().Write(findings, positions: null);
        var pg005 = Root(sarif).GetProperty("runs")[0].GetProperty("results")
            .EnumerateArray().Single(r => r.GetProperty("ruleId").GetString() == "PG005");
        Assert.Equal("error", pg005.GetProperty("level").GetString());
    }

    // ---- physical locations (file:line) from the position index --------------------------------

    [Fact]
    public void Results_carry_file_line_region_when_positions_resolve()
    {
        var dir = Directory.CreateTempSubdirectory("pgproj-sarif-").FullName;
        try
        {
            var proj = Path.Combine(dir, "db.pgproj");
            File.WriteAllText(proj, "<Project></Project>");
            // Put the function on line 2 so a startLine > 1 is meaningful.
            File.WriteAllText(Path.Combine(dir, "f.sql"),
                "-- header\nCREATE FUNCTION s.f() RETURNS int LANGUAGE sql SECURITY DEFINER AS $$ SELECT 1 $$;\n");

            var project = DatabaseProject.Load(proj);
            var positions = SourcePositionIndex.Build(project);
            var findings = new List<Diagnostic>();
            foreach (var file in project.ResolveSqlFiles())
                findings.AddRange(Analyze(File.ReadAllText(file)));

            var sarif = new SarifWriter().Write(findings, positions);
            var pg001 = Root(sarif).GetProperty("runs")[0].GetProperty("results")
                .EnumerateArray().Single(r => r.GetProperty("ruleId").GetString() == "PG001");

            var loc = pg001.GetProperty("locations")[0].GetProperty("physicalLocation");
            Assert.Equal("f.sql", loc.GetProperty("artifactLocation").GetProperty("uri").GetString());

            // The region's startLine must match exactly what the position index resolved for this object.
            var expected = positions.Find("function:s.f()")!.Value;
            Assert.Equal(expected.Line, loc.GetProperty("region").GetProperty("startLine").GetInt32());
            Assert.True(loc.GetProperty("region").GetProperty("startLine").GetInt32() >= 1);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Result_has_no_locations_when_position_unresolved()
    {
        // A finding whose target the index can't resolve → no locations array (SARIF allows a locationless result).
        var diag = new Diagnostic("PG009", DiagnosticSeverity.Info, "msg", "query");
        var sarif = new SarifWriter().Write(new[] { diag }, positions: null);
        var result = Root(sarif).GetProperty("runs")[0].GetProperty("results")[0];
        Assert.False(result.TryGetProperty("locations", out _));
    }

    // ---- determinism ---------------------------------------------------------------------------

    [Fact]
    public void Output_is_deterministic()
    {
        var findings = Analyze(DirtySql);
        var a = new SarifWriter().Write(findings, positions: null, toolVersion: "1.2.3");
        var b = new SarifWriter().Write(findings, positions: null, toolVersion: "1.2.3");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Results_preserve_input_order()
    {
        var findings = new[]
        {
            new Diagnostic("PG003", DiagnosticSeverity.Warning, "a", "s.t1"),
            new Diagnostic("PG009", DiagnosticSeverity.Info, "b", "query"),
            new Diagnostic("PG001", DiagnosticSeverity.Warning, "c", "s.f"),
        };
        var sarif = new SarifWriter().Write(findings, positions: null);
        var ids = Root(sarif).GetProperty("runs")[0].GetProperty("results")
            .EnumerateArray().Select(r => r.GetProperty("ruleId").GetString()).ToList();
        Assert.Equal(new[] { "PG003", "PG009", "PG001" }, ids);
    }
}
