// EP-VS. Per-user "enter the connection once" persistence for the Publish / Schema Compare /
// Import dialogs. Connection strings are NEVER written into the .pgproj or a .pgpublish.json
// (committed files must stay secret-free); instead they live OUTSIDE the repo in
// %APPDATA%\pgproj\connections.json, keyed by project path, DPAPI-encrypted for the current
// Windows user (unreadable by other users/machines).
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PgProj.VisualStudio.Engine;

/// <summary>
/// Remembers the last-used connection string per <c>.pgproj</c> so the dialogs prefill it.
/// Values are DPAPI-protected (CurrentUser scope); a blob that fails to decrypt (other
/// user/machine, corruption) is treated as absent. All methods are safe to call concurrently.
/// </summary>
internal static class ConnectionStore
{
    private static readonly Lock Gate = new();

    private static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "pgproj", "connections.json");

    /// <summary>The remembered connection for the project, or null when none/undecryptable.</summary>
    public static string? TryGet(string projectPath)
    {
        lock (Gate)
        {
            if (!Load().TryGetValue(Key(projectPath), out var blob))
                return null;
            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(blob), optionalEntropy: null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>Remembers (or replaces) the project's connection string.</summary>
    public static void Save(string projectPath, string connectionString)
    {
        lock (Gate)
        {
            var map = Load();
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(connectionString), optionalEntropy: null, DataProtectionScope.CurrentUser);
            map[Key(projectPath)] = Convert.ToBase64String(bytes);
            Write(map);
        }
    }

    /// <summary>Drops the remembered connection for the project (the dialog checkbox was unchecked).</summary>
    public static void Forget(string projectPath)
    {
        lock (Gate)
        {
            var map = Load();
            if (map.Remove(Key(projectPath)))
                Write(map);
        }
    }

    private static Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(StorePath))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StorePath)) ?? new();
        }
        catch
        {
            // A corrupt store must never break a dialog — start fresh; the next Save rewrites it.
        }
        return new Dictionary<string, string>();
    }

    private static void Write(Dictionary<string, string> map)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Key(string projectPath) => Path.GetFullPath(projectPath).ToLowerInvariant();
}
