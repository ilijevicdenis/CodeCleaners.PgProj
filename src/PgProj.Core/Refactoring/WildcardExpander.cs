using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PgProj.Core.Refactoring;

/// <summary>
/// Source-level <c>SELECT *</c> → explicit-column-list rewriter for the <c>expand-wildcards</c> refactor
/// (#152). It locates a named view's <c>CREATE VIEW … AS SELECT</c> header in a <c>.sql</c> file and replaces
/// ONLY the top-level star tokens (<c>*</c> and <c>alias.*</c>) in its select projection with the explicit
/// columns — leaving every other character byte-identical (SSDT's minimal-edit contract). The scan is
/// comment-, string-, identifier-, dollar-quote- and paren-aware, so a <c>count(*)</c> (star inside parens),
/// a <c>'*'</c> string literal, or a <c>--</c>/<c>/* */</c> comment are never touched.
/// </summary>
internal static class WildcardExpander
{
    /// <summary>A resolved FROM source: the name it is visible under (alias or relation) and its ordered columns.</summary>
    public sealed record Source(string Name, IReadOnlyList<string> Columns);

    private static readonly Regex BareStar = new(@"^\*$", RegexOptions.Compiled);
    private static readonly Regex QualifiedStar = new("""^"?(?<id>[A-Za-z_][\w$]*)"?\s*\.\s*\*$""", RegexOptions.Compiled);

    /// <summary>
    /// Rewrite the named view's wildcard projection in <paramref name="text"/>. Returns the new text and the
    /// number of star items expanded. Returns <c>(text, 0)</c> when the view header is not present in this
    /// text (so the caller can scan every project file). Throws <see cref="RefactorException"/> when the
    /// header IS present but the shape is unsupported, there is no star to expand, or a star references an
    /// unresolved source — a clear, actionable failure rather than a silent miss.
    /// </summary>
    public static (string Text, int Count) Rewrite(string text, string schema, string name, IReadOnlyList<Source> sources)
    {
        var headerEnd = FindViewHeader(text, schema, name);
        if (headerEnd < 0) return (text, 0);

        var selectStart = FindSelectProjectionStart(text, headerEnd);
        if (selectStart < 0)
            throw new RefactorException($"Could not locate a plain 'SELECT' projection for view '{schema}.{name}' (WITH/UNION/parenthesized bodies are not supported by expand-wildcards).");

        var (items, fromPos) = ScanProjectionItems(text, selectStart);
        if (fromPos < 0)
            throw new RefactorException($"View '{schema}.{name}' has no FROM clause — nothing to expand.");

        // Collect the star edits (each: the trimmed star span + its replacement), then apply right-to-left.
        var edits = new List<(int Start, int End, string Replacement)>();
        foreach (var (start, end) in items)
        {
            var (ts, te) = Trim(text, start, end);
            if (te <= ts) continue;
            var token = text[ts..te];
            if (BareStar.IsMatch(token))
                edits.Add((ts, te, ExpandBare(sources, schema, name)));
            else if (QualifiedStar.Match(token) is { Success: true } m)
                edits.Add((ts, te, ExpandQualified(sources, m.Groups["id"].Value, schema, name)));
        }

        if (edits.Count == 0)
            throw new RefactorException($"View '{schema}.{name}' has no SELECT * (or alias.*) to expand.");

        var sb = new StringBuilder(text);
        foreach (var (start, end, replacement) in edits.OrderByDescending(e => e.Start))
            sb.Remove(start, end - start).Insert(start, replacement);
        return (sb.ToString(), edits.Count);
    }

    private static string ExpandBare(IReadOnlyList<Source> sources, string schema, string name)
    {
        if (sources.Count == 0)
            throw new RefactorException($"Cannot expand '*' for view '{schema}.{name}': no resolvable FROM source.");
        // Single source → unqualified columns; multiple sources → qualify each to avoid ambiguity.
        if (sources.Count == 1)
            return string.Join(", ", sources[0].Columns);
        return string.Join(", ", sources.SelectMany(s => s.Columns.Select(c => $"{s.Name}.{c}")));
    }

    private static string ExpandQualified(IReadOnlyList<Source> sources, string alias, string schema, string name)
    {
        var src = sources.FirstOrDefault(s => string.Equals(s.Name, alias, StringComparison.OrdinalIgnoreCase))
            ?? throw new RefactorException($"Cannot expand '{alias}.*' for view '{schema}.{name}': no FROM source named '{alias}'.");
        return string.Join(", ", src.Columns.Select(c => $"{alias}.{c}"));
    }

    // ---- source scanning ------------------------------------------------------------------------

    /// <summary>Find the end offset of the <c>CREATE … VIEW &lt;schema.name&gt;</c> header, or -1 if absent.</summary>
    private static int FindViewHeader(string text, string schema, string name)
    {
        var pattern = $@"\bVIEW\s+(?:""?{Regex.Escape(schema)}""?\s*\.\s*)?""?{Regex.Escape(name)}""?(?![\w""])";
        var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        return m.Success ? m.Index + m.Length : -1;
    }

