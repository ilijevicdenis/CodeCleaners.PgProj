using System.Collections.Generic;
using System.Linq;
using PgProj.Core.Analysis;
using PgProj.Core.Model;
using PgProj.Core.Syntax;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Tests for the model-level analysis pass (ModelAnalyzer) — cross-object rules that run over the
/// merged DatabaseModel rather than a single parsed file. Each helper merges every SQL snippet into
/// ONE model (the per-file boundary must not matter: an index in another "file" still covers an FK).
/// </summary>
public class ModelAnalyzerTests
{
    private static DatabaseModel Model(params string[] files)
    {
        var model = new DatabaseModel();
        var mb = new ModelBuilder();
        foreach (var sql in files)
            mb.Build(new PgParser().Parse(sql), model);
        return model;
    }

    private static IReadOnlyList<Diagnostic> A(params string[] files) =>
        new ModelAnalyzer().Analyze(Model(files));

    [Fact]
    public void PG014_fk_without_any_index()
    {
        var d = A("""
            CREATE TABLE s.parent (id int PRIMARY KEY);
            CREATE TABLE s.child (id int PRIMARY KEY, parent_id int REFERENCES s.parent(id));
            """);
        Assert.Contains(d, x => x.RuleId == "PG014" && x.Severity == DiagnosticSeverity.Warning && x.Target == "s.child");
    }

    [Fact]
    public void PG014_clear_when_covering_index_exists_in_another_file()
    {
        var d = A(
            "CREATE TABLE s.parent (id int PRIMARY KEY);",
            "CREATE TABLE s.child (id int PRIMARY KEY, parent_id int REFERENCES s.parent(id));",
            "CREATE INDEX ix_child_parent ON s.child (parent_id);");
        Assert.DoesNotContain(d, x => x.RuleId == "PG014");
    }

    [Fact]
    public void PG014_clear_when_fk_is_leading_pk_column()
    {
        // Junction table: PK (a_id, b_id) covers the a_id FK (leading prefix) but NOT the b_id FK.
        var d = A("""
            CREATE TABLE s.a (id int PRIMARY KEY);
            CREATE TABLE s.b (id int PRIMARY KEY);
            CREATE TABLE s.ab (
                a_id int REFERENCES s.a(id),
                b_id int REFERENCES s.b(id),
                PRIMARY KEY (a_id, b_id)
            );
            """);
        var findings = d.Where(x => x.RuleId == "PG014").ToList();
        Assert.Single(findings);
        Assert.Contains("b_id", findings[0].Message);
    }

    [Fact]
    public void PG014_composite_fk_covered_by_prefix_in_any_order()
    {
        // The index leads with (y, x) — same set as the FK (x, y), so equality lookups are covered.
        var d = A("""
            CREATE TABLE s.parent (x int, y int, PRIMARY KEY (x, y));
            CREATE TABLE s.child (id int PRIMARY KEY, x int, y int,
                FOREIGN KEY (x, y) REFERENCES s.parent (x, y));
            CREATE INDEX ix_child_yx ON s.child (y, x, id);
            """);
        Assert.DoesNotContain(d, x => x.RuleId == "PG014");
    }

    [Fact]
    public void PG014_partial_index_does_not_cover()
    {
        var d = A("""
            CREATE TABLE s.parent (id int PRIMARY KEY);
            CREATE TABLE s.child (id int PRIMARY KEY, parent_id int REFERENCES s.parent(id));
            CREATE INDEX ix_partial ON s.child (parent_id) WHERE parent_id IS NOT NULL;
            """);
        Assert.Contains(d, x => x.RuleId == "PG014");
    }

    [Fact]
    public void PG014_unique_constraint_covers()
    {
        var d = A("""
            CREATE TABLE s.parent (id int PRIMARY KEY);
            CREATE TABLE s.child (id int PRIMARY KEY, parent_id int REFERENCES s.parent(id), UNIQUE (parent_id));
            """);
        Assert.DoesNotContain(d, x => x.RuleId == "PG014");
    }

    [Fact]
    public void PG014_respects_config_disable_and_severity_override()
    {
        var model = Model("""
            CREATE TABLE s.parent (id int PRIMARY KEY);
            CREATE TABLE s.child (id int PRIMARY KEY, parent_id int REFERENCES s.parent(id));
            """);

        var off = AnalysisConfig.FromOverrides(new Dictionary<string, RuleOverride> { ["PG014"] = new(Enabled: false) });
        Assert.Empty(new ModelAnalyzer(off).Analyze(model));

        var asError = AnalysisConfig.FromOverrides(new Dictionary<string, RuleOverride> { ["PG014"] = new(Severity: DiagnosticSeverity.Error) });
        Assert.Contains(new ModelAnalyzer(asError).Analyze(model), x => x.RuleId == "PG014" && x.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Registry_is_consistent()
    {
        Assert.Equal(ModelAnalyzer.RuleCount, ModelAnalyzer.RuleDefaults.Count);
        Assert.True(ModelAnalyzer.IsKnownRule("PG014"));
        Assert.False(ModelAnalyzer.IsKnownRule("PG001"));   // per-file rules live in PgAnalyzer
        Assert.Equal(DiagnosticSeverity.Warning, ModelAnalyzer.DefaultSeverityOf("PG014"));
    }

    private sealed class TestModelRule : IModelRule
    {
        public string Id => "TST101";
        public DiagnosticSeverity DefaultSeverity => DiagnosticSeverity.Info;
        public string Title => "Every model gets one finding";
        public IEnumerable<Diagnostic> Analyze(DatabaseModel model)
        {
            yield return new Diagnostic(Id, DefaultSeverity, $"model has {model.Tables.Count} table(s)", "model");
        }
    }

    [Fact]
    public void External_model_rules_run_with_config_overrides()
    {
        var model = Model("CREATE TABLE s.t (id int PRIMARY KEY);");
        var rules = new IModelRule[] { new TestModelRule() };

        var d = ExternalModelRules.Run(rules, model, AnalysisConfig.Empty);
        Assert.Single(d);
        Assert.Equal("TST101", d[0].RuleId);

        var off = AnalysisConfig.FromOverrides(new Dictionary<string, RuleOverride> { ["TST101"] = new(Enabled: false) });
        Assert.Empty(ExternalModelRules.Run(rules, model, off));

        var warn = AnalysisConfig.FromOverrides(new Dictionary<string, RuleOverride> { ["TST101"] = new(Severity: DiagnosticSeverity.Warning) });
        Assert.Equal(DiagnosticSeverity.Warning, ExternalModelRules.Run(rules, model, warn).Single().Severity);
    }

    [Fact]
    public void Model_rule_discovery_finds_IModelRule_implementations()
    {
        var rules = RulePackLoader.ModelRulesFromAssemblies(new[] { typeof(ModelAnalyzerTests).Assembly });
        Assert.Contains(rules, r => r.Id == "TST101");
    }
}
