// EP-VS #25 Route B (modern). Resolving the active .pgproj + the connection from selection/context.
namespace PgProj.VisualStudio.Engine;

/// <summary>
/// Helpers to locate the <c>.pgproj</c> a command should act on and the publish/compare connection. The
/// modern (out-of-process) model gives commands the selected item path via the client context; from there
/// we walk up to the nearest project. Connection resolution reads the <c>PGPROJ_CONNECTION</c> env var the
/// engine also honours (the connection string is never stored in the project).
/// </summary>
internal static class PgProjContext
{
    /// <summary>The connection-string environment variable the engine/CLI also honour.</summary>
    public const string ConnectionEnvVar = "PGPROJ_CONNECTION";

    /// <summary>
    /// Given a selected file/folder path, returns the nearest <c>.pgproj</c> — the file itself if it is
    /// one, else the first <c>*.pgproj</c> found walking up the directory tree. Null if none.
    /// </summary>
    public static string? FindNearestProject(string? selectedPath)
    {
        if (string.IsNullOrWhiteSpace(selectedPath))
            return null;

        if (selectedPath.EndsWith(".pgproj", StringComparison.OrdinalIgnoreCase) && File.Exists(selectedPath))
            return selectedPath;

        var dir = Directory.Exists(selectedPath) ? selectedPath : Path.GetDirectoryName(selectedPath);
        while (!string.IsNullOrEmpty(dir))
        {
            var match = Directory.EnumerateFiles(dir, "*.pgproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (match is not null)
                return match;

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    /// <summary>
    /// Resolves the publish/compare target connection from <c>PGPROJ_CONNECTION</c> (never stored in the
    /// project). Returns null when unset (the caller advises the user how to set it).
    /// </summary>
    public static string? ResolveConnection()
        => Environment.GetEnvironmentVariable(ConnectionEnvVar) is { Length: > 0 } conn ? conn : null;

    /// <summary>
    /// The default publish profile next to the project, if one exists: <c>&lt;Name&gt;.pgpublish.json</c>
    /// first, else the single <c>*.pgpublish.json</c> in the project directory. Used to prefill the
    /// Publish dialog; the user can point it anywhere else.
    /// </summary>
    public static string? FindDefaultProfile(string projectPath)
    {
        var dir = Path.GetDirectoryName(projectPath);
        if (string.IsNullOrEmpty(dir))
            return null;

        var named = Path.Combine(dir, Path.GetFileNameWithoutExtension(projectPath) + ".pgpublish.json");
        if (File.Exists(named))
            return named;

        var all = Directory.EnumerateFiles(dir, "*.pgpublish.json", SearchOption.TopDirectoryOnly).Take(2).ToList();
        return all.Count == 1 ? all[0] : null;
    }
}
