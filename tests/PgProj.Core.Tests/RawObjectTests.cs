using System.Linq;
using PgProj.Core.Comparison;
using PgProj.Core.Model;
using PgProj.Core.Parsing;
using PgProj.Core.Syntax;
using Xunit;

namespace PgProj.Core.Tests;

public class RawObjectTests
{
    private static DatabaseModel Parse(string sql) => TestModel.Build(sql);

    [Fact]
    public void Parses_extension_with_quoted_name()
    {
        var o = Assert.Single(Parse("CREATE EXTENSION IF NOT EXISTS \"uuid-ossp\";").Objects);
        Assert.Equal(ObjectKind.Extension, o.Kind);
        Assert.Equal("uuid-ossp", o.Name);
        Assert.Equal("extension:uuid-ossp", o.Identity);
    }

    [Fact]
    public void Parses_enum_type()
    {
        var o = Assert.Single(Parse("CREATE TYPE app.tier AS ENUM ('bronze','silver','gold');").Objects);
        Assert.Equal(ObjectKind.Type, o.Kind);
        Assert.Equal("app", o.Schema);
        Assert.Equal("tier", o.Name);
        Assert.Equal("type:app.tier", o.Identity);
    }

    [Fact]
    public void Parses_domain()
    {
        var o = Assert.Single(Parse("CREATE DOMAIN app.email AS text CHECK (VALUE ~ '@');").Objects);
        Assert.Equal(ObjectKind.Domain, o.Kind);
        Assert.Equal("domain:app.email", o.Identity);
    }

    [Fact]
    public void Parses_trigger_with_table_scope()
    {
        const string sql = "CREATE TRIGGER t_touch BEFORE UPDATE ON app.customers FOR EACH ROW EXECUTE FUNCTION app.f();";
        var o = Assert.Single(Parse(sql).Objects);
        Assert.Equal(ObjectKind.Trigger, o.Kind);
        Assert.Equal("t_touch", o.Name);
        Assert.Equal("app.customers", o.OnObject);
        Assert.Equal("trigger:t_touch on app.customers", o.Identity);
    }

    [Fact]
    public void Parses_policy_with_table_scope()
    {
        const string sql = "CREATE POLICY p_sel ON app.customers FOR SELECT USING (true);";
        var o = Assert.Single(Parse(sql).Objects);
        Assert.Equal(ObjectKind.Policy, o.Kind);
        Assert.Equal("policy:p_sel on app.customers", o.Identity);
    }

    [Fact]
    public void Parses_comment()
    {
        var o = Assert.Single(Parse("COMMENT ON TABLE app.customers IS 'hi';").Objects);
        Assert.Equal(ObjectKind.Comment, o.Kind);
        Assert.Contains("comment:", o.Identity);
        Assert.Contains("table app.customers", o.Identity);
    }

    [Fact]
    public void Dollar_quoted_trigger_function_body_is_not_split()
    {
        const string sql = """
            CREATE FUNCTION app.f() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                NEW.x = 1; RETURN NEW;
            END;
            $$;
            """;
        var model = Parse(sql);
        Assert.Single(model.Functions);
        Assert.Empty(model.Objects); // the inner semicolons did not spawn phantom statements
    }

    [Fact]
    public void New_raw_object_produces_create_change()
    {
        var source = Parse("CREATE EXTENSION IF NOT EXISTS citext;");
        var change = Assert.Single(new SchemaComparer().Compare(source, new DatabaseModel()).OfType<CreateRawObjectChange>());
        Assert.Contains("CREATE EXTENSION", change.ToSql());
    }

    [Fact]
    public void Changed_trigger_recreates_with_drop_first()
    {
        var source = Parse("CREATE TRIGGER t BEFORE INSERT ON app.x FOR EACH ROW EXECUTE FUNCTION app.f();");
        var target = Parse("CREATE TRIGGER t BEFORE UPDATE ON app.x FOR EACH ROW EXECUTE FUNCTION app.f();");

        var change = Assert.Single(new SchemaComparer().Compare(source, target).OfType<RecreateRawObjectChange>());
        var sql = change.ToSql();
        Assert.Contains("DROP TRIGGER IF EXISTS \"t\" ON \"app\".\"x\"", sql);
        Assert.Contains("CREATE TRIGGER t BEFORE INSERT", sql);
    }

