using System;
using System.Collections.Generic;
using PgProj.Core.Model.Identity;

namespace PgProj.Core.Semantics.Incremental;

/// <summary>
/// The per-object analysis cache (issue #57, Phase 15). It stores one <see cref="AnalyzedObject"/> per schema
/// object, keyed by the object's stable symbol-graph key, each entry tagged with the
/// <see cref="CanonicalHash"/> it was produced from. The cache is the memory the incremental analyzer reuses
/// across builds so that work scales with the size of a change, not with the size of the project.
/// <para>
/// <b>Staleness</b> is a <see cref="CanonicalHash"/> comparison: <see cref="IsStale"/>/<see cref="IsCurrent"/>
/// answer "does the cached entry still match the object's current canonical form?". A miss (no entry) is also
/// stale — there is nothing to reuse. This is the single rule the analyzer's invalidation builds on.
/// </para>
/// <para>The cache is a plain in-memory map; it is not thread-safe (a build holds it on one thread, a future
/// file-watch debounces edits onto one). Cloning (<see cref="Clone"/>) gives an isolated snapshot so a new
/// incremental pass never mutates the prior result the caller still holds.</para>
/// </summary>
public sealed class ObjectCache
{
    private readonly Dictionary<string, AnalyzedObject> _byKey;

    public ObjectCache() => _byKey = new Dictionary<string, AnalyzedObject>(StringComparer.OrdinalIgnoreCase);

    private ObjectCache(Dictionary<string, AnalyzedObject> seed) =>
        _byKey = new Dictionary<string, AnalyzedObject>(seed, StringComparer.OrdinalIgnoreCase);

    /// <summary>Number of cached objects.</summary>
    public int Count => _byKey.Count;

    /// <summary>Every cached entry (deterministic insertion order).</summary>
    public IReadOnlyCollection<AnalyzedObject> Entries => _byKey.Values;

    /// <summary>Every cached object key.</summary>
    public IReadOnlyCollection<string> Keys => _byKey.Keys;

    /// <summary>Store (or replace) an entry, keyed on its <see cref="AnalyzedObject.Key"/>.</summary>
    public void Put(AnalyzedObject entry)
    {
        if (entry is null) throw new ArgumentNullException(nameof(entry));
        _byKey[entry.Key] = entry;
    }

    /// <summary>The cached entry for <paramref name="key"/>, or null on a miss.</summary>
    public AnalyzedObject? Get(string key) => _byKey.TryGetValue(key, out var e) ? e : null;

    public bool TryGet(string key, out AnalyzedObject entry)
    {
        if (_byKey.TryGetValue(key, out var e)) { entry = e; return true; }
        entry = null!;
        return false;
    }

    public bool Contains(string key) => _byKey.ContainsKey(key);

    /// <summary>Drop a cached entry (e.g. an object that was deleted). Returns true if one was present.</summary>
    public bool Remove(string key) => _byKey.Remove(key);

    /// <summary>
    /// True when the cached entry for <paramref name="key"/> is a usable hit for <paramref name="currentHash"/> —
    /// i.e. an entry exists AND its <see cref="AnalyzedObject.CanonicalHash"/> equals the supplied current hash.
    /// </summary>
    public bool IsCurrent(string key, CanonicalHash currentHash) =>
        _byKey.TryGetValue(key, out var e) && e.CanonicalHash == currentHash;

    /// <summary>Staleness detection: the inverse of <see cref="IsCurrent"/>. A miss or a hash mismatch is stale.</summary>
    public bool IsStale(string key, CanonicalHash currentHash) => !IsCurrent(key, currentHash);

    /// <summary>An isolated deep-enough copy (entries are immutable records, so a shallow value copy suffices).</summary>
    public ObjectCache Clone() => new(_byKey);
}
