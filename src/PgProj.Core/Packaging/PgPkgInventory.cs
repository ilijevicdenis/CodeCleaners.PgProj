using System.Collections.Generic;
using PgProj.Core.Model;

namespace PgProj.Core.Packaging;

/// <summary>One object in a package's inventory: its kind and qualified identity.</summary>
public sealed record PgPkgInventoryItem(string Kind, string Identity);

/// <summary>
/// Flattens a <see cref="DatabaseModel"/> into a sorted, kind-tagged object list for <c>pkg inspect</c>.
/// </summary>
public static class PgPkgInventory
{
    public static IReadOnlyList<PgPkgInventoryItem> Of(DatabaseModel model)
    {
        var items = new List<PgPkgInventoryItem>();
        foreach (var s in model.Schemas) items.Add(new("Schema", s.Name));
        foreach (var t in model.Tables) items.Add(new("Table", $"{t.Schema}.{t.Name}"));
        foreach (var i in model.Indexes) items.Add(new("Index", $"{i.Schema}.{i.Name}"));
        foreach (var v in model.Views) items.Add(new(v.IsMaterialized ? "MaterializedView" : "View", $"{v.Schema}.{v.Name}"));
        foreach (var q in model.Sequences) items.Add(new("Sequence", $"{q.Schema}.{q.Name}"));
        foreach (var f in model.Functions) items.Add(new("Function", f.Signature));
        foreach (var o in model.Objects) items.Add(new(o.Kind.ToString(), o.Identity));

        items.Sort(static (a, b) =>
        {
            var k = string.CompareOrdinal(a.Kind, b.Kind);
            return k != 0 ? k : string.CompareOrdinal(a.Identity, b.Identity);
        });
        return items;
    }
}
