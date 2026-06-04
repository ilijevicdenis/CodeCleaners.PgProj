using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace PgProj.Core.Packaging;

/// <summary>
/// Computes a deterministic checksum over a project's <c>.sql</c> sources. The inputs are normalized
/// (CRLF→LF, trailing whitespace stripped, sources joined in a fixed order with their relative path as
/// a delimiter) so the result is independent of OS line endings and stable across machines. Used as the
/// manifest's <c>sourceChecksum</c> to detect build/deploy drift.
/// </summary>
public static class SourceChecksum
{
    /// <param name="sources">Relative-path → file-content pairs. Order is fixed internally (sorted by
    /// path, ordinal) so the caller's enumeration order does not affect the digest.</param>
    public static string Compute(IEnumerable<(string RelativePath, string Content)> sources)
    {
        var ordered = new List<(string Path, string Content)>();
        foreach (var (path, content) in sources)
            ordered.Add((Normalize(path.Replace('\\', '/')), Normalize(content)));
        ordered.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));

        var sb = new StringBuilder();
        foreach (var (path, content) in ordered)
        {
            sb.Append(path).Append('\n');
            sb.Append(content).Append('\n');
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return "sha256:" + Convert.ToHexStringLower(bytes);
    }

    private static string Normalize(string text)
    {
        // CRLF/CR → LF, then trim trailing whitespace per line, then trim a trailing blank tail.
        var lf = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = lf.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            lines[i] = lines[i].TrimEnd();
        return string.Join('\n', lines).TrimEnd('\n');
    }
}
