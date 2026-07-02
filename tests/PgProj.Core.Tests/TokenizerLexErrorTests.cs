using System.Linq;
using PgProj.Core.Parsing;
using PgProj.Core.Syntax;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Unterminated lexical constructs (block comment / string / quoted identifier / dollar-quote) consume
/// to end-of-input — the scan must terminate — but they can SWALLOW whole statements, so they must
/// surface as a hard diagnostic, never as a silent FullyRecognized pass (P0 audit finding, 2026-07-02:
/// an unclosed /* before a CREATE TABLE made that table vanish from the model with zero diagnostics).
/// </summary>
public class TokenizerLexErrorTests
{
    [Fact]
    public void Unterminated_block_comment_reports_a_lex_error_at_its_start()
    {
        Tokenizer.TokenizePooled("SELECT 1; /* oops", out var error).Return();
        Assert.NotNull(error);
        Assert.Contains("unterminated block comment", error!.Value.Message);
        Assert.Equal(10, error.Value.Offset);
    }

    [Fact]
    public void Nested_block_comment_missing_one_close_is_still_unterminated()
    {
        Tokenizer.TokenizePooled("/* outer /* inner */ still open", out var error).Return();
        Assert.NotNull(error);
        Assert.Contains("unterminated block comment", error!.Value.Message);
    }

    [Fact]
    public void Unterminated_string_and_quoted_identifier_report_lex_errors()
    {
        Tokenizer.TokenizePooled("SELECT 'never closed", out var s).Return();
        Assert.Contains("unterminated string literal", s!.Value.Message);

        Tokenizer.TokenizePooled("SELECT \"never closed", out var q).Return();
        Assert.Contains("unterminated quoted identifier", q!.Value.Message);
    }

    [Fact]
    public void Unterminated_dollar_quote_reports_the_missing_tag()
    {
        Tokenizer.TokenizePooled("SELECT $body$ no close", out var error).Return();
        Assert.NotNull(error);
        Assert.Contains("$body$", error!.Value.Message);
    }

    [Fact]
    public void Terminated_constructs_report_no_error()
    {
        Tokenizer.TokenizePooled("/* fine /* nested */ */ SELECT 'ok', \"id\", $q$x$q$;", out var error).Return();
        Assert.Null(error);
    }

    [Fact]
    public void Only_the_first_error_is_kept()
    {
        // The unterminated string swallows the rest; a cascading report would point at noise.
        Tokenizer.TokenizePooled("SELECT 'open /* also open", out var error).Return();
        Assert.Contains("unterminated string literal", error!.Value.Message);
    }

    [Fact]
    public void Parser_turns_a_swallowed_statement_into_a_diagnostic_not_a_silent_pass()
    {
        // The regression that motivated this: table b silently vanished from the model.
        var result = new PgParser().Parse("CREATE TABLE a(id int); /* forgot to close\nCREATE TABLE b(id int);");
        Assert.False(result.FullyRecognized);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("unterminated block comment"));
        result.ReleaseTokens();
    }

    [Fact]
    public void Diagnostic_carries_the_real_line_and_column()
    {
        var result = new PgParser().Parse("CREATE TABLE a(id int);\nSELECT 'oops");
        var d = result.Diagnostics.Single(x => x.Message.Contains("unterminated string literal"));
        Assert.Equal(2, d.Line);
        Assert.Equal(8, d.Column);
        result.ReleaseTokens();
    }
}
