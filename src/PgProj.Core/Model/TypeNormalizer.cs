using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace PgProj.Core.Model;

/// <summary>
/// Reduces a Postgres data type to a single canonical spelling so that the two sources we
/// compare — a hand-written .sql file and the live catalog read back through Npgsql — agree.
/// Without this, "varchar(50)" (project) vs "character varying(50)" (catalog) would generate
/// an endless phantom ALTER COLUMN on every deploy. This is the single most important piece of
/// diff-quality plumbing, mirroring the type canonicalization SSDT performs internally.
/// </summary>
public static class TypeNormalizer
{
    // Alias -> canonical base name (lengths/precision are preserved separately).
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["int"] = "integer",
        ["int4"] = "integer",
        ["int2"] = "smallint",
        ["int8"] = "bigint",
        ["serial"] = "integer",      // serial is sugar for integer + sequence default
        ["serial4"] = "integer",
        ["serial8"] = "bigint",
        ["bigserial"] = "bigint",
        ["bool"] = "boolean",
        ["float4"] = "real",
        ["float8"] = "double precision",
        ["float"] = "double precision",
        ["double"] = "double precision",
        ["decimal"] = "numeric",
        ["varchar"] = "character varying",
        ["char"] = "character",
        ["bpchar"] = "character",
        ["timestamptz"] = "timestamp with time zone",
        ["timetz"] = "time with time zone",
        ["timestamp"] = "timestamp without time zone",
        ["time"] = "time without time zone",
    };

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public static string Normalize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var text = Whitespace.Replace(raw.Trim(), " ");

        // Split off a trailing array marker so "int[]" -> base "int", suffix "[]".
        var arraySuffix = string.Empty;
        while (text.EndsWith("[]", StringComparison.Ordinal))
        {
            arraySuffix += "[]";
            text = text[..^2].TrimEnd();
        }

        // Split off a parenthesised length/precision spec, e.g. "varchar(50)" or "numeric(12, 2)".
        var argSpec = string.Empty;
        var open = text.IndexOf('(');
        if (open >= 0 && text.EndsWith(")", StringComparison.Ordinal))
        {
            var inner = text[(open + 1)..^1];
            argSpec = "(" + NormalizeArgs(inner) + ")";
            text = text[..open].TrimEnd();
        }

        var baseType = text.ToLowerInvariant();
        if (Aliases.TryGetValue(baseType, out var canonical))
            baseType = canonical;

        // "character varying" with no length is just "text"-like but keep as-is; spacing already collapsed.
        return baseType + argSpec + arraySuffix;
    }

    private static string NormalizeArgs(string inner)
    {
        var parts = inner.Split(',');
        var sb = new StringBuilder();
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(parts[i].Trim());
        }
        return sb.ToString();
    }
}
