using System.Collections.Generic;
using PgProj.Core.Model;
using PgProj.Core.Model.Identity;

namespace PgProj.Core.Extensibility;

/// <summary>
/// Presents a <see cref="DatabaseModel"/> as a flat sequence of <see cref="IProjectObject"/> so
/// introspection, diff, and codegen can iterate every kind through the one contract instead of a
/// per-collection / per-<see cref="ObjectKind"/> switch (issue #44). A single shared
/// <see cref="ObjectIdentityComputer"/> backs the whole registry, so ObjectIds are unique and stable
/// within one registry instance and identities are computed once per object.
/// </summary>
public sealed class ProjectObjectRegistry
{
    private readonly List<IProjectObject> _objects;

    public ProjectObjectRegistry(DatabaseModel model, ObjectIdentityComputer? computer = null)
    {
        var c = computer ?? new ObjectIdentityComputer();
        _objects = new List<IProjectObject>(
            model.Schemas.Count + model.Tables.Count + model.Indexes.Count + model.Views.Count +
            model.Sequences.Count + model.Functions.Count + model.Objects.Count);

        foreach (var s in model.Schemas) _objects.Add(new SchemaProjectObject(s, c));
        foreach (var t in model.Tables) _objects.Add(new TableProjectObject(t, c));
        foreach (var i in model.Indexes) _objects.Add(new IndexProjectObject(i, c));
        foreach (var v in model.Views) _objects.Add(new ViewProjectObject(v, c));
        foreach (var q in model.Sequences) _objects.Add(new SequenceProjectObject(q, c));
        foreach (var f in model.Functions) _objects.Add(new FunctionProjectObject(f, c));
        foreach (var o in model.Objects) _objects.Add(new RawProjectObject(o, c));
    }

    /// <summary>Every object in the model, as contract instances (finely-modelled kinds first, then raw).</summary>
    public IReadOnlyList<IProjectObject> All => _objects;

    /// <summary>Contract instances of one kind token (e.g. <c>"table"</c>, <c>"type"</c>).</summary>
    public IEnumerable<IProjectObject> OfKind(string kindToken)
    {
        foreach (var o in _objects)
            if (string.Equals(o.Kind, kindToken, System.StringComparison.Ordinal))
                yield return o;
    }

    /// <summary>The distinct kind tokens present in the model, for kind-filtered iteration.</summary>
    public IEnumerable<string> KindTokens()
    {
        var seen = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var o in _objects)
            if (seen.Add(o.Kind)) yield return o.Kind;
    }
}
