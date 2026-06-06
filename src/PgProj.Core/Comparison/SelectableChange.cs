using System;
using System.Security.Cryptography;
using System.Text;

namespace PgProj.Core.Comparison;

/// <summary>
/// One change in a Schema Compare result, wrapped with the metadata a reviewable, <em>selectable</em>
/// diff needs: a stable <see cref="Id"/> (so a UI/CLI can refer to a specific change across runs), the
/// coarse <see cref="ObjectType"/> it belongs to (for include/exclude filters), and whether it is
/// currently <see cref="Included"/> in the subset to script/apply.
/// </summary>
/// <remarks>
/// The underlying <see cref="SchemaChange"/> is immutable; selection is layered on top so the engine
/// (diff) and the policy (which subset to apply) stay separate — exactly the split the spec asks for
/// ("each change checkable, apply a subset and skip the rest").
/// </remarks>
public sealed class SelectableChange
{
    internal SelectableChange(string id, SchemaChange change, bool included)
    {
        Id = id;
        Change = change;
        ObjectType = SchemaCompareObjectType.Of(change);
        Included = included;
    }

    /// <summary>
    /// A stable, deterministic identifier for this change: it depends only on <em>what</em> the change is
    /// (its record kind + human description), not on its position in the list, so a UI selection survives a
    /// re-compare and the order is irrelevant. Duplicate changes get a disambiguating suffix at build time.
    /// </summary>
    public string Id { get; }

    /// <summary>The underlying engine change (the SQL it emits, its phase, destructiveness).</summary>
    public SchemaChange Change { get; }

    /// <summary>The coarse object-type token this change belongs to (e.g. <c>table</c>, <c>index</c>, <c>extension</c>).</summary>
    public string ObjectType { get; }

    /// <summary>Whether this change is part of the subset to script/apply. Toggle to include/exclude it.</summary>
    public bool Included { get; set; }

    /// <summary>True when applying this change can lose data/objects (drop/destructive recreate).</summary>
    public bool IsDestructive => Change.IsDestructive;

    /// <summary>Deploy-ordering phase (lower runs first).</summary>
    public int Phase => Change.Phase;

    /// <summary>The change-record type name (e.g. <c>CreateTableChange</c>) — a stable kind discriminator.</summary>
    public string Kind => Change.GetType().Name;

    /// <summary>A one-line human description of the change.</summary>
    public string Description => Change.Describe();

    /// <summary>
    /// The stable signature a change's id is derived from: <c>{record-kind}|{description}</c>. Two changes
    /// with the same signature are genuinely indistinguishable to a reviewer, so they share a base id and
    /// the builder appends an occurrence suffix to keep ids unique.
    /// </summary>
    internal static string Signature(SchemaChange change) => $"{change.GetType().Name}|{change.Describe()}";

    /// <summary>An 8-hex-char content hash of a signature (stable across processes; FIPS-safe SHA-256).</summary>
    internal static string HashOf(string signature)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(signature));
        var sb = new StringBuilder(8);
        for (var i = 0; i < 4; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }
}
