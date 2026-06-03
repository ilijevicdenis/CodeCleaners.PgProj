using System.Collections.Generic;

namespace PgProj.Core.Syntax;

// Real expression AST for PgParser. Kept lightweight but typed — enough to inspect structure and
// to drive accept/reject decisions; expression bodies are not lowered to semantics.

public abstract class Expr { }

public sealed class LiteralExpr : Expr { public string Kind { get; init; } = ""; public string Text { get; init; } = ""; }   // number/string/bool/null/typed
public sealed class StarExpr : Expr { public List<string> Qualifier { get; } = new(); }                                       // *  or  t.*
public sealed class ColumnRef : Expr { public List<string> Parts { get; } = new(); }                                          // a / t.a / s.t.a
public sealed class ParamExpr : Expr { public string Text { get; init; } = ""; }                                              // $1
public sealed class UnaryExpr : Expr { public string Op { get; init; } = ""; public Expr Operand { get; init; } = null!; }
public sealed class BinaryExpr : Expr { public string Op { get; init; } = ""; public Expr Left { get; init; } = null!; public Expr Right { get; init; } = null!; }
public sealed class PostfixExpr : Expr { public string Op { get; init; } = ""; public Expr Operand { get; init; } = null!; }  // ISNULL / NOTNULL
public sealed class CastExpr : Expr { public Expr Operand { get; init; } = null!; public string TypeText { get; init; } = ""; }
public sealed class CollateExpr : Expr { public Expr Operand { get; init; } = null!; public string Collation { get; init; } = ""; }
public sealed class SubscriptExpr : Expr { public Expr Operand { get; init; } = null!; public string IndexText { get; init; } = ""; }
public sealed class FieldAccessExpr : Expr { public Expr Operand { get; init; } = null!; public string Field { get; init; } = ""; }  // (composite).field / (composite).*
public sealed class RowExpr : Expr { public List<Expr> Items { get; } = new(); public bool ExplicitRow { get; init; } }
public sealed class ArrayExpr : Expr { public List<Expr> Elements { get; } = new(); public SelectQuery? Subquery { get; init; } }
public sealed class SubqueryExpr : Expr { public SelectQuery Query { get; init; } = null!; }
public sealed class ExistsExpr : Expr { public SelectQuery Query { get; init; } = null!; }

public sealed class CaseExpr : Expr
{
    public Expr? Operand { get; init; }                       // simple CASE x WHEN …
    public List<(Expr When, Expr Then)> Branches { get; } = new();
    public Expr? Else { get; set; }
}

public sealed class FuncCallExpr : Expr
{
    public List<string> Name { get; } = new();
    public List<Expr> Args { get; } = new();
    public bool Distinct { get; set; }
    public bool Star { get; set; }                            // count(*)
    public bool Variadic { get; set; }
    public List<OrderByItem> OrderBy { get; } = new();        // agg ORDER BY
    public List<OrderByItem> WithinGroup { get; } = new();    // ordered-set agg
    public Expr? Filter { get; set; }                         // FILTER (WHERE …)
    public WindowSpec? Over { get; set; }                     // OVER (…) or OVER name
}

public sealed class BetweenExpr : Expr
{
    public Expr Operand { get; init; } = null!;
    public Expr Low { get; init; } = null!;
    public Expr High { get; init; } = null!;
    public bool Not { get; init; }
    public bool Symmetric { get; init; }
}

public sealed class InExpr : Expr
{
    public Expr Operand { get; init; } = null!;
    public bool Not { get; init; }
    public List<Expr>? List { get; init; }
    public SelectQuery? Subquery { get; init; }
}

/// <summary>op ANY/ALL/SOME (array-or-subquery)</summary>
public sealed class QuantifiedExpr : Expr
{
    public Expr Left { get; init; } = null!;
    public string Op { get; init; } = "";
    public string Quantifier { get; init; } = "";            // ANY / ALL / SOME
    public Expr? Array { get; init; }
    public SelectQuery? Subquery { get; init; }
}

public sealed class PatternMatchExpr : Expr                   // [NOT] LIKE/ILIKE/SIMILAR TO … [ESCAPE …]
{
    public Expr Operand { get; init; } = null!;
    public string Kind { get; init; } = "";                  // LIKE / ILIKE / SIMILAR TO
    public bool Not { get; init; }
    public Expr Pattern { get; init; } = null!;
    public Expr? Escape { get; set; }
}

public sealed class IsCheckExpr : Expr                        // IS [NOT] NULL/TRUE/FALSE/UNKNOWN/DISTINCT FROM/DOCUMENT/JSON
{
    public Expr Operand { get; init; } = null!;
    public bool Not { get; init; }
    public string What { get; init; } = "";                  // NULL / TRUE / FALSE / UNKNOWN / DOCUMENT / JSON …
    public Expr? Other { get; init; }                        // for IS [NOT] DISTINCT FROM other
}
