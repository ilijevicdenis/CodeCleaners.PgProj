using System;
using System.Security.Cryptography;
using System.Text;

namespace PgProj.Core.Model.Identity;

/// <summary>
/// Deterministic, culture-independent content hashing for the identity triple. Every input is encoded
/// as UTF-8 and digested with SHA-256, so the result is byte-identical across builds, machines, OSes
/// and .NET versions — no <see cref="Guid"/>-per-run, no culture-sensitive <c>string.GetHashCode</c>,
/// no platform line-ending drift (callers feed already-normalised, LF-free canonical text).
/// <para>
/// Fields are joined with the ASCII Unit Separator (0x1F), a byte that never appears in SQL identifiers
/// or canonical SQL text, so the field framing is unambiguous (no "a|b" vs "a" + "|b" collision).
/// </para>
/// </summary>
public static class StableHash
{
    // ASCII Unit Separator — an unambiguous field delimiter that can't occur in the hashed payloads.
    private const char FieldSep = '';

    /// <summary>Hash an ordered set of fields into a lowercase 64-char hex SHA-256 digest.</summary>
    public static string Of(params string?[] fields)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < fields.Length; i++)
        {
            if (i > 0) sb.Append(FieldSep);
            sb.Append(fields[i] ?? string.Empty);
        }
        return OfText(sb.ToString());
    }

    /// <summary>Hash a single pre-assembled payload string. Lowercase 64-char hex SHA-256.</summary>
    public static string OfText(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(bytes);
    }
}
