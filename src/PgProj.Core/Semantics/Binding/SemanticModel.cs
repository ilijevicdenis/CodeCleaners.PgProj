using System.Collections.Generic;
using System.Linq;
using PgProj.Core.Syntax;

namespace PgProj.Core.Semantics.Binding;

/// <summary>
/// The query API over the Typed Semantic Model (issue #47, pillar 3). Built from a <see cref="Catalog"/>
/// (its <see cref="SymbolTable"/>) plus the parsed statements of one or more source files, it answers the
/// three questions every IDE/validation/graph feature needs:
/// <list type="bullet">
/// <item><b>by symbol</b> — <see cref="GetSymbol"/> / <see cref="Symbols"/>: look an object up by name/kind;</item>
/// <item><b>by source location</b> — <see cref="SymbolAt"/>: file + character offset → the symbol defined or
///   referenced there (Go To Definition / hover);</item>
/// <item><b>by reference</b> — <see cref="ReferencesTo"/> / <see cref="OccurrencesOf"/>: every site that
///   references a symbol (Find References / Rename).</item>
/// </list>
/// It is a read-only projection: it never mutates the catalog or the syntax AST. Locations come from the
/// bound model (which wraps syntax nodes carrying their <see cref="SqlStatement.Position"/>) and from the
/// individual reference/definition occurrences the binder records as it walks each file.
/// </summary>
public sealed class SemanticModel
{
    private readonly Catalog _catalog;

    /// <summary>Every recorded occurrence (definition or reference) of a symbol, with its source span.</summary>
    private readonly List<SymbolOccurrence> _occurrences = new();

    /// <summary>The bound views built from the input (keyed by view symbol key, lower-cased).</summary>
    private readonly Dictionary<string, BoundView> _views = new(System.StringComparer.OrdinalIgnoreCase);

    private SemanticModel(Catalog catalog) => _catalog = catalog;

    /// <summary>The catalog/symbol-table this model projects.</summary>
    public Catalog Catalog => _catalog;
    public SymbolTable Symbols => _catalog.Symbols;

    /// <summary>The bound views built while constructing this model.</summary>
    public IReadOnlyCollection<BoundView> Views => _views.Values;

    // ---- construction -------------------------------------------------------

    /// <summary>
    /// Build a semantic model over <paramref name="result"/>'s statements, resolved against
    /// <paramref name="catalog"/>. Records a definition occurrence for each created object and a reference
    /// occurrence for each resolved relation/column/function it finds, and binds every CREATE VIEW body so
    /// its columns carry concrete types. <paramref name="file"/> is the project-relative path used to anchor
    /// occurrences for the location query.
    /// </summary>
    public static SemanticModel Build(Catalog catalog, ParseResult result, string file = "")
    {
        var model = new SemanticModel(catalog);
        var binder = new Binder(catalog);
        foreach (var stmt in result.Statements)
            model.IndexStatement(binder, stmt, file, result);
        return model;
    }

    private void IndexStatement(Binder binder, SqlStatement stmt, string file, ParseResult result)
    {
        switch (stmt)
        {
            case CreateViewStatement v:
            {
                var bound = binder.BindView(v);
                if (bound.View is not null)
                {
                    _views[bound.View.Key] = bound;
                    RecordDefinition(bound.View, file, v.Position);
                }
                IndexQueryOccurrences(bound.Body, file, v.Position);
                break;
            }
            case QueryStatement q:
            {
                var bound = binder.BindQuery(q.Query);
                IndexQueryOccurrences(bound, file, q.Position);
                break;
            }
            case CreateTableStatement t:
            {
                var sym = _catalog.Symbols.ResolveQualified(t.Schema ?? _catalog.DefaultSchema, t.Name);
                if (sym is not null) RecordDefinition(sym, file, t.Position);
                break;
            }
            case CreateFunctionStatement f:
            {
                var sig = new FunctionSignature(NormalizeArgs(f.ArgTypes));
                var sym = _catalog.Symbols.ResolveFunction(f.Schema ?? _catalog.DefaultSchema, f.Name, sig);
                if (sym is not null) RecordDefinition(sym, file, f.Position);
                break;
            }
        }
    }

    /// <summary>Walk a bound query and record a reference occurrence for every resolved symbol, anchored at
    /// the enclosing statement offset (the parser records position per statement, not per expression).</summary>
    private void IndexQueryOccurrences(BoundQuery q, string file, int anchor)
    {
        foreach (var s in q.Sources)
            if (s.Symbol is not null && s.Symbol.Kind == SymbolKind.Relation)
                RecordReference(s.Symbol, file, anchor);
        foreach (var item in q.SelectItems) IndexExprOccurrences(item, file, anchor);
    }

