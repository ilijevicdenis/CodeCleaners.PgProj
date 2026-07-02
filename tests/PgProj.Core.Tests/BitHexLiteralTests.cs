using System.Linq;
using PgProj.Core.Parsing;
using PgProj.Core.Syntax;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// B'…' / X'…' content validation and the dollar-tag identifier rule (audit P2/P3), each case
/// ground-truthed against live PG18. Previously B'2', X'GG' and $1$foo$1$ were accepted silently —
/// their corpus error-assertions only ran on the DB-gated tier.
/// </summary>
public class BitHexLiteralTests
{
    private static LexError? LexError(string sql)
    {
        Tokenizer.TokenizePooled(sql, out var error).Return();
        return error;
    }

    [Fact]
    public void Invalid_bit_and_hex_content_is_a_lex_error()
    {
        // live PG18: "2" is not a valid binary digit / "G" is not a valid hexadecimal digit
        Assert.Contains("not a valid binary digit", LexError("SELECT B'2'")!.Value.Message);
        Assert.Contains("not a valid hexadecimal digit", LexError("SELECT X'GG'")!.Value.Message);
        Assert.Contains("not a valid binary digit", LexError("SELECT b'012'")!.Value.Message);
    }

    [Fact]
    public void Valid_bit_and_hex_content_passes()
    {
        // live PG18: B'0101' → 0101, X'ff' → 11111111, x'DEAD' → …, B'' → empty
        Assert.Null(LexError("SELECT B'0101'"));
        Assert.Null(LexError("SELECT B''"));
        Assert.Null(LexError("SELECT X'ff'"));
        Assert.Null(LexError("SELECT x'DEAD'"));
        // non-prefixed strings are never validated, and a word ending in b/x is not a prefix
        Assert.Null(LexError("SELECT 'GG2', club'GG', matrix'zz'"));
    }

    [Fact]
    public void Dollar_tag_must_not_start_with_a_digit()
    {
        // live PG18: $1$foo$1$ = positional param $1 + "unterminated dollar-quoted string at $foo$1$".
        var error = LexError("SELECT $1$foo$1$");
        Assert.Contains("unterminated dollar-quoted string", error!.Value.Message);

        // and a $ followed by a number still parses as a positional parameter
        var parsed = new PgParser().Parse("SELECT $1;");
        Assert.Empty(parsed.Diagnostics);
        var q = ((QueryStatement)parsed.Statements.Single()).Query;
        Assert.Equal("$1", Assert.IsType<ParamExpr>(q.Items.Single().Expr).Text);
        parsed.ReleaseTokens();
    }

    [Fact]
    public void Identifier_like_tags_still_work()
    {
        // live PG18: $tag1$x$tag1$ and $_x$y$_x$ are valid (digits allowed after the first char)
        Assert.Null(LexError("SELECT $tag1$x$tag1$"));
        Assert.Null(LexError("SELECT $_x$y$_x$"));
        Assert.Null(LexError("SELECT $$plain$$"));
    }
}
