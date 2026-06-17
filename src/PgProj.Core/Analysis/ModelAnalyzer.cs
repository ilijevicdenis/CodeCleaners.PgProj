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
///   PG019 foreign key with neither ON DELETE nor ON UPDATE action (silently defaults to NO ACTION)
///   PG024 duplicate index — two indexes with the same columns + predicate (dead write-amplification)
///   PG025 redundant index — its columns are a leading prefix of a wider index (already covered)
/// Findings honour the same <see cref="AnalysisConfig"/> enable/severity overrides as the per-file
/// rules, keyed by rule id.
/// </summary>
public sealed class ModelAnalyzer
{
    /// <summary>Every model-level rule this analyzer can emit, with its default severity and title.</summary>
    public static readonly IReadOnlyList<RuleInfo> RuleDefaults = new[]
    {
        new RuleInfo("PG014", DiagnosticSeverity.Warning, "Foreign key without a covering index"),
        new RuleInfo("PG019", DiagnosticSeverity.Info,    "Foreign key without an ON DELETE/ON UPDATE action"),
        new RuleInfo("PG024", DiagnosticSeverity.Info,    "Duplicate index (same columns and predicate)"),
        new RuleInfo("PG025", DiagnosticSeverity.Info,    "Redundant index (leading prefix of a wider index)"),
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
        CheckForeignKeyActions(model, diags);
        CheckRedundantIndexes(model, diags);
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

    // PG019 — a foreign key that declares neither ON DELETE nor ON UPDATE silently gets NO ACTION for
    // both. That's a legitimate choice, but far more often it's a forgotten decision (did you mean
    // CASCADE / SET NULL / RESTRICT?). Info-level nudge; the FK model carries the parsed action strings.
    private void CheckForeignKeyActions(DatabaseModel model, List<Diagnostic> diags)
    {
        if (!_config.IsEnabled("PG019")) return;

        foreach (var table in model.Tables)
            foreach (var fk in table.ForeignKeys)
            {
                if (!string.IsNullOrWhiteSpace(fk.OnDelete) || !string.IsNullOrWhiteSpace(fk.OnUpdate)) continue;
                var colList = string.Join(", ", fk.Columns);
                var fkName = fk.Name is null ? "" : $" \"{fk.Name}\"";
                Emit(diags, "PG019",
                    $"Foreign key{fkName} on ({colList}) referencing {fk.ReferencedSchema}.{fk.ReferencedTable} declares no ON DELETE/ON UPDATE action — " +
                    "it defaults to NO ACTION; state the intended behaviour (CASCADE / SET NULL / RESTRICT / NO ACTION) explicitly.",
                    $"{table.Schema}.{table.Name}");
            }
    }

    // PG024/PG025 — index hygiene per table. Build the b-tree-ordered column list of every "index-like"
    // object: the primary key, each unique constraint, and each explicit non-partial CREATE INDEX. PG024
    // flags an explicit index whose (ordered columns + predicate) duplicate an earlier index-like object
    // (the first occurrence is the keeper). PG025 flags a non-partial explicit index whose columns are a
    // STRICT leading prefix of another non-partial index-like object — the wider one already serves it.
    // Only explicit indexes are ever flagged (we don't tell a user their PK/UNIQUE is redundant); partial
    // indexes (WHERE …) never participate in PG025, on either side, because the predicate changes coverage.
    private void CheckRedundantIndexes(DatabaseModel model, List<Diagnostic> diags)
    {
        var wantDup = _config.IsEnabled("PG024");
        var wantRedundant = _config.IsEnabled("PG025");
        if (!wantDup && !wantRedundant) return;

        // Group every index-like object by table. Implicit (PK/unique) entries sort first so an explicit
        // index that duplicates a constraint is the one flagged, not the constraint.
        var byTable = new Dictionary<string, List<IndexLike>>(StringComparer.OrdinalIgnoreCase);
        void Add(string schema, string table, IndexLike e)
        {
            var key = $"{schema}.{table}";
            if (!byTable.TryGetValue(key, out var list)) byTable[key] = list = new List<IndexLike>();
            list.Add(e);
        }

        foreach (var t in model.Tables)
        {
            if (t.PrimaryKey is not null)
                Add(t.Schema, t.Name, new IndexLike($"PRIMARY KEY{NameOf(t.PrimaryKey.Name)}",
                    t.PrimaryKey.Columns.Select(NormalizeName).ToArray(), null, IsExplicit: false));
            foreach (var u in t.Unique)
                Add(t.Schema, t.Name, new IndexLike($"UNIQUE{NameOf(u.Name)}",
                    u.Columns.Select(NormalizeName).ToArray(), null, IsExplicit: false));
        }
        foreach (var ix in model.Indexes)
            Add(ix.Schema, ix.Table, new IndexLike($"index \"{ix.Name}\"",
                ix.Columns.Select(NormalizeIndexColumn).ToArray(), ix.WhereClause, IsExplicit: true));

        foreach (var kv in byTable)
        {
            // Stable processing order: implicit (PK/unique) first, then explicit in model order.
            var ordered = kv.Value.OrderBy(e => e.IsExplicit ? 1 : 0).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var e = ordered[i];
                if (!e.IsExplicit) continue;   // only explicit indexes are reported

                // PG024 — duplicate of an earlier entry (same ordered columns + same predicate).
                if (wantDup && ordered.Take(i).Any(o => SamePredicate(o, e) && SameColumns(o.Columns, e.Columns)))
                {
                    EmitIndex(diags, "PG024", kv.Key, e, ordered.First(o => SamePredicate(o, e) && SameColumns(o.Columns, e.Columns)),
                        "duplicates", "drop one — duplicate indexes only add write and storage overhead");
                    continue;   // a duplicate isn't also reported as redundant
                }

                // PG025 — strict leading prefix of a wider non-partial index-like object.
                if (wantRedundant && e.WhereClause is null)
                {
                    var wider = ordered.FirstOrDefault(o => o.WhereClause is null && IsStrictPrefix(e.Columns, o.Columns));
                    if (wider is not null)
                        EmitIndex(diags, "PG025", kv.Key, e, wider,
                            "is a leading prefix of", "the wider index already serves these lookups — drop the narrower one");
                }
            }
        }
    }

    private void EmitIndex(List<Diagnostic> diags, string ruleId, string target, IndexLike subject, IndexLike other, string relation, string advice) =>
        Emit(diags, ruleId,
            $"{Cap(subject.Label)} on ({string.Join(", ", subject.Columns)}) {relation} {other.Label} on ({string.Join(", ", other.Columns)}) — {advice}.",
            target);

    /// <summary>An index, primary key, or unique constraint reduced to its ordered columns + optional predicate.</summary>
    private sealed record IndexLike(string Label, IReadOnlyList<string> Columns, string? WhereClause, bool IsExplicit);

    private static string NameOf(string? name) => string.IsNullOrEmpty(name) ? "" : $" \"{name}\"";
    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static bool SamePredicate(IndexLike a, IndexLike b) =>
        string.Equals((a.WhereClause ?? "").Trim(), (b.WhereClause ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool SameColumns(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>True when <paramref name="prefix"/> is a STRICT leading prefix of <paramref name="full"/> (order matters; not equal).</summary>
    private static bool IsStrictPrefix(IReadOnlyList<string> prefix, IReadOnlyList<string> full)
    {
        if (prefix.Count == 0 || prefix.Count >= full.Count) return false;
        for (var i = 0; i < prefix.Count; i++)
            if (!string.Equals(prefix[i], full[i], StringComparison.OrdinalIgnoreCase)) return false;
        return true;
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
