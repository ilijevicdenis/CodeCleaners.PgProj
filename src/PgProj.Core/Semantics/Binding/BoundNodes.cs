using System.Collections.Generic;
using PgProj.Core.Syntax;

namespace PgProj.Core.Semantics.Binding;

// ============================================================================
// The Typed Semantic Model (issue #47, Phases 3-4).
//
// A *bound* AST: a parallel tree that WRAPS the syntax AST (PgParser's Expr /
// SelectQuery, never mutated) and adds two things the syntax tree lacks:
//   1. a resolved <see cref="SymbolEntry"/> for every reference (Phase 3 — Bind), and
//   2. a <see cref="ResolvedType"/> on every expression (Phase 4 — Type).
//
// It is purely additive: the syntax nodes are referenced (the `Syntax` back-pointer)
// so callers keep their source spans, while binding/type live on the bound node.
// Find-References, Go-To-Definition, validation (#48) and the dependency graph
// (#50) all read this model instead of re-deriving semantics from strings.
// ============================================================================

/// <summary>
/// A resolved Postgres type carried by a bound expression. Holds the normalized type name (the same
/// canonical spelling <see cref="Model.TypeNormalizer"/> produces, so <c>int</c> and <c>integer</c> are
/// one type) plus the <see cref="SymbolEntry"/> when the type is a user-defined type/domain known to the
/// symbol table. <see cref="Unknown"/> is the conservative bottom — used when a type cannot be inferred
/// statically (it never claims a wrong type, mirroring the analyzer's conservatism).
/// </summary>
public sealed class ResolvedType
{
    /// <summary>The normalized type name (e.g. <c>integer</c>, <c>text</c>, <c>numeric</c>), or <c>?</c> when unknown.</summary>
    public string Name { get; }

    /// <summary>The user-defined type/domain symbol, when the type resolves to one in the symbol table.</summary>
    public SymbolEntry? Symbol { get; }

    /// <summary>True when the type could not be inferred statically (the conservative bottom).</summary>
    public bool IsUnknown => ReferenceEquals(this, Unknown) || Name == "?";

    private ResolvedType(string name, SymbolEntry? symbol = null) { Name = name; Symbol = symbol; }

    /// <summary>The bottom type: "we don't know" (never asserts a concrete, possibly-wrong type).</summary>
    public static readonly ResolvedType Unknown = new("?");
    public static readonly ResolvedType Boolean = new("boolean");
    public static readonly ResolvedType Bigint = new("bigint");
    public static readonly ResolvedType Numeric = new("numeric");
    public static readonly ResolvedType Text = new("text");

    /// <summary>A named type with no symbol handle (a built-in or an unresolved name).</summary>
    public static ResolvedType Of(string name) => string.IsNullOrEmpty(name) ? Unknown : new ResolvedType(name);

    /// <summary>A type backed by a resolved user-defined type/domain symbol.</summary>
    public static ResolvedType OfSymbol(SymbolEntry typeSymbol) => new(typeSymbol.Name, typeSymbol);

    public override string ToString() => Name;
}

/// <summary>Base of every node in the bound model. Each wraps the syntax node it was projected from.</summary>
public abstract class BoundNode
{
    /// <summary>The syntax node this was bound from (null for a synthesized node).</summary>
    public object? Syntax { get; init; }
}

// ---- bound expressions ------------------------------------------------------

/// <summary>A bound expression: it always carries a <see cref="Type"/> (Phase 4), <see cref="ResolvedType.Unknown"/> when not inferable.</summary>
public abstract class BoundExpr : BoundNode
{
    public ResolvedType Type { get; internal set; } = ResolvedType.Unknown;
}

/// <summary>A bound literal (number / string / bool / null). Its type is inferred from the literal kind.</summary>
public sealed class BoundLiteral : BoundExpr
{
    public string LiteralKind { get; init; } = "";
    public string Text { get; init; } = "";
}

/// <summary>
/// A bound column reference: resolves to a concrete (relation, column) and, through the column's
/// declared type, to a <see cref="BoundExpr.Type"/>. <see cref="Column"/> is the column
/// <see cref="SymbolEntry"/> when the reference resolved; <see cref="Relation"/> is the relation it belongs
/// to. Both null when unresolved (an unknown column, an outer-scope ref we don't track) — conservative.
/// </summary>
public sealed class BoundColumnRef : BoundExpr
{
    /// <summary>The dotted parts as written (e.g. <c>["t","a"]</c> or <c>["a"]</c>).</summary>
    public IReadOnlyList<string> Parts { get; init; } = System.Array.Empty<string>();

