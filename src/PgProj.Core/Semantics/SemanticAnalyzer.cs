using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PgProj.Core.Syntax;
using UnifiedDiagnostic = PgProj.Core.Diagnostics.Diagnostic;

namespace PgProj.Core.Semantics;

/// <summary>A semantic problem found without executing the statement (the kind that breaks a deploy).</summary>
public sealed record SemanticDiagnostic(string Message)
{
    /// <summary>Lift this semantic problem into the unified compiler-style diagnostic (always an error; code <c>SEM</c>).</summary>
    public UnifiedDiagnostic ToUnified(string? file = null, int line = 0, int column = 0) =>
        UnifiedDiagnostic.FromSemantic(Message, file, line, column);
}

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

    /// <summary>
    /// Same analysis as <see cref="Analyze"/>, returning the unified compiler-style diagnostics with the
    /// caller-supplied source anchor stamped on each finding (the semantic checks share one statement's
    /// position). Lets the reference/build layers carry file/line/col instead of bare messages.
    /// </summary>
    public IReadOnlyList<UnifiedDiagnostic> AnalyzeUnified(ParseResult result, string? file = null, int line = 0, int column = 0)
    {
        var found = Analyze(result);
        var lifted = new List<UnifiedDiagnostic>(found.Count);
        foreach (var d in found) lifted.Add(d.ToUnified(file, line, column));
        return lifted;
    }

    private void Report(string msg) => _diags.Add(new SemanticDiagnostic(msg));

    private void AnalyzeStatement(SqlStatement stmt)
    {
        switch (stmt)
        {
            case QueryStatement q: AnalyzeQuery(q.Query); break;
            case CreateTableAsStatement ctas: AnalyzeQuery(ctas.Source); break;
            case DropStatement drop: AnalyzeDrop(drop); break;
            case CommandStatement { Kind: "DO" } doBlock: AnalyzePlpgsql(doBlock.Body, doBlock.Detail, defaultIsPlpgsql: true, new PlpgsqlContext(IsDo: true, false, false, false, false)); break;
            case CreateFunctionStatement fn: AnalyzePlpgsql(fn.Body, fn.Language, defaultIsPlpgsql: false, new PlpgsqlContext(false, fn.IsProcedure, fn.ReturnsVoid, fn.ReturnsSetof, fn.HasOutParams)); break;
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
            case CreateIndexStatement ix: ResolveTable(ix.Schema, ix.Table); break;   // CREATE INDEX ON a missing table
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
        // LIMIT / OFFSET apply to a set-operation query too, so validate them before the SetOp early-return.
        CheckExpr(q.LimitExpr); CheckExpr(q.OffsetExpr);
        CheckRowCount(q.LimitExpr, "LIMIT"); CheckRowCount(q.OffsetExpr, "OFFSET");
        foreach (var cte in q.With) AnalyzeQuery(cte.Query);
        if (q.SetOp is not null) { AnalyzeQuery(q.SetOp.Left); AnalyzeQuery(q.SetOp.Right); return; }
        if (q.From is not null) AnalyzeFrom(q.From);
        foreach (var it in q.Items) CheckExpr(it.Expr);
        CheckExpr(q.Where);
        CheckExpr(q.Having);
        foreach (var g in q.GroupBy) CheckExpr(g);
        foreach (var row in q.ValuesRows) foreach (var e in row) CheckExpr(e);
    }

    // LIMIT / OFFSET must be a non-negative integer. Catch the statically-decidable bad cases:
    // a constant that folds negative, or a string literal that isn't a valid non-negative integer.
    private void CheckRowCount(Expr? e, string clause)
    {
        if (e is null) return;
        if (Fold(e) is { } v && v < 0) { Report($"{clause} must not be negative"); return; }
        if (e is LiteralExpr { Kind: "string", Text: var s } && !(long.TryParse(s.Trim(), out var iv) && iv >= 0))
            Report($"{clause} value '{s}' is not a valid non-negative integer");
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
            case FuncCallExpr f: CheckFuncDomain(f); CheckFuncCall(f); foreach (var a in f.Args) CheckExpr(a); CheckExpr(f.Filter); break;
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

    // Validate a PL/pgSQL body (DO block or LANGUAGE plpgsql function) for compile-time structural errors.
    private void AnalyzePlpgsql(string? body, string? language, bool defaultIsPlpgsql, PlpgsqlContext ctx)
    {
        if (body is null) return;
        var lang = language?.Trim().Trim('\'').ToLowerInvariant();
        bool isPlpgsql = lang is "plpgsql" || (lang is null && defaultIsPlpgsql);
        if (!isPlpgsql) return;
        foreach (var e in PlpgsqlValidator.Validate(body, ctx)) Report(e);
    }

    // DROP of a relation (table/view/…) that does not exist in a managed schema — unless IF EXISTS.
    // Restricted to relation kinds the catalog tracks reliably; other kinds are left for later.
    private void AnalyzeDrop(DropStatement drop)
    {
        if (drop.IfExists || _scriptRenames || _scriptAlters) return;   // an ALTER may have moved/renamed the target
        if (drop.ObjectKind is not ("TABLE" or "VIEW" or "MATERIALIZED VIEW" or "FOREIGN TABLE" or "SEQUENCE")) return;
        foreach (var raw in drop.Names)
        {
            var dot = raw.LastIndexOf('.');
            if (dot <= 0) continue;                          // unqualified — schema unknown, skip
            ResolveTable(raw[..dot], raw[(dot + 1)..]);
        }
    }

    private void CheckFuncDomain(FuncCallExpr f)
    {
        if (f.Name.Count != 1) return;
        var name = f.Name[0].ToLowerInvariant();
        if (_catalog.HasFunction(name)) return;              // a user-defined function may have a wider domain

        if (f.Args.Count == 1)
        {
            if (Fold(f.Args[0]) is not { } a) return;
            switch (name)
            {
                case "sqrt" when a < 0: Report("cannot take square root of a negative number"); break;
                case "ln" when a <= 0: Report("argument of ln must be positive"); break;
                case "log" or "log10" when a <= 0: Report("argument of log must be positive"); break;
                case "factorial" when a < 0: Report("factorial of a negative number is undefined"); break;
                case "chr" when a <= 0: Report("chr() argument must be a positive integer"); break;
                case "setseed" when a < -1 || a > 1: Report("setseed() parameter must be between -1 and 1"); break;
                case "asin" or "asind" or "acos" or "acosd" when a < -1 || a > 1: Report($"input is out of range for {name}"); break;
            }
        }
        else if (f.Args.Count == 2)
        {
            switch (name)
            {
                case "div" or "mod" when Fold(f.Args[1]) is 0.0: Report("division by zero"); break;
                case "encode" when StrLit(f.Args[1]) is { } fmt && !EncodeFormats.Contains(fmt): Report($"unrecognized encoding format \"{fmt}\""); break;
                case "date_trunc" when StrLit(f.Args[0]) is { } unit && !DateTruncUnits.Contains(unit): Report($"unrecognized date_trunc field \"{unit}\""); break;
            }
        }
        else if (f.Args.Count == 3 && name == "make_date")
        {
            if (Fold(f.Args[1]) is { } mo && (mo < 1 || mo > 12)) Report("month must be between 1 and 12 in make_date");
            else if (Fold(f.Args[2]) is { } d && (d < 1 || d > 31)) Report("day must be between 1 and 31 in make_date");
        }
        else if (f.Args.Count == 3 && name == "make_time")
        {
            if (Fold(f.Args[0]) is { } h && (h < 0 || h > 23)) Report("hour must be between 0 and 23 in make_time");
            else if (Fold(f.Args[1]) is { } mi && (mi < 0 || mi > 59)) Report("minute must be between 0 and 59 in make_time");
        }
    }

    private static string? StrLit(Expr e) => e is LiteralExpr { Kind: "string", Text: var t } ? t : null;
    private static readonly HashSet<string> EncodeFormats = new(StringComparer.OrdinalIgnoreCase) { "base64", "hex", "escape" };
    private static readonly HashSet<string> DateTruncUnits = new(StringComparer.OrdinalIgnoreCase)
    { "microseconds", "milliseconds", "second", "minute", "hour", "day", "week", "month", "quarter", "year", "decade", "century", "millennium" };

    // Built-in scalar/aggregate functions whose argument count is fixed and unambiguous, as (min, max).
    // Used to catch arity mistakes (abs(), round(1,2,3), sum(a,b)). Only applied to an UNQUALIFIED call
    // whose name is NOT shadowed by a user-defined function in the catalog, so it never rejects valid SQL.
    private const int Unbounded = int.MaxValue;
    private static readonly Dictionary<string, (int Min, int Max)> BuiltinArity = new(StringComparer.OrdinalIgnoreCase)
    {
        // numeric
        ["abs"] = (1, 1), ["sign"] = (1, 1), ["ceil"] = (1, 1), ["ceiling"] = (1, 1), ["floor"] = (1, 1),
        ["sqrt"] = (1, 1), ["cbrt"] = (1, 1), ["exp"] = (1, 1), ["ln"] = (1, 1), ["factorial"] = (1, 1),
        ["degrees"] = (1, 1), ["radians"] = (1, 1), ["round"] = (1, 2), ["trunc"] = (1, 2), ["log"] = (1, 2),
        ["mod"] = (2, 2), ["power"] = (2, 2), ["div"] = (2, 2), ["atan2"] = (2, 2), ["gcd"] = (2, 2), ["lcm"] = (2, 2),
        ["sin"] = (1, 1), ["cos"] = (1, 1), ["tan"] = (1, 1), ["cot"] = (1, 1), ["asin"] = (1, 1), ["acos"] = (1, 1),
        ["atan"] = (1, 1), ["sinh"] = (1, 1), ["cosh"] = (1, 1), ["tanh"] = (1, 1), ["asinh"] = (1, 1),
        ["acosh"] = (1, 1), ["atanh"] = (1, 1), ["pi"] = (0, 0),
        ["sind"] = (1, 1), ["cosd"] = (1, 1), ["tand"] = (1, 1), ["cotd"] = (1, 1), ["asind"] = (1, 1),
        ["acosd"] = (1, 1), ["atand"] = (1, 1), ["log10"] = (1, 1), ["scale"] = (1, 1), ["min_scale"] = (1, 1),
        ["trim_scale"] = (1, 1), ["bit_count"] = (1, 1), ["setseed"] = (1, 1), ["age"] = (1, 2), ["width_bucket"] = (2, 4),
        // string
        ["length"] = (1, 1), ["char_length"] = (1, 1), ["character_length"] = (1, 1), ["octet_length"] = (1, 1),
        ["bit_length"] = (1, 1), ["upper"] = (1, 1), ["lower"] = (1, 1), ["initcap"] = (1, 1), ["reverse"] = (1, 1),
        ["ascii"] = (1, 1), ["chr"] = (1, 1), ["md5"] = (1, 1), ["to_hex"] = (1, 1), ["quote_ident"] = (1, 1),
        ["quote_literal"] = (1, 1), ["quote_nullable"] = (1, 1), ["ltrim"] = (1, 2), ["rtrim"] = (1, 2),
        ["btrim"] = (1, 2), ["lpad"] = (2, 3), ["rpad"] = (2, 3), ["left"] = (2, 2), ["right"] = (2, 2),
        ["repeat"] = (2, 2), ["strpos"] = (2, 2), ["starts_with"] = (2, 2), ["substr"] = (2, 3),
        ["replace"] = (3, 3), ["translate"] = (3, 3), ["split_part"] = (3, 3),
        ["concat_ws"] = (1, Unbounded), ["format"] = (1, Unbounded),
        ["string_to_array"] = (2, 3), ["encode"] = (2, 2), ["decode"] = (2, 2), ["convert_from"] = (2, 2),
        ["convert_to"] = (2, 2), ["convert"] = (3, 3), ["unistr"] = (1, 1), ["sha224"] = (1, 1),
        ["sha256"] = (1, 1), ["sha384"] = (1, 1), ["sha512"] = (1, 1), ["make_date"] = (3, 3), ["make_time"] = (3, 3),
        // ordinary aggregates
        ["sum"] = (1, 1), ["avg"] = (1, 1), ["min"] = (1, 1), ["max"] = (1, 1), ["array_agg"] = (1, 1),
        ["bool_and"] = (1, 1), ["bool_or"] = (1, 1), ["every"] = (1, 1), ["bit_and"] = (1, 1), ["bit_or"] = (1, 1),
        ["stddev"] = (1, 1), ["variance"] = (1, 1), ["var_pop"] = (1, 1), ["var_samp"] = (1, 1),
        ["stddev_pop"] = (1, 1), ["stddev_samp"] = (1, 1), ["json_agg"] = (1, 1), ["jsonb_agg"] = (1, 1),
        ["string_agg"] = (2, 2), ["corr"] = (2, 2), ["covar_pop"] = (2, 2), ["covar_samp"] = (2, 2),
    };
    private static readonly HashSet<string> OrderedSetRequired = new(StringComparer.OrdinalIgnoreCase)
    { "percentile_cont", "percentile_disc", "mode" };
    private static readonly HashSet<string> WithinGroupAllowed = new(StringComparer.OrdinalIgnoreCase)
    { "percentile_cont", "percentile_disc", "mode", "rank", "dense_rank", "percent_rank", "cume_dist" };

    private void CheckFuncCall(FuncCallExpr f)
    {
        if (f.Name.Count != 1) return;                       // only unqualified calls
        var name = f.Name[0];
        if (_catalog.HasFunction(name)) return;              // user-defined function may have a different arity

        if (f.Distinct && f.Args.Any(a => a is StarExpr)) { Report("DISTINCT cannot be used with * in an aggregate"); return; }
        if (f.WithinGroup.Count > 0 && !WithinGroupAllowed.Contains(name)) { Report($"{name.ToLowerInvariant()} is not an ordered-set aggregate and cannot use WITHIN GROUP"); return; }
        if (OrderedSetRequired.Contains(name) && f.WithinGroup.Count == 0) { Report($"{name.ToLowerInvariant()} must be used as an ordered-set aggregate with WITHIN GROUP"); return; }
        if ((name.Equals("percentile_cont", StringComparison.OrdinalIgnoreCase) || name.Equals("percentile_disc", StringComparison.OrdinalIgnoreCase))
            && f.Args.Count == 1 && Fold(f.Args[0]) is { } p && (p < 0 || p > 1))
        { Report("percentile value must be between 0 and 1"); return; }

        if (name.Equals("count", StringComparison.OrdinalIgnoreCase))
        {
            if (!f.Star && f.Args.Count != 1) Report("count requires exactly one argument or *");
            return;
        }

        if (f.Variadic || f.Star) return;                    // variadic / star calls don't have a fixed plain-arg count
        if (!BuiltinArity.TryGetValue(name, out var ar)) return;
        int n = f.Args.Count;
        if (n < ar.Min || n > ar.Max)
            Report($"function {name.ToLowerInvariant()} cannot be called with {n} argument{(n == 1 ? "" : "s")}");
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
