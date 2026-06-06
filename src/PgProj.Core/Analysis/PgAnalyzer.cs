using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PgProj.Core.Syntax;

namespace PgProj.Core.Analysis;

/// <summary>
/// Static safety analysis over the PgParser AST (replaces the legacy SqlAnalyzer). Catches the
/// high-value lints a database project should not deploy without a look:
///   PG001 SECURITY DEFINER function without SET search_path (privilege-escalation risk)
///   PG002 dynamic SQL (EXECUTE) in a function body
///   PG003 UPDATE/DELETE without a WHERE clause (whole-table mutation)
///   PG004 schema mutation (CREATE/ALTER/DROP) inside a function body
///   PG005 function without a declared volatility (defaults to VOLATILE)
///   PG007 SELECT * in a view body (brittle to column changes)
///   PG009 LIMIT without ORDER BY (non-deterministic result)
/// Complements the semantic analyzer (catalog/type/structural). Function-body checks are textual
/// because PgParser keeps bodies verbatim; everything else is AST-precise.
/// </summary>
/// <remarks>
/// Findings are configurable (EP-ANALYSIS+): an <see cref="AnalysisConfig"/> can disable a rule or
/// override its severity, layered CLI &gt; sidecar &gt; the rule defaults below. The default-free
/// <see cref="Analyze(ParseResult)"/> overload keeps the original all-rules-on behaviour for callers
/// (and the model layer) that don't carry a config.
/// </remarks>
public sealed class PgAnalyzer
{
    /// <summary>Every rule this analyzer can emit, with its natural (default) severity and a one-line title.</summary>
    public static readonly IReadOnlyList<RuleInfo> RuleDefaults = new[]
    {
        new RuleInfo("PG001", DiagnosticSeverity.Warning, "SECURITY DEFINER without SET search_path"),
        new RuleInfo("PG002", DiagnosticSeverity.Info,    "Dynamic SQL (EXECUTE) in a function body"),
        new RuleInfo("PG003", DiagnosticSeverity.Warning, "UPDATE/DELETE without a WHERE clause"),
        new RuleInfo("PG004", DiagnosticSeverity.Warning, "Schema mutation inside a function body"),
        new RuleInfo("PG005", DiagnosticSeverity.Info,    "Function without a declared volatility"),
        new RuleInfo("PG007", DiagnosticSeverity.Info,    "SELECT * in a view body"),
        new RuleInfo("PG009", DiagnosticSeverity.Info,    "LIMIT without ORDER BY"),
    };

    private static readonly Dictionary<string, RuleInfo> ById =
        RuleDefaults.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>The number of distinct rules the analyzer knows about.</summary>
    public static int RuleCount => RuleDefaults.Count;

    /// <summary>The known rule ids, in declaration order (for usage/error messages).</summary>
    public static IEnumerable<string> RuleIds => RuleDefaults.Select(r => r.Id);

    /// <summary>True when <paramref name="ruleId"/> is a rule this analyzer can emit.</summary>
    public static bool IsKnownRule(string ruleId) => ruleId is not null && ById.ContainsKey(ruleId);

    /// <summary>The natural default severity of a known rule, or <see cref="DiagnosticSeverity.Warning"/>.</summary>
    public static DiagnosticSeverity DefaultSeverityOf(string ruleId) =>
        ById.TryGetValue(ruleId, out var r) ? r.DefaultSeverity : DiagnosticSeverity.Warning;

    private readonly AnalysisConfig _config;

    /// <summary>Creates an analyzer with all rules at their defaults (backward-compatible).</summary>
    public PgAnalyzer() : this(AnalysisConfig.Empty) { }

    /// <summary>Creates an analyzer that honours <paramref name="config"/> (rule enable/severity overrides).</summary>
    public PgAnalyzer(AnalysisConfig? config) => _config = config ?? AnalysisConfig.Empty;

