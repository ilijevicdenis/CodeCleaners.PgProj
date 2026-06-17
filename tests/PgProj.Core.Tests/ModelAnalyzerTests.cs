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
        Assert.DoesNotContain(new ModelAnalyzer(off).Analyze(model), x => x.RuleId == "PG014");

        var asError = AnalysisConfig.FromOverrides(new Dictionary<string, RuleOverride> { ["PG014"] = new(Severity: DiagnosticSeverity.Error) });
        Assert.Contains(new ModelAnalyzer(asError).Analyze(model), x => x.RuleId == "PG014" && x.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void PG019_fk_without_referential_action()
    {
        var d = A("""
            CREATE TABLE s.parent (id int PRIMARY KEY);
            CREATE TABLE s.child (id int PRIMARY KEY, parent_id int REFERENCES s.parent(id));
            """);
        Assert.Contains(d, x => x.RuleId == "PG019" && x.Severity == DiagnosticSeverity.Info && x.Target == "s.child");
    }

    [Fact]
    public void PG019_clear_when_any_action_declared()
    {
        // ON DELETE alone is enough to count as a stated decision.
        var onDelete = A("""
            CREATE TABLE s.parent (id int PRIMARY KEY);
            CREATE TABLE s.child (id int PRIMARY KEY, parent_id int REFERENCES s.parent(id) ON DELETE CASCADE);
            """);
        Assert.DoesNotContain(onDelete, x => x.RuleId == "PG019");

        var onUpdate = A("""
            CREATE TABLE s.parent (id int PRIMARY KEY);
            CREATE TABLE s.child (id int PRIMARY KEY, parent_id int REFERENCES s.parent(id) ON UPDATE RESTRICT);
            """);
        Assert.DoesNotContain(onUpdate, x => x.RuleId == "PG019");
    }

    [Fact]
    public void PG024_duplicate_index()
    {
        var d = A("""
            CREATE TABLE s.t (id int PRIMARY KEY, a int, b int);
            CREATE INDEX ix1 ON s.t (a, b);
            CREATE INDEX ix2 ON s.t (a, b);
            """);
        var dup = d.Where(x => x.RuleId == "PG024").ToList();
        Assert.Single(dup);                       // only the second is flagged; the first is the keeper
        Assert.Equal(DiagnosticSeverity.Info, dup[0].Severity);
    }

    [Fact]
    public void PG024_clear_when_columns_or_predicate_differ()
    {
        // Different column order is a different b-tree — not a duplicate.
        var order = A("""
            CREATE TABLE s.t (id int PRIMARY KEY, a int, b int);
            CREATE INDEX ix1 ON s.t (a, b);
            CREATE INDEX ix2 ON s.t (b, a);
            """);
        Assert.DoesNotContain(order, x => x.RuleId == "PG024");

        // Same columns but different predicates — not a duplicate.
        var pred = A("""
            CREATE TABLE s.t (id int PRIMARY KEY, a int);
            CREATE INDEX ix1 ON s.t (a) WHERE a > 0;
            CREATE INDEX ix2 ON s.t (a) WHERE a < 0;
            """);
        Assert.DoesNotContain(pred, x => x.RuleId == "PG024");
    }

    [Fact]
    public void PG025_redundant_prefix_index()
    {
        // (a) is a leading prefix of (a, b) — the wider index already serves it.
        var d = A("""
            CREATE TABLE s.t (id int PRIMARY KEY, a int, b int);
            CREATE INDEX ix_a ON s.t (a);
            CREATE INDEX ix_ab ON s.t (a, b);
            """);
        var redundant = d.Where(x => x.RuleId == "PG025").ToList();
        Assert.Single(redundant);
        Assert.Contains("ix_a", redundant[0].Message);
    }

    [Fact]
    public void PG025_clear_when_not_a_leading_prefix_or_partial()
    {
        // (b) is NOT a leading prefix of (a, b) — a separate-column index, not redundant.
        var notPrefix = A("""
            CREATE TABLE s.t (id int PRIMARY KEY, a int, b int);
            CREATE INDEX ix_b ON s.t (b);
            CREATE INDEX ix_ab ON s.t (a, b);
            """);
        Assert.DoesNotContain(notPrefix, x => x.RuleId == "PG025");

        // A partial index changes coverage — never reported as redundant.
        var partial = A("""
            CREATE TABLE s.t (id int PRIMARY KEY, a int, b int);
            CREATE INDEX ix_a ON s.t (a) WHERE a IS NOT NULL;
            CREATE INDEX ix_ab ON s.t (a, b);
            """);
        Assert.DoesNotContain(partial, x => x.RuleId == "PG025");
    }

    [Fact]
    public void Registry_is_consistent()
    {
        Assert.Equal(ModelAnalyzer.RuleCount, ModelAnalyzer.RuleDefaults.Count);
        Assert.True(ModelAnalyzer.IsKnownRule("PG014"));
        Assert.True(ModelAnalyzer.IsKnownRule("PG019"));
        Assert.True(ModelAnalyzer.IsKnownRule("PG024"));
        Assert.True(ModelAnalyzer.IsKnownRule("PG025"));
        Assert.False(ModelAnalyzer.IsKnownRule("PG001"));   // per-file rules live in PgAnalyzer
        Assert.Equal(DiagnosticSeverity.Warning, ModelAnalyzer.DefaultSeverityOf("PG014"));
        Assert.Equal(DiagnosticSeverity.Info, ModelAnalyzer.DefaultSeverityOf("PG019"));
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
