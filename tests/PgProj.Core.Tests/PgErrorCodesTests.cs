using PgProj.Core.Diagnostics;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>The complete PostgreSQL SQLSTATE table + the Describe enrichment used in validate/publish output.</summary>
public sealed class PgErrorCodesTests
{
    [Fact]
    public void Table_is_complete()
    {
        // errcodes.txt (REL_18_STABLE) lists 268 rows across 43 classes; 6 SQLSTATEs appear twice with
        // alias condition names, so there are 262 DISTINCT codes (first spelling wins).
        Assert.Equal(262, PgErrorCodes.Count);
    }

    [Theory]
    [InlineData("42704", "undefined_object", "42")]
    [InlineData("23505", "unique_violation", "23")]
    [InlineData("23503", "foreign_key_violation", "23")]
    [InlineData("00000", "successful_completion", "00")]
    [InlineData("0A000", "feature_not_supported", "0A")]
    [InlineData("42P01", "undefined_table", "42")]
    [InlineData("40P01", "deadlock_detected", "40")]
    public void Known_codes_resolve(string code, string condition, string cls)
    {
        var e = PgErrorCodes.Lookup(code);
        Assert.NotNull(e);
        Assert.Equal(condition, e!.ConditionName);
        Assert.Equal(cls, e.ClassCode);
        Assert.False(string.IsNullOrEmpty(e.ClassName));
    }

    [Fact]
    public void Lookup_is_case_insensitive_on_hex_letters()
    {
        Assert.Equal("feature_not_supported", PgErrorCodes.Lookup("0a000")!.ConditionName);
    }

    [Fact]
    public void Describe_formats_code_condition_and_class()
    {
        var s = PgErrorCodes.Describe("42704");
        Assert.Contains("42704", s);
        Assert.Contains("undefined_object", s);
        Assert.Contains("class 42", s);
    }

    [Fact]
    public void Describe_falls_back_for_unknown_and_blank()
    {
        Assert.Equal("ZZZZZ", PgErrorCodes.Describe("ZZZZZ"));   // unknown → bare input
        Assert.Equal("", PgErrorCodes.Describe(null));
        Assert.Equal("", PgErrorCodes.Describe("   "));
        Assert.Null(PgErrorCodes.Lookup("ZZZZZ"));
    }
}
