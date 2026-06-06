using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PgProj.Core.Analysis;
using PgProj.Core.Syntax;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// EP-ANALYSIS+: per-rule configuration (enable/disable + severity override) honoured by the analyzer,
/// layered CLI &gt; sidecar config &gt; rule default. Reuses the PgAnalyzerTests fixtures (raw SQL → diags).
/// </summary>
public sealed class AnalysisConfigTests
{
    private static IReadOnlyList<Diagnostic> Analyze(string sql, AnalysisConfig config)
        => new PgAnalyzer(config).Analyze(new PgParser().Parse(sql));

    // A SECURITY DEFINER function with no search_path and no volatility → PG001 (warning) + PG005 (info).
    private const string SecDefSql =
        "CREATE FUNCTION s.f() RETURNS int LANGUAGE sql SECURITY DEFINER AS $$ SELECT 1 $$;";
    private const string UnguardedUpdate = "UPDATE s.t SET a = 1";

    // ---- rule metadata -------------------------------------------------------------------------

    [Fact]
    public void Known_rules_match_rule_count_and_are_recognized()
    {
        Assert.Equal(PgAnalyzer.RuleCount, PgAnalyzer.RuleDefaults.Count);
        foreach (var id in PgAnalyzer.RuleIds)
            Assert.True(PgAnalyzer.IsKnownRule(id));
        Assert.True(PgAnalyzer.IsKnownRule("pg003")); // case-insensitive
        Assert.False(PgAnalyzer.IsKnownRule("PG999"));
    }

    [Fact]
    public void Default_severities_match_baked_in_rule_info()
    {
        Assert.Equal(DiagnosticSeverity.Warning, PgAnalyzer.DefaultSeverityOf("PG001"));
        Assert.Equal(DiagnosticSeverity.Info, PgAnalyzer.DefaultSeverityOf("PG005"));
        Assert.Equal(DiagnosticSeverity.Warning, PgAnalyzer.DefaultSeverityOf("PG003"));
    }

    // ---- empty config == legacy behaviour ------------------------------------------------------

    [Fact]
    public void Empty_config_matches_no_arg_analyzer()
    {
        var withEmpty = Analyze(SecDefSql, AnalysisConfig.Empty);
        var noArg = new PgAnalyzer().Analyze(new PgParser().Parse(SecDefSql));
        Assert.Equal(noArg.Select(d => (d.RuleId, d.Severity)), withEmpty.Select(d => (d.RuleId, d.Severity)));
        Assert.Contains(withEmpty, d => d.RuleId == "PG001" && d.Severity == DiagnosticSeverity.Warning);
        Assert.Contains(withEmpty, d => d.RuleId == "PG005" && d.Severity == DiagnosticSeverity.Info);
    }

    // ---- disable a rule ------------------------------------------------------------------------

    [Fact]
    public void Disabled_rule_produces_no_finding()
    {
        var config = AnalysisConfig.FromOverrides(new Dictionary<string, RuleOverride>
        {
            ["PG001"] = new RuleOverride(Enabled: false),
        });
        var diags = Analyze(SecDefSql, config);
        Assert.DoesNotContain(diags, d => d.RuleId == "PG001");
        Assert.Contains(diags, d => d.RuleId == "PG005"); // unaffected rule still fires
    }

    [Fact]
    public void Disabling_via_cli_off_suppresses_finding()
    {
        var config = AnalysisConfig.Empty.WithCliOverrides(new Dictionary<string, string> { ["PG003"] = "off" });
        Assert.DoesNotContain(Analyze(UnguardedUpdate, config), d => d.RuleId == "PG003");
    }

    [Theory]
    [InlineData("off")]
    [InlineData("false")]
    [InlineData("disabled")]
    [InlineData("no")]
    public void Disable_synonyms_all_suppress(string token)
    {
        var config = AnalysisConfig.Empty.WithCliOverrides(new Dictionary<string, string> { ["PG003"] = token });
        Assert.DoesNotContain(Analyze(UnguardedUpdate, config), d => d.RuleId == "PG003");
    }

    // ---- severity override ---------------------------------------------------------------------

