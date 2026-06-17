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
    public void PG004_fires_through_modifiers_between_verb_and_object_keyword()
    {
        // #65: TEMP/OR REPLACE/UNIQUE (and friends) between the verb and the object keyword
        // defeated the old regex - a function body mutating schema was silently unflagged.
        string Fn(string body) =>
            $"CREATE FUNCTION s.f() RETURNS void LANGUAGE plpgsql VOLATILE AS $$ BEGIN {body} END $$;";

        Assert.Contains(A(Fn("CREATE TEMP TABLE scratch (x int);")), x => x.RuleId == "PG004");
        Assert.Contains(A(Fn("CREATE TEMPORARY TABLE scratch (x int);")), x => x.RuleId == "PG004");
        Assert.Contains(A(Fn("CREATE UNLOGGED TABLE scratch (x int);")), x => x.RuleId == "PG004");
        Assert.Contains(A(Fn("CREATE OR REPLACE VIEW s.v AS SELECT 1;")), x => x.RuleId == "PG004");
        Assert.Contains(A(Fn("CREATE UNIQUE INDEX ix ON s.t (a);")), x => x.RuleId == "PG004");
        Assert.Contains(A(Fn("CREATE MATERIALIZED VIEW s.mv AS SELECT 1;")), x => x.RuleId == "PG004");
        Assert.Contains(A(Fn("DROP INDEX CONCURRENTLY s.ix;")), x => x.RuleId == "PG004");
        // the plain form keeps firing, and a body with no DDL stays clean
        Assert.Contains(A(Fn("CREATE TABLE scratch (x int);")), x => x.RuleId == "PG004");
        Assert.DoesNotContain(A(Fn("INSERT INTO s.t VALUES (1);")), x => x.RuleId == "PG004");
    }

    [Fact]
    public void PG009_fires_inside_view_bodies()
    {
        // #65: views are where LIMIT realistically lives in a declarative schema - the old check
        // only ran on bare top-level SELECTs, so PG009 effectively never fired.
        Assert.Contains(A("CREATE VIEW s.v AS SELECT id FROM s.t LIMIT 10;"), x => x.RuleId == "PG009");
        Assert.DoesNotContain(A("CREATE VIEW s.v AS SELECT id FROM s.t ORDER BY id LIMIT 10;"), x => x.RuleId == "PG009");
        Assert.DoesNotContain(A("CREATE VIEW s.v AS SELECT id FROM s.t;"), x => x.RuleId == "PG009");
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
    public void PG010_blank_padded_char()
    {
        Assert.Contains(A("CREATE TABLE s.t (a int PRIMARY KEY, code char(3));"), x => x.RuleId == "PG010" && x.Severity == DiagnosticSeverity.Info);
        Assert.Contains(A("CREATE TABLE s.t (a int PRIMARY KEY, code character(3));"), x => x.RuleId == "PG010");
        Assert.Contains(A("CREATE TABLE s.t (a int PRIMARY KEY, code char);"), x => x.RuleId == "PG010");
        // varchar / character varying / text are fine.
        Assert.DoesNotContain(A("CREATE TABLE s.t (a int PRIMARY KEY, code varchar(3));"), x => x.RuleId == "PG010");
        Assert.DoesNotContain(A("CREATE TABLE s.t (a int PRIMARY KEY, code character varying(3));"), x => x.RuleId == "PG010");
        Assert.DoesNotContain(A("CREATE TABLE s.t (a int PRIMARY KEY, code text);"), x => x.RuleId == "PG010");
    }

    [Fact]
    public void PG011_timestamp_without_time_zone()
    {
        Assert.Contains(A("CREATE TABLE s.t (a int PRIMARY KEY, at timestamp);"), x => x.RuleId == "PG011" && x.Severity == DiagnosticSeverity.Info);
        Assert.Contains(A("CREATE TABLE s.t (a int PRIMARY KEY, at timestamp(3));"), x => x.RuleId == "PG011");
        Assert.Contains(A("CREATE TABLE s.t (a int PRIMARY KEY, at timestamp without time zone);"), x => x.RuleId == "PG011");
        // The tz-aware forms are fine.
        Assert.DoesNotContain(A("CREATE TABLE s.t (a int PRIMARY KEY, at timestamptz);"), x => x.RuleId == "PG011");
        Assert.DoesNotContain(A("CREATE TABLE s.t (a int PRIMARY KEY, at timestamp with time zone);"), x => x.RuleId == "PG011");
    }

    [Fact]
    public void PG012_serial_column()
    {
        Assert.Contains(A("CREATE TABLE s.t (id serial PRIMARY KEY);"), x => x.RuleId == "PG012" && x.Severity == DiagnosticSeverity.Info);
        Assert.Contains(A("CREATE TABLE s.t (id bigserial PRIMARY KEY);"), x => x.RuleId == "PG012");
        Assert.Contains(A("CREATE TABLE s.t (id smallserial PRIMARY KEY);"), x => x.RuleId == "PG012");
        // Identity columns are the recommended form — never flagged.
        Assert.DoesNotContain(A("CREATE TABLE s.t (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY);"), x => x.RuleId == "PG012");
    }

    [Fact]
    public void PG013_money_column()
    {
        Assert.Contains(A("CREATE TABLE s.t (a int PRIMARY KEY, price money);"), x => x.RuleId == "PG013" && x.Severity == DiagnosticSeverity.Info);
        Assert.DoesNotContain(A("CREATE TABLE s.t (a int PRIMARY KEY, price numeric(12,2));"), x => x.RuleId == "PG013");
    }

    [Fact]
    public void PG015_uppercase_identifier_in_table_or_column()
    {
        // A mixed-case table name and a mixed-case column name each fire.
        Assert.Contains(A("CREATE TABLE s.Customer (id int PRIMARY KEY);"),
            x => x.RuleId == "PG015" && x.Severity == DiagnosticSeverity.Info);
        Assert.Contains(A("CREATE TABLE s.orders (id int PRIMARY KEY, customerId int);"),
            x => x.RuleId == "PG015");
        // All-lowercase snake_case is clean.
        Assert.DoesNotContain(A("CREATE TABLE s.customer (id int PRIMARY KEY, customer_id int);"),
            x => x.RuleId == "PG015");
    }

    [Fact]
    public void PG016_identifier_longer_than_63_bytes()
    {
        var longName = new string('a', 64);                 // 64 bytes > the 63-byte limit
        Assert.Contains(A($"CREATE TABLE s.{longName} (id int PRIMARY KEY);"),
            x => x.RuleId == "PG016" && x.Severity == DiagnosticSeverity.Warning);
        Assert.Contains(A($"CREATE TABLE s.t (id int PRIMARY KEY, {longName} int);"),
            x => x.RuleId == "PG016");
        // A name at the 63-byte boundary is fine.
        var okName = new string('a', 63);
        Assert.DoesNotContain(A($"CREATE TABLE s.{okName} (id int PRIMARY KEY);"),
            x => x.RuleId == "PG016");
    }

    [Fact]
    public void PG017_json_column_prefers_jsonb()
    {
        Assert.Contains(A("CREATE TABLE s.t (id int PRIMARY KEY, doc json);"), x => x.RuleId == "PG017" && x.Severity == DiagnosticSeverity.Info);
        Assert.Contains(A("CREATE TABLE s.t (id int PRIMARY KEY, docs json[]);"), x => x.RuleId == "PG017");
        // jsonb is the recommended form — never flagged.
        Assert.DoesNotContain(A("CREATE TABLE s.t (id int PRIMARY KEY, doc jsonb);"), x => x.RuleId == "PG017");
    }

    [Fact]
    public void PG020_exception_when_others()
    {
        var swallow = "CREATE FUNCTION s.f() RETURNS void LANGUAGE plpgsql VOLATILE AS $$ BEGIN PERFORM 1; EXCEPTION WHEN OTHERS THEN NULL; END $$;";
        Assert.Contains(A(swallow), x => x.RuleId == "PG020" && x.Severity == DiagnosticSeverity.Warning);
        // Catching a specific condition is fine.
        var specific = "CREATE FUNCTION s.f() RETURNS void LANGUAGE plpgsql VOLATILE AS $$ BEGIN PERFORM 1; EXCEPTION WHEN unique_violation THEN NULL; END $$;";
        Assert.DoesNotContain(A(specific), x => x.RuleId == "PG020");
    }

    [Fact]
    public void PG021_select_into_without_strict()
    {
        string Fn(string body) => $"CREATE FUNCTION s.f() RETURNS int LANGUAGE plpgsql VOLATILE AS $$ DECLARE v int; BEGIN {body} RETURN v; END $$;";

        Assert.Contains(A(Fn("SELECT id INTO v FROM s.t WHERE id = 1;")), x => x.RuleId == "PG021" && x.Severity == DiagnosticSeverity.Warning);
        // INTO STRICT is the safe form.
        Assert.DoesNotContain(A(Fn("SELECT id INTO STRICT v FROM s.t WHERE id = 1;")), x => x.RuleId == "PG021");
        // INSERT ... INTO is unrelated DML, not a select-into target.
        Assert.DoesNotContain(A(Fn("INSERT INTO s.t (id) VALUES (1);")), x => x.RuleId == "PG021");
    }

    [Fact]
    public void Clean_statements_produce_no_findings()
    {
        Assert.Empty(A("CREATE FUNCTION s.f(x int) RETURNS int LANGUAGE sql IMMUTABLE AS $$ SELECT x $$;"));
        Assert.Empty(A("CREATE TABLE s.t (id int PRIMARY KEY, amount numeric(12,2));"));
    }
}
