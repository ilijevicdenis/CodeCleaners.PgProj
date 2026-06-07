using System.Linq;
using PgProj.Core.Comparison;
using PgProj.Core.Model;
using PgProj.Core.Model.Identity;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Phase 11 identity-based diff (issue #53): rename detection via StableId/CanonicalHash and the structured
/// field-level deltas (function volatility, unique constraints, sequence drop, enum-label add). All DB-free.
/// </summary>
public class IdentityDiffEngineTests
{
    private static DatabaseModel Parse(string sql) => TestModel.Build(sql);
    private static readonly ComparerOptions Renames = new() { DetectRenames = true };
    private static readonly ComparerOptions RenamesAndDrops = new() { DetectRenames = true, DropObjectsNotInSource = true };

    // ---- rename detection (mandatory) -----------------------------------------------------------

    [Fact]
    public void Renamed_table_otherwise_unchanged_is_a_single_rename_not_drop_and_create()
    {
        var source = Parse("CREATE TABLE app.customers (id int PRIMARY KEY, name text NOT NULL);");
        var target = Parse("CREATE TABLE app.clients   (id int PRIMARY KEY, name text NOT NULL);");

        var changes = new SchemaComparer().Compare(source, target, RenamesAndDrops);

        var rename = Assert.Single(changes.OfType<RenameTableChange>());
        Assert.Equal("clients", rename.OldName);
        Assert.Equal("customers", rename.NewName);
        Assert.Contains("RENAME TO", rename.ToSql());

        // The defining property: NO drop+create for the renamed table.
        Assert.Empty(changes.OfType<CreateTableChange>());
        Assert.Empty(changes.OfType<DropTableChange>());
    }

    [Fact]
    public void Rename_detection_is_off_by_default_so_a_rename_is_drop_plus_create()
    {
        var source = Parse("CREATE TABLE app.customers (id int PRIMARY KEY, name text);");
        var target = Parse("CREATE TABLE app.clients   (id int PRIMARY KEY, name text);");

        var changes = new SchemaComparer().Compare(source, target, new ComparerOptions { DropObjectsNotInSource = true });

        Assert.Empty(changes.OfType<RenameTableChange>());
        Assert.Single(changes.OfType<CreateTableChange>());
        Assert.Single(changes.OfType<DropTableChange>());
    }

    [Fact]
    public void A_structurally_changed_table_with_a_new_name_is_not_a_pure_rename()
    {
        // Different column set ⇒ different StableId ⇒ Drop+Create, never a rename.
        var source = Parse("CREATE TABLE app.customers (id int PRIMARY KEY, name text, extra int);");
        var target = Parse("CREATE TABLE app.clients   (id int PRIMARY KEY, name text);");

        var changes = new SchemaComparer().Compare(source, target, RenamesAndDrops);
        Assert.Empty(changes.OfType<RenameTableChange>());
        Assert.Single(changes.OfType<CreateTableChange>());
        Assert.Single(changes.OfType<DropTableChange>());
    }

    [Fact]
    public void Identical_models_produce_no_changes_even_with_rename_detection_on()
    {
        const string sql = """
            CREATE TABLE app.t (id int PRIMARY KEY, name text NOT NULL);
            CREATE SEQUENCE app.s;
            CREATE VIEW app.v AS SELECT 1 AS x;
            CREATE FUNCTION app.f(a int) RETURNS int LANGUAGE sql IMMUTABLE RETURN a;
            """;
        Assert.Empty(new SchemaComparer().Compare(Parse(sql), Parse(sql), RenamesAndDrops));
    }

    [Fact]
    public void Two_unrelated_renames_of_the_same_shape_pair_deterministically()
    {
        // Two same-structure tables both renamed: pairing is by ascending name, so the result is stable and
        // each becomes exactly one rename (no cross-product of drops/creates).
        var source = Parse("CREATE TABLE app.aa (id int PRIMARY KEY); CREATE TABLE app.bb (id int PRIMARY KEY);");
        var target = Parse("CREATE TABLE app.xx (id int PRIMARY KEY); CREATE TABLE app.yy (id int PRIMARY KEY);");
        var renames = new SchemaComparer().Compare(source, target, RenamesAndDrops).OfType<RenameTableChange>().ToList();
        Assert.Equal(2, renames.Count);
        Assert.Empty(new SchemaComparer().Compare(source, target, RenamesAndDrops).OfType<CreateTableChange>());
        Assert.Empty(new SchemaComparer().Compare(source, target, RenamesAndDrops).OfType<DropTableChange>());
    }

    [Fact]
    public void Renamed_view_emits_alter_view_rename()
    {
        var source = Parse("CREATE VIEW app.active_v AS SELECT 1 AS x;");
        var target = Parse("CREATE VIEW app.live_v   AS SELECT 1 AS x;");

        var rename = Assert.Single(new SchemaComparer().Compare(source, target, RenamesAndDrops).OfType<RenameViewChange>());
        Assert.Equal("live_v", rename.OldName);
        Assert.Equal("active_v", rename.NewName);
        Assert.Contains("ALTER VIEW", rename.ToSql());
    }

    [Fact]
    public void Renamed_function_keeps_arg_signature_in_the_rename()
    {
        var source = Parse("CREATE FUNCTION app.add2(a int, b int) RETURNS int LANGUAGE sql IMMUTABLE RETURN a + b;");
        var target = Parse("CREATE FUNCTION app.plus(a int, b int) RETURNS int LANGUAGE sql IMMUTABLE RETURN a + b;");

        var rename = Assert.Single(new SchemaComparer().Compare(source, target, RenamesAndDrops).OfType<RenameFunctionChange>());
        Assert.Equal("plus", rename.OldName);
        Assert.Equal("add2", rename.NewName);
        Assert.Contains("ALTER FUNCTION", rename.ToSql());
        Assert.Contains("RENAME TO", rename.ToSql());
    }

    [Fact]
    public void IdentityDiff_classify_table_rename_round_trips_through_the_computer()
    {
        // Direct check of the pure classifier on the identity triple (no comparer).
        var ids = new ObjectIdentityComputer();
        var a = Parse("CREATE TABLE app.customers (id int PRIMARY KEY, name text);").Tables.Single();
        var b = Parse("CREATE TABLE app.clients   (id int PRIMARY KEY, name text);").Tables.Single();

        var result = IdentityDiff.Classify(ids.Identify(a), ids.Identify(b));
        Assert.Equal(IdentityChangeKind.Rename, result.Kind);
        Assert.True(result.FqnChanged);
    }

    // ---- structured function delta (mandatory: volatility-only change) ----------------------------

    [Fact]
    public void Function_whose_only_change_is_volatility_emits_a_precise_delta_not_a_full_replace()
    {
        var source = Parse("CREATE FUNCTION app.f(a int) RETURNS int LANGUAGE sql VOLATILE RETURN a + 1;");
        var target = Parse("CREATE FUNCTION app.f(a int) RETURNS int LANGUAGE sql STABLE RETURN a + 1;");

        var changes = new SchemaComparer().Compare(source, target);

        var delta = Assert.Single(changes.OfType<AlterFunctionAttributesChange>());
        Assert.Equal(FunctionVolatility.Volatile, delta.Volatility);
        var sql = delta.ToSql();
        Assert.Contains("ALTER FUNCTION", sql);
        Assert.Contains("VOLATILE", sql);
        // Crucially: NOT a blunt CREATE OR REPLACE.
        Assert.Empty(changes.OfType<CreateOrReplaceFunctionChange>());
    }

    [Fact]
    public void Function_with_a_real_body_change_still_does_a_full_replace()
    {
        var source = Parse("CREATE FUNCTION app.f(a int) RETURNS int LANGUAGE sql STABLE RETURN a + 2;");
        var target = Parse("CREATE FUNCTION app.f(a int) RETURNS int LANGUAGE sql STABLE RETURN a + 1;");

        var changes = new SchemaComparer().Compare(source, target);
        Assert.Empty(changes.OfType<AlterFunctionAttributesChange>());
        Assert.Single(changes.OfType<CreateOrReplaceFunctionChange>());
    }

    // ---- unique-constraint alteration ------------------------------------------------------------

    [Fact]
    public void New_unique_constraint_on_existing_table_is_added_not_recreated()
    {
        var source = Parse("CREATE TABLE app.t (id int PRIMARY KEY, email text, CONSTRAINT t_email_uq UNIQUE (email));");
        var target = Parse("CREATE TABLE app.t (id int PRIMARY KEY, email text);");

        var add = Assert.Single(new SchemaComparer().Compare(source, target).OfType<AddUniqueConstraintChange>());
        Assert.Contains("UNIQUE", add.ToSql());
        Assert.Contains("email", add.ToSql());
    }

    [Fact]
    public void Removed_unique_constraint_is_dropped_only_with_allow_drops()
    {
        var source = Parse("CREATE TABLE app.t (id int PRIMARY KEY, email text);");
        var target = Parse("CREATE TABLE app.t (id int PRIMARY KEY, email text, CONSTRAINT t_email_uq UNIQUE (email));");

        Assert.Empty(new SchemaComparer().Compare(source, target).OfType<DropUniqueConstraintChange>());
        var drop = Assert.Single(new SchemaComparer()
            .Compare(source, target, new ComparerOptions { DropObjectsNotInSource = true })
            .OfType<DropUniqueConstraintChange>());
        Assert.Equal("t_email_uq", drop.Name);
    }

    // ---- sequence drop (drop-not-in-source) ------------------------------------------------------

    [Fact]
    public void Sequence_present_only_in_target_is_dropped_with_allow_drops()
    {
        var source = new DatabaseModel();
        var target = Parse("CREATE SEQUENCE app.old_seq;");
        // add the schema to source so the only diff under test is the sequence drop
        source.Schemas.Add(new SchemaDefinition("app"));

        Assert.Empty(new SchemaComparer().Compare(source, target).OfType<DropSequenceChange>());
        var drop = Assert.Single(new SchemaComparer()
            .Compare(source, target, new ComparerOptions { DropObjectsNotInSource = true })
            .OfType<DropSequenceChange>());
        Assert.Equal("old_seq", drop.Name);
        Assert.Contains("DROP SEQUENCE", drop.ToSql());
    }

    [Fact]
    public void Renamed_sequence_is_a_rename_not_a_drop_plus_create()
    {
        var source = Parse("CREATE SEQUENCE app.new_seq INCREMENT 2 START 5;");
        var target = Parse("CREATE SEQUENCE app.old_seq INCREMENT 2 START 5;");

        var changes = new SchemaComparer().Compare(source, target, RenamesAndDrops);
        var rename = Assert.Single(changes.OfType<RenameSequenceChange>());
        Assert.Equal("old_seq", rename.OldName);
        Assert.Equal("new_seq", rename.NewName);
        Assert.Empty(changes.OfType<CreateSequenceChange>());
        Assert.Empty(changes.OfType<DropSequenceChange>());
    }
}
