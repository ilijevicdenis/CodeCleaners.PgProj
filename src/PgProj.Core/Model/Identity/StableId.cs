using System;

namespace PgProj.Core.Model.Identity;

/// <summary>
/// A durable, NAME-INDEPENDENT identity derived from an object's intrinsic structural traits — its
/// kind plus an ordered structural fingerprint (for a table: ordered column names+types+nullability +
/// keys; for a function: the argument-type signature; etc.) — deliberately EXCLUDING the object's own
/// schema-qualified name.
/// <para>
/// Excluding the FQN is precisely what makes a pure <b>rename</b> identity-preserving: rename a table
/// and its columns/keys are unchanged, so its <see cref="StableId"/> is unchanged, and the diff engine
/// can emit <c>RENAME</c> instead of a heuristic Drop+Create. A <b>structural</b> change (add/drop/retype
/// a column, change the arg signature) changes the fingerprint and therefore the id.
/// </para>
/// <para>
/// Deterministic and stable across builds and machines: the fingerprint is hashed via
/// <see cref="StableHash"/> (ordinal UTF-8 SHA-256), never a per-run <see cref="Guid"/> or culture-
/// sensitive hash. The exact same fingerprint is computed for a project-built record and an
/// introspected (live) record, so equivalent objects match (see <see cref="ObjectIdentityComputer"/>).
/// </para>
/// </summary>
public readonly struct StableId : IEquatable<StableId>
{
    /// <summary>The lowercase 64-char hex SHA-256 of <c>kind + structural fingerprint</c>.</summary>
    public string Value { get; }

    private StableId(string value) => Value = value;

    /// <summary>Build a StableId from an object kind discriminator and its already-assembled,
    /// name-independent structural fingerprint.</summary>
    public static StableId From(string kind, string structuralFingerprint) =>
        new(StableHash.Of(kind, structuralFingerprint));

    /// <summary>Wrap a precomputed hex digest (e.g. when rehydrating from a manifest).</summary>
    public static StableId FromDigest(string digest) => new(digest);

    public bool Equals(StableId other) =>
        string.Equals(Value, other.Value, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is StableId o && Equals(o);
    public override int GetHashCode() => Value is null ? 0 : StringComparer.Ordinal.GetHashCode(Value);
    public override string ToString() => Value ?? "";

    public static bool operator ==(StableId a, StableId b) => a.Equals(b);
    public static bool operator !=(StableId a, StableId b) => !a.Equals(b);
}
