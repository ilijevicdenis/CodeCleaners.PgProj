namespace PgProj.Core.Project.References;

/// <summary>
/// Stable build error codes for the reference subsystem (EP-REF). They appear verbatim in build output
/// so a UI / CI log can match on the code, not the message text.
/// </summary>
public static class ReferenceErrorCodes
{
    /// <summary>A referenced project/artifact file does not exist on disk.</summary>
    public const string NotFound = "PGREF001";

    /// <summary>The reference graph contains a cycle (A → B → A). Reported instead of stack-overflowing.</summary>
    public const string Circular = "PGREF002";

    /// <summary>A referenced project failed its own build, so its model cannot be injected.</summary>
    public const string ReferencedBuildFailed = "PGREF003";

    /// <summary>A referenced artifact is not a readable/valid <c>.pgpkg</c>.</summary>
    public const string InvalidArtifact = "PGREF004";

    /// <summary>A <c>&lt;PackageReference/&gt;</c> was declared but NuGet restore is not yet implemented.</summary>
    public const string PackageRestoreNotImplemented = "PGREF005";
}

/// <summary>A diagnostic emitted while resolving references, carrying a stable <see cref="Code"/>.</summary>
public sealed record ReferenceDiagnostic(string Code, string Message)
{
    public override string ToString() => $"{Code}: {Message}";
}
