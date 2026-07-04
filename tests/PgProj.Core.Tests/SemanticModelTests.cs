using System.Linq;
using PgProj.Core.Semantics;
using PgProj.Core.Semantics.Binding;
using PgProj.Core.Syntax;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Phase 3 (Bind) + Phase 4 (Type) — the Typed Semantic Model (issue #47). All DB-free: built straight
/// from PgParser output + the Phase 2 symbol table. Proves the three acceptance criteria — view-body
/// column binding to a concrete (relation, column, type), the location/reference query API, and overload
/// resolution by inferred argument types.
/// </summary>
public sealed class SemanticModelTests
{
    // ---- ACCEPTANCE 1: every view-body column ref binds to (relation, column, resolved type) --------

    [Fact]
    public void View_body_column_refs_bind_to_concrete_relation_column_and_type()
    {
        var c = CatalogBuilder.Build(
            "CREATE TABLE app.t (id int, name varchar(50));\n" +
            "CREATE VIEW app.v AS SELECT t.id, t.name FROM app.t;", "app");

        var binder = new Binder(c);
        var viewStmt = ParseFirstView(
            "CREATE VIEW app.v AS SELECT t.id, t.name FROM app.t;");
        var bound = binder.BindView(viewStmt);

        // The view symbol resolved, and its body produced an inferred column list.
        Assert.NotNull(bound.View);
        Assert.Equal(2, bound.Columns.Count);

        // Every output column carries a concrete (relation, column, resolved type).
        var id = bound.Columns[0];
        Assert.Equal("id", id.Name);
        Assert.Equal("integer", id.Type.Name);              // normalized
        Assert.NotNull(id.Source);                          // a concrete column symbol
        Assert.Equal("app.t.id", id.Source!.Fqn);

        var name = bound.Columns[1];
        Assert.Equal("name", name.Name);
        Assert.Equal("character varying(50)", name.Type.Name);
        Assert.Equal("app.t.name", name.Source!.Fqn);

        // The bound SELECT-item expressions are bound column refs that point at their relation + column.
        var firstItem = Assert.IsType<BoundColumnRef>(bound.Body.SelectItems[0]);
        Assert.True(firstItem.IsResolved);
        Assert.Equal("app.t", firstItem.Relation!.Fqn);
        Assert.Equal("app.t.id", firstItem.Column!.Fqn);
        Assert.Equal("integer", firstItem.Type.Name);
    }

    [Fact]
    public void Bare_unqualified_view_column_refs_bind_via_single_source_scope()
    {
        // No table qualifier on the columns; the binder resolves them against the single in-scope relation.
        var c = CatalogBuilder.Build(
            "CREATE TABLE app.t (id int, name text);\n" +
            "CREATE VIEW app.v AS SELECT id, name FROM app.t;", "app");

        var bound = new Binder(c).BindView(ParseFirstView("CREATE VIEW app.v AS SELECT id, name FROM app.t;"));

        Assert.Equal(2, bound.Columns.Count);
        Assert.All(bound.Columns, col => Assert.NotNull(col.Source));
        Assert.Equal("integer", bound.Columns[0].Type.Name);
        Assert.Equal("text", bound.Columns[1].Type.Name);
        Assert.Equal("app.t.id", bound.Columns[0].Source!.Fqn);
    }

    [Fact]
    public void Star_select_expands_to_every_source_column_with_types()
    {
        var c = CatalogBuilder.Build(
            "CREATE TABLE app.t (id int, name text);\n" +
            "CREATE VIEW app.v AS SELECT * FROM app.t;", "app");

        var bound = new Binder(c).BindView(ParseFirstView("CREATE VIEW app.v AS SELECT * FROM app.t;"));

        Assert.Equal(new[] { "id", "name" }, bound.Columns.Select(x => x.Name));
        Assert.Equal(new[] { "integer", "text" }, bound.Columns.Select(x => x.Type.Name));
    }

    [Fact]
    public void Cte_columns_are_inferred_and_visible_to_the_outer_query()
    {
        var c = CatalogBuilder.Build(
            "CREATE TABLE app.t (id int, name text);\n" +
            "CREATE VIEW app.v AS WITH s AS (SELECT t.id FROM app.t) SELECT s.id FROM s;", "app");

        var bound = new Binder(c).BindView(ParseFirstView(
            "CREATE VIEW app.v AS WITH s AS (SELECT t.id FROM app.t) SELECT s.id FROM s;"));

        Assert.Single(bound.Columns);
        Assert.Equal("id", bound.Columns[0].Name);
        Assert.Equal("integer", bound.Columns[0].Type.Name);
    }

    // ---- ACCEPTANCE 2: query API — by location and by reference -------------------------------------

    [Fact]
    public void Query_api_returns_symbol_at_a_file_offset_and_all_references_to_a_symbol()
    {
        const string sql =
            "CREATE TABLE app.t (id int, name text);\n" +
            "CREATE VIEW app.v1 AS SELECT t.id FROM app.t;\n" +
            "CREATE VIEW app.v2 AS SELECT t.name FROM app.t;";

        var c = CatalogBuilder.Build(sql, "app");
        // Populate the reverse index (who-references-X) exactly as the build pipeline does.
        ReferenceCollector.Collect(c, new PgParser().Parse(sql), "schema.sql");

        var parsed = new PgParser().Parse(sql);
        var model = SemanticModel.Build(c, parsed, "schema.sql");

        // --- BY SYMBOL ---
        var table = model.GetSymbol("app", "t");
        Assert.NotNull(table);

        // --- BY SOURCE LOCATION ---  occurrences anchor at each statement's parser offset (token index).
        // At the CREATE TABLE statement's own offset, the symbol "at" that location is the table itself.
        int tablePos = parsed.Statements.OfType<CreateTableStatement>().First().Position;
        var atDef = model.SymbolAt("schema.sql", tablePos);
        Assert.Equal(table!.Key, atDef!.Key);

        // At the v1 view statement offset, the symbol at that location is v1 (its definition).
        int v1Pos = parsed.Statements.OfType<CreateViewStatement>().First(v => v.Name == "v1").Position;
        var atV1 = model.SymbolAt("schema.sql", v1Pos);
        Assert.NotNull(atV1);
        Assert.Equal("app.v1", atV1!.Fqn);

        // --- BY REFERENCE ---  both views reference the table.
        var refs = model.ReferencesTo(table);
        Assert.Equal(2, refs.Count);
        Assert.Contains(refs, r => r.ReferencerKey == "app.v1");
        Assert.Contains(refs, r => r.ReferencerKey == "app.v2");

        // Occurrences: the table has a definition occurrence plus a reference occurrence per view body.
        var occ = model.OccurrencesOf(table);
        Assert.Contains(occ, o => o.Kind == OccurrenceKind.Definition);
        Assert.True(occ.Count(o => o.Kind == OccurrenceKind.Reference) >= 2);
    }

    // ---- #162: references nested inside an irregular JSON/XML call reach the dependency graph -------

    [Fact]
    public void References_inside_a_json_call_reach_the_dependency_graph_162()
    {
        // A subquery inside json_object used to be discarded (SkipBalancedParens), so the table it reads
        // was invisible to the reverse index. It now flows through, so app.detail is a reference of app.v.
        const string sql =
            "CREATE TABLE app.detail (id int);\n" +
            "CREATE VIEW app.v AS SELECT json_object('items' VALUE (SELECT count(*) FROM app.detail));";

        var c = CatalogBuilder.Build(sql, "app");
        ReferenceCollector.Collect(c, new PgParser().Parse(sql), "schema.sql");
        var model = SemanticModel.Build(c, new PgParser().Parse(sql), "schema.sql");

        var detail = model.GetSymbol("app", "detail");
        Assert.NotNull(detail);
        Assert.Contains(model.ReferencesTo(detail!), r => r.ReferencerKey == "app.v");
    }

    [Fact]
    public void KeywordCall_captures_value_subexpressions_but_not_bare_element_names_162()
    {
        // The harvester takes the unambiguous value-subexpression (the subquery) but NOT the element NAME
        // token — turning "foo" into a column ref would bind to nothing and emit a false "column does not
        // exist". So the captured args contain the subquery and zero bare column refs.
        var r = new PgParser().Parse("SELECT xmlelement(NAME foo, (SELECT id FROM app.t));");
        var call = (FuncCallExpr)((QueryStatement)r.Statements[0]).Query!.Items[0].Expr;

        Assert.Equal("xmlelement", call.Name[^1].ToLowerInvariant());
        Assert.NotEmpty(call.Args);                                  // the subquery was captured
        Assert.DoesNotContain(call.Args, a => a is ColumnRef);       // the element name "foo" was not
    }

    // ---- ACCEPTANCE 3: overloaded function call binds to the correct overload by arg types ----------

    [Fact]
    public void Overloaded_function_call_binds_to_the_correct_overload_by_argument_types()
    {
        var c = CatalogBuilder.Build(
            "CREATE TABLE app.t (n integer, s text);\n" +
            "CREATE FUNCTION app.f(a integer) RETURNS integer AS $$ SELECT a $$ LANGUAGE sql;\n" +
            "CREATE FUNCTION app.f(a text) RETURNS text AS $$ SELECT a $$ LANGUAGE sql;", "app");

        var binder = new Binder(c);

        // f(n) where n :: integer  →  the integer overload.
        var vInt = ParseFirstView("CREATE VIEW app.vi AS SELECT app.f(t.n) FROM app.t t;");
        var boundInt = binder.BindView(vInt);
        var callInt = Assert.IsType<BoundFuncCall>(boundInt.Body.SelectItems[0]);
        Assert.True(callInt.IsResolved);
        Assert.Equal("integer", callInt.Signature.ArgTypes);

        // f(s) where s :: text  →  the text overload (a DIFFERENT symbol).
        var vText = ParseFirstView("CREATE VIEW app.vt AS SELECT app.f(t.s) FROM app.t t;");
        var boundText = binder.BindView(vText);
        var callText = Assert.IsType<BoundFuncCall>(boundText.Body.SelectItems[0]);
        Assert.True(callText.IsResolved);
        Assert.Equal("text", callText.Signature.ArgTypes);

        Assert.NotEqual(callInt.Function!.Key, callText.Function!.Key);   // bound to two distinct overloads
    }

    [Fact]
    public void Cast_argument_pins_the_overload_signature()
    {
        var c = CatalogBuilder.Build(
            "CREATE FUNCTION app.g(a integer) RETURNS integer AS $$ SELECT a $$ LANGUAGE sql;\n" +
            "CREATE FUNCTION app.g(a text) RETURNS text AS $$ SELECT a $$ LANGUAGE sql;", "app");

        var bound = new Binder(c).BindView(ParseFirstView(
            "CREATE VIEW app.v AS SELECT app.g(CAST(1 AS integer));"));
        var call = Assert.IsType<BoundFuncCall>(bound.Body.SelectItems[0]);
        Assert.True(call.IsResolved);
        Assert.Equal("integer", call.Signature.ArgTypes);
    }

    // ---- typing of literals + expressions ----------------------------------------------------------

    [Fact]
    public void Literals_and_comparisons_carry_resolved_types()
    {
        var c = CatalogBuilder.Build("CREATE TABLE app.t (id int);", "app");
        var bound = new Binder(c).BindQuery(new PgParser()
            .Parse("SELECT 'x', 42, 1 = 1 FROM app.t;").Statements.OfType<QueryStatement>().First().Query);

        var items = bound.SelectItems;
        Assert.Equal("text", items[0].Type.Name);
        Assert.Equal("integer", items[1].Type.Name);     // PG smallest-fit: 42 is int4, not bigint
        Assert.Equal("boolean", items[2].Type.Name);     // comparison → boolean
    }

    private static CreateViewStatement ParseFirstView(string sql) =>
        new PgParser().Parse(sql).Statements.OfType<CreateViewStatement>().First();
}
