using System.Linq;
using PgProj.Core.Analysis;
using PgProj.Core.Syntax;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>Tests for the PgParser-based static analysis gate (PgAnalyzer).</summary>
public class PgAnalyzerTests
{
    private static System.Collections.Generic.IReadOnlyList<Diagnostic> A(string sql)
        => new PgAnalyzer().Analyze(new PgParser().Parse(sql));

    [Fact]
    public void PG001_security_definer_without_search_path()
    {
        var d = A("CREATE FUNCTION s.f() RETURNS int LANGUAGE sql STABLE SECURITY DEFINER AS $$ SELECT 1 $$;");
        Assert.Contains(d, x => x.RuleId == "PG001" && x.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void PG001_clear_when_search_path_set()
    {
        var d = A("CREATE FUNCTION s.f() RETURNS int LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$ SELECT 1 $$;");
        Assert.DoesNotContain(d, x => x.RuleId == "PG001");
    }

    [Fact]
    public void PG005_missing_volatility_and_PG002_dynamic_sql()
    {
        var d = A("CREATE FUNCTION s.f() RETURNS void LANGUAGE plpgsql AS $$ BEGIN EXECUTE 'drop table x'; END $$;");
        Assert.Contains(d, x => x.RuleId == "PG005");
        Assert.Contains(d, x => x.RuleId == "PG002");
    }

    [Fact]
    public void PG003_unguarded_update_and_delete()
    {
        Assert.Contains(A("UPDATE s.t SET a = 1"), x => x.RuleId == "PG003");
        Assert.Contains(A("DELETE FROM s.t"), x => x.RuleId == "PG003");
        Assert.DoesNotContain(A("UPDATE s.t SET a = 1 WHERE id = 2"), x => x.RuleId == "PG003");
    }

    [Fact]
    public void PG007_select_star_in_view_and_PG009_limit_without_order_by()
    {
        Assert.Contains(A("CREATE VIEW s.v AS SELECT * FROM s.t"), x => x.RuleId == "PG007");
        Assert.Contains(A("SELECT a FROM s.t LIMIT 5"), x => x.RuleId == "PG009");
        Assert.DoesNotContain(A("SELECT a FROM s.t ORDER BY a LIMIT 5"), x => x.RuleId == "PG009");
    }

    [Fact]
    public void PG006_table_without_primary_key()
    {
        Assert.Contains(A("CREATE TABLE s.t (a int, b text);"), x => x.RuleId == "PG006" && x.Severity == DiagnosticSeverity.Info);
        // A PK — inline or table-level — clears it.
        Assert.DoesNotContain(A("CREATE TABLE s.t (a int PRIMARY KEY, b text);"), x => x.RuleId == "PG006");
        Assert.DoesNotContain(A("CREATE TABLE s.t (a int, b text, PRIMARY KEY (a, b));"), x => x.RuleId == "PG006");
        // Partition children / typed tables get their key elsewhere — not flagged.
        Assert.DoesNotContain(A("CREATE TABLE s.t PARTITION OF s.p DEFAULT;"), x => x.RuleId == "PG006");
    }

    [Fact]
    public void PG008_numeric_without_precision()
    {
        Assert.Contains(A("CREATE TABLE s.t (a int PRIMARY KEY, amount numeric);"), x => x.RuleId == "PG008");
        Assert.Contains(A("CREATE TABLE s.t (a int PRIMARY KEY, amount decimal);"), x => x.RuleId == "PG008");
        Assert.DoesNotContain(A("CREATE TABLE s.t (a int PRIMARY KEY, amount numeric(10,2));"), x => x.RuleId == "PG008");
        Assert.DoesNotContain(A("CREATE TABLE s.t (a int PRIMARY KEY, amount bigint);"), x => x.RuleId == "PG008");
    }

    [Fact]
    public void Clean_statements_produce_no_findings()
    {
        Assert.Empty(A("CREATE FUNCTION s.f(x int) RETURNS int LANGUAGE sql IMMUTABLE AS $$ SELECT x $$;"));
        Assert.Empty(A("CREATE TABLE s.t (id int PRIMARY KEY, amount numeric(12,2));"));
    }
}
