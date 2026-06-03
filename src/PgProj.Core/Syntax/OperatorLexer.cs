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
    private const string OpChars = "+-*/<>=~!@#%^&|?:";

    private static bool IsOpChar(char c) => OpChars.IndexOf(c) >= 0;

    public static List<Token> Merge(IReadOnlyList<Token> tokens)
    {
        var result = new List<Token>(tokens.Count);
        int i = 0;
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
                result.Add(new Token(TokenKind.Symbol, run, start));
                i = j;
            }
            else
            {
                result.Add(t);
                i++;
            }
        }
        return result;
    }
}
