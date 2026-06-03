using System;
using System.Collections.Generic;

namespace PgProj.Core.Parsing;

/// <summary>A forward cursor over a single statement's tokens.</summary>
internal sealed class TokenReader
{
    private readonly IReadOnlyList<Token> _tokens;
    private int _pos;

    public TokenReader(IReadOnlyList<Token> tokens) => _tokens = tokens;

    public bool Eof => _pos >= _tokens.Count;
    public Token? Cur => _pos < _tokens.Count ? _tokens[_pos] : null;
    public Token? Peek(int ahead = 1) => _pos + ahead < _tokens.Count ? _tokens[_pos + ahead] : null;

    public Token Next()
    {
        if (Eof) throw new ParseException("Unexpected end of statement.");
        return _tokens[_pos++];
    }

    public bool MatchWord(string keyword)
    {
        if (Cur is { } t && t.IsWord(keyword)) { _pos++; return true; }
        return false;
    }

    public bool MatchSymbol(char c)
    {
        if (Cur is { } t && t.IsSymbol(c)) { _pos++; return true; }
        return false;
    }

    public bool IsWord(string keyword) => Cur is { } t && t.IsWord(keyword);
    public bool IsSymbol(char c) => Cur is { } t && t.IsSymbol(c);

    public void ExpectSymbol(char c)
    {
        if (!MatchSymbol(c))
            throw new ParseException($"Expected '{c}' but found '{Cur?.Value ?? "<end>"}'.");
    }

    /// <summary>Reads an identifier, folding unquoted names to lower case (Postgres semantics).</summary>
    public string ParseIdentifier()
    {
        var t = Cur ?? throw new ParseException("Expected an identifier but reached end of statement.");
        if (t.Kind == TokenKind.Word) { _pos++; return t.Value.ToLowerInvariant(); }
        if (t.Kind == TokenKind.QuotedIdent) { _pos++; return t.Value; }
        throw new ParseException($"Expected an identifier but found '{t.Value}'.");
    }
}

public sealed class ParseException : Exception
{
    public ParseException(string message) : base(message) { }
}