    [Fact]
    public void Changed_type_is_guarded_by_allow_drops()
    {
        // A LABEL REMOVAL (target has 'a','b'; source keeps only 'a') is destructive — ALTER TYPE cannot
        // drop an enum value, so it must fall through to the guarded drop+recreate (not the ADD VALUE delta).
        var source = Parse("CREATE TYPE app.tier AS ENUM ('a');");
        var target = Parse("CREATE TYPE app.tier AS ENUM ('a','b');");

        // Destructive recreate suppressed by default...
        Assert.Empty(new SchemaComparer().Compare(source, target).OfType<RecreateRawObjectChange>());
        Assert.Empty(new SchemaComparer().Compare(source, target).OfType<AddEnumValuesChange>());
        // ...allowed when drops are permitted.
        Assert.Single(new SchemaComparer()
            .Compare(source, target, new ComparerOptions { DropObjectsNotInSource = true })
            .OfType<RecreateRawObjectChange>());
    }

    [Fact]
    public void Added_enum_label_produces_precise_add_value_not_recreate()
    {
        // Appending a label is non-destructive — ALTER TYPE … ADD VALUE, never a drop+recreate, and it is
        // emitted even WITHOUT --allow-drops (issue #53 field-level delta).
        var source = Parse("CREATE TYPE app.tier AS ENUM ('a','b','c');");
        var target = Parse("CREATE TYPE app.tier AS ENUM ('a','b');");

        var add = Assert.Single(new SchemaComparer().Compare(source, target).OfType<AddEnumValuesChange>());
        Assert.Equal(new[] { "c" }, add.NewLabels);
        Assert.Contains("ADD VALUE", add.ToSql());
        Assert.Contains("'c'", add.ToSql());
        Assert.Empty(new SchemaComparer().Compare(source, target).OfType<RecreateRawObjectChange>());
    }

    [Fact]
    public void Reordered_enum_labels_is_not_treated_as_add()
    {
        // A reorder is not expressible as ADD VALUE (it can't move existing labels) → it must NOT misfire
        // the delta; with the labels merely permuted (no genuine add) it falls through to the guarded path.
        var source = Parse("CREATE TYPE app.tier AS ENUM ('b','a');");
        var target = Parse("CREATE TYPE app.tier AS ENUM ('a','b');");
        Assert.Empty(new SchemaComparer().Compare(source, target).OfType<AddEnumValuesChange>());
    }

    [Fact]
    public void Identity_only_target_prevents_create_and_recreate()
    {
        // Simulates an introspected existence-only record (no body): the policy already exists,
        // so it must be neither created nor recreated.
        var source = Parse("CREATE POLICY p ON app.t FOR SELECT USING (true);");
        var target = new DatabaseModel();
        target.Objects.Add(new RawObjectDefinition(
            ObjectKind.Policy, "app", "p", "policy:p on app.t", Body: "", OnObject: "app.t", BodyComparable: false));

        var changes = new SchemaComparer().Compare(source, target);
        Assert.Empty(changes.OfType<CreateRawObjectChange>());
        Assert.Empty(changes.OfType<RecreateRawObjectChange>());
    }

    // ---- round-trip idempotency (issue #36): a project model compared against a model holding the
    // live reader's *canonical* reconstruction of the same object must produce ZERO phantom diffs. -----

