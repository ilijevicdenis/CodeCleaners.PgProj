using System.Linq;
using PgProj.Core.Analysis;
using PgProj.Core.Ast;
using PgProj.Core.Parsing;
using Xunit;

namespace PgProj.Core.Tests;

public class AstAnalysisTests
{
    private static SqlScript Parse(string sql) => new AstParser().Parse(sql);
    private static System.Collections.Generic.IReadOnlyList<Diagnostic> Analyze(string sql) =>
        SqlAnalyzer.Default().Analyze(Parse(sql));

    // ---- AST shape ----------------------------------------------------------------------

    [Fact]
    public void Parses_function_header_into_structured_node()
    {
        var fn = SqlTree.Descendants<CreateFunctionStatement>(Parse("""
            CREATE FUNCTION app.f(a integer, b text DEFAULT 'x')
            RETURNS integer LANGUAGE plpgsql STABLE SECURITY DEFINER
            SET search_path = pg_catalog AS $$ BEGIN RETURN a; END; $$;
            """)).Single();

        Assert.Equal("app", fn.Header.Schema);
        Assert.Equal("f", fn.Header.Name);
        Assert.Equal("plpgsql", fn.Header.Language);
        Assert.Equal("STABLE", fn.Header.Volatility);
        Assert.Equal("DEFINER", fn.Header.Security);
        Assert.Equal(2, fn.Header.Parameters.Count);
        Assert.Contains("search_path", fn.Header.SetClauses.Single());
        Assert.Equal("integer, text", fn.Header.ArgTypes);
    }

    [Fact]
    public void Classifies_function_body_statements()
    {
        var fn = SqlTree.Descendants<CreateFunctionStatement>(Parse("""
            CREATE FUNCTION app.f() RETURNS void LANGUAGE plpgsql AS $$
            BEGIN
                UPDATE app.t SET x = 1;
                DELETE FROM app.t WHERE id = 5;
                EXECUTE 'DROP TABLE x';
                PERFORM app.g();
            END; $$;
            """)).Single();

        var dml = fn.Body.Statements.OfType<DmlStatementNode>().ToList();
        Assert.Contains(dml, d => d.Verb == "UPDATE" && !d.HasWhere);
        Assert.Contains(dml, d => d.Verb == "DELETE" && d.HasWhere);
        Assert.Single(fn.Body.Statements.OfType<DynamicSqlStatementNode>());
    }

    [Fact]
    public void Expression_parser_builds_a_tree_for_check()
    {
        var t = SqlTree.Descendants<CheckConstraintNode>(
            Parse("CREATE TABLE app.t (qty int, CONSTRAINT ck CHECK (qty > 0));")).Single();
        var bin = Assert.IsType<BinaryExpr>(t.Expression);
        Assert.Equal(">", bin.Op);
        Assert.IsType<IdentifierExpr>(bin.Left);
        Assert.IsType<LiteralExpr>(bin.Right);
    }

    [Fact]
    public void Walker_visits_every_node()
    {
        var script = Parse("CREATE TABLE app.t (id int CHECK (id > 0));");
        var count = SqlTree.DescendantsAndSelf(script).Count();
        Assert.True(count >= 5); // script, table, column, type, check, expression...
    }

    // ---- safety rules -------------------------------------------------------------------

    [Fact]
    public void PG001_flags_security_definer_without_search_path()
    {
        var d = Analyze("CREATE FUNCTION app.f() RETURNS int LANGUAGE sql SECURITY DEFINER AS $$ SELECT 1 $$;");
        Assert.Contains(d, x => x.RuleId == "PG001");
    }

    [Fact]
    public void PG001_satisfied_when_search_path_pinned()
    {
        var d = Analyze("CREATE FUNCTION app.f() RETURNS int LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog AS $$ SELECT 1 $$;");
        Assert.DoesNotContain(d, x => x.RuleId == "PG001");
    }

    [Fact]
    public void PG002_flags_dynamic_sql()
    {
        var d = Analyze("CREATE FUNCTION app.f() RETURNS void LANGUAGE plpgsql AS $$ BEGIN EXECUTE 'select 1'; END; $$;");
        Assert.Contains(d, x => x.RuleId == "PG002");
    }

    [Fact]
    public void PG003_flags_unguarded_mutation_only_without_where()
    {
        Assert.Contains(Analyze("CREATE FUNCTION app.f() RETURNS void LANGUAGE plpgsql AS $$ BEGIN UPDATE app.t SET x=1; END; $$;"),
            x => x.RuleId == "PG003");
        Assert.DoesNotContain(Analyze("CREATE FUNCTION app.f() RETURNS void LANGUAGE plpgsql AS $$ BEGIN UPDATE app.t SET x=1 WHERE id=2; END; $$;"),
            x => x.RuleId == "PG003");
    }

    [Fact]
    public void PG004_flags_schema_mutation_in_body()
    {
        var d = Analyze("CREATE FUNCTION app.f() RETURNS void LANGUAGE plpgsql AS $$ BEGIN DROP TABLE app.t; END; $$;");
        Assert.Contains(d, x => x.RuleId == "PG004");
    }

    [Fact]
    public void PG005_flags_missing_volatility()
    {
        Assert.Contains(Analyze("CREATE FUNCTION app.f() RETURNS int LANGUAGE sql AS $$ SELECT 1 $$;"),
            x => x.RuleId == "PG005");
        Assert.DoesNotContain(Analyze("CREATE FUNCTION app.f() RETURNS int LANGUAGE sql IMMUTABLE AS $$ SELECT 1 $$;"),
            x => x.RuleId == "PG005");
    }
}
