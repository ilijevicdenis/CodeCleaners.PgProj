using System.Linq;
using PgProj.Core.Comparison;
using PgProj.Core.Model;
using PgProj.Core.Parsing;
using Xunit;

namespace PgProj.Core.Tests;

public class SequenceTests
{
    private static DatabaseModel Parse(string sql) => new SqlParser().Parse(sql);

    [Fact]
    public void Parses_sequence_options()
    {
        var s = Assert.Single(Parse("CREATE SEQUENCE app.s AS bigint INCREMENT BY 2 MINVALUE 10 MAXVALUE 100 START WITH 10 CACHE 5 CYCLE;").Sequences);
        Assert.Equal("bigint", s.DataType);
        Assert.Equal(2, s.Increment);
        Assert.Equal(10, s.MinValue);
        Assert.Equal(100, s.MaxValue);
        Assert.Equal(10, s.Start);
        Assert.Equal(5, s.Cache);
        Assert.True(s.Cycle);
    }

    [Fact]
    public void Emits_options_on_create()
    {
        var s = Parse("CREATE SEQUENCE app.s INCREMENT BY 4 CYCLE;").Sequences.Single();
        var sql = SqlEmitter.CreateSequence(s);
        Assert.Contains("INCREMENT BY 4", sql);
        Assert.Contains("CYCLE", sql);
    }

    [Fact]
    public void Changed_option_emits_alter_sequence()
    {
        var source = Parse("CREATE SEQUENCE app.s INCREMENT BY 4;");
        var target = Parse("CREATE SEQUENCE app.s INCREMENT BY 1;");
        var alter = Assert.Single(new SchemaComparer().Compare(source, target).OfType<AlterSequenceChange>());
        Assert.Contains("INCREMENT BY 4", alter.ToSql());
    }

    [Fact]
    public void Unspecified_options_do_not_churn()
    {
        // Source sets nothing; target (as if introspected) reports defaults. No diff expected.
        var source = Parse("CREATE SEQUENCE app.s;");
        var target = new DatabaseModel();
        target.Sequences.Add(new SequenceDefinition("app", "s", "bigint", 1, 1, long.MaxValue, 1, 1, false));
        Assert.Empty(new SchemaComparer().Compare(source, target).OfType<AlterSequenceChange>());
    }
}
