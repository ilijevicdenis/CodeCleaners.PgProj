using System;

namespace PgProj.Core.Model.Identity;

/// <summary>How a source object relates to a target object, decided purely from the identity triple.</summary>
public enum IdentityChangeKind
{
    /// <summary>Same <see cref="StableId"/> and same <see cref="CanonicalHash"/> and same FQN — no change.</summary>
    Unchanged,

    /// <summary>Same <see cref="StableId"/>, same <see cref="CanonicalHash"/>, but the FQN changed —
    /// a pure rename (structure and meaning preserved, only the name moved).</summary>
    Rename,

    /// <summary>Same <see cref="StableId"/> but a different <see cref="CanonicalHash"/> — the object is the
    /// "same" entity whose meaning changed in place. (If the FQN ALSO changed, it's a rename + alter, still
    /// reported as <see cref="Alter"/> with <see cref="IdentityDiffResult.FqnChanged"/> set.)</summary>
    Alter,

    /// <summary>Different <see cref="StableId"/> — structurally a different object: Drop the target, Create
    /// the source. (The degenerate "one side is absent" cases are also Drop/Create.)</summary>
    DropAndCreate,
}

/// <summary>The classifier's verdict for one (source, target) pair, with the flags the engine needs.</summary>
public readonly record struct IdentityDiffResult(IdentityChangeKind Kind, bool FqnChanged)
{
    public bool IsUnchanged => Kind == IdentityChangeKind.Unchanged;
}

/// <summary>
/// The diff rule the deploy engine (B11, issue #53) consumes: given the identity triple of a source
/// object and the matching target object, classify the change WITHOUT looking at names or bodies again.
/// <list type="bullet">
/// <item>same StableId + same CanonicalHash + same FQN → <see cref="IdentityChangeKind.Unchanged"/></item>
/// <item>same StableId + same CanonicalHash + different FQN → <see cref="IdentityChangeKind.Rename"/></item>
/// <item>same StableId + different CanonicalHash → <see cref="IdentityChangeKind.Alter"/> (FQN change flagged)</item>
/// <item>different StableId → <see cref="IdentityChangeKind.DropAndCreate"/></item>
/// </list>
/// This is intentionally NOT wired into <see cref="Comparison.SchemaComparer"/> yet — it is the pure
/// decision function the engine layer will call once it pairs objects across the two models.
/// </summary>
public static class IdentityDiff
{
    /// <summary>Classify a matched (source, target) pair from their identities.</summary>
    public static IdentityDiffResult Classify(ObjectIdentity source, ObjectIdentity target)
    {
        if (source.StableId != target.StableId)
            return new IdentityDiffResult(IdentityChangeKind.DropAndCreate, FqnChanged: false);

        var fqnChanged = !string.Equals(source.QualifiedName, target.QualifiedName, StringComparison.OrdinalIgnoreCase);

        if (source.CanonicalHash != target.CanonicalHash)
            return new IdentityDiffResult(IdentityChangeKind.Alter, fqnChanged);

        return fqnChanged
            ? new IdentityDiffResult(IdentityChangeKind.Rename, FqnChanged: true)
            : new IdentityDiffResult(IdentityChangeKind.Unchanged, FqnChanged: false);
    }

    /// <summary>Source object with no matching target → Create (modelled as Drop+Create with nothing to drop).</summary>
    public static IdentityDiffResult Create() =>
        new(IdentityChangeKind.DropAndCreate, FqnChanged: false);

    /// <summary>Target object with no matching source → Drop (modelled as Drop+Create with nothing to create).</summary>
    public static IdentityDiffResult Drop() =>
        new(IdentityChangeKind.DropAndCreate, FqnChanged: false);
}
