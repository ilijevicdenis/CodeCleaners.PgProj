using System;
using System.Collections.Generic;
using System.Linq;

namespace PgProj.Core.Semantics.Dependencies;

/// <summary>
/// A directed dependency graph over schema objects: nodes are <see cref="SymbolEntry"/> (keyed by
/// <see cref="SymbolEntry.Key"/>), edges are <see cref="DependencyEdge"/> (dependent → referent) carrying
/// a <see cref="DependencyKind"/> (Hard / Soft / Runtime). It is derived from the bound/symbol model — see
/// <see cref="DependencyGraphBuilder"/> — never from raw text, so two builds of the same model produce the
/// same graph.
/// <para>
/// It complements (does not replace) the hand-coded type-level deploy phases in
/// <c>Comparison/RawObjectMeta.Phase()</c> + <c>Comparison/SchemaChange.Phase</c>: those order
/// <em>kinds</em>, this orders <em>instances</em> by their actual references. Wiring the deploy planner to
/// consume the graph is a later phase (#55) — this type only builds, queries, and cycle-checks the graph.
/// </para>
/// <para>
/// Traversal API: <see cref="ForwardDependencies"/> ("what X needs, transitively"),
/// <see cref="ReverseDependencies"/> ("who needs X, transitively" — the closure Phase 15 incremental
/// analysis uses to find every object made dirty by a change), and the general <see cref="Traverse"/>.
/// By default traversal ignores <see cref="DependencyKind.Runtime"/> edges (they are not real ordering
/// dependencies); pass <c>includeRuntime: true</c> to walk them for impact analysis.
/// </para>
/// </summary>
public sealed class DependencyGraph
{
    private readonly Dictionary<string, SymbolEntry> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<DependencyEdge>> _out = new(StringComparer.OrdinalIgnoreCase); // from -> edges
    private readonly Dictionary<string, List<DependencyEdge>> _in = new(StringComparer.OrdinalIgnoreCase);  // to   -> edges
    private readonly HashSet<string> _edgeKeys = new(StringComparer.Ordinal);                               // de-dup

    /// <summary>All nodes in the graph (deterministic insertion order).</summary>
    public IReadOnlyCollection<SymbolEntry> Nodes => _nodes.Values;

    /// <summary>Every edge in the graph (deterministic insertion order).</summary>
    public IReadOnlyList<DependencyEdge> Edges { get; } = new List<DependencyEdge>();

    private List<DependencyEdge> EdgesList => (List<DependencyEdge>)Edges;

    /// <summary>Register a node. Idempotent on its <see cref="SymbolEntry.Key"/> (last write wins).</summary>
    public void AddNode(SymbolEntry entry) => _nodes[entry.Key] = entry;

    /// <summary>Look up a node by key (case-insensitive), or null if absent.</summary>
    public SymbolEntry? Node(string key) => _nodes.TryGetValue(key, out var e) ? e : null;

    public bool HasNode(string key) => _nodes.ContainsKey(key);

    /// <summary>
    /// Add a directed edge <paramref name="fromKey"/> → <paramref name="toKey"/>. A self-edge or a
    /// duplicate (same from/to/kind) is dropped. Endpoints are auto-registered as bare placeholder nodes if
    /// not already present, so an edge to an external/unmodelled referent is still queryable.
    /// </summary>
    public void AddEdge(string fromKey, string toKey, DependencyKind kind, string reason)
    {
        if (string.Equals(fromKey, toKey, StringComparison.OrdinalIgnoreCase)) return; // a self-reference is not a cycle
        var dedup = $"{fromKey.ToLowerInvariant()}|{toKey.ToLowerInvariant()}|{kind}";
        if (!_edgeKeys.Add(dedup)) return;

        var edge = new DependencyEdge(fromKey, toKey, kind, reason);
        EdgesList.Add(edge);
        Bucket(_out, fromKey).Add(edge);
        Bucket(_in, toKey).Add(edge);
    }

    /// <summary>Edges leaving <paramref name="key"/> (its direct dependencies).</summary>
    public IReadOnlyList<DependencyEdge> OutgoingEdges(string key) =>
        _out.TryGetValue(key, out var l) ? l : (IReadOnlyList<DependencyEdge>)Array.Empty<DependencyEdge>();

    /// <summary>Edges entering <paramref name="key"/> (the objects that directly depend on it).</summary>
    public IReadOnlyList<DependencyEdge> IncomingEdges(string key) =>
        _in.TryGetValue(key, out var l) ? l : (IReadOnlyList<DependencyEdge>)Array.Empty<DependencyEdge>();

    // ---- Traversal ---------------------------------------------------------------------------------

    /// <summary>
    /// The transitive set of objects <paramref name="key"/> depends on (its forward closure, excluding
    /// itself). Follows <c>from → to</c> edges. Runtime edges are skipped unless <paramref name="includeRuntime"/>.
    /// </summary>
    public IReadOnlyList<string> ForwardDependencies(string key, bool includeRuntime = false) =>
        Traverse(key, forward: true, includeRuntime);

    /// <summary>
    /// The transitive set of objects that depend on <paramref name="key"/> (its reverse closure, excluding
    /// itself) — <b>the closure Phase 15 incremental analysis consumes</b>: when <paramref name="key"/>
    /// changes, every object in this set must be re-evaluated/redeployed. Follows <c>to ← from</c> edges.
    /// Runtime edges are skipped unless <paramref name="includeRuntime"/>.
    /// </summary>
    public IReadOnlyList<string> ReverseDependencies(string key, bool includeRuntime = false) =>
        Traverse(key, forward: false, includeRuntime);

    /// <summary>
    /// General BFS closure from <paramref name="key"/> in the chosen direction. The start node is excluded
    /// from the result; the result is deterministic (discovery order). Cycle-safe (visited-guarded).
    /// </summary>
    public IReadOnlyList<string> Traverse(string key, bool forward, bool includeRuntime = false)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { key };
        var queue = new Queue<string>();
        queue.Enqueue(key);

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            var edges = forward ? OutgoingEdges(cur) : IncomingEdges(cur);
            foreach (var e in edges)
            {
                if (!includeRuntime && e.Kind == DependencyKind.Runtime) continue;
                var next = forward ? e.ToKey : e.FromKey;
                if (seen.Add(next)) { result.Add(next); queue.Enqueue(next); }
            }
        }
        return result;
    }

    private static List<DependencyEdge> Bucket(Dictionary<string, List<DependencyEdge>> map, string key)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = new List<DependencyEdge>();
        return list;
    }
}
