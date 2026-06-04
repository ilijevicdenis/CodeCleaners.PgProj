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

public sealed record Token(TokenKind Kind, string Value, int Position)
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
    public static string Render(IReadOnlyList<Token> tokens)
    {
        // Fast paths: the overwhelming majority of Render calls are a single token (a column type like
        // "bigint"/"jsonb", a one-word identifier) — return its text directly, no StringBuilder/ToString.
        if (tokens.Count == 0) return "";
        if (tokens.Count == 1) return tokens[0].Render();

        // Pre-size: without a capacity hint the builder block-expands from 16 chars repeatedly (the #1
        // allocation source in the alloc trace). Sum of token lengths + a space each is a safe estimate.
        var cap = 0;
        for (var i = 0; i < tokens.Count; i++) cap += tokens[i].Value.Length + 1;
        var sb = new StringBuilder(cap);
        Token? prev = null;
        // Indexed, not foreach: a segment may be an IReadOnlyList view (TokenSegment) whose enumerator
        // would allocate; indexing is allocation-free for every IReadOnlyList including List<Token>.
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            // Separate a value token from a preceding value token or a closing bracket, so
            // "count(o.id) AS x" and "timestamp without time zone" both read naturally while
            // "numeric(12, 2)" stays tight.
            if (prev is not null && t.IsValueLike
                && (prev.IsValueLike || prev.IsSymbol(')') || prev.IsSymbol(']')))
                sb.Append(' ');
            sb.Append(t.Render());
            prev = t;
        }
        return sb.ToString();
    }
}
