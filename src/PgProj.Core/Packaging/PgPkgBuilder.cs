using System.Collections.Generic;
using System.IO;
using PgProj.Core.Model;
using PgProj.Core.Project;

namespace PgProj.Core.Packaging;

/// <summary>
/// Assembles a <see cref="PgPkg"/> from a built <see cref="DatabaseProject"/>: gathers the project's
/// resolved <c>.sql</c> sources, computes the <see cref="SourceChecksum"/>, and stamps the manifest.
/// </summary>
/// <remarks>
/// Determinism contract: this type takes the volatile fields (<c>createdUtc</c>, <c>toolVersion</c>) as
/// explicit parameters — it never reads <c>DateTime.Now</c>. The CLI injects the stamp, so two builds of
/// the same sources with the same stamp yield byte-identical packages.
/// </remarks>
public static class PgPkgBuilder
{
    /// <summary>
    /// Builds a package from an already-built model plus the project's file list. <paramref name="files"/>
    /// are absolute paths (as produced by <see cref="ProjectBuildResult.Files"/>); they are carried under
    /// <c>sources/</c> keyed by their path relative to the project directory.
    /// </summary>
    public static PgPkg FromBuild(
        DatabaseProject project,
        DatabaseModel model,
        IReadOnlyList<string> files,
        string toolVersion,
        string createdUtc)
    {
        var sources = new List<PgPkgSource>();
        foreach (var file in files)
        {
            var rel = Path.GetRelativePath(project.ProjectDirectory, file).Replace('\\', '/');
            sources.Add(new PgPkgSource(rel, File.ReadAllText(file)));
        }

        var checksum = SourceChecksum.Compute(sources.ConvertAll(s => (s.RelativePath, s.Content)));

        var manifest = new PgPkgManifest(
            Name: project.Name,
            PgVersion: project.TargetPostgresVersion,
            ToolVersion: toolVersion,
            CreatedUtc: createdUtc,
            SourceChecksum: checksum);

        return PgPkg.Create(manifest, model, sources);
    }
}
