using System.Buffers;
using System.Collections.Generic;
using PgProj.Core.Parsing;

namespace PgProj.Core.Syntax;

/// <summary>
/// Post-processes the shared tokenizer's single-character symbol stream into PostgreSQL operator
/// tokens: adjacent runs of operator characters (no whitespace between) are merged into one Symbol
/// token, so the expression parser sees "&lt;=", "::", "||", "-&gt;&gt;", "@&gt;", "!~*" as single
/// operators. Structural punctuation ( ) [ ] , ; . is never merged. Used only by PgParser, so the
/// legacy parser's token expectations are unaffected.
/// </summary>
public static class OperatorLexer
{
    // PostgreSQL operator characters, plus ':' so "::" merges (lone ':' for array slices stays single).
    // SearchValues gives a vectorized, allocation-free membership test on this per-symbol hot path.
    private static readonly SearchValues<char> OpChars = SearchValues.Create("+-*/<>=~!@#%^&|?:");

    private static bool IsOpChar(char c) => OpChars.Contains(c);

    // PostgreSQL's trailing-sign lexer rule: a multi-character operator may only END in '+' or '-' when
    // it also contains one of these characters. Otherwise the trailing sign starts its own token — so
    // "a<=-1" lexes as a <= -1, not as a bogus "<=-" operator (audit P1: the unconditional merge silently
    // built BinaryExpr{Op="<=-"} for any unspaced comparison against a negative literal).
    private static readonly SearchValues<char> SignAllowing = SearchValues.Create("~!@#%^&|?");

    private static bool IsSign(char c) => c is '+' or '-';

    /// <summary>
    /// Merges adjacent operator-character runs into single Symbol tokens, in place. The merged stream is
    /// never longer than the input, so it is compacted into the same <see cref="List{Token}"/> with a
    /// write pointer (output index ≤ read index throughout) and the tail trimmed — eliminating the second
    /// full <c>List&lt;Token&gt;</c>/<c>Token[]</c> the old copy-into-a-new-list version allocated (the
    /// largest remaining reducible Token[] source — AllocProbe). All callers pass a freshly-tokenized,
    /// unshared list (<c>Tokenizer.Tokenize(...)</c>), so mutating it is safe; the same list is returned.
    /// When nothing merges (the common case for a non-operator-heavy statement) it is a single rewrite
    /// pass with zero allocation.
    /// </summary>
    public static List<Token> Merge(List<Token> tokens)
    {
        Span<char> scratch = stackalloc char[ScratchLen];   // reused across runs; no per-token string growth
        int write = 0, i = 0;
        while (i < tokens.Count)
        {
            var t = tokens[i];
            if (t.Kind == TokenKind.Symbol && t.Value.Length == 1 && IsOpChar(t.Value[0]))
            {
                int start = t.Position;
                int j = i + 1;
                int endPos = t.Position + 1;
                while (j < tokens.Count
                       && tokens[j].Kind == TokenKind.Symbol
                       && tokens[j].Value.Length == 1
                       && IsOpChar(tokens[j].Value[0])
                       && tokens[j].Position == endPos)   // adjacent (no whitespace/comment between)
                {
                    endPos++;
                    j++;
                }
                // Trailing-sign back-off (PG lexer rule): shed trailing +/- when no sign-allowing char is
                // present, so "<=-" becomes "<=" and the '-' re-enters the loop as its own run/token.
                // (Containment is invariant under shedding — the shed chars are signs, never allowing.)
                if (j - i > 1 && IsSign(tokens[j - 1].Value[0]) && !ContainsSignAllowing(tokens, i, j))
                    do j--; while (j - i > 1 && IsSign(tokens[j - 1].Value[0]));
                // Allocate a new Token only when a run actually merged; a lone operator is kept as-is.
                // Build the merged value once via a span (one final string), not char-by-char string +=.
                tokens[write++] = j == i + 1 ? t : new Token(TokenKind.Symbol, BuildRun(tokens, i, j, scratch), start);
                i = j;
            }
            else
            {
                tokens[write++] = t;
                i++;
            }
        }
        if (write < tokens.Count) tokens.RemoveRange(write, tokens.Count - write);
        return tokens;
    }

    // Longest run we build on the stack; PostgreSQL operator names cap at NAMEDATALEN-1 (63). A run that
    // somehow exceeds this falls back to a heap span — correctness preserved, just not stack-allocated.
    private const int ScratchLen = 64;

    // Materialize the merged operator [i, j) — each source token is a single op char — as one string,
    // writing into the caller's reused scratch span (or a heap span for the rare over-long run).
    private static string BuildRun(List<Token> tokens, int i, int j, Span<char> scratch)
    {
        int len = j - i;
        Span<char> buf = len <= scratch.Length ? scratch[..len] : new char[len];
        for (int k = 0; k < len; k++) buf[k] = tokens[i + k].Value[0];
        return new string(buf);
    }

    private static string BuildRun(Token[] arr, int i, int j, Span<char> scratch)
    {
        int len = j - i;
        Span<char> buf = len <= scratch.Length ? scratch[..len] : new char[len];
        for (int k = 0; k < len; k++) buf[k] = arr[i + k].Value[0];
        return new string(buf);
    }

    private static bool ContainsSignAllowing(List<Token> tokens, int i, int j)
    {
        for (int k = i; k < j; k++)
            if (SignAllowing.Contains(tokens[k].Value[0])) return true;
        return false;
    }

    private static bool ContainsSignAllowing(Token[] arr, int i, int j)
    {
        for (int k = i; k < j; k++)
            if (SignAllowing.Contains(arr[k].Value[0])) return true;
        return false;
    }

    /// <summary>Same in-place operator merge as <see cref="Merge"/>, but over a pooled token buffer
    /// (the main parse path) — compacts the rented array and trims the logical Count, no new list.</summary>
    public static PooledTokens MergeInPlace(PooledTokens tokens)
    {
        var arr = tokens.Array;
        int count = tokens.Count;
        Span<char> scratch = stackalloc char[ScratchLen];   // reused across runs; no per-token string growth
        int write = 0, i = 0;
        while (i < count)
        {
            var t = arr[i];
            if (t.Kind == TokenKind.Symbol && t.Value.Length == 1 && IsOpChar(t.Value[0]))
            {
                int start = t.Position;
                int j = i + 1;
                int endPos = t.Position + 1;
                while (j < count
                       && arr[j].Kind == TokenKind.Symbol
                       && arr[j].Value.Length == 1
                       && IsOpChar(arr[j].Value[0])
                       && arr[j].Position == endPos)
                {
                    endPos++;
                    j++;
                }
                // Trailing-sign back-off — see Merge for the PG rule.
                if (j - i > 1 && IsSign(arr[j - 1].Value[0]) && !ContainsSignAllowing(arr, i, j))
                    do j--; while (j - i > 1 && IsSign(arr[j - 1].Value[0]));
                arr[write++] = j == i + 1 ? t : new Token(TokenKind.Symbol, BuildRun(arr, i, j, scratch), start);
                i = j;
            }
            else
            {
                arr[write++] = t;
                i++;
            }
        }
        tokens.SetCount(write);
        return tokens;
    }
}
