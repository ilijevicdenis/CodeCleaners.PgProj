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
        int write = 0, i = 0;
        while (i < tokens.Count)
        {
            var t = tokens[i];
            if (t.Kind == TokenKind.Symbol && t.Value.Length == 1 && IsOpChar(t.Value[0]))
            {
                int start = t.Position;
                var run = t.Value;
                int j = i + 1;
                int endPos = t.Position + 1;
                while (j < tokens.Count
                       && tokens[j].Kind == TokenKind.Symbol
                       && tokens[j].Value.Length == 1
                       && IsOpChar(tokens[j].Value[0])
                       && tokens[j].Position == endPos)   // adjacent (no whitespace/comment between)
                {
                    run += tokens[j].Value;
                    endPos++;
                    j++;
                }
                // Allocate a new Token only when a run actually merged; a lone operator is kept as-is
                // (the old version re-allocated even single operators).
                tokens[write++] = j == i + 1 ? t : new Token(TokenKind.Symbol, run, start);
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
}
