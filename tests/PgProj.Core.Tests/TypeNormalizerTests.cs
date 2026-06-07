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
    // --- #51 additions: new aliases + documented pass-throughs --------------------------------------
    [InlineData("varbit", "bit varying")]
    [InlineData("VARBIT (8)", "bit varying(8)")]
    [InlineData("dec(10,2)", "numeric(10, 2)")]
    [InlineData("jsonb", "jsonb")]      // already canonical → pass through
    [InlineData("JSONB", "jsonb")]
    [InlineData("bytea", "bytea")]
    [InlineData("xml", "xml")]
    [InlineData("money", "money")]
    [InlineData("uuid", "uuid")]
    [InlineData("UUID[]", "uuid[]")]
    [InlineData("inet", "inet")]
    [InlineData("tsvector", "tsvector")]
    [InlineData("my_domain", "my_domain")]   // user-defined/domain passes through unchanged
    public void Normalizes_common_types(string input, string expected) =>
        Assert.Equal(expected, TypeNormalizer.Normalize(input));
}