    public IReadOnlyList<Diagnostic> Analyze(ParseResult result)
    {
        var diags = new List<Diagnostic>();
        foreach (var stmt in result.Statements)
        {
            switch (stmt)
            {
                case CreateFunctionStatement f: AnalyzeFunction(f, diags); break;
                case CreateViewStatement v: CheckSelectStar(v, diags); break;
                case QueryStatement q: CheckLimit(q.Query, $"{q.Query.From?.Relations.Count}", diags); break;
                case UpdateStatement u when u.Where is null && u.WhereCurrentOf is null:
                    Emit(diags, "PG003", "UPDATE without a WHERE clause mutates every row.", Q(u.Schema, u.Table)); break;
                case DeleteStatement d when d.Where is null && d.WhereCurrentOf is null:
                    Emit(diags, "PG003", "DELETE without a WHERE clause removes every row.", Q(d.Schema, d.Table)); break;
            }
        }
        return diags;
    }

    private void AnalyzeFunction(CreateFunctionStatement f, List<Diagnostic> diags)
    {
        var sig = $"{f.Schema ?? "public"}.{f.Name}";
        var full = (f.SourceText ?? "").ToLowerInvariant();

        // header-level checks run over the whole statement
        if (!Regex.IsMatch(full, @"\b(immutable|stable|volatile)\b"))
            Emit(diags, "PG005", "No volatility declared; defaults to VOLATILE, which the planner cannot optimize or inline.", sig);
        if (Regex.IsMatch(full, @"\bsecurity\s+definer\b") && !Regex.IsMatch(full, @"\bsearch_path\b"))
            Emit(diags, "PG001", "SECURITY DEFINER without SET search_path is a privilege-escalation risk.", sig);

        // body-level checks run only over the routine body (the dollar-quoted block), not the header
        var bm = Regex.Match(f.SourceText ?? "", @"\$(\w*)\$(.*)\$\1\$", RegexOptions.Singleline);
        var body = bm.Success ? bm.Groups[2].Value.ToLowerInvariant() : "";
        if (Regex.IsMatch(body, @"\bexecute\b"))
            Emit(diags, "PG002", "Dynamic SQL (EXECUTE) in a function body — ensure inputs are quoted (quote_ident/format).", sig);
        if (Regex.IsMatch(body, @"\b(create|alter|drop)\s+(table|view|index|sequence|schema|type|function|materialized)\b"))
            Emit(diags, "PG004", "Schema mutation (CREATE/ALTER/DROP) inside a function body.", sig);
    }

    private void CheckSelectStar(CreateViewStatement v, List<Diagnostic> diags)
    {
        if (Regex.IsMatch(v.BodyText, @"\bselect\b[^;]*\*", RegexOptions.IgnoreCase))
            Emit(diags, "PG007", "SELECT * in a view body is brittle to underlying column changes.", Q(v.Schema, v.Name));
    }

    private void CheckLimit(SelectQuery? q, string target, List<Diagnostic> diags)
    {
        if (q is null) return;
        if (q.Limit is not null && q.OrderBy.Count == 0 && q.SetOp is null)
            Emit(diags, "PG009", "LIMIT without ORDER BY returns a non-deterministic subset.", "query");
    }

    /// <summary>
    /// Records a finding for <paramref name="ruleId"/> unless the config disabled it, applying the
    /// configured severity override (else the rule's default severity). Central choke-point so every
    /// rule honours the config without each call site re-deriving severity.
    /// </summary>
    private void Emit(List<Diagnostic> diags, string ruleId, string message, string target)
    {
        if (!_config.IsEnabled(ruleId)) return;
        var severity = _config.EffectiveSeverity(ruleId, DefaultSeverityOf(ruleId));
        diags.Add(new Diagnostic(ruleId, severity, message, target));
    }

    private static string Q(string? schema, string name) => schema is null ? name : $"{schema}.{name}";
}

/// <summary>Static metadata for an analysis rule: its id, default severity, and a short title (for SARIF rule descriptors).</summary>
public sealed record RuleInfo(string Id, DiagnosticSeverity DefaultSeverity, string Title);
