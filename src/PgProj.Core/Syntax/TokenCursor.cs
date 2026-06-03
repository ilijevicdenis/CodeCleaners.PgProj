using System;
using System.Collections.Generic;
using PgProj.Core.Parsing;

namespace PgProj.Core.Syntax;

/// <summary>Thrown by the parser on a grammar violation; carries the offending token's position.</summary>
public sealed class ParseException : Exception
{
    public int Offset { get; }
    public ParseException(string message, int offset) : base(message) => Offset = offset;
}

/// <summary>
/// A forward cursor over a token list with the small, explicit vocabulary a recursive-descent
/// parser needs: Peek / Match* / Expect*. Every method is trivial and debuggable; there is no
/// hidden token-capture state. Expect* throws a <see cref="ParseException"/> positioned at the
/// current token so errors report a real line:column.
/// </summary>
public sealed class TokenCursor
{
    private readonly IReadOnlyList<Token> _tokens;
    private int _i;

    public TokenCursor(IReadOnlyList<Token> tokens) => _tokens = tokens;

    public Token? Current => _i < _tokens.Count ? _tokens[_i] : null;
    public Token? Peek(int ahead = 1) => _i + ahead < _tokens.Count ? _tokens[_i + ahead] : null;
    public bool AtEnd => _i >= _tokens.Count;

    /// <summary>Position to report an error "here" (current token, or just past the end).</summary>
    public int Here => Current?.Position ?? (_tokens.Count > 0 ? _tokens[^1].Position + _tokens[^1].Value.Length : 0);

    /// <summary>Save the current position for speculative parsing; restore with <see cref="Reset"/>.</summary>
    public int Mark() => _i;
    public void Reset(int mark) => _i = mark;

    public Token Advance()
    {
        if (AtEnd) throw new ParseException("unexpected end of input", Here);
        return _tokens[_i++];
    }

    // ---- predicates ---------------------------------------------------------
    public bool AtWord(string word) => Current?.IsWord(word) == true;
    public bool AtSymbol(char c) => Current?.IsSymbol(c) == true;
    public bool AtAnyWord(params string[] words)
    {
        foreach (var w in words) if (AtWord(w)) return true;
        return false;
    }

    /// <summary>True if the next tokens are these words in order (case-insensitive), not consuming.</summary>
    public bool LookaheadWords(params string[] words)
    {
        for (int k = 0; k < words.Length; k++)
        {
            var t = k == 0 ? Current : Peek(k);
            if (t is null || !t.IsWord(words[k])) return false;
        }
        return true;
    }

    /// <summary>The current token as an operator (merged multi-char symbol), or null.</summary>
    public string? CurrentOperator => Current is { Kind: TokenKind.Symbol } t ? t.Value : null;
    public bool AtOperator(string op) => Current is { Kind: TokenKind.Symbol } t && t.Value == op;

    // ---- optional matches (return false if absent) --------------------------
    public bool MatchWord(string word) { if (AtWord(word)) { _i++; return true; } return false; }
    public bool MatchSymbol(char c) { if (AtSymbol(c)) { _i++; return true; } return false; }
    public bool MatchOperator(string op) { if (AtOperator(op)) { _i++; return true; } return false; }

    /// <summary>Consume a sequence of words only if all are present in order.</summary>
    public bool MatchWords(params string[] words)
    {
        if (!LookaheadWords(words)) return false;
        _i += words.Length;
        return true;
    }

    // ---- required matches (throw if absent) ---------------------------------
    public void ExpectWord(string word)
    {
        if (!MatchWord(word))
            throw new ParseException($"expected '{word}' but found {Describe(Current)}", Here);
    }

    public void ExpectSymbol(char c)
    {
        if (!MatchSymbol(c))
            throw new ParseException($"expected '{c}' but found {Describe(Current)}", Here);
    }

    /// <summary>Consume and return an identifier (unquoted word or quoted identifier).</summary>
    public string ExpectIdentifier()
    {
        var t = Current;
        if (t is { Kind: TokenKind.Word or TokenKind.QuotedIdent }) { _i++; return t.Value; }
        throw new ParseException($"expected an identifier but found {Describe(t)}", Here);
    }

    private static string Describe(Token? t) =>
        t is null ? "end of input"
        : t.Kind == TokenKind.Symbol ? $"'{t.Value}'"
        : $"'{t.Value}'";
}
