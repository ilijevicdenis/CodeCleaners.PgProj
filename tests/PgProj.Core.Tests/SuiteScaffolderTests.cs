using System.Linq;
using PgProj.Core.Model;
using PgProj.Core.Syntax;
using PgProj.Core.Testing;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Issue #154 — the whole-project <see cref="SuiteScaffolder"/>. DB-free: builds a model from SQL text and
/// asserts the generated suite's file names, the load-bearing <c>@expect-sqlstate</c>/sentinel headers, the
/// synthesised INSERTs, deterministic output, and the downgrade-to-inconclusive path. Live execution of the
/// generated SQL is covered by the [DbFact] round-trip.
/// </summary>
public sealed class SuiteScaffolderTests
{
    private static DatabaseModel Model(params string[] files)
    {
        var model = new DatabaseModel();
        var mb = new ModelBuilder();
        foreach (var sql in files)
            mb.Build(new PgParser().Parse(sql), model);
        return model;
    }

    private const string Schema = """
        CREATE SCHEMA app;
        CREATE TABLE app.customers (
            id    int GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            name  text NOT NULL,
            email text NOT NULL UNIQUE);
        CREATE TABLE app.orders (
            id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            customer_id int NOT NULL REFERENCES app.customers (id),
            total       numeric NOT NULL);
        CREATE VIEW app.v_orders AS SELECT * FROM app.orders;
        CREATE SEQUENCE app.counter;
        """;

    private static System.Collections.Generic.IReadOnlyList<ScaffoldResult> Suite() =>
        SuiteScaffolder.GenerateSuite(Model(Schema), SuiteOptions.All);

    private static ScaffoldResult One(string fileNameFragment) =>
        Suite().Single(r => r.FileName.Contains(fileNameFragment));

    [Fact]
    public void Every_file_is_build_skipped_test_discovered_and_carries_the_sentinel()
    {
        foreach (var r in Suite())
        {
            Assert.StartsWith("_gen.", r.FileName);
            Assert.EndsWith(".test.sql", r.FileName);
            Assert.Contains(SuiteScaffolder.Sentinel, r.Content);
        }
    }

    [Fact]
    public void Not_null_test_expects_23502_with_the_directive_first()
    {
        var r = One("orders.notnull.total");
        Assert.StartsWith("-- @expect-sqlstate: 23502", r.Content);   // must be the first line for ParseExpectedSqlState
        Assert.Equal("23502", PgUnitRunner.ParseExpectedSqlState(r.Content));
        Assert.Contains("total) VALUES", r.Content);
        Assert.Contains("NULL", r.Content);
    }

    [Fact]
    public void Primary_key_test_expects_23505_and_inserts_the_key_twice()
    {
        var r = One("customers.pk");
        Assert.Equal("23505", PgUnitRunner.ParseExpectedSqlState(r.Content));
        // Identity-always PK forced with an explicit value needs OVERRIDING SYSTEM VALUE.
        Assert.Contains("OVERRIDING SYSTEM VALUE", r.Content);
        Assert.Equal(2, r.Content.Split("INSERT INTO app.customers").Length - 1);
    }

    [Fact]
    public void Unique_test_expects_23505_on_the_unique_column()
    {
        var r = One("customers.unique.email");
        Assert.Equal("23505", PgUnitRunner.ParseExpectedSqlState(r.Content));
        Assert.Contains("email", r.Content);
        Assert.Equal(2, r.Content.Split("INSERT INTO app.customers").Length - 1);
    }

    [Fact]
    public void Foreign_key_test_expects_23503_with_an_orphan_value()
    {
        var r = One("orders.fk");
        Assert.Equal("23503", PgUnitRunner.ParseExpectedSqlState(r.Content));
        Assert.Contains("987654321", r.Content);     // deterministic orphan id
        Assert.DoesNotContain("INSERT INTO app.customers", r.Content); // orphan column NOT satisfied with a parent
    }

    [Fact]
    public void Crud_test_seeds_the_mandatory_parent_then_asserts_a_row_landed()
    {
        var r = One("orders.crud");
        Assert.Null(PgUnitRunner.ParseExpectedSqlState(r.Content));   // positive test, no directive
        Assert.Contains("INSERT INTO app.customers", r.Content);       // depth-1 parent seeded first
        Assert.Contains("INSERT INTO app.orders", r.Content);
        Assert.Contains("pgproj_assert_not_empty", r.Content);
    }

