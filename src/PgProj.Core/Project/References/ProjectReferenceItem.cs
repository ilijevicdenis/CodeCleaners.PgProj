using System;
using System.IO;

namespace PgProj.Core.Project.References;

/// <summary>The three reference kinds a <c>.pgproj</c> can declare (mirrors SSDT project/DACPAC/NuGet refs).</summary>
public enum ReferenceKind
{
    /// <summary>Another <c>.pgproj</c> — built from source, then injected as external objects.</summary>
    Project,

    /// <summary>A pre-built <c>.pgpkg</c> artifact (the DACPAC analogue) — its embedded model is injected.</summary>
    Artifact,

    /// <summary>A NuGet-distributed <c>.pgpkg</c> by id (+ optional version). NuGet restore is a follow-up.</summary>
    Package,
}

/// <summary>
/// One declared reference, exactly as it appeared in the <c>.pgproj</c>. The <see cref="Include"/> is a
/// path (project/artifact, relative to the referencing project's directory) or a package id (package).
/// Resolution into an external model is the resolver's job — this record only carries the declaration.
/// </summary>
public sealed record ProjectReferenceItem(
    ReferenceKind Kind,
    string Include,
    string? Version,
    string OwningProjectDirectory)
{
    /// <summary>
    /// The absolute, normalized path the <see cref="Include"/> resolves to for path-based references
    /// (Project / Artifact). Always resolved against the OWNING project's directory so a chain of
    /// references uses each project's own relative roots. Meaningless for <see cref="ReferenceKind.Package"/>.
    /// </summary>
    public string ResolvedPath =>
        Path.GetFullPath(Path.Combine(OwningProjectDirectory, Include.Replace('\\', '/')));

    /// <summary>A stable, case-insensitive key used for cycle detection and de-duplication.</summary>
    public string Key => Kind == ReferenceKind.Package
        ? $"package::{Include.ToLowerInvariant()}"
        : ResolvedPath.ToLowerInvariant();

    public override string ToString() => Kind switch
    {
        ReferenceKind.Package => $"PackageReference {Include}{(Version is null ? "" : $" {Version}")}",
        _ => $"{Kind}Reference {Include}",
    };
}
