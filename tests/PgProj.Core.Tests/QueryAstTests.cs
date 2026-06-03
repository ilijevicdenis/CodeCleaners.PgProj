using System.Linq;
using PgProj.Core.Ast;
using PgProj.Core.Parsing;
using Xunit;

namespace PgProj.Core.Tests;

public class QueryAstTests
{
    private static SqlScript Parse(string sql) => new AstParser().Parse(sql);

    [Fact]
    public void Case_expression_parses_to_case_node()
    {
        var check = SqlTree.Descendants<CheckColumnConstraintNode>(
            Parse("CREATE TABLE app.t (x int CHECK (CASE WHEN x > 0 THEN true ELSE false END));")).Single();
        var c = Assert.IsType<CaseExpr>(check.Expression);
        Assert.Single(c.Branches);
        Assert.NotNull(c.Else);
    }

    [Fact]
    public void View_where_is_a_parsed_expression()
    {
        var view = SqlTree.Descendants<CreateViewStatement>(
            Parse("CREATE VIEW app.v AS SELECT 1 FROM app.t WHERE a = 1 AND b > 2;")).Single();
        Assert.NotNull(view.Query);
        var where = Assert.IsType<BinaryExpr>(view.Query!.Where);
        Assert.Equal("AND", where.Op);
    }

    [Fact]
    public void View_with_cte_captures_the_cte()
    {
        var view = SqlTree.Descendants<CreateViewStatement>(
            Parse("CREATE VIEW app.v AS WITH recent AS (SELECT id FROM app.t) SELECT * FROM recent;")).Single();
        var cte = Assert.Single(view.Query!.With);
        Assert.Equal("recent", cte.Name);
        Assert.NotNull(cte.Query);
    }

    [Fact]
    public void Subselect_in_where_is_structured()
    {
        var view = SqlTree.Descendants<CreateViewStatement>(
            Parse("CREATE VIEW app.v AS SELECT id FROM app.t WHERE id IN (SELECT id FROM app.u);")).Single();
        var inExpr = Assert.IsType<InExpr>(view.Query!.Where);
        Assert.NotNull(inExpr.Subquery);
        Assert.Equal("app.u", inExpr.Subquery!.Query.From!.Relations.Single().TableName);
    }

    [Fact]
    public void Function_body_update_where_is_parsed()
    {
        var fn = SqlTree.Descendants<CreateFunctionStatement>(Parse("""
            CREATE FUNCTION app.f() RETURNS void LANGUAGE plpgsql AS $$
            BEGIN UPDATE app.t SET x = 1 WHERE id = 5; END; $$;
            """)).Single();
        var upd = SqlTree.Descendants<DmlStatementNode>(fn.Body).Single(d => d.Verb == "UPDATE");
        Assert.True(upd.HasWhere);
        var w = Assert.IsType<BinaryExpr>(upd.WhereExpression);
        Assert.Equal("=", w.Op);
    }

    [Fact]
    public void Nested_subquery_verbs_do_not_leak_into_classification()
    {
        // The inner SELECT must not be mistaken for the statement verb.
        var fn = SqlTree.Descendants<CreateFunctionStatement>(Parse("""
            CREATE FUNCTION app.f() RETURNS void LANGUAGE plpgsql AS $$
            BEGIN DELETE FROM app.t WHERE id IN (SELECT id FROM app.u); END; $$;
            """)).Single();
        var del = SqlTree.Descendants<DmlStatementNode>(fn.Body).Single();
        Assert.Equal("DELETE", del.Verb);
        Assert.True(del.HasWhere);
    }
}
