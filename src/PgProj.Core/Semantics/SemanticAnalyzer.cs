using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PgProj.Core.Syntax;

namespace PgProj.Core.Semantics;

/// <summary>A semantic problem found without executing the statement (the kind that breaks a deploy).</summary>
public sealed record SemanticDiagnostic(string Message);

/// <summary>
/// Static semantic analysis over the PgParser AST: it catches mistakes a syntactically-valid script
/// would only fail on at run time — references to objects that do not exist, INSERT column/value
/// count mismatches, re-creating an existing object, and constant-foldable domain errors (division
/// by a constant zero, log/sqrt of a constant out of domain, invalid constant casts).
///
/// Deliberately CONSERVATIVE: it only reports a problem when it is certain (e.g. a schema-qualified
/// relation in a schema it manages, a literal operand). Anything it cannot resolve is left alone, so
/// it never rejects valid SQL.
/// </summary>
public sealed class SemanticAnalyzer
{
    private readonly Catalog _catalog;       // fixture/project ∪ objects defined in this script (for resolution)
    private readonly Catalog _preExisting;   // objects that existed before this script (for duplicate detection)
    private readonly List<SemanticDiagnostic> _diags = new();
    private bool _scriptRenames;             // a RENAME in the script changes names we cannot track — skip relation checks
    private bool _scriptAlters;              // an ALTER may add/rename columns we don't track — skip column checks

    public SemanticAnalyzer(Catalog catalog, Catalog? preExisting = null)
    {
        _catalog = catalog;
        _preExisting = preExisting ?? catalog;
    }

    public IReadOnlyList<SemanticDiagnostic> Analyze(ParseResult result)
    {
        _scriptRenames = result.Statements.OfType<AlterStatement>().Any(a => a.Actions.Contains("RENAME"));
        _scriptAlters = result.Statements.OfType<AlterStatement>().Any();
        foreach (var stmt in result.Statements) AnalyzeStatement(stmt);
        return _diags;
    }

    private void Report(string msg) => _diags.Add(new SemanticDiagnostic(msg));

    private void AnalyzeStatement(SqlStatement stmt)
    {
        switch (stmt)
        {
            case QueryStatement q: AnalyzeQuery(q.Query); break;
            case InsertStatement ins: AnalyzeInsert(ins); break;
            case UpdateStatement up:
                ResolveTable(up.Schema, up.Table);
                if (up.From is null)   // with a FROM, a SET target could be ambiguous to attribute — only check simple updates
                    CheckColumnsExist(up.Schema, up.Table, up.Set.Where(s => !s.Multi).SelectMany(s => s.Columns));
                if (up.From is not null) AnalyzeFrom(up.From);
                CheckExpr(up.Where);
                foreach (var s in up.Set) CheckExpr(s.Value);
                break;
            case DeleteStatement del: ResolveTable(del.Schema, del.Table); if (del.Using is not null) AnalyzeFrom(del.Using); CheckExpr(del.Where); break;
            case MergeStatement m: ResolveTable(m.Schema, m.Table); AnalyzeTableRef(m.Source); CheckExpr(m.On); break;
            case CreateTableStatement ct:
                if (!ct.IfNotExists && _preExisting.HasRelation(ct.Schema, ct.Name))
                    Report($"relation \"{Qual(ct.Schema, ct.Name)}\" already exists");
                break;
        }
    }

    private void AnalyzeInsert(InsertStatement ins)
    {
        ResolveTable(ins.Schema, ins.Table);
        if (ins.Source is { IsValues: true } v && ins.Columns.Count > 0)
            foreach (var row in v.ValuesRows)
                if (row.Count != ins.Columns.Count)
                { Report($"INSERT has {ins.Columns.Count} columns but {row.Count} values"); break; }
        CheckColumnsExist(ins.Schema, ins.Table, ins.Columns);
        if (ins.Source is not null) AnalyzeQuery(ins.Source);
        foreach (var r in ins.Returning) CheckExpr(r.Expr);
    }

