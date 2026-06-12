using System.Linq;
using PgProj.Core.Analysis;
using PgProj.Core.Semantics;
using PgProj.Core.Semantics.Binding;
using PgProj.Core.Syntax;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Phase 5 — type-aware semantic validation over the Typed Semantic Model (issue #48). All DB-free: a
/// <see cref="Catalog"/> built from PgParser output feeds the <see cref="SemanticValidator"/>, which consumes
/// the binder's bound model. Proves the acceptance criteria (view/trigger/constraint validity, type safety,
/// overload resolution) AND, critically, that valid SQL produces NO diagnostics (no false positives).
/// </summary>
public sealed class SemanticValidatorTests
{
    private const string File = "schema.sql";

    private static (Catalog Catalog, ParseResult Parsed) Setup(string sql, string defaultSchema = "app")
    {
        var catalog = CatalogBuilder.Build(sql, defaultSchema);
        var parsed = new PgParser().Parse(sql);
        return (catalog, parsed);
    }

    private static System.Collections.Generic.IReadOnlyList<Diagnostics.Diagnostic> Run(string sql, string defaultSchema = "app")
    {
        var (catalog, parsed) = Setup(sql, defaultSchema);
        var v = new SemanticValidator(catalog);
        v.IndexFile(File, sql, parsed);
        return v.Validate(File, sql, parsed);
    }

    // ---- VIEW validity -------------------------------------------------------

    [Fact]
    public void View_selecting_nonexistent_column_on_existing_table_errors_with_file_line_and_related()
    {
        const string sql =
            "CREATE TABLE app.t (id int, name text);\n" +
            "CREATE VIEW app.v AS SELECT t.nope FROM app.t t;";

        var diags = Run(sql);

        var err = Assert.Single(diags);
        Assert.Equal(DiagnosticSeverity.Error, err.Severity);
        Assert.Contains("nope", err.Message);
        Assert.Contains("app.t", err.Message);
        Assert.Equal(File, err.File);
        Assert.True(err.Line >= 2, $"expected the view's line, got {err.Line}");

        // RELATED LOCATION points at the table's definition (line 1).
        var related = Assert.Single(err.Related);
        Assert.Equal(File, related.File);
        Assert.Equal(1, related.Line);
    }

    [Fact]
    public void View_selecting_valid_columns_produces_no_diagnostics()
    {
        const string sql =
            "CREATE TABLE app.t (id int, name text);\n" +
            "CREATE VIEW app.v AS SELECT t.id, t.name FROM app.t t;";
        Assert.Empty(Run(sql));
    }

    [Fact]
    public void View_over_unmanaged_external_relation_is_not_flagged()
    {
        // No CREATE for ext.thing — its schema is unmanaged, so an unknown column must NOT be flagged.
        const string sql = "CREATE VIEW app.v AS SELECT x.whatever FROM ext.thing x;";
        Assert.Empty(Run(sql));
    }

    [Fact]
    public void View_with_bare_unqualified_bad_column_on_single_source_errors()
    {
        const string sql =
            "CREATE TABLE app.t (id int);\n" +
            "CREATE VIEW app.v AS SELECT missing FROM app.t;";
        var err = Assert.Single(Run(sql));
        Assert.Contains("missing", err.Message);
    }

    // ---- TRIGGER validity ----------------------------------------------------

    [Fact]
    public void Trigger_pointing_at_non_trigger_returning_function_errors()
    {
        const string sql =
            "CREATE TABLE app.t (id int);\n" +
            "CREATE FUNCTION app.f() RETURNS integer AS $$ SELECT 1 $$ LANGUAGE sql;\n" +
            "CREATE TRIGGER tg BEFORE INSERT ON app.t FOR EACH ROW EXECUTE FUNCTION app.f();";

        var err = Assert.Single(Run(sql));
        Assert.Contains("must return type trigger", err.Message);
        Assert.Contains("integer", err.Message);
        // related → the function definition
        var related = Assert.Single(err.Related);
        Assert.Equal(File, related.File);
    }

    [Fact]
    public void Trigger_pointing_at_trigger_returning_function_is_valid()
    {
        const string sql =
            "CREATE TABLE app.t (id int);\n" +
            "CREATE FUNCTION app.f() RETURNS trigger AS $$ BEGIN RETURN NEW; END $$ LANGUAGE plpgsql;\n" +
            "CREATE TRIGGER tg BEFORE INSERT ON app.t FOR EACH ROW EXECUTE FUNCTION app.f();";
        Assert.Empty(Run(sql));
    }

    [Fact]
    public void Trigger_on_missing_table_errors()
    {
        const string sql =
            "CREATE FUNCTION app.f() RETURNS trigger AS $$ BEGIN RETURN NEW; END $$ LANGUAGE plpgsql;\n" +
            "CREATE TRIGGER tg BEFORE INSERT ON app.gone FOR EACH ROW EXECUTE FUNCTION app.f();";
        var diags = Run(sql);
        Assert.Contains(diags, d => d.Message.Contains("app.gone") && d.Message.Contains("does not exist"));
    }

    [Fact]
    public void Trigger_with_external_or_unknown_function_is_not_flagged()
    {
        // The trigger function lives in an unmanaged schema (no CREATE FUNCTION) → cannot prove its return
        // type, so no error (conservative).
        const string sql =
            "CREATE TABLE app.t (id int);\n" +
            "CREATE TRIGGER tg BEFORE INSERT ON app.t FOR EACH ROW EXECUTE FUNCTION audit.track();";
        Assert.Empty(Run(sql));
    }

    // ---- CONSTRAINT validity (CHECK / DEFAULT) -------------------------------

    [Fact]
    public void Check_referencing_nonexistent_column_errors()
    {
        const string sql = "CREATE TABLE app.t (id int, CONSTRAINT c CHECK (missing > 0));";
        var err = Assert.Single(Run(sql));
        Assert.Contains("missing", err.Message);
        Assert.Contains("does not exist", err.Message);
    }

    [Fact]
    public void Inline_check_on_valid_column_produces_no_diagnostics()
    {
        const string sql = "CREATE TABLE app.t (id int CHECK (id > 0), qty int CHECK (qty >= 0));";
        Assert.Empty(Run(sql));
    }

    [Fact]
    public void Check_with_type_mismatched_comparison_errors()
    {
        // id is integer, compared against a string literal → operator does not exist.
        const string sql = "CREATE TABLE app.t (id int, CONSTRAINT c CHECK (id = 'abc'));";
        var err = Assert.Single(Run(sql));
        Assert.Contains("operator does not exist", err.Message);
    }

    [Fact]
    public void Default_expression_referencing_a_valid_column_is_fine()
    {
        const string sql = "CREATE TABLE app.t (a int, b int DEFAULT 0);";
        Assert.Empty(Run(sql));
    }

    [Fact]
    public void Sql_special_values_in_defaults_and_checks_are_not_column_references()
    {
        // Found by the sample-db round-trip (2026-06-12): DEFAULT CURRENT_USER parses as a bare
        // ColumnRef and was flagged "column CURRENT_USER does not exist". All parameterless
        // special values must pass, in any casing, in DEFAULT and CHECK alike.
        const string sql =
            "CREATE TABLE app.audit_log (\n" +
            "    id int,\n" +
            "    changed_by text NOT NULL DEFAULT CURRENT_USER,\n" +
            "    session_owner text DEFAULT session_user,\n" +
            "    db name DEFAULT current_catalog,\n" +
            "    created date DEFAULT current_date,\n" +
            "    stamped timestamptz DEFAULT Current_Timestamp,\n" +
            "    CONSTRAINT not_future CHECK (created <= current_date)\n" +
            ");";
        Assert.Empty(Run(sql));
    }

    [Fact]
    public void Genuinely_missing_column_in_default_is_still_flagged_next_to_special_values()
    {
        const string sql = "CREATE TABLE app.t (a int, b text DEFAULT CURRENT_USER, c int DEFAULT missing_col);";
        var err = Assert.Single(Run(sql));
        Assert.Contains("missing_col", err.Message);
        Assert.Contains("does not exist", err.Message);
    }

    // ---- TYPE SAFETY (comparisons) -------------------------------------------

    [Fact]
    public void View_comparison_of_integer_to_string_literal_errors()
    {
        const string sql =
            "CREATE TABLE app.t (id int, name text);\n" +
            "CREATE VIEW app.v AS SELECT t.id = 'x' FROM app.t t;";
        var err = Assert.Single(Run(sql));
        Assert.Contains("operator does not exist", err.Message);
    }

    [Fact]
    public void View_comparison_of_compatible_numeric_types_is_fine()
    {
        const string sql =
            "CREATE TABLE app.t (id int, score numeric);\n" +
            "CREATE VIEW app.v AS SELECT t.id = 5, t.score > 1.5 FROM app.t t;";
        Assert.Empty(Run(sql));
    }

    [Fact]
    public void View_comparison_of_two_text_columns_is_fine()
    {
        const string sql =
            "CREATE TABLE app.t (a text, b text);\n" +
            "CREATE VIEW app.v AS SELECT t.a = t.b FROM app.t t;";
        Assert.Empty(Run(sql));
    }

    // ---- FUNCTION OVERLOAD RESOLUTION ----------------------------------------

    [Fact]
    public void Function_call_with_no_matching_overload_errors()
    {
        // f has integer and text overloads; calling it on a boolean column matches neither.
        const string sql =
            "CREATE TABLE app.t (flag boolean);\n" +
            "CREATE FUNCTION app.f(a integer) RETURNS integer AS $$ SELECT a $$ LANGUAGE sql;\n" +
            "CREATE FUNCTION app.f(a text) RETURNS text AS $$ SELECT a $$ LANGUAGE sql;\n" +
            "CREATE VIEW app.v AS SELECT app.f(t.flag) FROM app.t t;";
        var err = Assert.Single(Run(sql));
        Assert.Contains("no function matches", err.Message);
        Assert.Contains("app.f", err.Message);
    }

    [Fact]
    public void Function_call_resolving_to_an_overload_produces_no_diagnostics()
    {
        const string sql =
            "CREATE TABLE app.t (n integer, s text);\n" +
            "CREATE FUNCTION app.f(a integer) RETURNS integer AS $$ SELECT a $$ LANGUAGE sql;\n" +
            "CREATE FUNCTION app.f(a text) RETURNS text AS $$ SELECT a $$ LANGUAGE sql;\n" +
            "CREATE VIEW app.vi AS SELECT app.f(t.n) FROM app.t t;\n" +
            "CREATE VIEW app.vt AS SELECT app.f(t.s) FROM app.t t;";
        Assert.Empty(Run(sql));
    }

    [Fact]
    public void Builtin_and_external_function_calls_are_not_flagged()
    {
        // upper() is a built-in (no managed overload), audit.track() is in an unmanaged schema → neither flagged.
        const string sql =
            "CREATE TABLE app.t (name text);\n" +
            "CREATE VIEW app.v AS SELECT upper(t.name) FROM app.t t;";
        Assert.Empty(Run(sql));
    }

    // ---- false-positive guards over a realistic mixed script -----------------

    [Fact]
    public void A_valid_multi_object_script_produces_no_diagnostics()
    {
        const string sql =
            "CREATE TABLE app.customer (id int, name text, balance numeric, active boolean);\n" +
            "CREATE TABLE app.order (id int, customer_id int, total numeric CHECK (total >= 0));\n" +
            "CREATE FUNCTION app.touch() RETURNS trigger AS $$ BEGIN RETURN NEW; END $$ LANGUAGE plpgsql;\n" +
            "CREATE TRIGGER tg BEFORE UPDATE ON app.customer FOR EACH ROW EXECUTE FUNCTION app.touch();\n" +
            "CREATE VIEW app.active_customers AS SELECT c.id, c.name FROM app.customer c WHERE c.active = true;\n" +
            "CREATE VIEW app.big_orders AS SELECT o.id, o.total FROM app.order o WHERE o.total > 100;";
        Assert.Empty(Run(sql));
    }
}
