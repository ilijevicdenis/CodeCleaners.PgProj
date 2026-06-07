using System;

namespace PgProj.Core.Snapshot;

/// <summary>
/// Raised when a <c>.schema.snapshot</c> file is malformed, missing its model/manifest, or fails its
/// integrity check (a tampered <c>modelChecksum</c>). Carries a clear, user-facing message so the CLI can
/// surface it without a stack trace — mirrors <see cref="Packaging.PgPkgFormatException"/>.
/// </summary>
public sealed class SchemaSnapshotFormatException : Exception
{
    public SchemaSnapshotFormatException(string message) : base(message) { }
    public SchemaSnapshotFormatException(string message, Exception inner) : base(message, inner) { }
}