    /// <summary>Flag a referenced column that does not exist on a known table (high-confidence: explicit targets).</summary>
    private void CheckColumnsExist(string? schema, string table, IEnumerable<string> columns)
    {
        if (_scriptAlters) return;                         // an ALTER may have added/renamed columns
        var known = _catalog.Columns(schema, table);
        if (known is null || known.Count == 0) return;     // table or its columns not known — don't guess
        var set = new HashSet<string>(known, StringComparer.OrdinalIgnoreCase);
        foreach (var c in columns)
            if (!set.Contains(c))
                Report($"column \"{c}\" of relation \"{Qual(schema, table)}\" does not exist");
    }

    private void AnalyzeQuery(SelectQuery? q)
    {
        if (q is null) return;
        foreach (var cte in q.With) AnalyzeQuery(cte.Query);
        if (q.SetOp is not null) { AnalyzeQuery(q.SetOp.Left); AnalyzeQuery(q.SetOp.Right); return; }
        if (q.From is not null) AnalyzeFrom(q.From);
        foreach (var it in q.Items) CheckExpr(it.Expr);
        CheckExpr(q.Where);
        CheckExpr(q.Having);
        foreach (var g in q.GroupBy) CheckExpr(g);
        foreach (var row in q.ValuesRows) foreach (var e in row) CheckExpr(e);
    }

    private void AnalyzeFrom(FromClause from)
    {
        foreach (var rel in from.Relations)
        {
            AnalyzeTableRef(rel);
            foreach (var j in rel.Joins) { AnalyzeTableRef(j.Right); CheckExpr(j.On); }
        }
    }

    private void AnalyzeTableRef(TableRef rel)
    {
        if (rel.Subquery is not null) { AnalyzeQuery(rel.Subquery); return; }
        if (rel.Function is not null) { foreach (var a in rel.Function.Args) CheckExpr(a); return; }
        if (rel.TableName is not null) ResolveTable(rel.Schema, rel.TableName);
    }

    /// <summary>Flag a schema-qualified relation in a managed schema that does not exist.</summary>
    private void ResolveTable(string? schema, string name)
    {
        if (_scriptRenames) return;                          // names changed mid-script — cannot resolve reliably
        if (schema is null) return;                          // unqualified: could be search_path/system — skip
        if (!_catalog.SchemaManaged(schema)) return;         // unknown schema (pg_catalog, …) — skip
        if (!_catalog.HasRelation(schema, name))
            Report($"relation \"{schema}.{name}\" does not exist");
    }

    // ---- expression / constant checks --------------------------------------

    private void CheckExpr(Expr? e)
    {
        switch (e)
        {
            case null: return;
            case BinaryExpr b:
                if ((b.Op == "/" || b.Op == "%") && Fold(b.Right) is 0.0)
                    Report("division by zero");
                CheckExpr(b.Left); CheckExpr(b.Right);
                break;
            case UnaryExpr u: CheckExpr(u.Operand); break;
            case PostfixExpr p: CheckExpr(p.Operand); break;
            case CastExpr c: CheckConstCast(c); CheckExpr(c.Operand); break;
            case CollateExpr cl: CheckExpr(cl.Operand); break;
            case FuncCallExpr f: CheckFuncDomain(f); foreach (var a in f.Args) CheckExpr(a); CheckExpr(f.Filter); break;
            case CaseExpr cs: CheckExpr(cs.Operand); foreach (var (w, t) in cs.Branches) { CheckExpr(w); CheckExpr(t); } CheckExpr(cs.Else); break;
            case BetweenExpr bt: CheckExpr(bt.Operand); CheckExpr(bt.Low); CheckExpr(bt.High); break;
            case InExpr inx: CheckExpr(inx.Operand); if (inx.List is not null) foreach (var x in inx.List) CheckExpr(x); AnalyzeQuery(inx.Subquery); break;
            case IsCheckExpr isc: CheckExpr(isc.Operand); CheckExpr(isc.Other); break;
            case PatternMatchExpr pm: CheckExpr(pm.Operand); CheckExpr(pm.Pattern); CheckExpr(pm.Escape); break;
            case QuantifiedExpr qx: CheckExpr(qx.Left); CheckExpr(qx.Array); AnalyzeQuery(qx.Subquery); break;
            case RowExpr r: foreach (var x in r.Items) CheckExpr(x); break;
            case ArrayExpr ar: foreach (var x in ar.Elements) CheckExpr(x); AnalyzeQuery(ar.Subquery); break;
            case SubqueryExpr sq: AnalyzeQuery(sq.Query); break;
            case ExistsExpr ex: AnalyzeQuery(ex.Query); break;
            case SubscriptExpr ss: CheckExpr(ss.Operand); break;
        }
    }

