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

        // Compute the EXACT output length in one pass — including quote-wrapping + embedded-quote doubling
        // for QuotedIdent/String, and the inter-token spacing — then write straight into the result string
        // via string.Create. No StringBuilder, no char[] buffer, for ALL runs (the earlier version fell back
        // to StringBuilder whenever a run contained a quoted identifier / string literal; that fallback was
        // ~5% of pipeline alloc — Char[] + StringBuilder — AllocProbe). Byte-identical to per-token Render().
        var len = 0; Token? p = null;
        for (var i = 0; i < count; i++)
        {
            var t = tokens[start + i];
            if (p is { } pv && t.IsValueLike && (pv.IsValueLike || pv.IsSymbol(')') || pv.IsSymbol(']'))) len++;
            len += RenderedLength(t);
            p = t;
        }
        return string.Create(len, (tokens, start, count), static (span, st) =>
        {
            var (tk, s, c) = st;
            int pos = 0; Token? prev = null;
            // Indexed, not foreach: a segment may be an IReadOnlyList view (TokenSegment) whose enumerator
            // would allocate; indexing is allocation-free for every IReadOnlyList including List<Token>.
            for (var i = 0; i < c; i++)
            {
                var t = tk[s + i];
                if (prev is { } pv && t.IsValueLike && (pv.IsValueLike || pv.IsSymbol(')') || pv.IsSymbol(']')))
                    span[pos++] = ' ';
                pos = WriteToken(span, pos, t);
                prev = t;
            }
        });
    }

    /// <summary>Rendered length of one token: Value verbatim, except QuotedIdent/String add their two quotes
    /// plus one extra char per embedded quote that gets doubled — matching <see cref="Render()"/> exactly.</summary>
    private static int RenderedLength(Token t) => t.Kind switch
    {
        TokenKind.QuotedIdent => 2 + t.Value.Length + CountChar(t.Value, '"'),
        TokenKind.String      => 2 + t.Value.Length + CountChar(t.Value, '\''),
        _                     => t.Value.Length,
    };

    private static int CountChar(string s, char ch)
    {
        var n = 0;
        foreach (var c in s) if (c == ch) n++;
        return n;
    }

    /// <summary>Write one token's rendered text into <paramref name="span"/> at <paramref name="pos"/>,
    /// returning the new position. Quote-wraps + doubles embedded quotes for QuotedIdent/String.</summary>
    private static int WriteToken(System.Span<char> span, int pos, Token t)
    {
        char q;
        switch (t.Kind)
        {
            case TokenKind.QuotedIdent: q = '"'; break;
            case TokenKind.String: q = '\''; break;
            default:
                t.Value.AsSpan().CopyTo(span[pos..]);
                return pos + t.Value.Length;
        }
        span[pos++] = q;
        foreach (var c in t.Value) { span[pos++] = c; if (c == q) span[pos++] = q; }
        span[pos++] = q;
        return pos;
    }
}
