using System;
using System.Collections.Generic;
using System.Linq;
using PgProj.Core.Syntax;

namespace PgProj.Core.Semantics.Dependencies;

/// <summary>
/// Builds a <see cref="DependencyGraph"/> from the bound model — a <see cref="SymbolTable"/> whose reverse
/// index was populated by <see cref="ReferenceCollector"/>. Every node is a real
/// <see cref="SymbolEntry"/>; every edge is derived from a <em>resolved</em> reference (never from a text
/// scan), so the graph is exactly as accurate as binding is.
///
/// <para><b>Edge derivation.</b> The symbol table's reverse index answers "who references X" as
/// <see cref="SymbolReference"/> records ( <see cref="SymbolReference.ReferencerKey"/> = the dependent's
/// key, stored under the referent's key). We invert that into directed edges dependent → referent. The
/// <see cref="SymbolReference.ReferentKind"/> drives classification:</para>
/// <list type="bullet">
///   <item><b>Hard</b> — a view/query reading a <see cref="SymbolKind.Relation"/> (the table must exist
///     first), or calling a <see cref="SymbolKind.Function"/> from a SQL body that resolves at create time,
///     or a column/argument typed as a user <see cref="SymbolKind.Type"/>. Postgres enforces this ordering.</item>
///   <item><b>Runtime</b> — a reference discovered inside dynamic SQL in a function body (an
///     <c>EXECUTE 'string'</c>): surfaced via <see cref="AddRuntimeEdges"/>, never used for ordering.</item>
/// </list>
/// Soft edges are not produced by reference inversion (references are real ordering needs); callers add
/// them explicitly (e.g. comment-after-parent) via <see cref="DependencyGraph.AddEdge"/>.
/// </summary>
public static class DependencyGraphBuilder
{
    /// <summary>
    /// Build the graph from <paramref name="symbols"/>. Nodes = every schema object the table knows
    /// (schemas/columns are skipped — they are not deploy-ordered units); edges = inverted references.
    /// </summary>
    public static DependencyGraph Build(SymbolTable symbols)
    {
        var graph = new DependencyGraph();

        // 1. Nodes: the deploy-ordered object kinds (relations, types, functions). Columns and schemas
        //    are members/containers, not independently deployed objects, so they are not graph nodes.
        foreach (var entry in symbols.Entries)
            if (entry.Kind is SymbolKind.Relation or SymbolKind.Type or SymbolKind.Function)
                graph.AddNode(entry);

        // 2. Edges: invert the reverse index. For each referent X and each reference pointing at it, the
        //    referencer depends on X. The referent's kind classifies the edge (relation/type/function are
        //    all create-time-enforced ⇒ Hard).
        foreach (var referent in symbols.Entries)
        {
            if (referent.Kind is SymbolKind.Schema or SymbolKind.Column) continue;
            foreach (var reference in symbols.ReferencesTo(referent))
            {
                // The referencer key is a symbol key (e.g. "app.v1") or a synthetic site ("query@N").
                // Only emit an edge when both ends are graph nodes — a free-floating query is not a
                // deploy-ordered object, so it has no node and contributes no ordering edge.
                if (!graph.HasNode(reference.ReferencerKey)) continue;
                graph.AddEdge(reference.ReferencerKey, referent.Key, DependencyKind.Hard,
                    EdgeReason(reference.ReferentKind));
            }
        }

        return graph;
    }

    /// <summary>
    /// Augment <paramref name="graph"/> with the <see cref="DependencyKind.Runtime"/> edges implied by the
    /// dynamic SQL inside each function body in <paramref name="result"/>. We scan a function body for
    /// <c>EXECUTE 'literal'</c> statements, parse the embedded SQL, resolve the relations/functions it names
    /// against <paramref name="symbols"/>, and add a Runtime edge from the function to each resolved object.
    /// These are surfaced (for visualization / impact analysis) but excluded from ordering and cycle checks.
    /// </summary>
    public static void AddRuntimeEdges(DependencyGraph graph, SymbolTable symbols, ParseResult result,
        SearchPath searchPath, string defaultSchema)
    {
        foreach (var stmt in result.Statements)
        {
            if (stmt is not CreateFunctionStatement fn || fn.Body is null) continue;
            var fnSchema = fn.Schema ?? defaultSchema;

            // A function's node key carries its overload signature ("app.f(integer)"); resolve the actual
            // entry by schema+name+normalized args so we attach edges to the right overload node.
            var sig = new FunctionSignature(NormalizeArgTypes(fn.ArgTypes));
            var entry = symbols.ResolveFunction(fnSchema, fn.Name, sig);
            if (entry is null || !graph.HasNode(entry.Key)) continue;

            foreach (var dynSql in ExtractDynamicSql(fn.Body))
                AddRuntimeEdgesFromSql(graph, symbols, searchPath, entry.Key, dynSql);
        }
    }

