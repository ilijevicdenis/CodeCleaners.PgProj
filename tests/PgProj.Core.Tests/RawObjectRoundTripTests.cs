using System.Linq;
using PgProj.Core.Comparison;
using PgProj.Core.Model;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// DB-free round-trip idempotency tests for the raw object kinds (#61) and finely-modelled objects (#64)
/// whose live-catalog reconstruction diverges textually from hand-written source. Each test builds the
/// project model from source SQL and a stand-in "live" model holding the EXACT reconstruction
/// <see cref="PgProj.Core.Introspection.LiveDatabaseReader"/> produces, then asserts
/// <c>Compare(project, live)</c> yields no phantom create/recreate — the same property the DB-gated guard in
/// <see cref="LiveReaderIntegrationTests"/> verifies end-to-end.
/// </summary>
public class RawObjectRoundTripTests
{
    private static DatabaseModel Parse(string sql) => TestModel.Build(sql);

    private static DatabaseModel Live(ObjectKind kind, string schema, string name, string identity, string body, string? on = null)
    {
        var m = new DatabaseModel();
        if (!string.IsNullOrEmpty(schema)) m.Schemas.Add(new SchemaDefinition(schema));
        m.Objects.Add(new RawObjectDefinition(kind, schema, name, identity.ToLowerInvariant(), body, on));
        return m;
    }

    private static void AssertNoPhantom(DatabaseModel source, DatabaseModel live)
    {
        // Mirror referenced schemas so the only thing under test is the raw-object diff, not CreateSchema.
        foreach (var s in source.Schemas) if (!live.HasSchema(s.Name)) live.Schemas.Add(s);
        AssertNoRawChurn(new SchemaComparer().Compare(source, live));
        AssertNoRawChurn(new SchemaComparer().Compare(source, live, new ComparerOptions { DropObjectsNotInSource = true }));
    }

    private static void AssertNoRawChurn(System.Collections.Generic.IReadOnlyList<SchemaChange> changes)
    {
        var churn = changes.Where(c => c is CreateRawObjectChange or RecreateRawObjectChange)
                           .Select(c => c.Describe()).ToList();
        Assert.True(churn.Count == 0, "phantom raw-object churn:\n" + string.Join("\n", churn));
    }

    // ---- #61: cast / operator / operator-class / trigger / comment --------------------------------

    [Fact]
    public void Cast_round_trips_against_reader_form()
    {
        // Parser identity `cast:(afd.mood as integer)` vs reader `cast:afd.mood->integer` — reconciled by the
        // comparer's kind-canonical comparison key, and Cast compares identity-only (canonical reconstruction).
        var source = Parse("CREATE CAST (afd.mood AS integer) WITH FUNCTION afd.mood_to_int(afd.mood) AS ASSIGNMENT;");
        var live = Live(ObjectKind.Cast, "", "(afd.mood AS integer)", "cast:afd.mood->integer",
            "CREATE CAST (afd.mood AS integer) WITH FUNCTION afd.mood_to_int(afd.mood) AS ASSIGNMENT;");
        AssertNoPhantom(source, live);
    }

    [Fact]
    public void Operator_round_trips_against_reader_form()
    {
        // Parser captures the whole options paren; the reader captures arg types — paired on the operator
        // symbol, compared identity-only.
        var source = Parse("CREATE OPERATOR afd.=== (FUNCTION = afd.mood_eq, LEFTARG = afd.mood, RIGHTARG = afd.mood, COMMUTATOR = OPERATOR(afd.===), HASHES, MERGES);");
        var live = Live(ObjectKind.Operator, "", "afd.=== (afd.mood, afd.mood)", "operator:afd.===(afd.mood,afd.mood)",
            "CREATE OPERATOR afd.=== (FUNCTION = afd.mood_eq, LEFTARG = afd.mood, RIGHTARG = afd.mood, MERGES, HASHES);");
        AssertNoPhantom(source, live);
    }