    private void CheckFuncDomain(FuncCallExpr f)
    {
        if (f.Args.Count != 1 || f.Name.Count != 1) return;
        var name = f.Name[0].ToLowerInvariant();
        var a = Fold(f.Args[0]);
        if (a is null) return;
        switch (name)
        {
            case "sqrt" when a < 0: Report("cannot take square root of a negative number"); break;
            case "ln" when a <= 0: Report("argument of ln must be positive"); break;
            case "log" when a <= 0: Report("argument of log must be positive"); break;
        }
    }

    private void CheckConstCast(CastExpr c)
    {
        if (c.Operand is not LiteralExpr { Kind: "string" } lit) return;
        var t = BaseType(c.TypeText);
        var v = lit.Text;
        bool bad = t switch
        {
            "integer" or "int" or "int4" or "smallint" or "int2" or "bigint" or "int8" => !long.TryParse(v.Trim(), out _),
            "numeric" or "decimal" or "real" or "double precision" or "float8" or "float4" => !double.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _),
            "boolean" or "bool" => !IsBool(v),
            // date/time/uuid intentionally omitted: format variability (datestyle, 'infinity', …)
            // makes static validation risky, and false positives are unacceptable here.
            _ => false,
        };
        if (bad) Report($"invalid input syntax for type {t}: \"{v}\"");
    }

    private static bool IsBool(string v) =>
        v.Trim().ToLowerInvariant() is "t" or "f" or "true" or "false" or "yes" or "no" or "on" or "off" or "1" or "0" or "y" or "n";

    private static string BaseType(string typeText)
    {
        var s = typeText.Trim().ToLowerInvariant();
        var paren = s.IndexOf('(');
        if (paren >= 0) s = s[..paren].Trim();
        var dot = s.LastIndexOf('.');
        if (dot >= 0 && !s.Contains(' ')) s = s[(dot + 1)..];
        return s;
    }

    /// <summary>Constant-fold a numeric expression; null when not a compile-time numeric constant.</summary>
    private static double? Fold(Expr? e) => e switch
    {
        LiteralExpr { Kind: "number" } l when double.TryParse(l.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        UnaryExpr { Op: "-" } u => Fold(u.Operand) is { } x ? -x : null,
        UnaryExpr { Op: "+" } u => Fold(u.Operand),
        BinaryExpr b => FoldBinary(b),
        _ => null,
    };

    private static double? FoldBinary(BinaryExpr b)
    {
        if (Fold(b.Left) is not { } l || Fold(b.Right) is not { } r) return null;
        return b.Op switch
        {
            "+" => l + r,
            "-" => l - r,
            "*" => l * r,
            "/" => r == 0 ? null : l / r,
            "%" => r == 0 ? null : l % r,
            "^" => Math.Pow(l, r),
            _ => null,
        };
    }

    private static string Qual(string? schema, string name) => schema is null ? name : $"{schema}.{name}";
}