    private static void AddRuntimeEdgesFromSql(DependencyGraph graph, SymbolTable symbols, SearchPath path,
        string fnKey, string sql)
    {
        ParseResult parsed;
        try { parsed = new PgParser().Parse(sql); }
        catch { return; }
        if (parsed.Diagnostics.Count != 0) return;

        foreach (var q in parsed.Statements.OfType<QueryStatement>().Select(s => s.Query))
            foreach (var key in ResolveQueryRelations(symbols, path, q))
                if (graph.HasNode(key))
                    graph.AddEdge(fnKey, key, DependencyKind.Runtime, "dynamic SQL (EXECUTE) in function body");
    }

    // Collect the resolved relation keys a query reads (FROM + joins + simple subqueries). Mirrors the
    // shape ReferenceCollector walks; deliberately conservative — only what resolves becomes an edge.
    private static IEnumerable<string> ResolveQueryRelations(SymbolTable symbols, SearchPath path, SelectQuery? q)
    {
        if (q is null || q.From is null) yield break;
        foreach (var rel in q.From.Relations)
        {
            foreach (var k in ResolveTableRef(symbols, path, rel)) yield return k;
            foreach (var j in rel.Joins)
                foreach (var k in ResolveTableRef(symbols, path, j.Right)) yield return k;
        }
    }

    private static IEnumerable<string> ResolveTableRef(SymbolTable symbols, SearchPath path, TableRef rel)
    {
        if (rel.Subquery is not null) { foreach (var k in ResolveQueryRelations(symbols, path, rel.Subquery)) yield return k; yield break; }
        if (rel.TableName is null) yield break;
        var entry = rel.Schema is not null
            ? symbols.ResolveQualified(rel.Schema, rel.TableName)
            : symbols.ResolveUnqualified(rel.TableName, SymbolKind.Relation, path);
        if (entry is not null) yield return entry.Key;
    }

    // Pull the SQL out of EXECUTE '...'; statements in a PL/pgSQL body. We only handle the safe, common
    // case: a single-quoted string literal directly after EXECUTE (no concatenation / USING params). A
    // built-up dynamic name (EXECUTE 'select * from ' || tbl) is intentionally NOT resolved — its target
    // isn't statically known, so there's nothing to point an edge at.
    private static IEnumerable<string> ExtractDynamicSql(string body)
    {
        const string kw = "execute";
        int i = 0;
        while ((i = IndexOfWord(body, kw, i)) >= 0)
        {
            int p = i + kw.Length;
            while (p < body.Length && char.IsWhiteSpace(body[p])) p++;
            if (p < body.Length && body[p] == '\'')
            {
                var (literal, end) = ReadSingleQuoted(body, p);
                if (literal is not null) yield return literal;
                i = end;
            }
            else i = p;
        }
    }

    private static int IndexOfWord(string s, string word, int start)
    {
        int idx = start;
        while ((idx = s.IndexOf(word, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            bool leftOk = idx == 0 || !char.IsLetterOrDigit(s[idx - 1]) && s[idx - 1] != '_';
            int after = idx + word.Length;
            bool rightOk = after >= s.Length || !char.IsLetterOrDigit(s[after]) && s[after] != '_';
            if (leftOk && rightOk) return idx;
            idx = after;
        }
        return -1;
    }

    // Read a Postgres single-quoted literal starting at body[start]=='\''; '' is an escaped quote.
    private static (string? literal, int end) ReadSingleQuoted(string body, int start)
    {
        var sb = new System.Text.StringBuilder();
        int p = start + 1;
        while (p < body.Length)
        {
            char ch = body[p];
            if (ch == '\'')
            {
                if (p + 1 < body.Length && body[p + 1] == '\'') { sb.Append('\''); p += 2; continue; }
                return (sb.ToString(), p + 1);
            }
            sb.Append(ch);
            p++;
        }
        return (null, body.Length); // unterminated — give up
    }

    // Canonicalize an arg-type list the SAME way CatalogBuilder does, so the overload key matches the
    // function's registered node ("app.f(integer)").
    private static string NormalizeArgTypes(string argTypes)
    {
        if (string.IsNullOrWhiteSpace(argTypes)) return "";
        return string.Join(",", argTypes.Split(',').Select(a => Model.TypeNormalizer.Normalize(a.Trim())));
    }

    private static string EdgeReason(SymbolKind referentKind) => referentKind switch
    {
        SymbolKind.Relation => "reads relation",
        SymbolKind.Function => "calls function",
        SymbolKind.Type => "uses type",
        _ => "references",
    };
}
