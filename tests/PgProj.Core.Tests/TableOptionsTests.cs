using System.Linq;
using PgProj.Core.Comparison;
using PgProj.Core.Model;
using PgProj.Core.Parsing;
using Xunit;

namespace PgProj.Core.Tests;

public class TableOptionsTests
{
    private static DatabaseModel Parse(string sql) => new SqlParser().Parse(sql);

    [Fact]
    public void Partition_by_clause_round_trips()
    {
        var t = Parse("CREATE TABLE app.events (id bigint, at timestamptz NOT NULL) PARTITION BY RANGE (at);")
            .FindTable("app", "events")!;
        Assert.Contains("PARTITION BY RANGE", t.TrailingOptions ?? "");
        Assert.Contains("PARTITION BY RANGE", SqlEmitter.CreateTable(t));
    }

    [Fact]
    public void Inherits_clause_round_trips()
    {
        var t = Parse("CREATE TABLE app.child (extra text) INHERITS (app.parent);").FindTable("app", "child")!;
        Assert.Contains("INHERITS", SqlEmitter.CreateTable(t));
    }

    [Fact]
    public void Partition_of_table_is_captured_as_raw_object_not_dropped()
    {
        // No column list -> must not crash; captured verbatim as a raw table object.
        var model = Parse("CREATE TABLE app.events_2026 PARTITION OF app.events FOR VALUES FROM ('2026-01-01') TO ('2027-01-01');");
        Assert.Empty(model.Tables); // not a finely-modelled table
        var o = Assert.Single(model.Objects);
        Assert.Equal(ObjectKind.Table, o.Kind);
        Assert.Equal("table:app.events_2026", o.Identity);
    }

    [Fact]
    public void Incompatible_type_change_emits_using_clause()
    {
        var source = Parse("CREATE TABLE app.t (c integer);");
        var target = Parse("CREATE TABLE app.t (c text);");
        var alter = Assert.Single(new SchemaComparer().Compare(source, target).OfType<AlterColumnChange>());
        Assert.Contains("USING \"c\"::integer", alter.ToSql());
    }

    [Fact]
    public void Length_only_change_does_not_add_using()
    {
        var source = Parse("CREATE TABLE app.t (c varchar(100));");
        var target = Parse("CREATE TABLE app.t (c varchar(50));");
        var alter = Assert.Single(new SchemaComparer().Compare(source, target).OfType<AlterColumnChange>());
        Assert.DoesNotContain("USING", alter.ToSql());
    }
}
