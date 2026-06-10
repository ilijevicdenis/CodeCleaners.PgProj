using System;
using System.Collections.Generic;
using System.Linq;
using PgProj.Core.Model;

namespace PgProj.Core.Analysis;

/// <summary>
/// Static safety analysis over the <b>merged project model</b> — the cross-object complement of the
/// per-file <see cref="PgAnalyzer"/>. Rules here need to see relationships that span files (a foreign
/// key in one file, its covering index in another), which a per-file AST pass cannot:
///   PG014 foreign key whose referencing columns have no covering index (parent UPDATE/DELETE scans
///         the child table; the classic PostgreSQL performance foot-gun — FKs are not auto-indexed)
/// Findings honour the same <see cref="AnalysisConfig"/> enable/severity overrides as the per-file
/// rules, keyed by rule id.
/// </summary>
public sealed class ModelAnalyzer
{
    /// <summary>Every model-level rule this analyzer can emit, with its default severity and title.</summary>
    public static readonly IReadOnlyList<RuleInfo> RuleDefaults = new[]
    {
        new RuleInfo("PG014", DiagnosticSeverity.Warning, "Foreign key without a covering index"),
    };

    private static readonly Dictionary<string, RuleInfo> ById =
        RuleDefaults.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>The number of distinct model-level rules the analyzer knows about.</summary>
    public static int RuleCount => RuleDefaults.Count;

    /// <summary>The known rule ids, in declaration order (for usage/error messages).</summary>
    public static IEnumerable<string> RuleIds => RuleDefaults.Select(r => r.Id);

    /// <summary>True when <paramref name="ruleId"/> is a rule this analyzer can emit.</summary>
    public static bool IsKnownRule(string ruleId) => ruleId is not null && ById.ContainsKey(ruleId);

    /// <summary>The natural default severity of a known rule, or <see cref="DiagnosticSeverity.Warning"/>.</summary>
    public static DiagnosticSeverity DefaultSeverityOf(string ruleId) =>
        ById.TryGetValue(ruleId, out var r) ? r.DefaultSeverity : DiagnosticSeverity.Warning;

    private readonly AnalysisConfig _config;

    /// <summary>Creates an analyzer with all rules at their defaults.</summary>
    public ModelAnalyzer() : this(AnalysisConfig.Empty) { }

    /// <summary>Creates an analyzer that honours <paramref name="config"/> (rule enable/severity overrides).</summary>
    public ModelAnalyzer(AnalysisConfig? config) => _config = config ?? AnalysisConfig.Empty;

    public IReadOnlyList<Diagnostic> Analyze(DatabaseModel model)
    {
        var diags = new List<Diagnostic>();
        CheckForeignKeyIndexes(model, diags);
        return diags;
    }

    // PG014 — every FK's referencing columns should be the LEADING columns of some index on the child
    // table (any order within the prefix; equality lookups don't care). Coverage sources: the primary
    // key, unique constraints (both are backed by indexes), and explicit indexes. Partial indexes
    // (WHERE …) don't cover all rows, so they don't count; an expression in a needed prefix position
    // simply never matches a plain column name.
    private void CheckForeignKeyIndexes(DatabaseModel model, List<Diagnostic> diags)
    {
        if (!_config.IsEnabled("PG014")) return;   // skip the index build when the rule is off

        // Pre-bucket explicit indexes by child table (one pass over model.Indexes, not per-FK scans).
        var indexesByTable = new Dictionary<string, List<IReadOnlyList<string>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var ix in model.Indexes)
        {
            if (ix.WhereClause is not null) continue;   // partial index: not a general cover
            var key = $"{ix.Schema}.{ix.Table}";
            if (!indexesByTable.TryGetValue(key, out var list)) indexesByTable[key] = list = new List<IReadOnlyList<string>>();
            list.Add(ix.Columns.Select(NormalizeIndexColumn).ToArray());
        }

