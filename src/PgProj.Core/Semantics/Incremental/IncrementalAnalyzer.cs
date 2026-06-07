using System;
using System.Collections.Generic;
using System.Linq;
using PgProj.Core.Model.Identity;
using PgProj.Core.Semantics.Dependencies;

namespace PgProj.Core.Semantics.Incremental;

/// <summary>
/// One object in the model as the incremental analyzer sees it: its stable symbol-graph
/// <see cref="Key"/> and the <see cref="CanonicalHash"/> of its current canonical form. A caller builds this
/// list from the new model (e.g. via <see cref="Model.Identity.ObjectIdentityComputer"/> →
/// <see cref="ObjectIdentity.CanonicalHash"/>, keyed by the same FQN the dependency graph uses). The hash is
/// the staleness signal; the key joins this object to its cache entry and its graph node.
/// </summary>
public readonly record struct ObjectSnapshot(string Key, CanonicalHash CanonicalHash);

/// <summary>
/// Incremental analysis &amp; object cache (issue #57, Phase 15 of EP-SEMCORE).
///
/// <para><b>What it is.</b> An <em>additive</em> layer on top of the full build (<c>DatabaseProject.BuildAsync</c>
/// is untouched and remains the from-scratch path). Given a <em>prior</em> <see cref="IncrementalAnalysisResult"/>
/// and the <em>current</em> set of objects, it returns an updated result that reuses every cached entry whose
/// <see cref="CanonicalHash"/> is unchanged and re-analyzes only the objects that actually changed plus the
/// objects that (transitively) depend on them — so incremental work scales with the change, not the project.</para>
///
/// <para><b>The invalidation algorithm</b> (<see cref="Update"/>):</para>
/// <list type="number">
///   <item><b>Staleness detection.</b> For each current object, compare its CanonicalHash to the cached entry's.
///     A hash mismatch (or a cache miss / brand-new object) marks the object <em>directly changed</em>. This is
///     exactly <see cref="ObjectCache.IsStale"/>.</item>
///   <item><b>Reverse-dependency closure.</b> Seed a worklist with the directly-changed keys; for each, add its
///     transitive <see cref="DependencyGraph.ReverseDependencies"/> ("who depends on X"). The union is the
///     <em>dirty set</em> — every object whose analysis could be invalidated by the change. Cache-current
///     objects outside the dirty set are reused verbatim.</item>
///   <item><b>Recompute &amp; reuse.</b> Re-run the caller's <c>analyze</c> delegate for each dirty object that
///     still exists; copy the cached entry for everything else; drop entries for objects that disappeared.</item>
/// </list>
///
/// <para>The analyzer is deliberately decoupled from the binder: the caller supplies an <c>analyze</c> delegate
/// (key + current hash → <see cref="AnalyzedObject"/>). The build wires it to a single-object bind/validate; a
/// future file-watch or the VS Code model-tree wires it the same way. The reverse-closure invalidation,
/// staleness rule and reuse accounting live here, once.</para>
/// </summary>
public sealed class IncrementalAnalyzer
{
    /// <summary>
    /// The from-scratch pass: analyze every object and seed the cache. Equivalent to a full build, but produces
    /// the cache + graph the subsequent <see cref="Update"/> calls reuse. Every object counts as recomputed.
    /// </summary>
    /// <param name="objects">The current model's objects (key + CanonicalHash).</param>
    /// <param name="graph">The dependency graph over those objects (drives later reverse-closure invalidation).</param>
    /// <param name="analyze">Produces the analysis (diagnostics + deps + optional bound result) for one object.</param>
    public IncrementalAnalysisResult AnalyzeFull(
        IReadOnlyList<ObjectSnapshot> objects,
        DependencyGraph graph,
        Func<ObjectSnapshot, AnalyzedObject> analyze)
    {
        if (objects is null) throw new ArgumentNullException(nameof(objects));
        if (graph is null) throw new ArgumentNullException(nameof(graph));
        if (analyze is null) throw new ArgumentNullException(nameof(analyze));

        var cache = new ObjectCache();
        var recomputed = new List<string>();
        foreach (var obj in objects)
        {
            cache.Put(analyze(obj));
            recomputed.Add(obj.Key);
        }
        return new IncrementalAnalysisResult(cache, graph, recomputed, Array.Empty<string>(), Array.Empty<string>());
    }

