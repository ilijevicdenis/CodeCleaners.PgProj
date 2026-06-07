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

    // Output buffer rented from ArrayPool (see PooledTokens). Filled via Emit with manual grow, so the
    // per-parse token array can be returned to the pool after the model is built — Token[] was the single
    // largest allocated type (~26%). Grows by re-rent+copy (rare: the len/4 pre-size usually suffices).
    private Token[] _buf = System.Array.Empty<Token>();
    private int _n;
    private bool _pooled;
    // Below this input size the token array is small and short-lived; ArrayPool rent/return + the
    // ReleaseTokens segment-drop overhead isn't worth it, so small inputs (single small files, the CLI
    // interactive case, and the secondary DeriveRaw/table-tail re-tokenizations) use a plain array.
    private const int PoolThreshold = 2048;

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

    /// <summary>Tokenize to a plain List — for the secondary, transient re-tokenizations (DeriveRaw, table
    /// tails) that are small, infrequent, and not worth pooling. Copies out of the pooled buffer then returns it.</summary>
    public static List<Token> Tokenize(string sql)
    {
        // The buffer is pooled and returned immediately (no deferred lifetime), so pool regardless of size:
        // only the retained List copy allocates, not the transient scan buffer.
        var t = new Tokenizer(sql ?? string.Empty);
        var p = t.Run(pool: true);
        var list = new List<Token>(p.Count);
        for (var i = 0; i < p.Count; i++) list.Add(p[i]);
        p.Return();
        return list;
    }

    /// <summary>Tokenize to a pooled buffer — the main PgParser.Parse path. The caller owns the returned
    /// <see cref="PooledTokens"/> and releases it (ParseResult.ReleaseTokens) once the model is built.
    /// Small inputs skip pooling (PoolThreshold) so a single small/CLI parse pays no rent/return overhead.</summary>
    public static PooledTokens TokenizePooled(string sql)
    {
        var t = new Tokenizer(sql ?? string.Empty);
        return t.Run(pool: t._s.Length >= PoolThreshold);
    }

    /// <summary>Tokenize to an <b>always-pooled</b> buffer the caller returns immediately — for the
    /// transient, read-only re-tokenizations (DeriveRaw identity, CREATE TABLE tail validation) that scan
    /// through a cursor and then drop the stream. Unlike <see cref="TokenizePooled"/> it does NOT fall back
    /// to a heap array below <see cref="PoolThreshold"/>: these paths are dominated by small inputs, and for
    /// them rent+return (≈0 retained) beats both a one-shot <c>new Token[]</c> and the old copied-out
    /// <c>List&lt;Token&gt;</c>. The buffer must be returned (a <c>finally</c> at the call site) once the
    /// cursor work is done — same drop-then-return contract as the main path.</summary>
    public static PooledTokens TokenizeTransient(string sql)
    {
        var t = new Tokenizer(sql ?? string.Empty);
        return t.Run(pool: true);
    }

    private void Emit(Token t)
    {
        if (_n == _buf.Length)
        {
            var newCap = _buf.Length == 0 ? 16 : _buf.Length * 2;
            var bigger = _pooled ? ArrayPool<Token>.Shared.Rent(newCap) : new Token[newCap];
            System.Array.Copy(_buf, bigger, _n);
            if (_pooled) ArrayPool<Token>.Shared.Return(_buf);
            _buf = bigger;
        }
        _buf[_n++] = t;
    }

    private PooledTokens Run(bool pool)
    {
        // Pre-size: SQL averages roughly a token every ~4 chars, so this avoids re-rent+copy growth as the
        // buffer fills. The caller decides whether to rent from the pool (large main parses) or use a plain
        // array (small main parses); the secondary List path always pools its transient buffer.
        var cap = _s.Length / 4 + 16;
        _pooled = pool;
        _buf = _pooled ? ArrayPool<Token>.Shared.Rent(cap) : new Token[cap];
        _n = 0;
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
                Emit(new Token(TokenKind.DollarString, dollar, start));
                continue;
            }

            if (c == '\'')
            {
                // Escape-string constant: a standalone E/e immediately before the quote (PG: "E'…'") turns on
                // C-style backslash escapes, so an escaped quote \' does NOT end the string. We require the
                // e/E to be a lone prefix (the char before it is not identifier-like — otherwise it is the tail
                // of a word/number such as TRUE'x' or 1e'x', which are a plain identifier/number + normal '…').
                bool eString = _i >= 1 && (_s[_i - 1] == 'e' || _s[_i - 1] == 'E')
                               && (_i < 2 || !IsIdentPart(_s[_i - 2]));
                Emit(new Token(TokenKind.String, ReadQuoted('\'', eString), start));
                continue;
            }
            if (c == '"') { Emit(new Token(TokenKind.QuotedIdent, ReadQuoted('"'), start)); continue; }

            if (char.IsDigit(c) || (c == '.' && char.IsDigit(Peek(1))))
            {
                Emit(new Token(TokenKind.Number, ReadNumber(), start));
                continue;
            }

            if (IsIdentStart(c))
            {
                Emit(new Token(TokenKind.Word, ReadWord(), start));
                continue;
            }

            // Anything else is a single-character symbol — reuse the cached ASCII string.
            _i++;
            Emit(new Token(TokenKind.Symbol, c < 128 ? SingleChar[c] : c.ToString(), start));
        }
        return new PooledTokens(_buf, _n, _pooled);
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

    private string ReadQuoted(char quote, bool backslashEscapes = false)
    {
        var sb = new StringBuilder();
        _i++; // opening quote
        while (_i < _s.Length)
        {
            var c = _s[_i];
            // E-string backslash escape: take the backslash and the following char verbatim so that \' does
            // not terminate the literal. We preserve the raw two-char sequence (not decode \n etc.) — correct
            // tokenisation is all that's needed, and keeping it raw round-trips the captured expression text.
            if (backslashEscapes && c == '\\' && _i + 1 < _s.Length)
            {
                sb.Append(c); sb.Append(_s[_i + 1]); _i += 2; continue;
            }
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
                                  || ((_s[_i] == '+' || _s[_i] == '-') && _i > start && (_s[_i - 1] == 'e' || _s[_i - 1] == 'E'))
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
