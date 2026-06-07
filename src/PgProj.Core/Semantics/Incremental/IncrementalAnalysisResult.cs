using System;
using System.Collections.Generic;
using System.Linq;
using PgProj.Core.Diagnostics;
using PgProj.Core.Semantics.Dependencies;

namespace PgProj.Core.Semantics.Incremental;

/// <summary>
/// The output of one incremental analysis pass (issue #57) — and the input to the next one. It carries the
/// populated <see cref="Cache"/> (reusable across passes), the <see cref="Graph"/> the closure was walked on,
/// and a precise audit of <em>what this pass actually recomputed</em> vs <em>what it reused</em> so a caller
/// (and the acceptance tests) can prove the work scaled with the change, not the project.
/// <para>
/// Pass the whole result back into <see cref="IncrementalAnalyzer.Update"/> as the "prior" to get the next
/// incremental update. The first/full pass is produced by <see cref="IncrementalAnalyzer.AnalyzeFull"/>.
/// </para>
/// </summary>
public sealed class IncrementalAnalysisResult
{
    /// <summary>The object cache after this pass (every current object has a fresh-or-reused entry).</summary>
    public ObjectCache Cache { get; }

    /// <summary>The dependency graph the reverse-closure invalidation was driven by.</summary>
    public DependencyGraph Graph { get; }

    /// <summary>The set of object keys this pass re-analyzed (the directly-changed objects ∪ their reverse
    /// closure ∪ newly-added objects). Its size is the headline metric: it must be ≪ <see cref="TotalObjects"/>
    /// for a small edit. Deterministic order.</summary>
    public IReadOnlyList<string> Recomputed { get; }

    /// <summary>The set of object keys served straight from cache (a hit — no re-analysis). Deterministic order.</summary>
    public IReadOnlyList<string> Reused { get; }

    /// <summary>The set of object keys dropped this pass because the object no longer exists. Deterministic order.</summary>
    public IReadOnlyList<string> Removed { get; }

    /// <summary>Total objects in the current model (cache size after the pass).</summary>
    public int TotalObjects => Cache.Count;

    /// <summary>How many objects were recomputed this pass — the count the acceptance tests assert is small.</summary>
    public int RecomputedCount => Recomputed.Count;

    /// <summary>How many objects were served from cache without re-binding.</summary>
    public int ReusedCount => Reused.Count;

    /// <summary>Every object's diagnostics, flattened — the unified Problems list for the whole model after the
    /// pass (reused entries contribute their <em>prior</em> diagnostics verbatim; recomputed ones their fresh
    /// findings). Order is by object key for determinism.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics =>
        Cache.Entries
             .OrderBy(e => e.Key, StringComparer.Ordinal)
             .SelectMany(e => e.Diagnostics)
             .ToList();

    public IncrementalAnalysisResult(
        ObjectCache cache,
        DependencyGraph graph,
        IReadOnlyList<string> recomputed,
        IReadOnlyList<string> reused,
        IReadOnlyList<string> removed)
    {
        Cache = cache ?? throw new ArgumentNullException(nameof(cache));
        Graph = graph ?? throw new ArgumentNullException(nameof(graph));
        Recomputed = recomputed ?? Array.Empty<string>();
        Reused = reused ?? Array.Empty<string>();
        Removed = removed ?? Array.Empty<string>();
    }
}
