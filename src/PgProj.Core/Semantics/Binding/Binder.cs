using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PgProj.Core.Model;
using PgProj.Core.Syntax;

namespace PgProj.Core.Semantics.Binding;

/// <summary>
/// Phase 3 (Bind) + Phase 4 (Type): turns a parsed query/view into the Typed Semantic Model
/// (<see cref="BoundNode"/> tree). Every reference is resolved against the <see cref="Catalog"/>'s
/// <see cref="SymbolTable"/> — relations/columns/functions/types — using its search_path and overload
/// resolution; every bound expression is given a <see cref="ResolvedType"/>, with view/CTE column lists
/// inferred and column types propagated up the tree.
///
/// <para>It is ADDITIVE and CONSERVATIVE: it never mutates the syntax AST and it leaves a reference
/// unresolved / a type <see cref="ResolvedType.Unknown"/> rather than guess — so it cannot turn valid SQL
/// into a spurious binding the way a wrong guess would. <see cref="SemanticAnalyzer"/>'s diagnostics are
/// untouched; this is a new capability layered on the same symbol table.</para>
/// </summary>
public sealed class Binder
{
    private readonly Catalog _catalog;
    private readonly SymbolTable _symbols;

    public Binder(Catalog catalog) { _catalog = catalog; _symbols = catalog.Symbols; }

    // ---- entry points -------------------------------------------------------

    /// <summary>Bind a CREATE VIEW: resolve the view symbol, bind its body, infer its column list + types.</summary>
    public BoundView BindView(CreateViewStatement view)
    {
        var schema = view.Schema ?? _catalog.DefaultSchema;
        var sym = _symbols.ResolveQualified(schema, view.Name);
        var body = ParseBody(view.BodyText);
        var bound = body is not null ? BindQuery(body) : new BoundQuery();
        return new BoundView { Syntax = view, View = sym, Body = bound };
    }

    /// <summary>Bind a SELECT (or VALUES/TABLE/WITH) query into a <see cref="BoundQuery"/> with an inferred column list.</summary>
    public BoundQuery BindQuery(SelectQuery query)
    {
        // CTEs first: they become named, column-typed sources visible to the main query.
        var ctes = new Dictionary<string, BoundRangeVar>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var cte in query.With)
        {
            var cteBound = BindQuery(cte.Query);
            var cols = cteBound.Columns;
            // explicit CTE column-rename list overrides inferred names (types stay inferred positionally)
            if (cte.Columns.Count > 0)
                cols = cte.Columns.Select((n, i) => new BoundResultColumn
                {
                    Name = n,
                    Type = i < cteBound.Columns.Count ? cteBound.Columns[i].Type : ResolvedType.Unknown,
                    Source = i < cteBound.Columns.Count ? cteBound.Columns[i].Source : null,
                }).ToList();
            ctes[cte.Name] = new BoundRangeVar { Name = cte.Name, Columns = cols };
        }

        // A set-operation query (UNION/…) takes its column shape from the left arm.
        if (query.SetOp is not null)
        {
            var left = BindQuery(query.SetOp.Left);
            BindQuery(query.SetOp.Right); // bind for completeness (references), shape comes from the left
            return new BoundQuery { Syntax = query, Sources = left.Sources, SelectItems = left.SelectItems, Columns = left.Columns };
        }

        var sources = BuildScope(query, ctes);
        var scope = new Scope(sources);

        var items = new List<BoundExpr>();
        var outCols = new List<BoundResultColumn>();
        foreach (var it in query.Items)
        {
            // SELECT * / t.* : expand to every column of the in-scope source(s).
            if (it.Expr is StarExpr star)
            {
                foreach (var rc in ExpandStar(star, sources)) outCols.Add(rc);
                continue;
            }
            var be = BindExpr(it.Expr, scope);
            items.Add(be);
            outCols.Add(new BoundResultColumn
            {
                Name = it.Alias ?? ColumnName(it.Expr, be),
                Type = be.Type,
                Source = (be as BoundColumnRef)?.Column,
            });
        }

        // VALUES query: shape is the first row's expression count (types best-effort).
        if (query.IsValues && query.ValuesRows.Count > 0)
            for (int i = 0; i < query.ValuesRows[0].Count; i++)
                outCols.Add(new BoundResultColumn { Name = $"column{i + 1}", Type = BindExpr(query.ValuesRows[0][i], scope).Type });

