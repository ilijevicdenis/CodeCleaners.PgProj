using System.Collections.Generic;

namespace PgProj.Core.Snapshot;

/// <summary>
/// The verdict of checking a <see cref="SchemaSnapshot"/> against the environment that would consume it:
/// whether the snapshot is <see cref="IsStale"/> and, if so, the human-readable <see cref="Reasons"/>.
/// A snapshot is stale when its <em>format version</em> is not the one this build understands, or when its
/// captured <em>source PostgreSQL major version</em> differs from the version the consumer expects (e.g.
/// the project's <c>TargetPostgresVersion</c>). Staleness is a <b>signal</b>, not a hard error — the
/// snapshot still resolves and compares; the caller decides whether to warn or fail.
/// </summary>
/// <param name="IsStale">True when at least one staleness reason applies.</param>
/// <param name="Reasons">Zero or more clear, user-facing explanations (empty when not stale).</param>
public sealed record SchemaSnapshotStaleness(bool IsStale, IReadOnlyList<string> Reasons)
{
    /// <summary>A fresh (not-stale) verdict with no reasons.</summary>
    public static SchemaSnapshotStaleness Fresh { get; } =
        new(false, System.Array.Empty<string>());

    /// <summary>Builds a verdict from a reason list (stale iff the list is non-empty).</summary>
    public static SchemaSnapshotStaleness From(IReadOnlyList<string> reasons) =>
        reasons.Count == 0 ? Fresh : new SchemaSnapshotStaleness(true, reasons);
}