    /// <summary>The resolved column symbol, or null when the column could not be resolved.</summary>
    public SymbolEntry? Column { get; init; }

    /// <summary>The relation the column belongs to (the FROM-clause source it bound against).</summary>
    public SymbolEntry? Relation { get; init; }

    /// <summary>True when the reference bound to a concrete column.</summary>
    public bool IsResolved => Column is not null;
}

/// <summary>
/// A bound function/procedure call: resolves to a single overload via the symbol table's overload
/// resolution (name + argument-type signature, search_path-aware). <see cref="Function"/> is null when no
/// overload matched. Its <see cref="BoundExpr.Type"/> is the function's resolved return type.
/// </summary>
public sealed class BoundFuncCall : BoundExpr
{
    public IReadOnlyList<string> Name { get; init; } = System.Array.Empty<string>();
    public IReadOnlyList<BoundExpr> Args { get; init; } = System.Array.Empty<BoundExpr>();

    /// <summary>The resolved overload, or null when the call did not resolve to a known function.</summary>
    public SymbolEntry? Function { get; init; }

    /// <summary>The signature the call was resolved with (the argument types, normalized).</summary>
    public FunctionSignature Signature { get; init; }

    public bool IsResolved => Function is not null;
}

/// <summary>A bound CAST: the target type is resolved (and linked to a type symbol when user-defined).</summary>
public sealed class BoundCast : BoundExpr
{
    public BoundExpr Operand { get; init; } = null!;
    public string TargetTypeText { get; init; } = "";
}

/// <summary>A bound binary/comparison expression. Its type is the result type (boolean for comparisons).</summary>
public sealed class BoundBinary : BoundExpr
{
    public string Op { get; init; } = "";
    public BoundExpr Left { get; init; } = null!;
    public BoundExpr Right { get; init; } = null!;
}

/// <summary>Any bound expression the binder does not model finely; children (if any) are still bound.</summary>
public sealed class BoundExpression : BoundExpr
{
    public IReadOnlyList<BoundExpr> Children { get; init; } = System.Array.Empty<BoundExpr>();
}

// ---- bound query / relation shape -------------------------------------------

/// <summary>
/// One source in scope for a query: a relation (the resolved <see cref="Symbol"/>), the alias it is
/// visible under (or its name), and its known columns. Subqueries/CTEs/functions-in-FROM appear with a
/// null <see cref="Symbol"/> but a <see cref="Name"/> and (when derivable) a column list.
/// </summary>
public sealed class BoundRangeVar
{
    public string Name { get; init; } = "";                 // alias if present, else relation name
    public SymbolEntry? Symbol { get; init; }               // the relation symbol, when it resolved
    public IReadOnlyList<BoundResultColumn> Columns { get; init; } = System.Array.Empty<BoundResultColumn>();
}

/// <summary>One output column of a query/view/CTE: a name and a resolved type (the inferred column list).</summary>
public sealed class BoundResultColumn
{
    public string Name { get; init; } = "";
    public ResolvedType Type { get; init; } = ResolvedType.Unknown;

    /// <summary>The column symbol this output column projects, when it is a plain column reference.</summary>
    public SymbolEntry? Source { get; init; }
}

/// <summary>
/// A bound query: its in-scope sources (<see cref="Sources"/>), its bound SELECT-item expressions, and the
/// inferred output <see cref="Columns"/> (the column list Phase 4 derives for a view/CTE/subquery).
/// </summary>
public sealed class BoundQuery : BoundNode
{
    public IReadOnlyList<BoundRangeVar> Sources { get; init; } = System.Array.Empty<BoundRangeVar>();
    public IReadOnlyList<BoundExpr> SelectItems { get; init; } = System.Array.Empty<BoundExpr>();
    public IReadOnlyList<BoundResultColumn> Columns { get; init; } = System.Array.Empty<BoundResultColumn>();
}

/// <summary>
/// The bound form of a CREATE VIEW: the defining relation symbol, the bound body query, and the view's
/// inferred column list (each output column resolved to a concrete type, the acceptance criterion).
/// </summary>
public sealed class BoundView : BoundNode
{
    public SymbolEntry? View { get; init; }
    public BoundQuery Body { get; init; } = null!;
    public IReadOnlyList<BoundResultColumn> Columns => Body.Columns;
}
