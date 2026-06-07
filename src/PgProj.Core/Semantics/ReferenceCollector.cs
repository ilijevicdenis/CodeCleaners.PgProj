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
        foreach (var stmt in result.Statements) CollectStatement(catalog, stmt, sourceFile);
    }

    private static void CollectStatement(Catalog c, SqlStatement stmt, string? file)
    {
        switch (stmt)
        {
            case CreateViewStatement v:
                CollectQuery(c, ParseBody(v.BodyText), ViewKey(c, v), file);
                break;
            case QueryStatement q:
                CollectQuery(c, q.Query, $"query@{stmt.Position}", file);
                break;
            case InsertStatement ins:
                CollectQuery(c, ins.Source, RelKey(c, ins.Schema, ins.Table), file);
                break;
            case CreateTableAsStatement ctas:
                CollectQuery(c, ctas.Source, RelKey(c, ctas.Schema, ctas.Name), file);
                break;
        }
    }

    private static void CollectQuery(Catalog c, SelectQuery? q, string referencerKey, string? file)
    {
        if (q is null) return;
        foreach (var cte in q.With) CollectQuery(c, cte.Query, referencerKey, file);
        if (q.SetOp is not null) { CollectQuery(c, q.SetOp.Left, referencerKey, file); CollectQuery(c, q.SetOp.Right, referencerKey, file); }
        foreach (var it in q.Items) CollectExpr(c, it.Expr, referencerKey, file);
        CollectExpr(c, q.Where, referencerKey, file);
        if (q.From is null) return;
        foreach (var rel in q.From.Relations)
        {
            CollectTableRef(c, rel, referencerKey, file);
            foreach (var j in rel.Joins) { CollectTableRef(c, j.Right, referencerKey, file); CollectExpr(c, j.On, referencerKey, file); }
        }
    }

    private static void CollectTableRef(Catalog c, TableRef rel, string referencerKey, string? file)
    {
        if (rel.Subquery is not null) { CollectQuery(c, rel.Subquery, referencerKey, file); return; }
        if (rel.TableName is null) return;

        var entry = rel.Schema is not null
            ? c.Symbols.ResolveQualified(rel.Schema, rel.TableName)
            : c.Symbols.ResolveUnqualified(rel.TableName, SymbolKind.Relation, c.SearchPath);
        if (entry is not null)
            c.Symbols.AddReference(entry.Key, new SymbolReference(referencerKey, file ?? "", entry.Kind));
    }

    private static void CollectExpr(Catalog c, Expr? e, string referencerKey, string? file)
    {
        switch (e)
        {
            case null: return;
            case FuncCallExpr f:
                RecordFunctionRef(c, f, referencerKey, file);
                foreach (var a in f.Args) CollectExpr(c, a, referencerKey, file);
                break;
            case BinaryExpr b: CollectExpr(c, b.Left, referencerKey, file); CollectExpr(c, b.Right, referencerKey, file); break;
            case UnaryExpr u: CollectExpr(c, u.Operand, referencerKey, file); break;
            case CastExpr ca: CollectExpr(c, ca.Operand, referencerKey, file); break;
            case SubqueryExpr sq: CollectQuery(c, sq.Query, referencerKey, file); break;
            case ExistsExpr ex: CollectQuery(c, ex.Query, referencerKey, file); break;
        }
    }

    private static void RecordFunctionRef(Catalog c, FuncCallExpr f, string referencerKey, string? file)
    {
        if (f.Name.Count == 0) return;
        // Unqualified vs schema.func — resolve the (single) overload bucket; record against every overload
        // since a bare call site doesn't pin a signature (closure must dirty all overloads of the name).
        string? schema = f.Name.Count >= 2 ? f.Name[^2] : null;
        var name = f.Name[^1];

        IEnumerable<SymbolEntry> overloads = schema is not null
            ? c.Symbols.FunctionOverloads(schema, name)
            : c.SearchPath.Schemas.SelectMany(s => c.Symbols.FunctionOverloads(s, name));

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
