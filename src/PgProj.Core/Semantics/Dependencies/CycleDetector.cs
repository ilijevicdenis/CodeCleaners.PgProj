using System;
using System.Collections.Generic;
using System.Linq;
using PgProj.Core.Analysis;
using UnifiedDiagnostic = PgProj.Core.Diagnostics.Diagnostic;

namespace PgProj.Core.Semantics.Dependencies;

/// <summary>One detected dependency cycle: the ordered path of object keys that closes back on itself.</summary>
/// <remarks>
/// <see cref="Path"/> lists the nodes in dependency order with the first node repeated at the end, so the
/// full loop reads as <c>A → B → C → A</c>. <see cref="Edges"/> is the edge taken at each hop (so the
/// classifier knows which kinds are involved). <see cref="IsHard"/> is true when ANY edge on the loop is
/// <see cref="DependencyKind.Hard"/> — that makes the cycle a deploy-blocking error; an all-soft loop is a
/// warning.
/// </remarks>
public sealed class DependencyCycle
{
    public IReadOnlyList<string> Path { get; }
    public IReadOnlyList<DependencyEdge> Edges { get; }

    public DependencyCycle(IReadOnlyList<string> path, IReadOnlyList<DependencyEdge> edges)
    {
        Path = path;
        Edges = edges;
    }

    /// <summary>True when the loop traverses at least one Postgres-enforced (Hard) edge → an error.</summary>
    public bool IsHard => Edges.Any(e => e.Kind == DependencyKind.Hard);

    /// <summary>The full path rendered as <c>A → B → C → A</c>.</summary>
    public string Describe() => string.Join(" → ", Path);

    public override string ToString() => Describe();
}

/// <summary>
/// Phase 7 — circular-dependency detection over a <see cref="DependencyGraph"/>.
///
/// <para><b>What counts as a cycle.</b> Only <see cref="DependencyKind.Hard"/> and
/// <see cref="DependencyKind.Soft"/> edges participate: a <see cref="DependencyKind.Runtime"/> edge fires at
/// call time (after every object exists), so it is never a creation-ordering dependency and is excluded — a
/// function whose dynamic SQL reads a table that calls back into the function is NOT a deploy cycle.</para>
///
/// <para><b>Self-references are not cycles.</b> A recursive view/function (depends on itself) is rejected at
/// the edge level (the graph drops self-edges), so single-object self-loops never surface — that matches
/// Postgres, which permits <c>WITH RECURSIVE</c> views and self-recursive functions.</para>
///
/// <para><b>Hard vs soft.</b> A cycle is an <em>error</em> if it contains at least one Hard edge (Postgres
/// cannot create the objects in any order — e.g. view A selects view B which selects view A). A cycle made
/// entirely of Soft edges is a <em>warning</em>: ordering is only a preference there, so the deploy still
/// succeeds, but the preference can't be honored.</para>
///
/// <para><b>Algorithm.</b> Tarjan strongly-connected-components over the (Hard+Soft) edges — linear time,
/// finds every cycle group at once. For each SCC of size &gt; 1 (or any node with a self-loop, which the
/// graph already excludes) we recover a concrete loop with a DFS inside the component and report it as a
/// path <c>A → B → … → A</c>, naming every object on the loop, not just the culprit.</para>
/// </summary>
public static class CycleDetector
{
    /// <summary>Diagnostic code for a hard (deploy-blocking) schema-object dependency cycle.</summary>
    public const string HardCycleCode = "PGDEP001";

    /// <summary>Diagnostic code for a soft (preference-only) dependency cycle.</summary>
    public const string SoftCycleCode = "PGDEP002";

    /// <summary>Find every dependency cycle in <paramref name="graph"/> (Runtime edges excluded).</summary>
    public static IReadOnlyList<DependencyCycle> Detect(DependencyGraph graph)
    {
        // Adjacency restricted to ordering edges (Hard + Soft).
        var adj = new Dictionary<string, List<DependencyEdge>>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in graph.Nodes) adj[n.Key] = new List<DependencyEdge>();
        foreach (var e in graph.Edges)
        {
            if (e.Kind == DependencyKind.Runtime) continue;
            if (!adj.ContainsKey(e.FromKey)) adj[e.FromKey] = new List<DependencyEdge>();
            if (!adj.ContainsKey(e.ToKey)) adj[e.ToKey] = new List<DependencyEdge>();
            adj[e.FromKey].Add(e);
        }

