using System;
using System.Collections.Generic;
using System.Text;

namespace PgProj.Core.Parsing;

/// <summary>
/// Lexes Postgres SQL into <see cref="Token"/>s. It is deliberately dialect-aware where it
/// matters for DDL: it understands dollar-quoted bodies (so semicolons inside a function body
/// are not mistaken for statement terminators), doubled-quote escaping in both '...' strings
/// and "..." identifiers, and line/block comments (block comments nest, as in Postgres).
/// </summary>
public sealed class Tokenizer
{
    private readonly string _s;
    private int _i;

    private Tokenizer(string s) => _s = s;

    public static List<Token> Tokenize(string sql)
    {
        var t = new Tokenizer(sql ?? string.Empty);
        return t.Run();
    }

    private List<Token> Run()
    {
        var tokens = new List<Token>();
        while (_i < _s.Length)
        {
            var c = _s[_i];

            if (char.IsWhiteSpace(c)) { _i++; continue; }

            // Comments
            if (c == '-' && Peek(1) == '-') { SkipLineComment(); continue; }
            if (c == '/' && Peek(1) == '*') { SkipBlockComment(); continue; }

            var start = _i;

            // Dollar-quoted string (or a bare '$' symbol if it isn't a valid open tag).
            if (c == '$' && TryReadDollarString(out var dollar))
            {
                tokens.Add(new Token(TokenKind.DollarString, dollar, start));
                continue;
            }

            if (c == '\'') { tokens.Add(new Token(TokenKind.String, ReadQuoted('\''), start)); continue; }
            if (c == '"') { tokens.Add(new Token(TokenKind.QuotedIdent, ReadQuoted('"'), start)); continue; }

            if (char.IsDigit(c) || (c == '.' && char.IsDigit(Peek(1))))
            {
                tokens.Add(new Token(TokenKind.Number, ReadNumber(), start));
                continue;
            }

            if (IsIdentStart(c))
            {
                tokens.Add(new Token(TokenKind.Word, ReadWord(), start));
                continue;
            }

            // Anything else is a single-character symbol.
            _i++;
            tokens.Add(new Token(TokenKind.Symbol, c.ToString(), start));
        }
        return tokens;
    }

    private char Peek(int ahead)
    {
        var j = _i + ahead;
        return j < _s.Length ? _s[j] : '\0';
    }

    private void SkipLineComment()
    {
        while (_i < _s.Length && _s[_i] != '\n') _i++;
    }

    private void SkipBlockComment()
    {
        _i += 2; // consume /*
        var depth = 1;
        while (_i < _s.Length && depth > 0)
        {
            if (_s[_i] == '/' && Peek(1) == '*') { depth++; _i += 2; }
            else if (_s[_i] == '*' && Peek(1) == '/') { depth--; _i += 2; }
            else _i++;
        }
    }

    private bool TryReadDollarString(out string value)
    {
        value = string.Empty;
        // Read a candidate tag: $ [A-Za-z_][A-Za-z0-9_]* $
        var j = _i + 1;
        while (j < _s.Length && (char.IsLetterOrDigit(_s[j]) || _s[j] == '_')) j++;
        if (j >= _s.Length || _s[j] != '$')
            return false; // not a dollar-quote open (e.g. a stray '$' or a positional param)

        var tag = _s.Substring(_i, j - _i + 1); // includes both '$' delimiters
        var bodyStart = j + 1;
        var close = _s.IndexOf(tag, bodyStart, StringComparison.Ordinal);
        if (close < 0)
        {
            // Unterminated — consume to end so we don't loop forever.
            value = _s.Substring(_i);
            _i = _s.Length;
            return true;
        }

        var end = close + tag.Length;
        value = _s.Substring(_i, end - _i);
        _i = end;
        return true;
    }

    private string ReadQuoted(char quote)
    {
        var sb = new StringBuilder();
        _i++; // opening quote
        while (_i < _s.Length)
        {
            var c = _s[_i];
            if (c == quote)
            {
                if (Peek(1) == quote) { sb.Append(quote); _i += 2; continue; } // doubled escape
                _i++; // closing quote
                break;
            }
            sb.Append(c);
            _i++;
        }
        return sb.ToString();
    }

    private string ReadNumber()
    {
        var start = _i;
        while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.' || _s[_i] == 'e' || _s[_i] == 'E'
                                  || ((_s[_i] == '+' || _s[_i] == '-') && (_s[_i - 1] == 'e' || _s[_i - 1] == 'E'))
                                  || (_s[_i] == '_' && _i > start && char.IsDigit(_s[_i - 1]) && char.IsDigit(Peek(1)))))  // digit group separators (PG16)
            _i++;
        return _s.Substring(start, _i - start);
    }

    private string ReadWord()
    {
        var start = _i;
        while (_i < _s.Length && IsIdentPart(_s[_i])) _i++;
        return _s.Substring(start, _i - start);
    }

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';
    private static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';
}