    [Fact]
    public void OperatorClass_round_trips_against_reader_form_despite_doubled_schema_identity()
    {
        // Parser builds `operatorclass:afd.afd.int_minmax_ops using btree` (doubled schema bug); the reader
        // builds `operatorclass:afd.int_minmax_ops using btree`. The comparison key collapses the doubling.
        var source = Parse("CREATE OPERATOR CLASS afd.int_minmax_ops FOR TYPE integer USING btree AS OPERATOR 1 <, OPERATOR 2 <=, OPERATOR 3 =, FUNCTION 1 btint4cmp(integer, integer);");
        var live = Live(ObjectKind.OperatorClass, "", "afd.int_minmax_ops USING btree", "operatorclass:afd.int_minmax_ops using btree",
            "CREATE OPERATOR CLASS afd.int_minmax_ops FOR TYPE integer USING btree AS\n    OPERATOR 1 < (integer,integer),\n    OPERATOR 2 <= (integer,integer),\n    OPERATOR 3 = (integer,integer),\n    FUNCTION 1 btint4cmp(integer,integer);");
        AssertNoPhantom(source, live);
    }

    [Fact]
    public void Trigger_round_trips_against_pg_get_triggerdef_form()
    {
        // pg_get_triggerdef double-wraps the WHEN predicate and may say EXECUTE PROCEDURE — both folded by
        // NormalizeTriggerBody so an unchanged trigger does not recreate.
        var source = Parse("CREATE TRIGGER customers_touch BEFORE UPDATE ON afd.customers FOR EACH ROW WHEN (OLD.full_name IS DISTINCT FROM NEW.full_name) EXECUTE FUNCTION afd.touch_updated_at();");
        var live = Live(ObjectKind.Trigger, "afd", "customers_touch", "trigger:customers_touch on afd.customers",
            "CREATE TRIGGER customers_touch BEFORE UPDATE ON afd.customers FOR EACH ROW WHEN ((old.full_name IS DISTINCT FROM new.full_name)) EXECUTE PROCEDURE afd.touch_updated_at()", "afd.customers");
        AssertNoPhantom(source, live);
    }

    [Fact]
    public void Changed_trigger_still_recreates()
    {
        // A genuine body change (BEFORE UPDATE -> AFTER INSERT) must still diff — triggers keep real body
        // comparison (NOT identity-only).
        var source = Parse("CREATE TRIGGER t AFTER INSERT ON afd.x FOR EACH ROW EXECUTE FUNCTION afd.f();");
        var live = Live(ObjectKind.Trigger, "afd", "t", "trigger:t on afd.x",
            "CREATE TRIGGER t BEFORE UPDATE ON afd.x FOR EACH ROW EXECUTE FUNCTION afd.f()", "afd.x");
        foreach (var s in source.Schemas) if (!live.HasSchema(s.Name)) live.Schemas.Add(s);
        Assert.Single(new SchemaComparer().Compare(source, live).OfType<RecreateRawObjectChange>());
    }

    [Theory]
    [InlineData("COMMENT ON SCHEMA afd IS 'showcase';", "comment:schema afd", "COMMENT ON SCHEMA afd IS 'showcase';")]
    [InlineData("COMMENT ON FUNCTION afd.add(integer, integer) IS 'add';", "comment:function afd.add(integer, integer)", "COMMENT ON FUNCTION afd.add(integer, integer) IS 'add';")]
    [InlineData("COMMENT ON TYPE afd.mood IS 'enum';", "comment:type afd.mood", "COMMENT ON TYPE afd.mood IS 'enum';")]
    [InlineData("COMMENT ON COLUMN afd.t.c IS 'col';", "comment:column afd.t.c", "COMMENT ON COLUMN afd.t.c IS 'col';")]
    [InlineData("COMMENT ON TRIGGER tg ON afd.t IS 'tg';", "comment:trigger tg on afd.t", "COMMENT ON TRIGGER tg ON afd.t IS 'tg';")]
    public void Comment_round_trips_across_object_classes(string sourceSql, string identity, string liveBody)
    {
        var source = Parse(sourceSql);
        var live = Live(ObjectKind.Comment, "", "", identity, liveBody);
        AssertNoPhantom(source, live);
    }

