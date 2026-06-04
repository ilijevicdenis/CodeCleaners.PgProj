using System;
using System.Buffers;
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

    // PG16 non-decimal integer prefixes (0x/0o/0b); vectorized membership beats "xXoObB".IndexOf per number.
    private static readonly SearchValues<char> RadixPrefix = SearchValues.Create("xXoObB");

    // Word/Number text is ~91% duplicated within a parse (measured: "integer"/"SELECT"/"id" recur
    // thousands of times, each an eager Substring). Intern per-Tokenizer so each distinct spelling is
    // allocated once; the span alternate-lookup keys on the source slice so a hit costs no string at all.
    // Per-instance (not static), so the parallel per-file build never shares this dictionary — thread-safe.
    // The dictionary is lazy + size-gated: interning a whole file amortises it heavily, but the many tiny
    // re-tokenisations (DeriveRaw of one raw statement, table tails) repeat too little to pay it back, so
    // below the threshold Intern just substrings. (AllocProbe: gate keeps the win without the per-call dict.)
    private const int InternThreshold = 1024;
    private readonly bool _intern;
    private Dictionary<string, string>? _interner;
    private Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> _lookup;

    // Single-character symbol text is a tiny fixed alphabet ('(' ')' ',' ';' '.' operators); cache the
    // ASCII strings once so a punctuation token costs no allocation (was c.ToString() per symbol).
    private static readonly string[] SingleChar = BuildSingleChar();
    private static string[] BuildSingleChar()
    {
        var a = new string[128];
        for (int i = 0; i < a.Length; i++) a[i] = ((char)i).ToString();
        return a;
    }

    private Tokenizer(string s)
    {
        _s = s;
        _intern = s.Length >= InternThreshold;
    }

    /// <summary>Return the canonical string for source slice [start, start+length), allocating it only on
    /// first sight within this parse (span-keyed lookup → no string is built for a cache hit). Below the
    /// size gate (or before the first distinct word) it is a plain substring — no dictionary is built.</summary>
    private string Intern(int start, int length)
    {
        var span = _s.AsSpan(start, length);
        // Static vocabulary first: keywords + built-in types recur in every file, so canonicalise them from
        // one shared immutable table — no per-file dict, no size gate, dedupes across the whole build.
        if (TokenVocabulary.Canonical.TryGetValue(span, out var known)) return known;
        // Then the per-file interner for this file's own identifiers (gated: only large inputs amortise it).
        if (!_intern) return _s.Substring(start, length);
        if (_interner is null)
        {
            _interner = new Dictionary<string, string>(StringComparer.Ordinal);
            _lookup = _interner.GetAlternateLookup<ReadOnlySpan<char>>();
        }
        if (_lookup.TryGetValue(span, out var cached)) return cached;
        var s = _s.Substring(start, length);
        _interner[s] = s;
        return s;
    }

    public static List<Token> Tokenize(string sql)
    {
        var t = new Tokenizer(sql ?? string.Empty);
        return t.Run();
    }

    private List<Token> Run()
    {
        // Pre-size: SQL averages roughly a token every ~4 chars, so this avoids the 1→4→8→…
        // doubling reallocations of the backing array as the list grows to thousands of entries.
        var tokens = new List<Token>(_s.Length / 4 + 16);
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

            // Anything else is a single-character symbol — reuse the cached ASCII string.
            _i++;
            tokens.Add(new Token(TokenKind.Symbol, c < 128 ? SingleChar[c] : c.ToString(), start));
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
        // PG16 non-decimal integer literals: 0x… (hex), 0o… (octal), 0b… (binary), with optional '_'
        // separators. Only consume when at least one radix-valid digit follows; otherwise fall through to
        // decimal so malformed forms (0x, 0b2, 0o9) surface as a parse error like Postgres rejects them.
        if (_s[_i] == '0' && _i + 1 < _s.Length && RadixPrefix.Contains(_s[_i + 1]))
        {
            char radix = char.ToLowerInvariant(_s[_i + 1]);
            int j = _i + 2, digits = 0;
            while (j < _s.Length && (IsRadixDigit(_s[j], radix) || (_s[j] == '_' && digits > 0))) { if (_s[j] != '_') digits++; j++; }
            if (digits > 0) { _i = j; return Intern(start, _i - start); }
        }
        while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.' || _s[_i] == 'e' || _s[_i] == 'E'
                                  || ((_s[_i] == '+' || _s[_i] == '-') && (_s[_i - 1] == 'e' || _s[_i - 1] == 'E'))
                                  || (_s[_i] == '_' && _i > start && char.IsDigit(_s[_i - 1]) && char.IsDigit(Peek(1)))))  // digit group separators (PG16)
            _i++;
        return Intern(start, _i - start);
    }

    private string ReadWord()
    {
        var start = _i;
        while (_i < _s.Length && IsIdentPart(_s[_i])) _i++;
        return Intern(start, _i - start);
    }

    private static bool IsRadixDigit(char c, char radix) => radix switch
    {
        'x' => Uri.IsHexDigit(c),
        'o' => c >= '0' && c <= '7',
        _   => c == '0' || c == '1',   // binary
    };

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';
    private static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';
}
