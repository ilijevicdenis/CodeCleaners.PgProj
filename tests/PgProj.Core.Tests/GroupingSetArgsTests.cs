using System.Linq;
using PgProj.Core.Syntax;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// ROLLUP / CUBE / GROUPING SETS argument lists must reach SelectQuery.GroupBy (audit P1: the whole
/// parenthesized list was skipped as raw tokens, hiding the participating columns from every semantic
/// consumer that walks GroupBy).
/// </summary>
public class GroupingSetArgsTests
{
    private static SelectQuery Parse(string sql)
    {
        var parsed = new PgParser().Parse(sql);
        Assert.Empty(parsed.Diagnostics);
        var q = Assert.IsType<QueryStatement>(parsed.Statements.Single()).Query;
        parsed.ReleaseTokens();
        return q;
    }

    private static string[] GroupByColumns(SelectQuery q) =>
        q.GroupBy.OfType<ColumnRef>().Select(c => c.NameParts.Last()).ToArray();

    [Fact]
    public void Rollup_args_reach_group_by()
    {
        var q = Parse("SELECT a, b, count(*) FROM t GROUP BY ROLLUP (a, b);");
        Assert.Equal("ROLLUP", q.GroupByKind);
        Assert.Equal(new[] { "a", "b" }, GroupByColumns(q));
    }

    [Fact]
    public void Cube_with_nested_list_flattens_the_leaves()
    {
        var q = Parse("SELECT 1 FROM t GROUP BY CUBE (a, (b, c));");
        Assert.Equal("CUBE", q.GroupByKind);
        Assert.Equal(new[] { "a", "b", "c" }, GroupByColumns(q));
    }

    [Fact]
    public void Grouping_sets_with_empty_set_and_nested_rollup()
    {
        var q = Parse("SELECT 1 FROM t GROUP BY GROUPING SETS ((a, b), (), ROLLUP (c));");
        Assert.Equal("GROUPING SETS", q.GroupByKind);
        Assert.Equal(new[] { "a", "b", "c" }, GroupByColumns(q));
    }

    [Fact]
    public void Plain_group_by_is_unchanged()
    {
        var q = Parse("SELECT 1 FROM t GROUP BY a, b;");
        Assert.Null(q.GroupByKind);
        Assert.Equal(new[] { "a", "b" }, GroupByColumns(q));
    }
}