    /// <summary>Builds a one-object model the way <c>LiveDatabaseReader</c> would, so a comparison
    /// stands in for an extract → re-compare without needing a live server.</summary>
    private static DatabaseModel Live(ObjectKind kind, string schema, string name, string identity, string body)
    {
        var m = new DatabaseModel();
        // The source model auto-creates any referenced schema; mirror that on the live side so the
        // only thing under test is the raw-object body comparison (not a spurious CreateSchema diff).
        if (!string.IsNullOrEmpty(schema)) m.Schemas.Add(new SchemaDefinition(schema));
        m.Objects.Add(new RawObjectDefinition(kind, schema, name, identity.ToLowerInvariant(), body));
        return m;
    }

    private static void AssertNoPhantomDiff(DatabaseModel source, DatabaseModel live)
    {
        // Both diff directions must be clean, with and without --allow-drops, mirroring extract/drift.
        Assert.Empty(new SchemaComparer().Compare(source, live));
        Assert.Empty(new SchemaComparer().Compare(source, live, new ComparerOptions { DropObjectsNotInSource = true }));
    }

    [Fact]
    public void Extension_bare_vs_if_not_exists_quoted_is_no_diff()
    {
        // Source writes it bare; the reader emits IF NOT EXISTS + quoted name.
        var source = Parse("CREATE EXTENSION btree_gist;");
        var live = Live(ObjectKind.Extension, "", "btree_gist", "extension:btree_gist",
            "CREATE EXTENSION IF NOT EXISTS \"btree_gist\";");
        AssertNoPhantomDiff(source, live);
    }

    [Fact]
    public void TextSearchDictionary_option_formatting_is_no_diff()
    {
        // Source: multi-line, spaced options; reader: single line, different option spelling/order.
        var source = Parse("""
            CREATE TEXT SEARCH DICTIONARY afd.english_dict (
                TEMPLATE  = pg_catalog.simple,
                STOPWORDS = english
            );
            """);
        var live = Live(ObjectKind.TextSearchDictionary, "afd", "english_dict",
            "textsearchdictionary:afd.english_dict",
            "CREATE TEXT SEARCH DICTIONARY afd.english_dict (TEMPLATE = pg_catalog.simple, stopwords = 'english');");
        AssertNoPhantomDiff(source, live);
    }

    [Fact]
    public void TextSearchConfiguration_copy_vs_parser_add_mapping_is_no_diff()
    {
        // The classic phantom: source COPYs a config + one ALTER MAPPING; the reader reconstructs the
        // end state as PARSER=… plus one ADD MAPPING per token type — structurally different DDL,
        // semantically the same object. Identity-only comparison keeps the round-trip clean.
        var source = Parse("""
            CREATE TEXT SEARCH CONFIGURATION afd.english_cfg (COPY = pg_catalog.english);
            ALTER TEXT SEARCH CONFIGURATION afd.english_cfg
                ALTER MAPPING FOR asciiword, word WITH afd.english_dict;
            """);
        var live = Live(ObjectKind.TextSearchConfiguration, "afd", "english_cfg",
            "textsearchconfiguration:afd.english_cfg",
            "CREATE TEXT SEARCH CONFIGURATION afd.english_cfg (PARSER = pg_catalog.default);\n"
            + "ALTER TEXT SEARCH CONFIGURATION afd.english_cfg ADD MAPPING FOR asciiword WITH afd.english_dict;\n"
            + "ALTER TEXT SEARCH CONFIGURATION afd.english_cfg ADD MAPPING FOR word WITH afd.english_dict;");
        AssertNoPhantomDiff(source, live);
    }

    [Fact]
    public void ForeignDataWrapper_option_and_handler_formatting_is_no_diff()
    {
        var source = Parse("CREATE FOREIGN DATA WRAPPER dummy_fdw NO HANDLER NO VALIDATOR OPTIONS (debug 'true');");
        // Reader omits the NO HANDLER / NO VALIDATOR keywords and may reorder/format OPTIONS.
        var live = Live(ObjectKind.ForeignDataWrapper, "", "dummy_fdw", "foreigndatawrapper:dummy_fdw",
            "CREATE FOREIGN DATA WRAPPER dummy_fdw OPTIONS (debug 'true');");
        AssertNoPhantomDiff(source, live);
    }

