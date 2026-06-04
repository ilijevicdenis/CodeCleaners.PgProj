using System;

namespace PgProj.Core.Packaging;

/// <summary>
/// Raised when a <c>.pgpkg</c> is malformed, missing a required entry, or fails its integrity check
/// (a tampered <c>sourceChecksum</c>). Carries a clear, user-facing message so the CLI can surface it
/// without a stack trace.
/// </summary>
public sealed class PgPkgFormatException : Exception
{
    public PgPkgFormatException(string message) : base(message) { }
    public PgPkgFormatException(string message, Exception inner) : base(message, inner) { }
}
