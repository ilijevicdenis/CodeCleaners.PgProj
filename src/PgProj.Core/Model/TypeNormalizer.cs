using System;
using System.Collections.Concurrent;
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
        // --- #51 additions -----------------------------------------------------------------------
        // Bit-string: only `varbit` is a true alias (→ "bit varying"); `bit`/`bit varying` are already
        // canonical. `bit` alone means bit(1) but we preserve the user's explicit length spec verbatim.
        ["varbit"] = "bit varying",
        // Pseudo / catalog-name spellings the live reader can emit for an unparenthesised numeric.
        ["dec"] = "numeric",
        // Geometric/network/uuid/json/xml/money/bytea have NO short aliases in Postgres — their only
        // spelling IS the canonical one, so they intentionally pass through NormalizeCore unchanged:
        //   bytea, jsonb, json, xml, money, uuid, inet, cidr, macaddr, macaddr8, point, line, lseg,
        //   box, path, polygon, circle, tsvector, tsquery, interval.
        // Listing them here as identity entries would be dead weight; documenting the pass-through is
        // the contract. Domains and user-defined types likewise pass through (we cannot resolve a
        // domain's base type without the catalog — that's a SymbolTable/#48 concern, not this layer).
    };

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    // Memoize raw -> canonical. Type spellings repeat massively (every "bigint"/"text"/"jsonb"
    // column across a project), so this both skips the regex/substring/ToLower/concat work on the
    // hot path AND hands back the SAME string instance for identical inputs — deduping the type
    // strings retained in every ColumnDefinition. The build parses files in parallel, so the cache
    // must be thread-safe. Distinct spellings are bounded (dozens), so it never grows unbounded.
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);

    public static string Normalize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        return Cache.GetOrAdd(raw, static key => NormalizeCore(key));
    }

    private static string NormalizeCore(string raw)
    {
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
