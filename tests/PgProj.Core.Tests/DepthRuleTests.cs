using System.Collections.Generic;
using System.Linq;
using PgProj.Core.Analysis;
using PgProj.Core.Parsing;
using Xunit;

namespace PgProj.Core.Tests;

public class DepthRuleTests
{
    private static IReadOnlyList<Diagnostic> Analyze(string sql) =>
        SqlAnalyzer.Default().Analyze(new AstParser().Parse(sql));

    private static bool Has(string sql, string rule) => Analyze(sql).Any(d => d.RuleId == rule);

    [Fact]
    public void PG006_join_without_condition()
    {
        Assert.True(Has("CREATE VIEW app.v AS SELECT 1 FROM app.a JOIN app.b;", "PG006"));
        Assert.False(Has("CREATE VIEW app.v AS SELECT 1 FROM app.a JOIN app.b ON a.id = b.id;", "PG006"));
        Assert.False(Has("CREATE VIEW app.v AS SELECT 1 FROM app.a CROSS JOIN app.b;", "PG006"));
    }

    [Fact]
    public void PG007_select_star_in_view()
    {
        Assert.True(Has("CREATE VIEW app.v AS SELECT * FROM app.t;", "PG007"));
        Assert.False(Has("CREATE VIEW app.v AS SELECT id, name FROM app.t;", "PG007"));
    }

    [Fact]
    public void PG008_not_in_subquery()
    {
        Assert.True(Has("CREATE VIEW app.v AS SELECT id FROM app.t WHERE id NOT IN (SELECT id FROM app.u);", "PG008"));
        Assert.False(Has("CREATE VIEW app.v AS SELECT id FROM app.t WHERE id IN (SELECT id FROM app.u);", "PG008"));
    }

    [Fact]
    public void PG009_limit_without_order_by()
    {
        Assert.True(Has("CREATE VIEW app.v AS SELECT id FROM app.t LIMIT 10;", "PG009"));
        Assert.False(Has("CREATE VIEW app.v AS SELECT id FROM app.t ORDER BY id LIMIT 10;", "PG009"));
    }

    [Fact]
    public void PG010_dml_in_loop()
    {
        Assert.True(Has("""
            CREATE FUNCTION app.f() RETURNS void LANGUAGE plpgsql AS $$
            BEGIN FOR r IN SELECT id FROM app.t LOOP UPDATE app.u SET x = 1 WHERE id = r.id; END LOOP; END; $$;
            """, "PG010"));
        Assert.False(Has("""
            CREATE FUNCTION app.f() RETURNS void LANGUAGE plpgsql AS $$
            BEGIN UPDATE app.u SET x = 1; END; $$;
            """, "PG010"));
    }

    [Fact]
    public void PG011_security_definer_writes()
    {
        Assert.True(Has("""
            CREATE FUNCTION app.f() RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $$
            BEGIN INSERT INTO app.audit VALUES (1); END; $$;
            """, "PG011"));
        Assert.False(Has("""
            CREATE FUNCTION app.f() RETURNS int LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog AS $$
            SELECT count(*) FROM app.t $$;
            """, "PG011"));
    }
}