    [Fact]
    public void Server_option_formatting_is_no_diff()
    {
        var source = Parse("CREATE SERVER dummy_server FOREIGN DATA WRAPPER dummy_fdw OPTIONS (host 'localhost', dbname 'x');");
        var live = Live(ObjectKind.Server, "", "dummy_server", "server:dummy_server",
            "CREATE SERVER dummy_server FOREIGN DATA WRAPPER dummy_fdw OPTIONS (host 'localhost', dbname 'x');");
        AssertNoPhantomDiff(source, live);
    }

    [Fact]
    public void IdentityOnly_kinds_still_create_when_target_is_missing()
    {
        // Identity-only comparison must NOT mask a genuinely absent object — it must still be created.
        var source = Parse("CREATE EXTENSION btree_gist;");
        var change = Assert.Single(new SchemaComparer().Compare(source, new DatabaseModel()).OfType<CreateRawObjectChange>());
        Assert.Contains("CREATE EXTENSION", change.ToSql());
    }

    [Fact]
    public void Typed_table_OF_type_round_trips_against_reader_form()
    {
        // Source models a typed table as a raw `table:` object (OF type); the reader now reconstructs
        // the same OF-type form (instead of flattening to a column list), so re-compare is clean.
        var source = Parse("CREATE TABLE afd.address_row OF afd.address (PRIMARY KEY (zip));");
        var live = Live(ObjectKind.Table, "afd", "address_row", "table:afd.address_row",
            "CREATE TABLE afd.address_row OF afd.address (PRIMARY KEY (zip));");
        AssertNoPhantomDiff(source, live);

        // And the typed-table OF-type body re-parses cleanly (extract fidelity).
        Assert.Empty(new PgParser().Parse(live.Objects.Single().Body).Diagnostics);
    }

    [Fact]
    public void Identity_only_metadata_covers_the_canonical_reconstruction_kinds()
    {
        Assert.True(RawObjectMeta.ComparesByIdentityOnly(ObjectKind.Extension));
        Assert.True(RawObjectMeta.ComparesByIdentityOnly(ObjectKind.TextSearchDictionary));
        Assert.True(RawObjectMeta.ComparesByIdentityOnly(ObjectKind.TextSearchConfiguration));
        Assert.True(RawObjectMeta.ComparesByIdentityOnly(ObjectKind.ForeignDataWrapper));
        Assert.True(RawObjectMeta.ComparesByIdentityOnly(ObjectKind.Server));
        // Body-faithful kinds must keep their real body diff (e.g. triggers detect changes).
        Assert.False(RawObjectMeta.ComparesByIdentityOnly(ObjectKind.Trigger));
        Assert.False(RawObjectMeta.ComparesByIdentityOnly(ObjectKind.Type));
    }

    [Fact]
    public void Extensions_precede_tables_and_comments_come_last()
    {
        var source = Parse("""
            CREATE EXTENSION IF NOT EXISTS citext;
            CREATE TYPE app.tier AS ENUM ('a','b');
            CREATE TABLE app.t (id int PRIMARY KEY);
            CREATE TRIGGER t_touch BEFORE UPDATE ON app.t FOR EACH ROW EXECUTE FUNCTION app.f();
            COMMENT ON TABLE app.t IS 'note';
            """);
        var script = new DeployScriptGenerator().Generate(new SchemaComparer().Compare(source, new DatabaseModel()));

        var ext = script.IndexOf("CREATE EXTENSION", System.StringComparison.Ordinal);
        var type = script.IndexOf("CREATE TYPE", System.StringComparison.Ordinal);
        var table = script.IndexOf("CREATE TABLE", System.StringComparison.Ordinal);
        var trig = script.IndexOf("CREATE TRIGGER", System.StringComparison.Ordinal);
        var comment = script.IndexOf("COMMENT ON", System.StringComparison.Ordinal);

        Assert.True(ext < type && type < table && table < trig && trig < comment,
            $"order was ext={ext} type={type} table={table} trig={trig} comment={comment}");
    }
}
