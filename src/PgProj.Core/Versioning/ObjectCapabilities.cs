using PgProj.Core.Model;

namespace PgProj.Core.Versioning;

/// <summary>
/// What kinds of change a given <see cref="ObjectKind"/> supports as an in-place <c>ALTER</c> versus
/// what must be done by drop-and-recreate, for one PostgreSQL version. This is the facet the
/// diff/generator consults to choose the migration shape — instead of an inline <c>if version &gt;= …</c>
/// scattered across the comparer.
///
/// The default (latest) profile reflects modern PostgreSQL: a table column's type/nullability/default
/// changes are all in-place <c>ALTER TABLE … ALTER COLUMN …</c> (so the comparer keeps emitting
/// <c>AlterColumnChange</c> exactly as before); types/domains/foreign-tables remain destructive-recreate
/// kinds (matching <see cref="Comparison.RawObjectMeta.IsDestructiveRecreate"/>). An older profile can
/// override a flag where that version genuinely lacked the ALTER path.
/// </summary>
public sealed class ObjectCapabilities
{
    /// <summary>Can a column's <em>data type</em> be changed in place with ALTER COLUMN … TYPE?</summary>
    public bool CanAlterColumnType { get; init; } = true;

    /// <summary>Can a column's NOT NULL be toggled in place (SET/DROP NOT NULL)?</summary>
    public bool CanAlterColumnNullability { get; init; } = true;

    /// <summary>Can a column's DEFAULT be changed in place (SET/DROP DEFAULT)?</summary>
    public bool CanAlterColumnDefault { get; init; } = true;

    /// <summary>
    /// Whether a body change to a raw object of <paramref name="kind"/> can be applied in place
    /// (e.g. CREATE OR REPLACE) versus requiring a drop + recreate. Mirrors the kinds the comparer
    /// already treats as in-place vs destructive; centralised here so the decision is one call.
    /// </summary>
    public bool CanAlterInPlace(ObjectKind kind) => !MustRecreate(kind);

    /// <summary>
    /// Whether a changed raw object of <paramref name="kind"/> must be dropped and recreated (it cannot
    /// be redefined in place). Defaults to the destructive-recreate set
    /// (<see cref="Comparison.RawObjectMeta.IsDestructiveRecreate"/>).
    /// </summary>
    public bool MustRecreate(ObjectKind kind) => Comparison.RawObjectMeta.IsDestructiveRecreate(kind);

    /// <summary>
    /// The single ALTER-vs-recreate verdict the comparer asks for a changed table column: true when all
    /// the column deltas that differ are individually ALTER-able on this version, false when at least one
    /// requires a table/column recreate. <paramref name="typeChanged"/>/<paramref name="nullabilityChanged"/>/
    /// <paramref name="defaultChanged"/> say which facets actually differ between old and new.
    /// </summary>
    public bool CanAlterColumn(bool typeChanged, bool nullabilityChanged, bool defaultChanged)
    {
        if (typeChanged && !CanAlterColumnType) return false;
        if (nullabilityChanged && !CanAlterColumnNullability) return false;
        if (defaultChanged && !CanAlterColumnDefault) return false;
        return true;
    }

    /// <summary>The default (modern PostgreSQL) capability set — every in-place ALTER path available.</summary>
    public static ObjectCapabilities Default { get; } = new();
}