    [Fact]
    public void View_test_asserts_queryability()
    {
        var r = One("v_orders.view");
        Assert.Contains("SELECT * FROM app.v_orders LIMIT 0", r.Content);
        Assert.Contains("pgproj_assert_rowcount", r.Content);
    }

    [Fact]
    public void Sequence_gets_a_catalog_existence_smoke_test()
    {
        var r = One("counter.exists");
        Assert.Contains("pg_sequences", r.Content);
        Assert.Contains("'counter'", r.Content);
    }

    [Fact]
    public void Generated_sql_parses_clean()
    {
        foreach (var r in Suite())
        {
            var parsed = new PgParser().Parse(r.Content);
            Assert.True(parsed.FullyRecognized, $"unparsed generated SQL in {r.FileName}:\n{r.Content}");
        }
    }

    [Fact]
    public void Output_is_deterministic()
    {
        var a = SuiteScaffolder.GenerateSuite(Model(Schema), SuiteOptions.All);
        var b = SuiteScaffolder.GenerateSuite(Model(Schema), SuiteOptions.All);
        Assert.Equal(a.Select(r => r.FileName), b.Select(r => r.FileName));
        Assert.Equal(a.Select(r => r.Content), b.Select(r => r.Content));
        // Sorted by file name (Ordinal).
        Assert.Equal(a.Select(r => r.FileName).OrderBy(x => x, System.StringComparer.Ordinal), a.Select(r => r.FileName));
    }

    [Fact]
    public void Unsynthesisable_column_type_downgrades_to_an_inconclusive_stub()
    {
        // A mandatory column of a user-defined/enum type has no literal mapping → the CRUD baseline can't be built.
        var model = Model("CREATE SCHEMA app; CREATE TYPE app.mood AS ENUM ('ok'); " +
                          "CREATE TABLE app.t (id int PRIMARY KEY, m app.mood NOT NULL);");
        var crud = SuiteScaffolder.GenerateSuite(model, SuiteOptions.All).Single(r => r.FileName.Contains("t.crud"));
        Assert.Contains("pgproj_inconclusive", crud.Content);
    }

    [Fact]
    public void Comment_objects_are_skipped_and_never_collide_on_one_file_name()
    {
        // Comments carry no schema/name from the extractor — every one would otherwise map to
        // _gen._._.exists.test.sql and silently overwrite the others. They are skipped outright.
        var model = Model("CREATE SCHEMA app; CREATE TABLE app.t (id int PRIMARY KEY);");
        model.Objects.Add(new RawObjectDefinition(ObjectKind.Comment, "", "", "comment:1", "COMMENT ON TABLE app.t IS 'a';"));
        model.Objects.Add(new RawObjectDefinition(ObjectKind.Comment, "", "", "comment:2", "COMMENT ON COLUMN app.t.id IS 'b';"));

        var suite = SuiteScaffolder.GenerateSuite(model, SuiteOptions.All);

        Assert.DoesNotContain(suite, r => r.FileName.Contains("_._"));        // no empty-schema/name collision
        Assert.DoesNotContain(suite, r => r.Content.Contains("for Comment")); // no comment existence stub
        Assert.Equal(suite.Select(r => r.FileName).Distinct().Count(), suite.Count); // every file name unique
    }

    [Fact]
    public void Unit_stub_is_inconclusive_by_default_not_a_failing_assertion()
    {
        // The generator can't know an object's expected behaviour, so a generated unit stub must raise
        // pgproj_inconclusive FIRST — never run a placeholder assert (which would FAIL) or call a
        // trigger-returning function directly (which errors). The template stays below as guidance.
        var model = Model("CREATE FUNCTION app.add(a int, b int) RETURNS int LANGUAGE sql AS $$ SELECT a + b $$;");
        var unit = SuiteScaffolder.GenerateSuite(model, SuiteOptions.Parse("unit")).Single(r => r.FileName.Contains("add.unit"));
        Assert.Contains("pgproj_inconclusive", unit.Content);
        // The inconclusive raise precedes the (dead-code) placeholder assert, so the test never fails.
        Assert.True(unit.Content.IndexOf("pgproj_inconclusive", System.StringComparison.Ordinal)
                  < unit.Content.IndexOf("<expected-value>", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Category_filter_limits_what_is_emitted()
    {
        var onlyViews = SuiteScaffolder.GenerateSuite(Model(Schema), SuiteOptions.Parse("view"));
        Assert.All(onlyViews, r => Assert.Contains(".view.test.sql", r.FileName));
        Assert.Contains(onlyViews, r => r.FileName.Contains("v_orders"));
    }
}
