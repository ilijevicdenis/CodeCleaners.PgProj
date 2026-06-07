using System;
using System.Collections.Generic;
using PgProj.Core.Diagnostics;
using PgProj.Core.Model.Identity;

namespace PgProj.Core.Semantics.Incremental;

/// <summary>
/// One entry in the <see cref="ObjectCache"/> — the analysis outcome for a single schema object, keyed
/// (in the cache) on the object's symbol-graph key and tagged with the <see cref="CanonicalHash"/> that
/// produced it. This is the cached value the issue asks for:
/// <code>key: CanonicalHash → value: { bound/analysis result, dependencies, diagnostics }</code>
/// <para>
/// We key the <see cref="ObjectCache"/> on the stable object key (its FQN-style symbol key) and store the
/// <see cref="CanonicalHash"/> <em>inside</em> the entry, so staleness is a hash <em>comparison</em> against
/// the new build's hash for the same object (CanonicalHash mismatch ⇒ stale). Keying the dictionary on the
/// hash directly would lose the identity-stable handle a renamed/edited object still needs to be matched up.
/// </para>
/// </summary>
public sealed record AnalyzedObject
{
    /// <summary>The object's stable symbol-graph key (e.g. <c>app.v1</c> or <c>app.f(integer)</c>), lower-cased.</summary>
    public required string Key { get; init; }

    /// <summary>The semantic hash of the canonical form this analysis was produced from. The cache-validity tag:
    /// an unchanged hash ⇒ the cached entry is reusable; a changed hash ⇒ the entry is stale and must recompute.</summary>
    public required CanonicalHash CanonicalHash { get; init; }

    /// <summary>The diagnostics this object produced (its slice of the build's Problems list). Never null.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = Array.Empty<Diagnostic>();

    /// <summary>The object's <em>direct</em> forward dependencies (the keys it references), captured at analysis
    /// time so a caller can rebuild the graph slice without re-binding. Never null.</summary>
    public IReadOnlyList<string> Dependencies { get; init; } = Array.Empty<string>();

    /// <summary>An optional opaque payload — the bound model / typed analysis result for this object. Left as
    /// <see cref="object"/> so the cache stays agnostic of the binder's concrete types (a caller that wants the
    /// bound view stashes it here; tests can ignore it). Null when the caller carries only diagnostics + deps.</summary>
    public object? BoundResult { get; init; }
}