    /// <summary>
    /// The incremental pass: reuse the <paramref name="prior"/> cache for everything unchanged, recompute only
    /// the changed objects and their reverse-dependency closure. See the class remarks for the full algorithm.
    /// </summary>
    /// <param name="prior">The previous pass's result (its cache is the memory we reuse; it is not mutated).</param>
    /// <param name="current">The current model's objects (key + CanonicalHash). Objects absent here were deleted.</param>
    /// <param name="graph">The current dependency graph (the reverse closure is walked on this). Pass the freshly
    ///   rebuilt graph — it is cheap relative to re-binding and keeps the closure correct after edits add/remove
    ///   edges. The result records this graph for the next pass.</param>
    /// <param name="analyze">Produces fresh analysis for a dirty object (same delegate shape as the full pass).</param>
    public IncrementalAnalysisResult Update(
        IncrementalAnalysisResult prior,
        IReadOnlyList<ObjectSnapshot> current,
        DependencyGraph graph,
        Func<ObjectSnapshot, AnalyzedObject> analyze)
    {
        if (prior is null) throw new ArgumentNullException(nameof(prior));
        if (current is null) throw new ArgumentNullException(nameof(current));
        if (graph is null) throw new ArgumentNullException(nameof(graph));
        if (analyze is null) throw new ArgumentNullException(nameof(analyze));

        var priorCache = prior.Cache;
        var currentByKey = new Dictionary<string, ObjectSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in current) currentByKey[o.Key] = o;

        // ---- 1. Staleness detection: which objects changed directly (hash mismatch / new) ----------
        var directlyChanged = new List<string>();
        foreach (var o in current)
            if (priorCache.IsStale(o.Key, o.CanonicalHash))   // miss OR hash mismatch ⇒ stale
                directlyChanged.Add(o.Key);

        // An object that vanished from the model is itself a change to the graph: its dependents must be
        // re-analyzed (their reference may now dangle). Seed the closure from removed keys too.
        var removed = new List<string>();
        foreach (var key in priorCache.Keys)
            if (!currentByKey.ContainsKey(key))
                removed.Add(key);

        // ---- 2. Reverse-dependency closure over the changed + removed seeds ⇒ the dirty set --------
        // A CHANGED object's dependents are walked on the CURRENT graph (post-edit edges). A REMOVED
        // object no longer has a node in the current graph, so its former dependents are only knowable from
        // the PRIOR graph — walk those there. The union of both closures is the dirty set.
        var dirty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var seed in directlyChanged)
        {
            dirty.Add(seed);
            foreach (var dependent in graph.ReverseDependencies(seed))
                dirty.Add(dependent);
        }
        foreach (var seed in removed)
        {
            dirty.Add(seed);   // dropped below (not in current), but its dependents must recompute
            foreach (var dependent in prior.Graph.ReverseDependencies(seed))
                dirty.Add(dependent);
        }

        // ---- 3. Recompute the dirty (still-existing) objects; reuse cache for the rest --------------
        var newCache = new ObjectCache();
        var recomputed = new List<string>();
        var reused = new List<string>();

        // Walk the current objects in their given order for deterministic recomputed/reused lists.
        foreach (var o in current)
        {
            if (dirty.Contains(o.Key))
            {
                newCache.Put(analyze(o));               // re-analyze: directly changed or a dependent of one
                recomputed.Add(o.Key);
            }
            else if (priorCache.TryGet(o.Key, out var cached))
            {
                newCache.Put(cached);                   // CACHE HIT: prior diagnostics/deps reused, no re-bind
                reused.Add(o.Key);
            }
            else
            {
                // Not dirty yet not in the prior cache — only possible if it's brand-new and somehow not flagged.
                // Treat as a recompute to stay correct (defensive; directlyChanged already catches new objects).
                newCache.Put(analyze(o));
                recomputed.Add(o.Key);
            }
        }
        // Objects in `removed` are simply not carried into newCache → dropped.

        removed.Sort(StringComparer.Ordinal);
        return new IncrementalAnalysisResult(newCache, graph, recomputed, reused, removed);
    }
}
