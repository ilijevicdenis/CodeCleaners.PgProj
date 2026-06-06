using System;
using System.Collections.Generic;
using System.Text;

namespace PgProj.Core.Parsing;

public enum TokenKind
{
    Word,         // unquoted identifier or keyword
    QuotedIdent,  // "..."  (Value holds the unquoted content)
    String,       // '...'  (Value holds the unescaped content)
    DollarString, // $tag$...$tag$ (Value holds the full raw text, tags included)
    Number,
    Symbol,       // a single punctuation char
}

public readonly record struct Token(TokenKind Kind, string Value, int Position)
{
    public bool IsWord(string keyword) =>
        Kind == TokenKind.Word && string.Equals(Value, keyword, StringComparison.OrdinalIgnoreCase);

    public bool IsSymbol(char c) =>
        Kind == TokenKind.Symbol && Value.Length == 1 && Value[0] == c;

    public bool IsIdentifierLike => Kind is TokenKind.Word or TokenKind.QuotedIdent;

    /// <summary>True for tokens that should be space-separated from an adjacent value token.</summary>
    public bool IsValueLike =>
        Kind is TokenKind.Word or TokenKind.QuotedIdent or TokenKind.String
             or TokenKind.DollarString or TokenKind.Number;

    /// <summary>Re-serialise a token to valid SQL source (used to rebuild view/function bodies).</summary>
    public string Render() => Kind switch
    {
        // Guard the Replace: it allocates a fresh string even when nothing needs escaping (the common case).
        TokenKind.QuotedIdent => "\"" + (Value.Contains('"') ? Value.Replace("\"", "\"\"") : Value) + "\"",
        TokenKind.String => "'" + (Value.Contains('\'') ? Value.Replace("'", "''") : Value) + "'",
        TokenKind.DollarString => Value,
        _ => Value,
    };

    /// <summary>
    /// Re-serialise a run of tokens with minimal, valid spacing: a space is inserted only
    /// between two value-like tokens (so "numeric ( 12 , 2 )" round-trips tightly while
    /// "timestamp without time zone" keeps its spaces).
    /// </summary>
    public static string Render(IReadOnlyList<Token> tokens) => Render(tokens, 0, tokens.Count);

    /// <summary>
    /// Render the <paramref name="count"/> tokens starting at <paramref name="start"/> directly from
    /// <paramref name="tokens"/>, without copying them into an intermediate list first. The grammar's
    /// capture helpers (CaptureRest / CaptureExpression / a balanced-paren body) used to collect a run
    /// into a fresh <c>List&lt;Token&gt;</c> purely to feed <see cref="Render(IReadOnlyList{Token})"/>;
    /// they now track [start, start+count) over the cursor's own token list and call this — killing the
    /// per-capture List + its doubling-grown <c>Token[]</c> backing array (AllocProbe-driven).
    /// </summary>
    public static string Render(IReadOnlyList<Token> tokens, int start, int count)
    {
        // Fast paths: the overwhelming majority of Render calls are a single token (a column type like
        // "bigint"/"jsonb", a one-word identifier) — return its text directly, no StringBuilder/ToString.
        if (count <= 0) return "";
        if (count == 1) return tokens[start].Render();

        // Fast path: when every token renders verbatim as its Value (i.e. none is a quote-wrapped
        // QuotedIdent/String that Render() would re-quote/escape), the exact output length is knowable in
        // one pass, so we write straight into the result string via string.Create — no StringBuilder and no
        // char[] backing buffer (together ~7.6% of pipeline alloc; AllocProbe). Type names, default/CHECK
        // expressions and most captured runs are all-plain and hit this. Spacing logic is identical to the
        // StringBuilder path below, so the text is byte-for-byte the same.
        var len = 0; var simple = true; Token? p = null;
        for (var i = 0; i < count; i++)
        {
            var t = tokens[start + i];
            if (t.Kind is TokenKind.QuotedIdent or TokenKind.String) { simple = false; break; }
            if (p is { } pv && t.IsValueLike && (pv.IsValueLike || pv.IsSymbol(')') || pv.IsSymbol(']'))) len++;
            len += t.Value.Length;
            p = t;
        }
        if (simple)
        {
            return string.Create(len, (tokens, start, count), static (span, st) =>
            {
                var (tk, s, c) = st;
                int pos = 0; Token? prev = null;
                for (var i = 0; i < c; i++)
                {
                    var t = tk[s + i];
                    if (prev is { } pv && t.IsValueLike && (pv.IsValueLike || pv.IsSymbol(')') || pv.IsSymbol(']')))
                        span[pos++] = ' ';
                    t.Value.AsSpan().CopyTo(span[pos..]); pos += t.Value.Length;
                    prev = t;
                }
            });
        }

        // Fallback (run contains a quoted identifier / string literal): StringBuilder, since Render() re-quotes
        // and may double embedded quotes, so the exact length isn't a simple sum of Value lengths.
        var cap = 0;
        for (var i = 0; i < count; i++) cap += tokens[start + i].Value.Length + 2;
        var sb = new StringBuilder(cap);
        Token? prev = null;
        // Indexed, not foreach: a segment may be an IReadOnlyList view (TokenSegment) whose enumerator
        // would allocate; indexing is allocation-free for every IReadOnlyList including List<Token>.
        for (var i = 0; i < count; i++)
        {
            var t = tokens[start + i];
            // Separate a value token from a preceding value token or a closing bracket, so
            // "count(o.id) AS x" and "timestamp without time zone" both read naturally while
            // "numeric(12, 2)" stays tight.
            if (prev is { } pv && t.IsValueLike
                && (pv.IsValueLike || pv.IsSymbol(')') || pv.IsSymbol(']')))
                sb.Append(' ');
            sb.Append(t.Render());
            prev = t;
        }
        return sb.ToString();
    }
}
