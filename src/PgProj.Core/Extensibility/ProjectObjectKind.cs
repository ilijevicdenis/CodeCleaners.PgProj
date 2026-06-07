using PgProj.Core.Comparison;
using PgProj.Core.Model;

namespace PgProj.Core.Extensibility;

/// <summary>
/// Per-kind metadata, surfaced through one accessor so diff/codegen/extract read it via the registry
/// instead of re-deriving it from scattered switch statements. It delegates to the existing canonical
/// tables (<see cref="RawObjectMeta"/>, <see cref="SchemaCompareObjectType"/>) rather than restating
/// them — so this is the single iteration point, not a second source of truth.
///
/// The #44 checklist (see <c>docs/reference/ADDING_AN_OBJECT_KIND.md</c>): a new raw object kind is
/// registered by adding it to the <see cref="ObjectKind"/> enum + the <see cref="RawObjectMeta"/> tables
/// + the version profile's catalog query — and it then flows through this metadata and the registry with
/// no further switch edits.
/// </summary>
public readonly record struct ProjectObjectKind(
    ObjectKind Kind,
    string TypeToken,
    int Phase,
    string Folder,
    bool ComparesByIdentityOnly,
    bool IsDestructiveRecreate)
{
    /// <summary>Builds the metadata for an <see cref="ObjectKind"/> from the canonical tables.</summary>
    public static ProjectObjectKind For(ObjectKind kind) => new(
        Kind: kind,
        TypeToken: SchemaCompareObjectType.OfKind(kind),
        Phase: RawObjectMeta.Phase(kind),
        Folder: RawObjectMeta.Folder(kind),
        ComparesByIdentityOnly: RawObjectMeta.ComparesByIdentityOnly(kind),
        IsDestructiveRecreate: RawObjectMeta.IsDestructiveRecreate(kind));
}