        var sccs = Tarjan(adj);
        var cycles = new List<DependencyCycle>();
        foreach (var scc in sccs)
        {
            if (scc.Count < 2) continue;                       // size-1 SCC = no loop (self-edges already dropped)
            var loop = RecoverLoop(adj, scc);
            if (loop is not null) cycles.Add(loop);
        }
        return cycles;
    }

    /// <summary>
    /// Detect cycles and lift each into a unified <see cref="UnifiedDiagnostic"/>: a Hard cycle is an
    /// <see cref="DiagnosticSeverity.Error"/> (<see cref="HardCycleCode"/>), an all-soft cycle a
    /// <see cref="DiagnosticSeverity.Warning"/> (<see cref="SoftCycleCode"/>). The message names the full path.
    /// </summary>
    public static IReadOnlyList<UnifiedDiagnostic> DetectDiagnostics(DependencyGraph graph)
    {
        var diags = new List<UnifiedDiagnostic>();
        foreach (var cycle in Detect(graph))
        {
            bool hard = cycle.IsHard;
            diags.Add(new UnifiedDiagnostic
            {
                Severity = hard ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
                Code = hard ? HardCycleCode : SoftCycleCode,
                Message = (hard
                    ? "Circular dependency between schema objects (cannot be created in any order): "
                    : "Circular dependency between schema objects (preferred ordering cannot be honored): ")
                    + cycle.Describe(),
                Target = cycle.Path.Count > 0 ? cycle.Path[0] : null,
            });
        }
        return diags;
    }

    // ---- Tarjan SCC --------------------------------------------------------------------------------

    private static List<List<string>> Tarjan(Dictionary<string, List<DependencyEdge>> adj)
    {
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var low = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var onStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        var sccs = new List<List<string>>();
        int counter = 0;

        // Iterative Tarjan (recursion-free) so a deep chain can't overflow the stack.
        foreach (var start in adj.Keys.ToList())
        {
            if (index.ContainsKey(start)) continue;
            var work = new Stack<(string node, int childIdx)>();
            work.Push((start, 0));

            while (work.Count > 0)
            {
                var (v, ci) = work.Pop();
                if (ci == 0)
                {
                    index[v] = low[v] = counter++;
                    stack.Push(v); onStack.Add(v);
                }

                bool recursed = false;
                var edges = adj[v];
                for (int k = ci; k < edges.Count; k++)
                {
                    var w = edges[k].ToKey;
                    if (!index.ContainsKey(w))
                    {
                        work.Push((v, k + 1));   // resume v after w
                        work.Push((w, 0));
                        recursed = true;
                        break;
                    }
                    if (onStack.Contains(w))
                        low[v] = Math.Min(low[v], index[w]);
                }
                if (recursed) continue;

                // Done with v: fold children low-links, then close an SCC if v is a root.
                foreach (var e in edges)
                    if (onStack.Contains(e.ToKey))
                        low[v] = Math.Min(low[v], low[e.ToKey]);

                if (low[v] == index[v])
                {
                    var comp = new List<string>();
                    string w;
                    do { w = stack.Pop(); onStack.Remove(w); comp.Add(w); } while (!string.Equals(w, v, StringComparison.OrdinalIgnoreCase));
                    sccs.Add(comp);
                }
            }
        }
        return sccs;
    }

    // Recover an explicit loop A → B → … → A within a strongly-connected component, naming the full path.
    private static DependencyCycle? RecoverLoop(Dictionary<string, List<DependencyEdge>> adj, List<string> scc)
    {
        var inScc = new HashSet<string>(scc, StringComparer.OrdinalIgnoreCase);
        // Deterministic start: the lexicographically-smallest member, so the reported path is stable.
        var start = scc.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).First();

        var parentEdge = new Dictionary<string, DependencyEdge>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start };
        var stack = new Stack<string>();
        stack.Push(start);

        while (stack.Count > 0)
        {
            var v = stack.Pop();
            foreach (var e in adj[v].Where(x => inScc.Contains(x.ToKey)).OrderBy(x => x.ToKey, StringComparer.OrdinalIgnoreCase))
            {
                var w = e.ToKey;
                if (string.Equals(w, start, StringComparison.OrdinalIgnoreCase))
                {
                    // Close the loop: walk parents from v back to start, then append the closing edge.
                    var nodes = new List<string> { v };
                    var edges = new List<DependencyEdge> { e };
                    var cur = v;
                    while (!string.Equals(cur, start, StringComparison.OrdinalIgnoreCase))
                    {
                        var pe = parentEdge[cur];
                        nodes.Add(pe.FromKey);
                        edges.Add(pe);
                        cur = pe.FromKey;
                    }
                    nodes.Reverse();
                    edges.Reverse();
                    nodes.Add(start);   // close the loop visibly: A → … → A
                    return new DependencyCycle(nodes, edges);
                }
                if (visited.Add(w)) { parentEdge[w] = e; stack.Push(w); }
            }
        }
        return null;
    }
}
