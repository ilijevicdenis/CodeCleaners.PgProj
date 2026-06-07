using System;

namespace PgProj.Core.Model.Identity;

/// <summary>
/// A semantic hash over an object's <em>canonical form</em> (issue #42, Phase 9). It changes ONLY when
/// the object's meaning changes — it is insensitive to whitespace, comments, reformatting, dollar-quote
/// tag choice, punctuation spacing and literal casts — because the canonical text it hashes is produced
/// by <see cref="Comparison.Canonicalizer"/>, the exact same normalizer the diff engine uses to decide
/// whether two bodies differ. Thus "CanonicalHash unchanged" ⇔ "the comparer sees no semantic diff".
/// <para>
/// Unlike <see cref="StableId"/>, the canonical form DOES include the object's defining text/structure
/// (so a real change is detected) but, like StableId, it excludes the schema-qualified name where the
/// name is incidental, so a rename is a CanonicalHash-stable Alter-of-nothing (the engine classifies it
/// as Rename via the FQN check, not via the hash). Deterministic across builds/machines (ordinal SHA-256).
/// </para>
/// <para>
/// NOTE: the canonical basis is today the comparer's regex normalizers. Full Phase-8 canonical-model
/// hardening (issue #51) — a parse-and-reprint canonical AST — will refine the canonical form;
/// CanonicalHash inherits that automatically because it hashes whatever <c>Canonicalizer</c> emits.
/// </para>
/// </summary>
public readonly struct CanonicalHash : IEquatable<CanonicalHash>
{
    /// <summary>The lowercase 64-char hex SHA-256 of <c>kind + canonical form</c>.</summary>
    public string Value { get; }

    private CanonicalHash(string value) => Value = value;

    /// <summary>Build from an object kind discriminator and its canonical (meaning-only) form.</summary>
    public static CanonicalHash From(string kind, string canonicalForm) =>
        new(StableHash.Of(kind, canonicalForm));

    /// <summary>Wrap a precomputed hex digest (e.g. when rehydrating from a manifest).</summary>
    public static CanonicalHash FromDigest(string digest) => new(digest);

    public bool Equals(CanonicalHash other) =>
        string.Equals(Value, other.Value, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is CanonicalHash o && Equals(o);
    public override int GetHashCode() => Value is null ? 0 : StringComparer.Ordinal.GetHashCode(Value);
    public override string ToString() => Value ?? "";

    public static bool operator ==(CanonicalHash a, CanonicalHash b) => a.Equals(b);
    public static bool operator !=(CanonicalHash a, CanonicalHash b) => !a.Equals(b);
}
