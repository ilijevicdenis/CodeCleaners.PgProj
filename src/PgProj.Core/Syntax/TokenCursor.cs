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

    /// <summary>The tokens consumed between two marks — used to recover the source text of a parsed sub-expression.</summary>
    public System.Collections.Generic.List<Token> Range(int from, int to)
    {
        var list = new System.Collections.Generic.List<Token>();
        for (int k = from; k < to && k < _tokens.Count; k++) list.Add(_tokens[k]);
        return list;
    }

    public Token Advance()
    {
        if (AtEnd) throw new ParseException("unexpected end of input", Here);
        return _tokens[_i++];
    }

    // ---- predicates ---------------------------------------------------------
    public bool AtWord(string word) => Current?.IsWord(word) == true;
    public bool AtSymbol(char c) => Current?.IsSymbol(c) == true;
    // params ReadOnlySpan<string> (not params string[]): for a call with constant string literals the
    // C# 13 compiler materialises the argument list as a cached static array wrapped in a span, so even
    // the 5+-word call sites (e.g. the 13-word clause-terminator check in the SELECT target loop) no
    // longer allocate a string[] per call — the largest remaining String[] source (AllocProbe §1f).
    public bool AtAnyWord(params ReadOnlySpan<string> words)
    {
        foreach (var w in words) if (AtWord(w)) return true;
        return false;
    }

    // Fixed-arity overloads for the common small cases: the compiler prefers these over the
    // span version, so the pervasive AtAnyWord("A","B")/MatchWords("IF","NOT","EXISTS") call
    // sites resolve to a branch-only check with no argument packing at all.
    public bool AtAnyWord(string a, string b) => AtWord(a) || AtWord(b);
    public bool AtAnyWord(string a, string b, string c) => AtWord(a) || AtWord(b) || AtWord(c);
    public bool AtAnyWord(string a, string b, string c, string d) => AtWord(a) || AtWord(b) || AtWord(c) || AtWord(d);

    private bool WordAt(int ahead, string w) => (ahead == 0 ? Current : Peek(ahead))?.IsWord(w) == true;

    /// <summary>True if the next tokens are these words in order (case-insensitive), not consuming.</summary>
    public bool LookaheadWords(params ReadOnlySpan<string> words)
    {
        for (int k = 0; k < words.Length; k++)
        {
            var t = k == 0 ? Current : Peek(k);
            if (t is null || !t.IsWord(words[k])) return false;
        }
        return true;
    }

    public bool LookaheadWords(string a, string b) => WordAt(0, a) && WordAt(1, b);
    public bool LookaheadWords(string a, string b, string c) => WordAt(0, a) && WordAt(1, b) && WordAt(2, c);

    /// <summary>The current token as an operator (merged multi-char symbol), or null.</summary>
    public string? CurrentOperator => Current is { Kind: TokenKind.Symbol } t ? t.Value : null;
    public bool AtOperator(string op) => Current is { Kind: TokenKind.Symbol } t && t.Value == op;

    // ---- optional matches (return false if absent) --------------------------
    public bool MatchWord(string word) { if (AtWord(word)) { _i++; return true; } return false; }
    public bool MatchSymbol(char c) { if (AtSymbol(c)) { _i++; return true; } return false; }
    public bool MatchOperator(string op) { if (AtOperator(op)) { _i++; return true; } return false; }

    /// <summary>Consume a sequence of words only if all are present in order.</summary>
    public bool MatchWords(params ReadOnlySpan<string> words)
    {
        if (!LookaheadWords(words)) return false;
        _i += words.Length;
        return true;
    }

    public bool MatchWords(string a, string b) { if (LookaheadWords(a, b)) { _i += 2; return true; } return false; }
    public bool MatchWords(string a, string b, string c) { if (LookaheadWords(a, b, c)) { _i += 3; return true; } return false; }

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
