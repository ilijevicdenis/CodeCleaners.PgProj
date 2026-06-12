using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PgProj.Core.Comparison;
using PgProj.Core.Model;
using PgProj.Core.Project;

namespace PgProj.Core.Sync;

/// <summary>
/// FILE-scoped sync between one project .sql file and the live database — the engine behind the
/// editor's "Sync with Database" command (right-click a file → side-by-side diff → take the
/// database version, push the local version, or cancel). Pure-compute except for the explicit
/// apply entry points, so hosts can preview before committing in either direction:
///   * <see cref="InspectAsync"/> — local text vs the DB's canonical rendering of the SAME objects
///     (reuses <see cref="ReverseSync"/>'s plan, so the verdict matches `pgproj drift` exactly);
///   * <see cref="ApplyToLocal"/> — write the database's version into the project file;
///   * <see cref="BuildPushScriptAsync"/> — the forward migration that makes the DATABASE match the
///     file (the same comparer/script-generator publish uses, filtered to this file's objects).
/// </summary>
public static class FileSync
{
    public enum FileSyncStatus
    {
        /// <summary>No drift for this file — local and database agree.</summary>
        Identical,
        /// <summary>Both sides define the objects, with differences.</summary>
        Differs,
        /// <summary>The file's objects were dropped from (or never reached) the database.</summary>
        OnlyLocal,
        /// <summary>The database has objects this file would canonically own, but the file doesn't exist.</summary>
        OnlyInDatabase,
    }

    /// <summary>One file's sync verdict, carrying both texts so a host can diff them.</summary>
    public sealed record FileSyncState(
        string RelativePath,
        FileSyncStatus Status,
        string? LocalText,
        string? DatabaseText,
        string Summary);

    /// <summary>Computes the file's sync state against <paramref name="live"/>. Touches nothing.</summary>
    public static async Task<FileSyncState> InspectAsync(DatabaseProject project, DatabaseModel live, string relativeFile)
    {
        var rel = Normalize(relativeFile);
        var full = Path.Combine(project.ProjectDirectory, rel);
        var localText = File.Exists(full) ? SourceReader.ReadAllText(full) : null;

        var plan = await ReverseSync.PlanAsync(project, live, new DriftOptions { AllowDeletes = true }).ConfigureAwait(false);
        var fc = plan.FileChanges.FirstOrDefault(f => PathEquals(f.RelativePath, rel));

        if (fc is null)
            return new FileSyncState(rel, FileSyncStatus.Identical, localText, localText,
                "The file matches the database.");

        return fc.Kind switch
        {
            ProjectFileChangeKind.Update => new FileSyncState(rel, FileSyncStatus.Differs, localText, fc.NewContent, fc.Summary),
            ProjectFileChangeKind.Delete => new FileSyncState(rel, FileSyncStatus.OnlyLocal, localText, null, fc.Summary),
            _ => new FileSyncState(rel, FileSyncStatus.OnlyInDatabase, localText, fc.NewContent, fc.Summary),
        };
    }

    /// <summary>
    /// Takes the database's side: writes <see cref="FileSyncState.DatabaseText"/> into the project
    /// file (or deletes the file when its objects no longer exist in the database).
    /// </summary>
    public static void ApplyToLocal(DatabaseProject project, FileSyncState state)
    {
        var full = Path.Combine(project.ProjectDirectory, state.RelativePath);
        if (state.Status == FileSyncStatus.OnlyLocal)
        {
            if (File.Exists(full)) File.Delete(full);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, state.DatabaseText ?? string.Empty);
    }

    /// <summary>
    /// The forward migration limited to THIS file's objects: every change the publish comparer
    /// would emit (project → database) whose canonical unit is owned by the file. Empty script
    /// (header only) when the database already matches. The caller executes it via
    /// <see cref="Publishing.DatabaseDeployer"/> after the user confirms.
    /// </summary>
    public static async Task<(string Script, int ChangeCount, bool HasDestructive)> BuildPushScriptAsync(
        DatabaseProject project, DatabaseModel live, string relativeFile, bool allowDrops = false)
    {
        var rel = Normalize(relativeFile);
        var projectModel = (await project.BuildAsync().ConfigureAwait(false)).Model;
        var changes = new SchemaComparer().Compare(projectModel, live, new ComparerOptions
        {
            DropObjectsNotInSource = allowDrops,
        });

        var units = new HashSet<string>(
            await ReverseSync.UnitsOfFileAsync(project, rel).ConfigureAwait(false),
            StringComparer.OrdinalIgnoreCase);

        // Index-drop resolution needs whichever model still HAS the index: prefer the project's,
        // fall back to the live one (the drop case means the project no longer has it).
        var filtered = changes
            .Where(ch => (ReverseSync.UnitOf(ch, projectModel) ?? ReverseSync.UnitOf(ch, live)) is { } u && units.Contains(u))
            .ToList();

        var script = new DeployScriptGenerator().Generate(filtered);
        return (script, filtered.Count, filtered.Any(c => c.IsDestructive));
    }

    private static string Normalize(string rel) => rel.Replace('\\', '/').TrimStart('/');

    private static bool PathEquals(string a, string b) =>
        string.Equals(a.Replace('\\', '/'), b.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
