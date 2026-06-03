using System.Linq;
using PgProj.Core.Ast;
using PgProj.Core.Parsing;
using Xunit;

namespace PgProj.Core.Tests;

public class SelectGrammarTests
{
    private static SelectQuery View(string body) =>
        SqlTree.Descendants<CreateViewStatement>(new AstParser().Parse($"CREATE VIEW app.v AS {body};"))
            .Single().Query!;

    [Fact]
    public void Projection_items_with_aliases_and_star()
    {
        var q = View("SELECT a, b AS bee, count(*) AS n, t.* FROM app.t");
        Assert.Equal(4, q.Items.Count);
        Assert.Equal("bee", q.Items[1].Alias);
        Assert.Equal("n", q.Items[2].Alias);
        Assert.IsType<FunctionCallExpr>(q.Items[2].Expr);
    }

    [Fact]
    public void From_with_left_join_and_on_condition()
    {
        var q = View("SELECT c.id FROM app.customers c LEFT JOIN app.orders o ON o.customer_id = c.id");
        var rel = q.From!.Relations.Single();
        Assert.Equal("app.customers", rel.TableName);
        Assert.Equal("c", rel.Alias);
        var join = rel.Joins.Single();
        Assert.Equal("LEFT", join.JoinType);
        Assert.Equal("app.orders", join.Right.TableName);
        Assert.Equal("o", join.Right.Alias);
        var on = Assert.IsType<BinaryExpr>(join.On);
        Assert.Equal("=", on.Op);
    }

    [Fact]
    public void Group_by_having_order_limit()
    {
        var q = View("SELECT c.id, count(*) FROM app.orders o GROUP BY c.id HAVING count(*) > 1 ORDER BY c.id DESC LIMIT 10 OFFSET 5");
        Assert.Single(q.GroupBy);
        Assert.IsType<BinaryExpr>(q.Having);
        var ob = q.OrderBy.Single();
        Assert.Equal("DESC", ob.Direction);
        Assert.NotNull(q.Limit);
        Assert.NotNull(q.Offset);
    }

    [Fact]
    public void Set_operation_chains_queries()
    {
        var q = View("SELECT a FROM app.t UNION ALL SELECT a FROM app.u");
        Assert.NotNull(q.SetOp);
        Assert.Equal("UNION ALL", q.SetOp!.Op);
        Assert.Equal("app.u", q.SetOp.Right.From!.Relations.Single().TableName);
    }

    [Fact]
    public void Distinct_and_subquery_in_from()
    {
        var q = View("SELECT DISTINCT x FROM (SELECT id AS x FROM app.t) sub");
        Assert.True(q.Distinct);
        var rel = q.From!.Relations.Single();
        Assert.NotNull(rel.Subquery);
        Assert.Equal("sub", rel.Alias);
    }

    [Fact]
    public void Cross_join_via_comma_is_two_relations()
    {
        var q = View("SELECT 1 FROM app.a, app.b");
        Assert.Equal(2, q.From!.Relations.Count);
    }
}
