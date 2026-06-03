using System.Linq;
using PgProj.Core.Syntax;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>Focused unit tests for the hand-written SELECT / expression grammar in PgParser.</summary>
public class SelectSyntaxTests
{
    private static SelectQuery Q(string sql)
    {
        var res = new PgParser().Parse(sql);
        Assert.True(res.FullyRecognized);
        Assert.Empty(res.Diagnostics);
        return Assert.IsType<QueryStatement>(res.Statements.Single()).Query;
    }

    private static ParseResult Parse(string sql) => new PgParser().Parse(sql);

    [Fact]
    public void Target_list_aliases_and_star()
    {
        var q = Q("SELECT a, b AS bee, t.*, count(*) FROM s.t t");
        Assert.Equal(4, q.Items.Count);
        Assert.Equal("bee", q.Items[1].Alias);
        Assert.IsType<StarExpr>(q.Items[2].Expr);
        Assert.IsType<FuncCallExpr>(q.Items[3].Expr);
    }

    [Fact]
    public void Joins_on_and_using()
    {
        var q = Q("SELECT 1 FROM a JOIN b ON a.id = b.id LEFT JOIN c USING (x)");
        var rel = q.From!.Relations.Single();
        Assert.Equal(2, rel.Joins.Count);
        Assert.Equal("INNER", rel.Joins[0].JoinType);
        Assert.IsType<BinaryExpr>(rel.Joins[0].On);
        Assert.Equal("LEFT", rel.Joins[1].JoinType);
        Assert.Equal(new[] { "x" }, rel.Joins[1].Using);
    }

    [Fact]
    public void Where_group_having_orderby_limit()
    {
        var q = Q("SELECT x, count(*) FROM t WHERE x > 0 GROUP BY x HAVING count(*) > 1 ORDER BY x DESC NULLS LAST LIMIT 10 OFFSET 5");
        Assert.NotNull(q.Where);
        Assert.Single(q.GroupBy);
        Assert.NotNull(q.Having);
        Assert.Equal("DESC", q.OrderBy.Single().Direction);
        Assert.Equal("LAST", q.OrderBy.Single().Nulls);
        Assert.Equal("10", q.Limit);
        Assert.Equal("5", q.Offset);
    }

    [Fact]
    public void Distinct_on_and_set_operation()
    {
        var q = Q("SELECT DISTINCT ON (a) a, b FROM t UNION ALL SELECT a, b FROM u");
        Assert.NotNull(q.SetOp);
        Assert.Equal("UNION ALL", q.SetOp!.Op);
        Assert.True(q.SetOp.Left.Distinct);
        Assert.Single(q.SetOp.Left.DistinctOn);
    }

    [Fact]
    public void Cte_window_and_subquery_in_from()
    {
        var q = Q("WITH r AS (SELECT 1 AS n) SELECT n, row_number() OVER (PARTITION BY n ORDER BY n) FROM (SELECT n FROM r) sub");
        Assert.Single(q.With);
        Assert.Equal("r", q.With[0].Name);
        Assert.NotNull(q.From!.Relations.Single().Subquery);
        var win = q.Items[1].Expr as FuncCallExpr;
        Assert.NotNull(win!.Over);
    }

    [Fact]
    public void Expression_operators_case_cast_in_between()
    {
        var q = Q("SELECT CASE WHEN a IN (1,2,3) THEN a::text ELSE 'x' END FROM t WHERE a BETWEEN 1 AND 10 AND b IS NOT NULL");
        Assert.IsType<CaseExpr>(q.Items.Single().Expr);
        Assert.NotNull(q.Where);
    }

    [Fact]
    public void For_update_locking()
    {
        var q = Q("SELECT * FROM t FOR UPDATE OF t SKIP LOCKED");
        var lk = q.Locking.Single();
        Assert.Equal("UPDATE", lk.Strength);
        Assert.Equal("SKIP LOCKED", lk.Wait);
    }

    [Theory]
    [InlineData("SELECT FROM")]                 // empty target then FROM with no table
    [InlineData("SELECT a b c FROM t")]         // two aliases
    [InlineData("SELECT * FROM a JOIN b")]      // JOIN without ON/USING
    [InlineData("SELECT CASE a END")]           // CASE with no WHEN
    [InlineData("SELECT (1 + )")]               // dangling operator
    public void Rejects_malformed(string sql)
    {
        var res = Parse(sql);
        Assert.True(res.FullyRecognized);
        Assert.NotEmpty(res.Diagnostics);
    }
}
