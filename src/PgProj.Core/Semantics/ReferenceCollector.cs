using System.Collections.Generic;
using System.Linq;
using PgProj.Core.Syntax;

namespace PgProj.Core.Semantics;

/// <summary>
/// Populates a <see cref="Catalog"/>'s reverse index: for each referencing object (a view, a query, a
/// function body) it resolves the relations/functions the object reads against the catalog's
/// <see cref="SymbolTable"/> and records a <see cref="SymbolReference"/> pointing AT each resolved symbol.
/// That makes <see cref="SymbolTable.ReferencesTo(SymbolEntry)"/> answer "who references X" — the basis for
/// Find References (Phase 3) and incremental rebuild closure (Phase 6: when X changes, every referencer is
/// dirty). Resolution is search_path-aware and symmetric with forward lookup (it uses the same table).
/// </summary>
public static class ReferenceCollector
{
    /// <summary>Record every reference in <paramref name="result"/> into <paramref name="catalog"/>'s reverse index.</summary>
    public static void Collect(Catalog catalog, ParseResult result, string? sourceFile = null)
    {
        // The active search_path resolves unqualified names; a `SET search_path = …` statement changes it
        // for every statement that FOLLOWS (#164). Start from the catalog default and track it across the
        // script — an empty list (SET search_path = DEFAULT) reverts to the default.
        var active = catalog.SearchPath;
        foreach (var stmt in result.Statements)
        {
            if (stmt is CommandStatement { Kind: "SET", SearchPath: { } sp })
            {
                active = sp.Count == 0 ? catalog.SearchPath : new SearchPath(sp, catalog.DefaultSchema);
                continue;
            }
            CollectStatement(catalog, stmt, sourceFile, active);
        }
    }

    private static void CollectStatement(Catalog c, SqlStatement stmt, string? file, SearchPath path)
    {
        switch (stmt)
        {
            case CreateViewStatement v:
                CollectQuery(c, ParseBody(v.BodyText), ViewKey(c, v), file, path);
                break;
            case QueryStatement q:
                CollectQuery(c, q.Query, $"query@{stmt.Position}", file, path);
                break;
            case InsertStatement ins:
                CollectQuery(c, ins.Source, RelKey(c, ins.Schema, ins.Table), file, path);
                break;
            case CreateTableAsStatement ctas:
                CollectQuery(c, ctas.Source, RelKey(c, ctas.Schema, ctas.Name), file, path);
                break;
        }
    }

    private static void CollectQuery(Catalog c, SelectQuery? q, string referencerKey, string? file, SearchPath path)
    {
        if (q is null) return;
        foreach (var cte in q.With) CollectQuery(c, cte.Query, referencerKey, file, path);
        if (q.SetOp is not null) { CollectQuery(c, q.SetOp.Left, referencerKey, file, path); CollectQuery(c, q.SetOp.Right, referencerKey, file, path); }
        foreach (var it in q.Items) CollectExpr(c, it.Expr, referencerKey, file, path);
        CollectExpr(c, q.Where, referencerKey, file, path);
        if (q.From is null) return;
        foreach (var rel in q.From.Relations)
        {
            CollectTableRef(c, rel, referencerKey, file, path);
            foreach (var j in rel.Joins) { CollectTableRef(c, j.Right, referencerKey, file, path); CollectExpr(c, j.On, referencerKey, file, path); }
        }
    }

    private static void CollectTableRef(Catalog c, TableRef rel, string referencerKey, string? file, SearchPath path)
    {
        if (rel.Subquery is not null) { CollectQuery(c, rel.Subquery, referencerKey, file, path); return; }
        if (rel.TableName is null) return;

        var entry = rel.Schema is not null
            ? c.Symbols.ResolveQualified(rel.Schema, rel.TableName)
            : c.Symbols.ResolveUnqualified(rel.TableName, SymbolKind.Relation, path);
        if (entry is not null)
            c.Symbols.AddReference(entry.Key, new SymbolReference(referencerKey, file ?? "", entry.Kind));
    }

    private static void CollectExpr(Catalog c, Expr? e, string referencerKey, string? file, SearchPath path)
    {
        switch (e)
        {
            case null: return;
            case FuncCallExpr f:
                RecordFunctionRef(c, f, referencerKey, file, path);
                foreach (var a in f.Args) CollectExpr(c, a, referencerKey, file, path);
                break;
            case BinaryExpr b: CollectExpr(c, b.Left, referencerKey, file, path); CollectExpr(c, b.Right, referencerKey, file, path); break;
            case UnaryExpr u: CollectExpr(c, u.Operand, referencerKey, file, path); break;
            case CastExpr ca: CollectExpr(c, ca.Operand, referencerKey, file, path); break;
            case SubqueryExpr sq: CollectQuery(c, sq.Query, referencerKey, file, path); break;
            case ExistsExpr ex: CollectQuery(c, ex.Query, referencerKey, file, path); break;
        }
    }

    private static void RecordFunctionRef(Catalog c, FuncCallExpr f, string referencerKey, string? file, SearchPath path)
    {
        if (f.Name.Count == 0) return;
        // Unqualified vs schema.func — resolve the (single) overload bucket; record against every overload
        // since a bare call site doesn't pin a signature (closure must dirty all overloads of the name).
        string? schema = f.Name.Count >= 2 ? f.Name[^2] : null;
        var name = f.Name[^1];

        IEnumerable<SymbolEntry> overloads = schema is not null
            ? c.Symbols.FunctionOverloads(schema, name)
            : path.Schemas.SelectMany(s => c.Symbols.FunctionOverloads(s, name));

        foreach (var entry in overloads.Distinct())
            c.Symbols.AddReference(entry.Key, new SymbolReference(referencerKey, file ?? "", entry.Kind));
    }

    private static string ViewKey(Catalog c, CreateViewStatement v) => $"{v.Schema ?? c.DefaultSchema}.{v.Name}".ToLowerInvariant();
    private static string RelKey(Catalog c, string? schema, string name) => $"{schema ?? c.DefaultSchema}.{name}".ToLowerInvariant();

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
}
