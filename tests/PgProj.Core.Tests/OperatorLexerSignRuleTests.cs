using System.Linq;
using PgProj.Core.Parsing;
using PgProj.Core.Syntax;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// PostgreSQL's trailing-sign lexer rule (audit P1, ground-truthed against live PG18): a multi-char
/// operator may only end in '+'/'-' when it also contains one of ~!@#%^&amp;|? — otherwise the sign starts
/// its own token. The old unconditional merge lexed "a&lt;=-1" as a bogus "&lt;=-" operator, silently losing
/// the unary minus in any unspaced comparison (CHECK (x&gt;=-100), WHERE a&lt;=-1, …).
/// Plus: '^' is LEFT-associative in PG (2^3^2 = 64, live-verified) — was right-associative here.
/// </summary>
public class OperatorLexerSignRuleTests
{
    private static List<Token> Lex(string sql)
    {
        var toks = OperatorLexer.Merge(Tokenizer.Tokenize(sql));
        return toks;
    }

    private static string[] Symbols(string sql) =>
        Lex(sql).Where(t => t.Kind == TokenKind.Symbol).Select(t => t.Value).ToArray();

    [Fact]
    public void Trailing_sign_backs_off_when_no_allowing_char()
    {
        // live PG18: SELECT 1<=-1 → false, i.e. '<=' then '-1'
        Assert.Equal(new[] { "<=", "-" }, Symbols("a<=-1"));
        Assert.Equal(new[] { ">=", "-" }, Symbols("x>=-100"));
        Assert.Equal(new[] { "=", "-" }, Symbols("a=-1"));
        Assert.Equal(new[] { "<", "-" }, Symbols("3<-1"));
        // multiple trailing signs shed one at a time, then re-lex
        Assert.Equal(new[] { "<=", "+", "-" }, Symbols("a<=+-1"));
    }

    [Fact]
    public void Allowing_chars_keep_the_sign_merged()
    {
        // '^' '|' '@' '!' etc. license a trailing sign — live PG18 lexes 2^-2 as the single op '^-'
        // (and then errors at operator resolution, which is PG's business, not the lexer's).
        Assert.Equal(new[] { "^-" }, Symbols("2^-2"));
        Assert.Equal(new[] { "||-" }, Symbols("a||-1"));
        Assert.Equal(new[] { "@-" }, Symbols("@-1"));
    }

    [Fact]
    public void Normal_operators_are_unaffected()
    {
        Assert.Equal(new[] { "->>" }, Symbols("a->>1"));
        Assert.Equal(new[] { "::" }, Symbols("a::int"));
        Assert.Equal(new[] { "-" }, Symbols("3-2"));      // single sign is plain binary minus
        Assert.Equal(new[] { "<>" }, Symbols("a<>1"));
    }

    [Fact]
    public void Unspaced_comparison_against_negative_literal_parses_correctly()
    {
        // The end-to-end regression: BinaryExpr{Op="<=-"} silently lost the unary minus.
        var result = new PgParser().Parse("SELECT 1 WHERE a<=-1;");
        Assert.Empty(result.Diagnostics);
        var q = Assert.IsType<QueryStatement>(result.Statements.Single()).Query;
        var cmp = Assert.IsType<BinaryExpr>(q.Where);
        Assert.Equal("<=", cmp.Op);
        var neg = Assert.IsType<UnaryExpr>(cmp.Right);
        Assert.Equal("-", neg.Op);
        result.ReleaseTokens();
    }

    [Fact]
    public void Exponent_is_left_associative()
    {
        // live PG18: SELECT 2^3^2 → 64 = (2^3)^2
        var result = new PgParser().Parse("SELECT 2^3^2;");
        var q = Assert.IsType<QueryStatement>(result.Statements.Single()).Query;
        var outer = Assert.IsType<BinaryExpr>(q.Items.Single().Expr);
        Assert.Equal("^", outer.Op);
        var inner = Assert.IsType<BinaryExpr>(outer.Left);   // (2^3) on the LEFT
        Assert.Equal("^", inner.Op);
        Assert.IsType<LiteralExpr>(outer.Right);
        result.ReleaseTokens();
    }

    [Fact]
    public void Unary_minus_binds_tighter_than_exponent()
    {
        // live PG18: SELECT -2^2 → 4 = (-2)^2. (The audit claimed -(2^2) — the oracle says otherwise.)
        var result = new PgParser().Parse("SELECT -2^2;");
        var q = Assert.IsType<QueryStatement>(result.Statements.Single()).Query;
        var top = Assert.IsType<BinaryExpr>(q.Items.Single().Expr);
        Assert.Equal("^", top.Op);
        Assert.IsType<UnaryExpr>(top.Left);                  // (-2) on the LEFT
        result.ReleaseTokens();
    }
}
