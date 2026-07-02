using System.Linq;
using PgProj.Core.Parsing;
using PgProj.Core.Syntax;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Numeric-literal grammar (audit P1, every case ground-truthed against live PG18): the old scan
/// accepted 1.2.3 / 1e+ / 1e5e6 as ONE Number token and 1a as number+alias — Postgres rejects them
/// all ("syntax error" for the double dot, "trailing junk after numeric literal" for the rest).
/// </summary>
public class NumericLiteralGrammarTests
{
    private static (string[] numbers, LexError? error) Lex(string sql)
    {
        var pooled = Tokenizer.TokenizePooled(sql, out var error);
        var numbers = new System.Collections.Generic.List<string>();
        for (var i = 0; i < pooled.Count; i++)
            if (pooled[i].Kind == TokenKind.Number) numbers.Add(pooled[i].Value);
        pooled.Return();
        return (numbers.ToArray(), error);
    }

    [Fact]
    public void Valid_forms_still_lex_as_one_number()
    {
        // live PG18: all valid — 1.e5 → 100000, .5 → 0.5, 1. → 1
        Assert.Equal(new[] { "1.2" }, Lex("SELECT 1.2").numbers);
        Assert.Equal(new[] { "1.2e-5" }, Lex("SELECT 1.2e-5").numbers);
        Assert.Equal(new[] { "1.e5" }, Lex("SELECT 1.e5").numbers);
        Assert.Equal(new[] { ".5" }, Lex("SELECT .5").numbers);
        Assert.Equal(new[] { "1." }, Lex("SELECT 1.").numbers);
        Assert.Equal(new[] { "1_000_000" }, Lex("SELECT 1_000_000").numbers);
        Assert.Equal(new[] { "0xFF_FF" }, Lex("SELECT 0xFF_FF").numbers);
        Assert.Null(Lex("SELECT 1.2e-5, .5, 0b1010").error);
    }

    [Fact]
    public void Double_decimal_point_stops_after_the_first()
    {
        // live PG18: SELECT 1.2.3 → syntax error at or near ".3" (i.e. the literal is 1.2)
        Assert.Equal(new[] { "1.2", ".3" }, Lex("SELECT 1.2.3").numbers);
        // …and our parser turns the stray literal into a diagnostic, matching PG's verdict
        var parsed = new PgParser().Parse("SELECT 1.2.3;");
        Assert.NotEmpty(parsed.Diagnostics);
        parsed.ReleaseTokens();
    }

    [Fact]
    public void Dot_dot_is_never_taken_into_the_number()
    {
        // live PG18: SELECT 1..3 → syntax error at or near ".." — the first literal is just "1" (PG's
        // dot-dot rule). The tail then lexes exactly as PG's scanner does: '.' followed by the number
        // ".10" (scan.l's \.{decdigit}+ rule), so the whole stream is 1 · . · .10.
        var (numbers, error) = Lex("FOR i IN 1..10");
        Assert.Equal(new[] { "1", ".10" }, numbers);
        Assert.Null(error);
    }

    [Fact]
    public void Trailing_junk_is_a_lex_error()
    {
        // live PG18: all "trailing junk after numeric literal"
        Assert.Contains("trailing junk", Lex("SELECT 1a").error!.Value.Message);
        Assert.Contains("trailing junk", Lex("SELECT 1e+").error!.Value.Message);
        Assert.Contains("trailing junk", Lex("SELECT 1e5e6").error!.Value.Message);
        Assert.Contains("trailing junk", Lex("SELECT 0x1G").error!.Value.Message);
        Assert.Contains("trailing junk", Lex("SELECT 100foo FROM bar").error!.Value.Message);

        // the parser converts it into a hard diagnostic (previously accepted silently as number+alias)
        var parsed = new PgParser().Parse("SELECT 100foo FROM bar;");
        Assert.Contains(parsed.Diagnostics, d => d.Message.Contains("trailing junk"));
        Assert.False(parsed.FullyRecognized);
        parsed.ReleaseTokens();
    }

    [Fact]
    public void Exponent_without_digits_is_not_an_exponent()
    {
        // '1e' splits into number 1 + word e → junk error; a spaced alias 'SELECT 1 e' stays legal
        Assert.Contains("trailing junk", Lex("SELECT 1e").error!.Value.Message);
        Assert.Null(Lex("SELECT 1 e").error);
    }
}
