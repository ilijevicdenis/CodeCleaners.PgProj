using System.Collections.Generic;
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
public sealed class PgAnalyzer
{
    public const int RuleCount = 7;

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
                    diags.Add(new Diagnostic("PG003", DiagnosticSeverity.Warning, "UPDATE without a WHERE clause mutates every row.", Q(u.Schema, u.Table))); break;
                case DeleteStatement d when d.Where is null && d.WhereCurrentOf is null:
                    diags.Add(new Diagnostic("PG003", DiagnosticSeverity.Warning, "DELETE without a WHERE clause removes every row.", Q(d.Schema, d.Table))); break;
            }
        }
        return diags;
    }

    private static void AnalyzeFunction(CreateFunctionStatement f, List<Diagnostic> diags)
    {
        var sig = $"{f.Schema ?? "public"}.{f.Name}";
        var full = (f.SourceText ?? "").ToLowerInvariant();

        // header-level checks run over the whole statement
        if (!Regex.IsMatch(full, @"\b(immutable|stable|volatile)\b"))
            diags.Add(new Diagnostic("PG005", DiagnosticSeverity.Info, "No volatility declared; defaults to VOLATILE, which the planner cannot optimize or inline.", sig));
        if (Regex.IsMatch(full, @"\bsecurity\s+definer\b") && !Regex.IsMatch(full, @"\bsearch_path\b"))
            diags.Add(new Diagnostic("PG001", DiagnosticSeverity.Warning, "SECURITY DEFINER without SET search_path is a privilege-escalation risk.", sig));

        // body-level checks run only over the routine body (the dollar-quoted block), not the header
        var bm = Regex.Match(f.SourceText ?? "", @"\$(\w*)\$(.*)\$\1\$", RegexOptions.Singleline);
        var body = bm.Success ? bm.Groups[2].Value.ToLowerInvariant() : "";
        if (Regex.IsMatch(body, @"\bexecute\b"))
            diags.Add(new Diagnostic("PG002", DiagnosticSeverity.Info, "Dynamic SQL (EXECUTE) in a function body — ensure inputs are quoted (quote_ident/format).", sig));
        if (Regex.IsMatch(body, @"\b(create|alter|drop)\s+(table|view|index|sequence|schema|type|function|materialized)\b"))
            diags.Add(new Diagnostic("PG004", DiagnosticSeverity.Warning, "Schema mutation (CREATE/ALTER/DROP) inside a function body.", sig));
    }

    private static void CheckSelectStar(CreateViewStatement v, List<Diagnostic> diags)
    {
        if (Regex.IsMatch(v.BodyText, @"\bselect\b[^;]*\*", RegexOptions.IgnoreCase))
            diags.Add(new Diagnostic("PG007", DiagnosticSeverity.Info, "SELECT * in a view body is brittle to underlying column changes.", Q(v.Schema, v.Name)));
    }

    private static void CheckLimit(SelectQuery? q, string target, List<Diagnostic> diags)
    {
        if (q is null) return;
        if (q.Limit is not null && q.OrderBy.Count == 0 && q.SetOp is null)
            diags.Add(new Diagnostic("PG009", DiagnosticSeverity.Info, "LIMIT without ORDER BY returns a non-deterministic subset.", "query"));
    }

    private static string Q(string? schema, string name) => schema is null ? name : $"{schema}.{name}";
}