        return new BoundQuery { Syntax = query, Sources = sources, SelectItems = items, Columns = outCols };
    }

    // ---- scope construction (the relations a query can see) ------------------

    private List<BoundRangeVar> BuildScope(SelectQuery query, Dictionary<string, BoundRangeVar> ctes)
    {
        var sources = new List<BoundRangeVar>();
        if (query.From is null) return sources;
        foreach (var rel in query.From.Relations)
        {
            AddRangeVar(rel, sources, ctes);
            foreach (var j in rel.Joins) AddRangeVar(j.Right, sources, ctes);
        }
        return sources;
    }

    private void AddRangeVar(TableRef rel, List<BoundRangeVar> sources, Dictionary<string, BoundRangeVar> ctes)
    {
        if (rel.Subquery is not null)
        {
            var sub = BindQuery(rel.Subquery);
            sources.Add(new BoundRangeVar { Name = rel.Alias ?? "", Columns = sub.Columns });
            return;
        }
        if (rel.TableName is null) return;

        // A bare name may be a CTE in scope before it is a base relation.
        if (rel.Schema is null && ctes.TryGetValue(rel.TableName, out var cte))
        {
            sources.Add(new BoundRangeVar { Name = rel.Alias ?? cte.Name, Symbol = cte.Symbol, Columns = cte.Columns });
            return;
        }

        var entry = rel.Schema is not null
            ? _symbols.ResolveQualified(rel.Schema, rel.TableName)
            : _symbols.ResolveUnqualified(rel.TableName, SymbolKind.Relation, _catalog.SearchPath);

        var cols = entry is not null ? ColumnsOf(entry) : System.Array.Empty<BoundResultColumn>();
        sources.Add(new BoundRangeVar { Name = rel.Alias ?? rel.TableName, Symbol = entry, Columns = cols });
    }

    /// <summary>The column list of a resolved relation symbol, each with its declared/normalized type.</summary>
    private IReadOnlyList<BoundResultColumn> ColumnsOf(SymbolEntry relation)
    {
        var cols = _catalog.ColumnsWithTypes(relation.Schema, relation.Name);
        if (cols is null) return System.Array.Empty<BoundResultColumn>();
        var list = new List<BoundResultColumn>(cols.Count);
        foreach (var c in cols)
        {
            var colSym = _symbols.Entries.FirstOrDefault(e =>
                e.Kind == SymbolKind.Column && e.Fqn.Equals($"{relation.Schema}.{relation.Name}.{c.Name}", System.StringComparison.OrdinalIgnoreCase));
            list.Add(new BoundResultColumn { Name = c.Name, Type = TypeFor(c.Type), Source = colSym });
        }
        return list;
    }

    private IEnumerable<BoundResultColumn> ExpandStar(StarExpr star, List<BoundRangeVar> sources)
    {
        if (star.Qualifier is { Count: > 0 } q)
        {
            var name = q[^1];
            var rv = sources.FirstOrDefault(s => s.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase));
            return rv?.Columns ?? Enumerable.Empty<BoundResultColumn>();
        }
        return sources.SelectMany(s => s.Columns);
    }

    // ---- expression binding + typing ----------------------------------------

    private BoundExpr BindExpr(Expr? e, Scope scope)
    {
        switch (e)
        {
            case null:
                return new BoundExpression { Type = ResolvedType.Unknown };

            case LiteralExpr lit:
                return new BoundLiteral { Syntax = lit, LiteralKind = lit.Kind, Text = lit.Text, Type = LiteralType(lit) };

            case ColumnRef col:
                return BindColumnRef(col, scope);

            case FuncCallExpr f:
                return BindFuncCall(f, scope);

            case CastExpr c:
            {
                var operand = BindExpr(c.Operand, scope);
                return new BoundCast { Syntax = c, Operand = operand, TargetTypeText = c.TypeText, Type = ResolveTypeName(c.TypeText) };
            }

            case BinaryExpr b:
            {
                var l = BindExpr(b.Left, scope);
                var r = BindExpr(b.Right, scope);
                return new BoundBinary { Syntax = b, Op = b.Op, Left = l, Right = r, Type = BinaryResultType(b.Op, l, r) };
            }

            case UnaryExpr u:
            {
                var inner = BindExpr(u.Operand, scope);
                return new BoundExpression { Syntax = u, Children = new[] { inner }, Type = inner.Type };
            }

            case ParamExpr:
                return new BoundExpression { Syntax = e, Type = ResolvedType.Unknown };

            default:
            {
                // Anything else: bind children (so references inside still resolve) but leave the type unknown.
                var kids = ChildExprs(e).Select(k => BindExpr(k, scope)).ToList();
                return new BoundExpression { Syntax = e, Children = kids, Type = ResolvedType.Unknown };
            }
        }
    }

    private BoundColumnRef BindColumnRef(ColumnRef col, Scope scope)
    {
        var parts = col.NameParts;
        if (parts.Count == 0)
            return new BoundColumnRef { Syntax = col, Parts = parts, Type = ResolvedType.Unknown };

        string colName = parts[^1];
        string? qualifier = parts.Count >= 2 ? parts[^2] : null;

        var (relation, column) = scope.Resolve(qualifier, colName);
        return new BoundColumnRef
        {
            Syntax = col,
            Parts = parts,
            Relation = relation,
            Column = column,
            Type = column?.ColumnType is { } t ? TypeFor(t) : ResolvedType.Unknown,
        };
    }

    private BoundFuncCall BindFuncCall(FuncCallExpr f, Scope scope)
    {
        var args = f.Args.Select(a => BindExpr(a, scope)).ToList();
        string? schema = f.Name.Count >= 2 ? f.Name[^2] : null;
        var name = f.Name.Count > 0 ? f.Name[^1] : "";

        // Build the call's argument-type signature from the bound (typed) arguments.
        var sig = new FunctionSignature(string.Join(",", args.Select(a => a.Type.IsUnknown ? "" : a.Type.Name)));

        // Overload resolution: try the exact inferred signature first (search_path-aware), then fall back to
        // the sole overload when there is exactly one (a bare call whose arg types we couldn't fully infer).
        SymbolEntry? fn = schema is not null
            ? _symbols.ResolveFunction(schema, name, sig)
            : _symbols.ResolveUnqualifiedFunction(name, sig, _catalog.SearchPath);

        if (fn is null)
        {
            var overloads = (schema is not null
                ? _symbols.FunctionOverloads(schema, name)
                : _catalog.SearchPath.Schemas.SelectMany(s => _symbols.FunctionOverloads(s, name))).Distinct().ToList();
            if (overloads.Count == 1) { fn = overloads[0]; sig = fn.Signature ?? sig; }
        }

        return new BoundFuncCall
        {
            Syntax = f,
            Name = f.Name.ToList(),
            Args = args,
            Function = fn,
            Signature = fn?.Signature ?? sig,
            Type = FunctionReturnType(fn),
        };
    }

    // ---- type resolution helpers --------------------------------------------

    /// <summary>The resolved type for a normalized type name, linking to a user-defined type symbol when known.</summary>
    private ResolvedType TypeFor(string? normalizedType)
    {
        if (string.IsNullOrEmpty(normalizedType)) return ResolvedType.Unknown;
        var baseName = BaseTypeName(normalizedType!);
        var sym = _symbols.ResolveUnqualified(baseName, SymbolKind.Type, _catalog.SearchPath);
        return sym is not null ? ResolvedType.OfSymbol(sym) : ResolvedType.Of(normalizedType!);
    }

    private ResolvedType ResolveTypeName(string typeText)
    {
        var normalized = TypeNormalizer.Normalize(typeText);
        return TypeFor(normalized);
    }

    /// <summary>
    /// A function's return type. The symbol table does not yet store return types (Phase 2 stored only the
    /// arg-signature overload key), so when the body is a single-relation SELECT we cannot recover it here —
    /// we return Unknown rather than guess. The hook is centralized so #48/#50 can enrich it once return
    /// types land on <see cref="SymbolEntry"/>.
    /// </summary>
    private static ResolvedType FunctionReturnType(SymbolEntry? fn) => ResolvedType.Unknown;

    private static ResolvedType LiteralType(LiteralExpr lit) => lit.Kind switch
    {
        "number" => lit.Text.Contains('.') || lit.Text.Contains('e') || lit.Text.Contains('E')
            ? ResolvedType.Numeric
            : ResolvedType.Bigint,
        "string" => ResolvedType.Text,
        "bool" => ResolvedType.Boolean,
        _ => ResolvedType.Unknown,
    };

    private static ResolvedType BinaryResultType(string op, BoundExpr l, BoundExpr r)
    {
        // Comparison / logical operators yield boolean; arithmetic propagates the operand type when they agree.
        switch (op)
        {
            case "=" or "<>" or "!=" or "<" or ">" or "<=" or ">=" or "AND" or "OR" or "IS" or "LIKE" or "ILIKE":
                return ResolvedType.Boolean;
            case "||":
                return ResolvedType.Text;
            case "+" or "-" or "*" or "/" or "%" or "^":
                if (!l.Type.IsUnknown && l.Type.Name == r.Type.Name) return l.Type;
                if (l.Type.IsUnknown) return r.Type;
                if (r.Type.IsUnknown) return l.Type;
                return ResolvedType.Numeric;
            default:
                return ResolvedType.Unknown;
        }
    }

    private static string BaseTypeName(string normalized)
    {
        var s = normalized;
        var paren = s.IndexOf('(');
        if (paren >= 0) s = s[..paren];
        while (s.EndsWith("[]", System.StringComparison.Ordinal)) s = s[..^2];
        return s.Trim();
    }

    private static string ColumnName(Expr e, BoundExpr bound) => e switch
    {
        ColumnRef c => c.NameParts.Count > 0 ? c.NameParts[^1] : "?column?",
        _ => "?column?",
    };

    // ---- syntax-tree traversal (children of an arbitrary Expr) ---------------

    private static IEnumerable<Expr> ChildExprs(Expr e) => e switch
    {
        PostfixExpr p => new[] { p.Operand },
        CollateExpr cl => new[] { cl.Operand },
        SubscriptExpr ss => new[] { ss.Operand },
        FieldAccessExpr fa => new[] { fa.Operand },
        CaseExpr cs => (cs.Operand is null ? Enumerable.Empty<Expr>() : new[] { cs.Operand })
            .Concat(cs.Branches.SelectMany(b => new[] { b.When, b.Then }))
            .Concat(cs.Else is null ? Enumerable.Empty<Expr>() : new[] { cs.Else }),
        BetweenExpr bt => new[] { bt.Operand, bt.Low, bt.High },
        InExpr inx => new[] { inx.Operand }.Concat(inx.List ?? Enumerable.Empty<Expr>()),
        IsCheckExpr isc => isc.Other is null ? new[] { isc.Operand } : new[] { isc.Operand, isc.Other },
        PatternMatchExpr pm => new[] { pm.Operand, pm.Pattern },
        QuantifiedExpr qx => qx.Array is null ? new[] { qx.Left } : new[] { qx.Left, qx.Array },
        RowExpr r => r.Items,
        ArrayExpr ar => ar.Elements,
        _ => Enumerable.Empty<Expr>(),
    };

    private static SelectQuery? ParseBody(string bodyText)
    {
        try
        {
            var parsed = new PgParser().Parse(bodyText);
            if (parsed.Diagnostics.Count != 0) return null;
            return parsed.Statements.OfType<QueryStatement>().FirstOrDefault()?.Query;
        }
        catch { return null; }
    }

    /// <summary>The set of relations visible to an expression and how an unqualified/qualified column resolves.</summary>
    private sealed class Scope
    {
        private readonly List<BoundRangeVar> _sources;
        public Scope(List<BoundRangeVar> sources) => _sources = sources;

        /// <summary>Resolve a (qualifier, column) pair to its (relation, column) symbols against the in-scope sources.</summary>
        public (SymbolEntry? Relation, SymbolEntry? Column) Resolve(string? qualifier, string column)
        {
            if (qualifier is not null)
            {
                var rv = _sources.FirstOrDefault(s => s.Name.Equals(qualifier, System.StringComparison.OrdinalIgnoreCase))
                         ?? _sources.FirstOrDefault(s => s.Symbol?.Name.Equals(qualifier, System.StringComparison.OrdinalIgnoreCase) == true);
                var c = rv?.Columns.FirstOrDefault(x => x.Name.Equals(column, System.StringComparison.OrdinalIgnoreCase));
                return (rv?.Symbol, c?.Source);
            }

            // Unqualified: the column must be unambiguous across the in-scope sources.
            BoundRangeVar? hitRv = null; BoundResultColumn? hit = null; int matches = 0;
            foreach (var s in _sources)
            {
                var c = s.Columns.FirstOrDefault(x => x.Name.Equals(column, System.StringComparison.OrdinalIgnoreCase));
                if (c is not null) { matches++; hitRv = s; hit = c; }
            }
            if (matches != 1) return (matches > 1 ? null : null, null); // ambiguous or absent → unresolved (conservative)
            return (hitRv!.Symbol, hit!.Source);
        }
    }
}