    private void IndexExprOccurrences(BoundExpr e, string file, int anchor)
    {
        switch (e)
        {
            case BoundColumnRef { Column: not null } cr:
                RecordReference(cr.Column!, file, anchor);
                break;
            case BoundFuncCall fc:
                if (fc.Function is not null) RecordReference(fc.Function, file, anchor);
                foreach (var a in fc.Args) IndexExprOccurrences(a, file, anchor);
                break;
            case BoundCast c: IndexExprOccurrences(c.Operand, file, anchor); break;
            case BoundBinary b: IndexExprOccurrences(b.Left, file, anchor); IndexExprOccurrences(b.Right, file, anchor); break;
            case BoundExpression x: foreach (var k in x.Children) IndexExprOccurrences(k, file, anchor); break;
        }
    }

    private void RecordDefinition(SymbolEntry sym, string file, int offset) =>
        _occurrences.Add(new SymbolOccurrence(sym, file, offset, OccurrenceKind.Definition));

    private void RecordReference(SymbolEntry sym, string file, int offset) =>
        _occurrences.Add(new SymbolOccurrence(sym, file, offset, OccurrenceKind.Reference));

    // ---- query API: BY SYMBOL ----------------------------------------------

    /// <summary>Resolve a relation/type/view by qualified name (schema defaults to the catalog default).</summary>
    public SymbolEntry? GetSymbol(string schema, string name) => Symbols.ResolveQualified(schema, name);

    /// <summary>Resolve a specific function overload by qualified name + argument signature.</summary>
    public SymbolEntry? GetFunction(string schema, string name, FunctionSignature signature) =>
        Symbols.ResolveFunction(schema, name, signature);

    /// <summary>All symbols in the model (the symbol table's entries).</summary>
    public IReadOnlyCollection<SymbolEntry> AllSymbols => Symbols.Entries;

    /// <summary>The bound view for a view symbol, when one was built.</summary>
    public BoundView? GetBoundView(SymbolEntry view) => _views.TryGetValue(view.Key, out var v) ? v : null;

    // ---- query API: BY SOURCE LOCATION (Go To Definition / hover) -----------

    /// <summary>
    /// The symbol defined or referenced at <paramref name="file"/> + <paramref name="offset"/>. Occurrences
    /// anchor at the enclosing statement's parser offset (<see cref="SqlStatement.Position"/> — a token index;
    /// the parser records position per statement, not per expression). Returns the occurrence whose anchor is
    /// the greatest one not past <paramref name="offset"/> in that file — the symbol "at or before" the cursor —
    /// preferring a definition over a reference at the same anchor. Null when nothing in that file precedes the
    /// offset. The contract is span-ready: when per-expression offsets land on <c>Expr</c>, only the anchor
    /// computation changes, not this signature.
    /// </summary>
    public SymbolEntry? SymbolAt(string file, int offset)
    {
        SymbolOccurrence? best = null;
        foreach (var o in _occurrences)
        {
            if (!string.Equals(o.File, file, System.StringComparison.OrdinalIgnoreCase)) continue;
            if (o.Offset > offset) continue;
            if (best is null
                || o.Offset > best.Offset
                || (o.Offset == best.Offset && o.Kind == OccurrenceKind.Definition && best.Kind != OccurrenceKind.Definition))
                best = o;
        }
        return best?.Symbol;
    }

    /// <summary>Every occurrence (definition + references) recorded for the model, in input order.</summary>
    public IReadOnlyList<SymbolOccurrence> Occurrences => _occurrences;

    // ---- query API: BY REFERENCE (Find References / Rename) -----------------

    /// <summary>
    /// Every reference pointing AT <paramref name="symbol"/> — the symbol table's reverse index
    /// (referencer + file + referent kind). This is the authoritative "who references X" used for
    /// Find References and incremental rebuild closure; populate it via <see cref="ReferenceCollector"/>.
    /// </summary>
    public IReadOnlyList<SymbolReference> ReferencesTo(SymbolEntry symbol) => Symbols.ReferencesTo(symbol);

    /// <summary>
    /// Every source occurrence (definition + each reference site) of <paramref name="symbol"/> with its
    /// file + offset — the span list a Rename rewrites and a Find-References panel lists.
    /// </summary>
    public IReadOnlyList<SymbolOccurrence> OccurrencesOf(SymbolEntry symbol) =>
        _occurrences.Where(o => string.Equals(o.Symbol.Key, symbol.Key, System.StringComparison.OrdinalIgnoreCase)).ToList();

    private static string NormalizeArgs(string argTypes)
    {
        if (string.IsNullOrWhiteSpace(argTypes)) return "";
        return string.Join(",", argTypes.Split(',').Select(a => Model.TypeNormalizer.Normalize(a.Trim())));
    }
}

/// <summary>Whether a <see cref="SymbolOccurrence"/> is where the symbol is defined or a site that references it.</summary>
public enum OccurrenceKind { Definition, Reference }

/// <summary>One source occurrence of a symbol: the symbol, the file, a 0-based char offset, and its kind.</summary>
public sealed record SymbolOccurrence(SymbolEntry Symbol, string File, int Offset, OccurrenceKind Kind);
