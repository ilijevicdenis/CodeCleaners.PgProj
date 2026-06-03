using PgProj.Core.Model;
using Xunit;

namespace PgProj.Core.Tests;

public class TypeNormalizerTests
{
    [Theory]
    [InlineData("int", "integer")]
    [InlineData("int4", "integer")]
    [InlineData("INT8", "bigint")]
    [InlineData("bool", "boolean")]
    [InlineData("varchar(50)", "character varying(50)")]
    [InlineData("VARCHAR (50)", "character varying(50)")]
    [InlineData("numeric( 12 , 2 )", "numeric(12, 2)")]
    [InlineData("timestamptz", "timestamp with time zone")]
    [InlineData("timestamp", "timestamp without time zone")]
    [InlineData("text", "text")]
    [InlineData("int[]", "integer[]")]
    [InlineData("decimal(10,0)", "numeric(10, 0)")]
    [InlineData("double precision", "double precision")]
    public void Normalizes_common_types(string input, string expected) =>
        Assert.Equal(expected, TypeNormalizer.Normalize(input));
}
