using System;
using System.IO;
using System.Linq;
using PgProj.Core.Project;

namespace PgProj.Lsp.Workspace;

/// <summary>
/// Locates the <c>.pgproj</c> for a workspace root and loads it with an open-buffer overlay so the live
/// model reflects UNSAVED edits. The overlay is wired through <see cref="DatabaseProject.ObjectContentTransform"/>
/// (relative-path + on-disk text → effective text): for any file whose buffer is open, we substitute the
/// editor's current text; every other file parses from disk. This keeps the live build path byte-identical
/// to <c>pgproj build</c> for saved files while picking up in-flight edits for open ones.
/// </summary>
public static class WorkspaceProject
{
    /// <summary>The first <c>.pgproj</c> at or under <paramref name="rootPath"/> (shallow-first), or null.</summary>
    public static string? FindProjectFile(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath)) return null;
        // Prefer a manifest in the root itself; fall back to the nearest one below it.
        var here = Directory.EnumerateFiles(rootPath, "*.pgproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (here is not null) return here;
        return Directory.EnumerateFiles(rootPath, "*.pgproj", SearchOption.AllDirectories)
            .OrderBy(p => p.Count(c => c is '/' or '\\'))
            .FirstOrDefault();
    }

    /// <summary>
    /// Loads the project at <paramref name="projectFilePath"/> with an overlay that swaps in the current
    /// text of any open document (looked up by absolute path against <paramref name="store"/>).
    /// </summary>
    public static DatabaseProject LoadWithOverlay(string projectFilePath, DocumentStore store)
    {
        var project = DatabaseProject.Load(projectFilePath);
        return project with
        {
            ObjectContentTransform = (relativePath, diskText) =>
            {
                var abs = Path.GetFullPath(Path.Combine(project.ProjectDirectory, relativePath));
                var uri = DocumentUri.FromPath(abs);
                return store.Get(uri)?.Text ?? diskText;
            },
        };
    }
}