        foreach (var table in model.Tables)
        {
            if (table.ForeignKeys.Count == 0) continue;

            var covers = new List<IReadOnlyList<string>>();
            if (table.PrimaryKey is not null)
                covers.Add(table.PrimaryKey.Columns.Select(NormalizeName).ToArray());
            foreach (var u in table.Unique)
                covers.Add(u.Columns.Select(NormalizeName).ToArray());
            if (indexesByTable.TryGetValue($"{table.Schema}.{table.Name}", out var explicitIxs))
                covers.AddRange(explicitIxs);

            foreach (var fk in table.ForeignKeys)
            {
                if (fk.Columns.Count == 0) continue;
                var fkCols = fk.Columns.Select(NormalizeName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (covers.Any(c => IsPrefixCover(c, fkCols))) continue;

                var colList = string.Join(", ", fk.Columns);
                var fkName = fk.Name is null ? "" : $" \"{fk.Name}\"";
                Emit(diags, "PG014",
                    $"Foreign key{fkName} on ({colList}) referencing {fk.ReferencedSchema}.{fk.ReferencedTable} has no covering index — " +
                    $"parent UPDATE/DELETE scans this table; create an index whose leading columns are ({colList}).",
                    $"{table.Schema}.{table.Name}");
            }
        }
    }

    /// <summary>True when the first <c>fkCols.Count</c> columns of <paramref name="indexCols"/> are exactly the FK columns (any order).</summary>
    private static bool IsPrefixCover(IReadOnlyList<string> indexCols, HashSet<string> fkCols)
    {
        if (indexCols.Count < fkCols.Count) return false;
        for (var i = 0; i < fkCols.Count; i++)
            if (!fkCols.Contains(indexCols[i])) return false;
        return true;
    }

    // Index column entries can carry decorations ("col DESC", "col text_pattern_ops", quoted names) or
    // be expressions ("lower(col)"). Take the bare leading identifier; an expression keeps its text and
    // simply never equals a plain column name.
    private static string NormalizeIndexColumn(string col)
    {
        var s = col.Trim();
        if (s.Length == 0) return s;
        if (s[0] == '"')
        {
            var end = s.IndexOf('"', 1);
            return end > 0 ? s[1..end] : s;
        }
        if (s.Contains('(')) return s;   // expression — leave as-is (won't match a column name)
        var sp = s.IndexOf(' ');
        return sp > 0 ? s[..sp] : s;
    }

    private static string NormalizeName(string name)
    {
        var s = name.Trim();
        return s.Length > 1 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;
    }

    /// <summary>Records a finding unless the config disabled the rule, applying the configured severity.</summary>
    private void Emit(List<Diagnostic> diags, string ruleId, string message, string target)
    {
        if (!_config.IsEnabled(ruleId)) return;
        var severity = _config.EffectiveSeverity(ruleId, DefaultSeverityOf(ruleId));
        diags.Add(new Diagnostic(ruleId, severity, message, target));
    }
}

/// <summary>Runs external model rules over the merged model, applying the project's analysis config by rule id.</summary>
public static class ExternalModelRules
{
    /// <summary>
    /// Runs each enabled rule over <paramref name="model"/> and returns its findings with the configured
    /// effective severity (the config override, else the rule's <see cref="IModelRule.DefaultSeverity"/>).
    /// </summary>
    public static IReadOnlyList<Diagnostic> Run(IReadOnlyList<IModelRule> rules, DatabaseModel model, AnalysisConfig config)
    {
        if (rules.Count == 0) return Array.Empty<Diagnostic>();
        var diags = new List<Diagnostic>();
        foreach (var rule in rules)
        {
            if (!config.IsEnabled(rule.Id)) continue;
            var sev = config.EffectiveSeverity(rule.Id, rule.DefaultSeverity);
            foreach (var d in rule.Analyze(model))
                diags.Add(d.Severity == sev ? d : d with { Severity = sev });
        }
        return diags;
    }
}
