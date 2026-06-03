using System.Linq;
using PgProj.Core.Analysis;
using PgProj.Core.Ast;
using PgProj.Core.Parsing;
using Xunit;

namespace PgProj.Core.Tests;

public class PlpgsqlTests
{
    private static CreateFunctionStatement Fn(string body, string lang = "plpgsql") =>
        SqlTree.Descendants<CreateFunctionStatement>(
            new AstParser().Parse($"CREATE FUNCTION app.f() RETURNS void LANGUAGE {lang} AS $$ {body} $$;")).Single();

    [Fact]
    public void Block_if_loop_are_structured()
    {
        var fn = Fn("""
            BEGIN
                IF x > 0 THEN
                    UPDATE app.t SET a = 1 WHERE id = x;
                ELSE
                    DELETE FROM app.t;
                END IF;
                WHILE y > 0 LOOP
                    PERFORM app.g();
                END LOOP;
            END;
            """);

        var block = Assert.IsType<BlockStatement>(fn.Body.Statements.Single());
        var ifStmt = block.Body.OfType<IfStatement>().Single();
        Assert.IsType<BinaryExpr>(ifStmt.Condition);
        Assert.Single(ifStmt.Then.OfType<DmlStatementNode>());
        Assert.Single(ifStmt.Else.OfType<DmlStatementNode>());
        Assert.Single(block.Body.OfType<LoopStatement>());
    }

    [Fact]
    public void Nested_dml_is_found_by_descendant_walk()
    {
        var fn = Fn("""
            BEGIN
                IF a THEN
                    IF b THEN
                        DELETE FROM app.t;   -- no WHERE, nested two levels deep
                    END IF;
                END IF;
            END;
            """);
        var del = SqlTree.Descendants<DmlStatementNode>(fn.Body).Single();
        Assert.Equal("DELETE", del.Verb);
        Assert.False(del.HasWhere);
    }

    [Fact]
    public void PG003_fires_on_unguarded_delete_inside_if()
    {
        var script = new AstParser().Parse("""
            CREATE FUNCTION app.f() RETURNS void LANGUAGE plpgsql AS $$
            BEGIN IF cond THEN DELETE FROM app.t; END IF; END; $$;
            """);
        Assert.Contains(SqlAnalyzer.Default().Analyze(script), d => d.RuleId == "PG003");
    }

    [Fact]
    public void Assignment_and_return_query_are_modelled()
    {
        var fn = Fn("""
            BEGIN
                total := count(*) + 1;
                RETURN QUERY SELECT id FROM app.t WHERE id > 0;
            END;
            """);
        var block = Assert.IsType<BlockStatement>(fn.Body.Statements.Single());
        Assert.Single(block.Body.OfType<AssignmentStatement>());
        var ret = block.Body.OfType<ReturnStatement>().Single();
        Assert.Equal("RETURN QUERY", ret.Kind);
        Assert.NotNull(ret.Query);
        Assert.NotNull(ret.Query!.Where);
    }

    [Fact]
    public void Exception_handler_block_is_captured()
    {
        var fn = Fn("""
            BEGIN
                INSERT INTO app.t VALUES (1);
            EXCEPTION WHEN unique_violation THEN
                PERFORM app.log();
            END;
            """);
        var block = Assert.IsType<BlockStatement>(fn.Body.Statements.Single());
        var handler = Assert.Single(block.Handlers);
        Assert.Contains("unique_violation", handler.ConditionText);
        Assert.Single(SqlTree.Descendants<DmlStatementNode>(handler));
    }

    [Fact]
    public void Sql_language_body_is_a_flat_statement()
    {
        var fn = Fn("SELECT count(*) FROM app.t WHERE x > 0", lang: "sql");
        var dml = SqlTree.Descendants<DmlStatementNode>(fn.Body).Single();
        Assert.Equal("SELECT", dml.Verb);
    }
}
