// EP-VS — per-user "enter the connection once" persistence, shared with the OOP extension's store:
// %APPDATA%\pgproj\connections.json, keys = lowercased project full paths, values = base64 DPAPI
// (CurrentUser) blobs. Connection strings are NEVER written into the .pgproj (committed files stay
// secret-free). net472 reads/writes the same JSON the OOP side produces with System.Text.Json; the
// flat string→string shape is (de)serialized here without a JSON library dependency to keep the
// VSIX payload lean.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PgProj.VisualStudio.ProjectSystem.Commands
{
    /// <summary>
    /// Remembers the last-used connection string per <c>.pgproj</c> so the import dialog prefills it.
    /// Values are DPAPI-protected (CurrentUser); a blob that fails to decrypt is treated as absent.
    /// </summary>
    internal static class ConnectionStore
    {
        private static readonly object Gate = new object();

        private static string StorePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "pgproj", "connections.json");

        /// <summary>The remembered connection for the project, or null. <paramref name="purpose"/> keeps
        /// independent connections per feature (null = the import connection, "testgen" = the test-run one).</summary>
        public static string TryGet(string projectPath, string purpose = null)
        {
            lock (Gate)
            {
                var map = Load();
                if (!map.TryGetValue(Key(projectPath, purpose), out var blob))
                    return null;
                try
                {
                    var bytes = ProtectedData.Unprotect(Convert.FromBase64String(blob), null, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(bytes);
                }
                catch
                {
                    return null;
                }
            }
        }

        /// <summary>Remembers (or replaces) the project's connection string.</summary>
        public static void Save(string projectPath, string connectionString, string purpose = null)
        {
            lock (Gate)
            {
                var map = Load();
                var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(connectionString), null, DataProtectionScope.CurrentUser);
                map[Key(projectPath, purpose)] = Convert.ToBase64String(bytes);
                Write(map);
            }
        }

        /// <summary>Drops the remembered connection for the project.</summary>
        public static void Forget(string projectPath, string purpose = null)
        {
            lock (Gate)
            {
                var map = Load();
                if (map.Remove(Key(projectPath, purpose)))
                    Write(map);
            }
        }

        // ---- flat {"key":"value"} JSON, compatible with the OOP store's System.Text.Json output ----

        private static readonly Regex Pair = new Regex(
            "\"(?<k>(?:[^\"\\\\]|\\\\.)*)\"\\s*:\\s*\"(?<v>(?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);

        private static Dictionary<string, string> Load()
        {
            var map = new Dictionary<string, string>();
            try
            {
                if (!File.Exists(StorePath))
                    return map;
                foreach (Match m in Pair.Matches(File.ReadAllText(StorePath)))
                    map[Unescape(m.Groups["k"].Value)] = Unescape(m.Groups["v"].Value);
            }
            catch
            {
                // A corrupt store must never break the dialog — start fresh; the next Save rewrites it.
            }
            return map;
        }

        private static void Write(Dictionary<string, string> map)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath));
            var sb = new StringBuilder().Append("{\n");
            var first = true;
            foreach (var pair in map)
            {
                if (!first) sb.Append(",\n");
                first = false;
                sb.Append("  \"").Append(Escape(pair.Key)).Append("\": \"").Append(Escape(pair.Value)).Append('"');
            }
            sb.Append("\n}");
            File.WriteAllText(StorePath, sb.ToString());
        }

        private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        private static string Unescape(string s) => Regex.Replace(s, "\\\\(.)", "$1");

        // null purpose keeps the historical key shape, so already-stored import connections survive.
        private static string Key(string projectPath, string purpose) =>
            Path.GetFullPath(projectPath).ToLowerInvariant() + (purpose == null ? "" : "|" + purpose);
    }
}