    /// <summary>
    /// From just past the view name, skip an optional <c>(col,…)</c> alias list and the <c>AS</c> keyword,
    /// then return the offset just after the leading <c>SELECT</c> (and any <c>DISTINCT [ON (…)]</c>).
    /// Returns -1 when the body is not a plain top-level SELECT.
    /// </summary>
    private static int FindSelectProjectionStart(string text, int from)
    {
        var i = SkipTrivia(text, from);
        if (i < text.Length && text[i] == '(') i = SkipBalancedParens(text, i);      // optional column alias list
        i = SkipTrivia(text, i);
        if (!MatchKeyword(text, i, "AS")) return -1;
        i = SkipTrivia(text, i + 2);
        if (!MatchKeyword(text, i, "SELECT")) return -1;                              // WITH/(/VALUES/TABLE → unsupported
        i = SkipTrivia(text, i + 6);
        if (MatchKeyword(text, i, "ALL")) i = SkipTrivia(text, i + 3);
        if (MatchKeyword(text, i, "DISTINCT"))
        {
            i = SkipTrivia(text, i + 8);
            if (MatchKeyword(text, i, "ON"))
            {
                i = SkipTrivia(text, i + 2);
                if (i < text.Length && text[i] == '(') i = SkipBalancedParens(text, i);
                i = SkipTrivia(text, i);
            }
        }
        return i;
    }

    /// <summary>
    /// Scan the projection from <paramref name="start"/>: return each top-level item's [start,end) span and
    /// the offset of the depth-0 <c>FROM</c> keyword that terminates it (-1 if none). Splits on depth-0
    /// commas; respects strings, identifiers, dollar-quotes, comments, and parentheses.
    /// </summary>
    private static (List<(int Start, int End)> Items, int FromPos) ScanProjectionItems(string text, int start)
    {
        var items = new List<(int, int)>();
        int depth = 0, itemStart = start, i = start;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\'' || c == '"') { i = SkipQuoted(text, i, c); continue; }
            if (c == '$' && TryDollarQuote(text, i, out var de)) { i = de; continue; }
            if (c == '-' && i + 1 < text.Length && text[i + 1] == '-') { i = SkipLineComment(text, i); continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*') { i = SkipBlockComment(text, i); continue; }
            if (c == '(') { depth++; i++; continue; }
            if (c == ')') { if (depth == 0) { items.Add((itemStart, i)); return (items, -1); } depth--; i++; continue; }
            if (depth == 0 && c == ',') { items.Add((itemStart, i)); i++; itemStart = i; continue; }
            if (depth == 0 && MatchKeyword(text, i, "FROM")) { items.Add((itemStart, i)); return (items, i); }
            i++;
        }
        items.Add((itemStart, text.Length));
        return (items, -1);
    }

    // ---- low-level skips (all return the index just past the construct) -------------------------

    private static int SkipTrivia(string text, int i)
    {
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            if (text[i] == '-' && i + 1 < text.Length && text[i + 1] == '-') { i = SkipLineComment(text, i); continue; }
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*') { i = SkipBlockComment(text, i); continue; }
            break;
        }
        return i;
    }

    private static int SkipQuoted(string text, int i, char q)
    {
        i++; // opening quote
        while (i < text.Length)
        {
            if (text[i] == q)
            {
                if (i + 1 < text.Length && text[i + 1] == q) { i += 2; continue; } // doubled escape
                return i + 1;
            }
            i++;
        }
        return i;
    }

    private static bool TryDollarQuote(string text, int i, out int end)
    {
        end = i;
        var close = Regex.Match(text[i..], @"^\$[A-Za-z_]?\w*\$");
        if (!close.Success) return false;
        var tag = close.Value;
        var idx = text.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
        end = idx < 0 ? text.Length : idx + tag.Length;
        return true;
    }

    private static int SkipLineComment(string text, int i)
    {
        var nl = text.IndexOf('\n', i);
        return nl < 0 ? text.Length : nl + 1;
    }

    private static int SkipBlockComment(string text, int i)
    {
        var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
        return end < 0 ? text.Length : end + 2;
    }

    private static int SkipBalancedParens(string text, int i)
    {
        int depth = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\'' || c == '"') { i = SkipQuoted(text, i, c); continue; }
            if (c == '(') { depth++; i++; continue; }
            if (c == ')') { depth--; i++; if (depth == 0) return i; continue; }
            i++;
        }
        return i;
    }

    /// <summary>True when the keyword sits at <paramref name="i"/> with a word boundary on both sides (case-insensitive).</summary>
    private static bool MatchKeyword(string text, int i, string kw)
    {
        if (i + kw.Length > text.Length) return false;
        if (string.Compare(text, i, kw, 0, kw.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
        if (i > 0 && (char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_')) return false;
        var after = i + kw.Length;
        if (after < text.Length && (char.IsLetterOrDigit(text[after]) || text[after] == '_')) return false;
        return true;
    }

    private static (int Start, int End) Trim(string text, int start, int end)
    {
        while (start < end && char.IsWhiteSpace(text[start])) start++;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        return (start, end);
    }
}
