using System.Collections.Generic;
using System.IO;
using System.Linq;
using PgProj.Core.Analysis;
using PgProj.Core.Syntax;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// External analyzer rule packs (EP-ANALYSIS+ #79): discovery from assemblies/paths, config-honouring
/// execution, sidecar `rulePacks` parsing, and end-to-end resolution via <see cref="AnalysisSetup"/>.
/// <see cref="SampleTableRule"/> below is the rule pack — discovered in this very test assembly.
/// </summary>
public sealed class RulePackTests
{
    private const string Sql = "CREATE TABLE app.thing (id int primary key);";

    private static ParseResult Parse(string sql) => new PgParser().Parse(sql);

    [Fact]
    public void FromAssemblies_discovers_public_parameterless_rules()
    {
        var rules = RulePackLoader.FromAssemblies(new[] { typeof(RulePackTests).Assembly });
        Assert.Contains(rules, r => r.Id == "ORG001");
        Assert.DoesNotContain(rules, r => r.Id == "ORG_NOCTOR");   // no parameterless ctor → skipped
        Assert.DoesNotContain(rules, r => r.Id == "ORG_ABSTRACT"); // abstract → skipped
    }

    [Fact]
    public void FromPaths_loads_the_dll_and_shares_the_contract_type()
    {
        var dll = typeof(RulePackTests).Assembly.Location;
        var rules = RulePackLoader.FromPaths(new[] { dll });   // loads a 2nd copy in an isolated context
        Assert.Contains(rules, r => r.Id == "ORG001");
    }

    [Fact]
    public void FromPaths_throws_for_a_missing_pack()
    {
        var ex = Assert.Throws<RulePackException>(() => RulePackLoader.FromPaths(new[] { "does-not-exist.dll" }));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public void Run_emits_findings_for_external_rules()
    {
        var rules = RulePackLoader.FromAssemblies(new[] { typeof(RulePackTests).Assembly });
        var diags = ExternalRules.Run(rules, Parse(Sql), AnalysisConfig.Empty);
        Assert.Contains(diags, d => d.RuleId == "ORG001" && d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void Run_honours_disable_and_severity_override_by_id()
    {
        var rules = RulePackLoader.FromAssemblies(new[] { typeof(RulePackTests).Assembly });

        var off = AnalysisConfig.FromOverrides(new Dictionary<string, RuleOverride> { ["ORG001"] = new(Enabled: false) });
        Assert.DoesNotContain(ExternalRules.Run(rules, Parse(Sql), off), d => d.RuleId == "ORG001");

        var error = AnalysisConfig.FromOverrides(new Dictionary<string, RuleOverride> { ["ORG001"] = new(Severity: DiagnosticSeverity.Error) });
        Assert.Contains(ExternalRules.Run(rules, Parse(Sql), error), d => d.RuleId == "ORG001" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Sidecar_parses_rulePacks_and_accepts_external_ids_when_known()
    {
        const string json = """
            { "rulePacks": ["./packs/Org.Rules.dll"],
              "rules": { "ORG001": { "severity": "error" }, "UNKNOWNX": { "enabled": false } } }
            """;

        // Without the external id being known, ORG001 is dropped like any unknown id.
        Assert.False(AnalysisConfig.Parse(json).Overrides.ContainsKey("ORG001"));

        var known = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "ORG001" };
        var cfg = AnalysisConfig.Parse(json, known);
        Assert.Single(cfg.RulePackPaths);
        Assert.Equal("./packs/Org.Rules.dll", cfg.RulePackPaths[0]);
        Assert.True(cfg.Overrides.ContainsKey("ORG001"));      // external id retained
        Assert.False(cfg.Overrides.ContainsKey("UNKNOWNX"));   // genuinely-unknown id still dropped
    }

    [Fact]
    public void Resolve_loads_pack_from_sidecar_and_disables_via_rules_block()
    {
        var dll = typeof(RulePackTests).Assembly.Location.Replace("\\", "\\\\");
        var dir = Path.Combine(Path.GetTempPath(), "pgproj_rulepack_" + System.Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        try
        {
            var projPath = Path.Combine(dir, "Sample.pgproj");
            File.WriteAllText(projPath, "<Project></Project>");
            var sidecar = Path.Combine(dir, ".pgproj.analysis.json");

            // Pack loaded, rule fires.
            File.WriteAllText(sidecar, $"{{ \"rulePacks\": [\"{dll}\"] }}");
            var (config, rules) = AnalysisSetup.Resolve(projPath);
            Assert.Contains(rules, r => r.Id == "ORG001");
            Assert.Contains(ExternalRules.Run(rules, Parse(Sql), config), d => d.RuleId == "ORG001");

            // Same pack, but disabled via the rules block (external id is known because the pack is loaded).
            File.WriteAllText(sidecar, $"{{ \"rulePacks\": [\"{dll}\"], \"rules\": {{ \"ORG001\": {{ \"enabled\": false }} }} }}");
            var (config2, rules2) = AnalysisSetup.Resolve(projPath);
            Assert.DoesNotContain(ExternalRules.Run(rules2, Parse(Sql), config2), d => d.RuleId == "ORG001");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}

// ---- the rule pack under test (discovered in this assembly) ----------------------------------

/// <summary>A sample external rule: flags every CREATE TABLE. Public + parameterless → discoverable.</summary>
public sealed class SampleTableRule : IPgRule
{
    public string Id => "ORG001";
    public DiagnosticSeverity DefaultSeverity => DiagnosticSeverity.Warning;
    public string Title => "Test rule: flags every CREATE TABLE";
    public IEnumerable<Diagnostic> Analyze(ParseResult result)
    {
        foreach (var s in result.Statements)
            if (s is CreateTableStatement)
                yield return new Diagnostic(Id, DefaultSeverity, "external rule fired on a CREATE TABLE", "table");
    }
}

/// <summary>Not discoverable: no public parameterless constructor.</summary>
public sealed class NoCtorRule : IPgRule
{
    public NoCtorRule(int _) { }
    public string Id => "ORG_NOCTOR";
    public DiagnosticSeverity DefaultSeverity => DiagnosticSeverity.Info;
    public string Title => "not constructible";
    public IEnumerable<Diagnostic> Analyze(ParseResult result) => System.Array.Empty<Diagnostic>();
}

/// <summary>Not discoverable: abstract.</summary>
public abstract class AbstractRule : IPgRule
{
    public string Id => "ORG_ABSTRACT";
    public DiagnosticSeverity DefaultSeverity => DiagnosticSeverity.Info;
    public string Title => "abstract";
    public abstract IEnumerable<Diagnostic> Analyze(ParseResult result);
}