    [Fact]
    public void Changed_comment_text_is_reapplied()
    {
        // Comments are keyed on their canonical body, so a changed comment text doesn't pair with the old one
        // and is re-emitted as a (idempotent) CreateRawObjectChange — running `COMMENT ON … IS 'new'` simply
        // overwrites the old comment. The point: an unchanged comment is silent, a changed one is not.
        var source = Parse("COMMENT ON TABLE afd.t IS 'new text';");
        var live = Live(ObjectKind.Comment, "", "", "comment:table afd.t", "COMMENT ON TABLE afd.t IS 'old text';");
        var create = Assert.Single(new SchemaComparer().Compare(source, live).OfType<CreateRawObjectChange>());
        Assert.Contains("new text", create.ToSql());
    }

    // ---- #64: functions / generated columns / BETWEEN / EXCLUDE -----------------------------------

    [Fact]
    public void Generated_column_round_trips_against_parenthesised_reader_form()
    {
        // Source `GENERATED ALWAYS AS (upper(full_name)) STORED`; the catalog re-parenthesises the
        // expression — NormalizeExpression folds the redundant parens so no phantom column recreate.
        var source = Parse("CREATE TABLE afd.t (full_name text, name_upper text GENERATED ALWAYS AS (upper(full_name)) STORED);");
        var live = Parse("CREATE TABLE afd.t (full_name text, name_upper text GENERATED ALWAYS AS ((upper(full_name))) STORED);");
        Assert.Empty(new SchemaComparer().Compare(source, live).OfType<AlterColumnChange>());
        Assert.Empty(new SchemaComparer().Compare(source, live).OfType<AddColumnChange>());
    }

    [Fact]
    public void Between_check_round_trips_against_parenthesised_reader_form()
    {
        var source = Parse("CREATE TABLE afd.t (discount int, CONSTRAINT t_disc CHECK (discount BETWEEN 0 AND 100));");
        var live = Parse("CREATE TABLE afd.t (discount int, CONSTRAINT t_disc CHECK ((discount BETWEEN 0 AND 100)));");
        Assert.Empty(new SchemaComparer().Compare(source, live).OfType<AddCheckConstraintChange>());
    }

    [Fact]
    public void Exclude_constraint_round_trips_against_reader_form()
    {
        // EXCLUDE is captured as a verbatim "other" constraint; spacing/paren folding keeps it stable.
        var source = Parse("CREATE TABLE afd.t (room int, during tstzrange, CONSTRAINT no_overlap EXCLUDE USING gist (room WITH =, during WITH &&));");
        var live = Parse("CREATE TABLE afd.t (room int, during tstzrange, CONSTRAINT no_overlap EXCLUDE USING gist (room WITH =, during WITH &&));");
        Assert.Empty(new SchemaComparer().Compare(source, live).OfType<AddRawTableConstraintChange>());
    }

    [Fact]
    public void Function_dollar_tag_and_spacing_round_trips()
    {
        // Different dollar-quote tag + whitespace; NormalizeBody (issue #64 canonical basis) folds both.
        var source = Parse("CREATE FUNCTION afd.f() RETURNS int LANGUAGE plpgsql AS $body$ BEGIN RETURN 1; END; $body$;");
        var live = Parse("CREATE FUNCTION afd.f() RETURNS int LANGUAGE plpgsql AS $function$\nBEGIN\n  RETURN 1;\nEND;\n$function$;");
        Assert.Empty(new SchemaComparer().Compare(source, live).OfType<CreateOrReplaceFunctionChange>());
    }
}
