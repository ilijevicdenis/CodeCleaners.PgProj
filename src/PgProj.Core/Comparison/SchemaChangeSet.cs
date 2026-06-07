using System;
using System.Collections.Generic;
using System.Linq;

namespace PgProj.Core.Comparison;

/// <summary>
/// A structured, <em>selectable</em> set of schema changes — the reviewable diff at the heart of EP-SCHEMACOMPARE.
/// It wraps the ordered <see cref="SchemaChange"/> list from <see cref="SchemaComparer"/>, assigns each change a
/// stable id, lets a caller include/exclude a subset (by id or by object-type), and scripts/lists only the
/// included subset. The set itself is direction-agnostic: it diffs whatever models it was built from
/// (source→target), so "compare project↔DB" and "compare DB↔project" are the same type used twice.
/// </summary>
public sealed class SchemaChangeSet
{
    private readonly List<SelectableChange> _changes;

    private SchemaChangeSet(List<SelectableChange> changes) => _changes = changes;

    /// <summary>
    /// Builds a selectable set from a source model diffed against a target model. Changes are ordered by
    /// deploy phase (as the comparer returns them) and each gets a stable id; duplicate signatures get a
    /// disambiguating <c>#n</c> suffix so every id is unique within the set.
    /// </summary>
    /// <param name="exclude">Object-type tokens to mark excluded up front (e.g. <c>extension</c>, <c>permission</c>).</param>
    public static SchemaChangeSet Build(
        Model.DatabaseModel source,
        Model.DatabaseModel target,
        ComparerOptions? options = null,
        IEnumerable<string>? exclude = null)
    {
        var raw = new SchemaComparer().Compare(source, target, options);
        var excluded = new HashSet<string>(
            (exclude ?? Enumerable.Empty<string>()).Select(SchemaCompareObjectType.Parse),
            StringComparer.Ordinal);

        // Stable ids: hash the signature, then disambiguate genuine duplicates by occurrence count. The id is
        // therefore position-independent — re-running the compare yields the same id for the same change.
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var list = new List<SelectableChange>(raw.Count);
        foreach (var change in raw)
        {
            var hash = SelectableChange.HashOf(SelectableChange.Signature(change));
            var n = seen.TryGetValue(hash, out var c) ? c + 1 : 0;
            seen[hash] = n;
            var id = n == 0 ? hash : $"{hash}#{n}";

            var included = !excluded.Contains(SchemaCompareObjectType.Of(change));
            list.Add(new SelectableChange(id, change, included));
        }

        return new SchemaChangeSet(list);
    }

    /// <summary>Every change in the set, in deploy order, regardless of selection.</summary>
    public IReadOnlyList<SelectableChange> Changes => _changes;

    /// <summary>The currently-included subset, in deploy order.</summary>
    public IReadOnlyList<SelectableChange> Included => _changes.Where(c => c.Included).ToList();

    /// <summary>True when the source and target are identical (no changes at all).</summary>
    public bool InSync => _changes.Count == 0;

    /// <summary>Total number of changes (included and excluded).</summary>
    public int Count => _changes.Count;

    /// <summary>Number of destructive changes across the whole set.</summary>
    public int DestructiveCount => _changes.Count(c => c.IsDestructive);

    /// <summary>The highest <see cref="Risk.RiskLevel"/> among the included changes (Safe when none).</summary>
    public Risk.RiskLevel MaxIncludedRiskLevel =>
        _changes.Where(c => c.Included).Select(c => c.RiskLevel)
                .DefaultIfEmpty(Risk.RiskLevel.Safe).Max();

    /// <summary>Number of included changes classified as data loss or worse (DataLoss/Blocking).</summary>
    public int IncludedDataLossCount =>
        _changes.Count(c => c.Included && c.RiskLevel >= Risk.RiskLevel.DataLoss);

    /// <summary>Number of currently-included changes.</summary>
    public int IncludedCount => _changes.Count(c => c.Included);

    /// <summary>The distinct object-types present in the set, sorted, for a UI's filter list.</summary>
    public IReadOnlyList<string> ObjectTypes =>
        _changes.Select(c => c.ObjectType).Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToList();

    /// <summary>Looks up a change by its stable id, or null if no such change is in the set.</summary>
    public SelectableChange? Find(string id) =>
        _changes.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal));

    // ---- selection -----------------------------------------------------------------------------

    /// <summary>Marks every change included (the default after a fresh build with no excludes).</summary>
    public SchemaChangeSet IncludeAll()
    {
        foreach (var c in _changes) c.Included = true;
        return this;
    }

    /// <summary>Marks every change excluded — start from nothing and opt changes back in.</summary>
    public SchemaChangeSet ExcludeAll()
    {
        foreach (var c in _changes) c.Included = false;
        return this;
    }

    /// <summary>Includes a single change by id. Returns false when no change has that id.</summary>
    public bool IncludeById(string id) => SetById(id, true);

    /// <summary>Excludes a single change by id. Returns false when no change has that id.</summary>
    public bool ExcludeById(string id) => SetById(id, false);

    private bool SetById(string id, bool included)
    {
        var c = Find(id);
        if (c is null) return false;
        c.Included = included;
        return true;
    }

    /// <summary>Excludes every change whose object-type matches one of <paramref name="objectTypes"/>.</summary>
    public SchemaChangeSet ExcludeObjectTypes(IEnumerable<string> objectTypes)
    {
        var set = new HashSet<string>(objectTypes.Select(SchemaCompareObjectType.Parse), StringComparer.Ordinal);
        foreach (var c in _changes.Where(c => set.Contains(c.ObjectType))) c.Included = false;
        return this;
    }

    /// <summary>
    /// Restricts the selection to exactly the given object-types: any change whose type is NOT listed is
    /// excluded, and listed ones are (re-)included. The classic "only show me tables and indexes" filter.
    /// </summary>
    public SchemaChangeSet IncludeOnlyObjectTypes(IEnumerable<string> objectTypes)
    {
        var set = new HashSet<string>(objectTypes.Select(SchemaCompareObjectType.Parse), StringComparer.Ordinal);
        foreach (var c in _changes) c.Included = set.Contains(c.ObjectType);
        return this;
    }

    /// <summary>
    /// Applies a saved selection: includes exactly the ids in <paramref name="includedIds"/> and excludes
    /// every other change. Ids not present in the set are silently ignored (the change may have vanished
    /// since the selection was captured). This is the round-trip partner of <see cref="SelectedIds"/>.
    /// </summary>
    public SchemaChangeSet ApplySelection(IEnumerable<string> includedIds)
    {
        var keep = new HashSet<string>(includedIds, StringComparer.Ordinal);
        foreach (var c in _changes) c.Included = keep.Contains(c.Id);
        return this;
    }

    /// <summary>The ids of the currently-included changes, in deploy order — a portable selection snapshot.</summary>
    public IReadOnlyList<string> SelectedIds => _changes.Where(c => c.Included).Select(c => c.Id).ToList();

    // ---- output --------------------------------------------------------------------------------

    /// <summary>
    /// Generates a deploy script for the included subset only. The omitted changes are simply not emitted;
    /// the remaining ones keep their phase ordering, so the partial script is still dependency-safe within
    /// what it contains.
    /// </summary>
    public string ScriptIncluded(DeployOptions? options = null) =>
        new DeployScriptGenerator().Generate(Included.Select(c => c.Change).ToList(), options);
}
