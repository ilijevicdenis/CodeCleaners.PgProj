using System.Text.Json.Serialization;

namespace PgProj.Core.Snapshot;

/// <summary>
/// The self-describing header of a <c>.schema.snapshot</c> artifact. Where a <c>.pgpkg</c> manifest
/// describes a <em>project build</em>, this describes a point-in-time capture of a <em>live database</em>:
/// the snapshot <see cref="FormatVersion"/>, the source server's PostgreSQL version (both the human
/// <see cref="SourcePgVersion"/> string and the parsed <see cref="SourcePgMajorVersion"/>), the
/// <see cref="ToolVersion"/> that wrote it, the caller-injected <see cref="CreatedUtc"/> stamp, and a
/// <see cref="ModelChecksum"/> over the serialized model payload.
/// </summary>
/// <remarks>
/// <see cref="CreatedUtc"/> and <see cref="ToolVersion"/> are <em>injected by the caller</em> (the CLI),
/// never read from <c>DateTime.Now</c> inside core code — the same determinism contract the
/// <see cref="Packaging.PgPkgManifest"/> follows. <see cref="SourcePgVersion"/> /
/// <see cref="SourcePgMajorVersion"/> are read from the live server at capture time and are the basis for
/// staleness detection (see <see cref="SchemaSnapshotStaleness"/>).
/// </remarks>
public sealed record SchemaSnapshotManifest(
    string SourcePgVersion,
    int SourcePgMajorVersion,
    string ToolVersion,
    string CreatedUtc,
    string ModelChecksum)
{
    /// <summary>The current snapshot format version, bumped on a breaking layout change.</summary>
    public string FormatVersion { get; init; } = SchemaSnapshot.CurrentFormatVersion;

    /// <summary>An optional non-secret label for the captured database (e.g. its database name).</summary>
    public string? SourceName { get; init; }
}