    [Fact]
    public void Severity_override_from_config_changes_level()
    {
        var config = AnalysisConfig.FromOverrides(new Dictionary<string, RuleOverride>
        {
            ["PG005"] = new RuleOverride(Severity: DiagnosticSeverity.Error),
        });
        var diags = Analyze(SecDefSql, config);
        Assert.Contains(diags, d => d.RuleId == "PG005" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Severity_override_from_cli_implies_enabled_and_sets_level()
    {
        var config = AnalysisConfig.Empty.WithCliOverrides(new Dictionary<string, string> { ["PG005"] = "error" });
        var diags = Analyze(SecDefSql, config);
        Assert.Contains(diags, d => d.RuleId == "PG005" && d.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("info", DiagnosticSeverity.Info)]
    [InlineData("warning", DiagnosticSeverity.Warning)]
    [InlineData("warn", DiagnosticSeverity.Warning)]
    [InlineData("error", DiagnosticSeverity.Error)]
    [InlineData("note", DiagnosticSeverity.Info)]
    public void Cli_severity_tokens_parse(string token, DiagnosticSeverity expected)
    {
        var config = AnalysisConfig.Empty.WithCliOverrides(new Dictionary<string, string> { ["PG001"] = token });
        Assert.Contains(Analyze(SecDefSql, config), d => d.RuleId == "PG001" && d.Severity == expected);
    }

    // ---- precedence: CLI > config file > default -----------------------------------------------

    [Fact]
    public void Cli_overrides_config_file_severity()
    {
        // File says PG005 = warning; CLI says PG005 = error → CLI wins.
        var fileConfig = AnalysisConfig.FromOverrides(new Dictionary<string, RuleOverride>
        {
            ["PG005"] = new RuleOverride(Severity: DiagnosticSeverity.Warning),
        });
        var merged = fileConfig.WithCliOverrides(new Dictionary<string, string> { ["PG005"] = "error" });
        Assert.Contains(Analyze(SecDefSql, merged), d => d.RuleId == "PG005" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Cli_can_reenable_a_rule_the_config_disabled()
    {
        var fileConfig = AnalysisConfig.FromOverrides(new Dictionary<string, RuleOverride>
        {
            ["PG003"] = new RuleOverride(Enabled: false),
        });
        var merged = fileConfig.WithCliOverrides(new Dictionary<string, string> { ["PG003"] = "on" });
        Assert.Contains(Analyze(UnguardedUpdate, merged), d => d.RuleId == "PG003");
    }

    [Fact]
    public void Cli_only_overrides_named_rules_others_keep_file_config()
    {
        var fileConfig = AnalysisConfig.FromOverrides(new Dictionary<string, RuleOverride>
        {
            ["PG001"] = new RuleOverride(Enabled: false),
            ["PG005"] = new RuleOverride(Severity: DiagnosticSeverity.Error),
        });
        var merged = fileConfig.WithCliOverrides(new Dictionary<string, string> { ["PG005"] = "warning" });
        var diags = Analyze(SecDefSql, merged);
        Assert.DoesNotContain(diags, d => d.RuleId == "PG001");                                   // file disable kept
        Assert.Contains(diags, d => d.RuleId == "PG005" && d.Severity == DiagnosticSeverity.Warning); // CLI won
    }

    // ---- sidecar JSON parsing ------------------------------------------------------------------

    [Fact]
    public void Parses_sidecar_json_enabled_and_severity()
    {
        var json = """
        { "rules": { "PG003": { "enabled": false }, "PG005": { "severity": "error" } } }
        """;
        var config = AnalysisConfig.Parse(json);
        Assert.False(config.IsEnabled("PG003"));
        Assert.Equal(DiagnosticSeverity.Error, config.EffectiveSeverity("PG005", DiagnosticSeverity.Info));
        Assert.True(config.IsEnabled("PG005"));
    }

    [Fact]
    public void Sidecar_ignores_unknown_rules_and_bad_severity()
    {
        var json = """
        { "rules": { "PG999": { "enabled": false }, "PG005": { "severity": "loud" } } }
        """;
        var config = AnalysisConfig.Parse(json);
        Assert.Empty(config.Overrides); // unknown id dropped; unparsable severity dropped → no override at all
        Assert.Equal(DiagnosticSeverity.Info, config.EffectiveSeverity("PG005", DiagnosticSeverity.Info));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{ \"rules\": 5 }")]
    public void Malformed_or_empty_sidecar_is_empty_config(string json)
    {
        var config = AnalysisConfig.Parse(json);
        Assert.Empty(config.Overrides);
        Assert.True(config.IsEnabled("PG001"));
    }

    [Fact]
    public void LoadForProject_reads_sidecar_next_to_project()
    {
        var dir = Directory.CreateTempSubdirectory("pgproj-analysis-").FullName;
        try
        {
            var proj = Path.Combine(dir, "db.pgproj");
            File.WriteAllText(proj, "<Project></Project>");
            File.WriteAllText(Path.Combine(dir, AnalysisConfig.SidecarFileName),
                """{ "rules": { "PG003": { "enabled": false } } }""");

            var config = AnalysisConfig.LoadForProject(proj);
            Assert.False(config.IsEnabled("PG003"));
            Assert.True(config.IsEnabled("PG001"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void LoadForProject_without_sidecar_is_empty()
    {
        var dir = Directory.CreateTempSubdirectory("pgproj-analysis-none-").FullName;
        try
        {
            var proj = Path.Combine(dir, "db.pgproj");
            File.WriteAllText(proj, "<Project></Project>");
            var config = AnalysisConfig.LoadForProject(proj);
            Assert.Same(AnalysisConfig.Empty, config);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- CLI override validation ---------------------------------------------------------------

    [Fact]
    public void Unknown_rule_in_cli_override_throws()
    {
        var ex = Assert.Throws<CliRuleException>(() =>
            AnalysisConfig.Empty.WithCliOverrides(new Dictionary<string, string> { ["PG999"] = "off" }));
        Assert.Contains("PG999", ex.Message);
    }

    [Fact]
    public void Invalid_cli_value_throws()
    {
        Assert.Throws<CliRuleException>(() =>
            AnalysisConfig.Empty.WithCliOverrides(new Dictionary<string, string> { ["PG003"] = "louder" }));
    }

    [Fact]
    public void Empty_cli_overrides_returns_same_instance()
    {
        var config = AnalysisConfig.Empty;
        Assert.Same(config, config.WithCliOverrides(new Dictionary<string, string>()));
    }

    // ---- multi-rule, end-to-end determinism ----------------------------------------------------

    [Fact]
    public void Disabling_all_rules_yields_no_findings()
    {
        var overrides = PgAnalyzer.RuleIds.ToDictionary(id => id, _ => new RuleOverride(Enabled: false));
        var config = AnalysisConfig.FromOverrides(overrides);
        Assert.Empty(Analyze(SecDefSql, config));
        Assert.Empty(Analyze(UnguardedUpdate, config));
    }
}
