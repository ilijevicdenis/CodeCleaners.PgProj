using System;
using PgProj.Core.Model;
using PgProj.Core.Syntax;
using PgProj.Core.Testing;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Issue #150 — the `pgproj test scaffold` stub generator. DB-free: builds a model from SQL text and
/// asserts the generated stub's file name (build-skipped, test-discovered) and that the act/assert body
/// targets the right object kind (function vs procedure vs trigger).
/// </summary>
public sealed class TestScaffolderTests
{
    private static DatabaseModel Model(params string[] files)
    {
        var model = new DatabaseModel();
        var mb = new ModelBuilder();
        foreach (var sql in files)
            mb.Build(new PgParser().Parse(sql), model);
        return model;
    }

    [Fact]
    public void Function_stub_names_file_underscore_test_sql_and_calls_the_function()
    {
        var model = Model("CREATE FUNCTION app.add(a int, b int) RETURNS int LANGUAGE sql AS $$ SELECT a + b $$;");

        var r = TestScaffolder.Scaffold(model, "app.add");

        Assert.Equal("function", r.Kind);
        // Leading underscore → build globber skips it; .test.sql suffix → the runner discovers it.
        Assert.Equal("_app.add.test.sql", r.FileName);
        Assert.StartsWith("_", r.FileName);
        Assert.EndsWith(".test.sql", r.FileName);
        // Act+assert section invokes the function under test via the scalar assertion helper.
        Assert.Contains("pgproj_assert_scalar", r.Content);
        Assert.Contains("SELECT app.add(", r.Content);
        // Arg types become NULL::type placeholders.
        Assert.Contains("NULL::int", r.Content);
        // No CALL — that's the procedure shape.
        Assert.DoesNotContain("CALL app.add", r.Content);
    }

    [Fact]
    public void Function_with_no_args_emits_empty_parens()
    {
        var model = Model("CREATE FUNCTION app.now_utc() RETURNS timestamptz LANGUAGE sql AS $$ SELECT now() $$;");

        var r = TestScaffolder.Scaffold(model, "app.now_utc");

        Assert.Contains("SELECT app.now_utc() $$", r.Content);
    }

    [Fact]
    public void Procedure_stub_uses_CALL_not_a_scalar_select()
    {
        var model = Model("CREATE PROCEDURE app.do_work(n int) LANGUAGE plpgsql AS $$ BEGIN END $$;");

        var r = TestScaffolder.Scaffold(model, "app.do_work");

        Assert.Equal("procedure", r.Kind);
        Assert.Contains("CALL app.do_work(NULL::int);", r.Content);
        Assert.DoesNotContain("SELECT app.do_work(", r.Content);
    }

    [Fact]
    public void Trigger_stub_references_its_table_and_is_inconclusive_until_filled()
    {
        var model = Model(
            "CREATE TABLE app.orders (id int PRIMARY KEY);",
            "CREATE FUNCTION app.audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$;",
            "CREATE TRIGGER trg_audit AFTER INSERT ON app.orders FOR EACH ROW EXECUTE FUNCTION app.audit();");

        var r = TestScaffolder.Scaffold(model, "app.trg_audit");

        Assert.Equal("trigger", r.Kind);
        Assert.Equal("_app.trg_audit.test.sql", r.FileName);
        Assert.Contains("app.orders", r.Content);             // fires via DML on the owning table
        Assert.Contains("pgproj_inconclusive", r.Content);    // a stub the author must complete
    }

    [Fact]
    public void Unknown_object_throws_scaffold_exception()
    {
        var model = Model("CREATE TABLE app.t (id int);");

        var ex = Assert.Throws<ScaffoldException>(() => TestScaffolder.Scaffold(model, "app.missing"));
        Assert.Contains("app.missing", ex.Message);
    }

    [Fact]
    public void Unqualified_name_throws()
    {
        var model = Model("CREATE FUNCTION app.f() RETURNS void LANGUAGE sql AS $$ SELECT 1 $$;");

        Assert.Throws<ScaffoldException>(() => TestScaffolder.Scaffold(model, "f"));
    }
}
