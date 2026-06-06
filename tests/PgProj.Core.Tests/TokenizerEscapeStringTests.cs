using System.Linq;
using PgProj.Core.Parsing;
using PgProj.Core.Syntax;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Tokenizer behaviour for escape-string constants (E'…'), where a backslash-escaped quote must NOT
/// terminate the literal — and the guard that keeps standard '…' strings (and e/E that are the tail of
/// a word or number) on the plain doubled-quote rule.
/// </summary>
public class TokenizerEscapeStringTests
{
    private static Token SingleString(string sql)
    {
        var toks = Tokenizer.Tokenize(sql);
        return toks.Single(t => t.Kind == TokenKind.String);
    }

    [Fact]
    public void EString_backslash_escaped_quote_does_not_end_the_string()
    {
        // E'it\'s' is one string; without escape awareness the lexer split it at \' and mis-tokenised.
        var toks = Tokenizer.Tokenize(@"E'it\'s'");
        // E word + one string token spanning the whole literal (no stray trailing tokens).
        Assert.Equal(TokenKind.Word, toks[0].Kind);
        Assert.Equal("E", toks[0].Value);
        Assert.Equal(TokenKind.String, toks[1].Kind);
        Assert.Equal(@"it\'s", toks[1].Value);
        Assert.Equal(2, toks.Count);
    }

    [Fact]
    public void Lowercase_e_prefix_also_enables_escapes()
    {
        var toks = Tokenizer.Tokenize(@"e'a\'b'");
        Assert.Equal(2, toks.Count);
        Assert.Equal(@"a\'b", toks[1].Value);
    }

    [Fact]
    public void Standard_string_treats_backslash_literally_and_only_doubles_quotes()
    {
        // Not an E-string: backslash is an ordinary char, the quote is closed by '' doubling.
        Assert.Equal(@"a\b", SingleString(@"'a\b'").Value);
        Assert.Equal("it's", SingleString("'it''s'").Value);
    }

    [Fact]
    public void Word_or_number_ending_in_e_before_quote_is_not_an_estring()
    {
        // "code" ends in 'e' but is a full identifier, so 'x' is a separate standard string: the backslash
        // must stay literal and the quote must still terminate normally.
        var toks = Tokenizer.Tokenize(@"code'a\'");
        // identifier "code", then a standard string 'a\' (closed at the second quote — backslash literal).
        Assert.Equal("code", toks[0].Value);
        Assert.Equal(TokenKind.String, toks[1].Kind);
        Assert.Equal(@"a\", toks[1].Value);
    }

    [Fact]
    public void EString_with_escaped_quote_in_a_default_parses_cleanly()
    {
        var res = new PgParser().Parse(@"CREATE TABLE s.t (c text DEFAULT E'it\'s')");
        Assert.True(res.FullyRecognized);
        Assert.Empty(res.Diagnostics);
    }

    [Fact]
    public void Scientific_notation_number_still_tokenises()
    {
        // Exercises the e+/e- sign clause in ReadNumber (guarded against an out-of-range lookbehind).
        var toks = Tokenizer.Tokenize("1e-9");
        Assert.Equal(TokenKind.Number, toks[0].Kind);
        Assert.Equal("1e-9", toks[0].Value);
        Assert.Single(toks);
    }
}
