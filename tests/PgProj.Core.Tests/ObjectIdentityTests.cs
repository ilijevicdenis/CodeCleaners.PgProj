using System.Collections.Generic;
using System.Linq;
using PgProj.Core.Model;
using PgProj.Core.Model.Identity;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Acceptance tests for issue #42 — the Object Identity Model (ObjectId / StableId / CanonicalHash)
/// and the B11 diff classifier. All DB-free: models are built from SQL via <see cref="TestModel"/>
/// or hand-constructed to stand in for an introspected (live) record.
/// </summary>
public sealed class ObjectIdentityTests
{
    private static readonly ObjectIdentityComputer Computer = new();

    // ---- ObjectId: opaque, cheap, model-local -----------------------------------------------------

    [Fact]
    public void ObjectId_is_unique_per_object_within_a_model()
    {
        var model = TestModel.Build(
            "CREATE TABLE s.a (id int); CREATE TABLE s.b (id int);", "s");
        var ids = Computer.ComputeAll(model);

        var objectIds = ids.Values.Select(v => v.ObjectId).ToList();
        Assert.All(objectIds, id => Assert.False(id.IsNone));
        Assert.Equal(objectIds.Count, objectIds.Distinct().Count());
    }

    [Fact]
    public void ObjectId_None_has_value_equality_and_cheap_hash()
    {
        Assert.Equal(new ObjectId(7), new ObjectId(7));
        Assert.NotEqual(new ObjectId(7), new ObjectId(8));
        Assert.True(ObjectId.None.IsNone);
        Assert.Equal(7, new ObjectId(7).GetHashCode());
    }

    // ---- CanonicalHash: cosmetic-insensitive ------------------------------------------------------

    [Fact]
    public void Cosmetic_only_edit_of_a_function_body_leaves_CanonicalHash_unchanged()
    {
        var tight = TestModel.Build(
            "CREATE FUNCTION s.f(a int) RETURNS int LANGUAGE sql AS $$SELECT a+1$$;", "s")
            .Functions.Single();
        var reformatted = TestModel.Build(
            "CREATE FUNCTION s.f(a int) RETURNS int LANGUAGE sql AS $body$\n   SELECT a + 1\n$body$;", "s")
            .Functions.Single();

        Assert.Equal(Computer.CanonicalHashOf(tight), Computer.CanonicalHashOf(reformatted));
    }

    [Fact]
    public void Cosmetic_only_edit_of_table_DDL_leaves_CanonicalHash_unchanged()
    {
        var compact = TestModel.Build(
            "CREATE TABLE s.t (id int NOT NULL, name text);", "s").Tables.Single();
        var spaced = TestModel.Build(
            "CREATE   TABLE  s.t\n(\n    id    int   NOT NULL,\n    name  text\n);", "s").Tables.Single();

        Assert.Equal(Computer.CanonicalHashOf(compact), Computer.CanonicalHashOf(spaced));
    }

    [Fact]
    public void Cosmetic_only_edit_of_a_view_body_leaves_CanonicalHash_unchanged()
    {
        var a = TestModel.Build("CREATE VIEW s.v AS SELECT 1 AS x;", "s").Views.Single();
        var b = TestModel.Build("CREATE VIEW s.v AS\n   SELECT   1   AS x;", "s").Views.Single();

        Assert.Equal(Computer.CanonicalHashOf(a), Computer.CanonicalHashOf(b));
    }

    [Fact]
    public void A_meaning_change_in_a_function_body_changes_CanonicalHash()
    {
        var v1 = TestModel.Build(
            "CREATE FUNCTION s.f(a int) RETURNS int LANGUAGE sql AS $$SELECT a+1$$;", "s").Functions.Single();
        var v2 = TestModel.Build(
            "CREATE FUNCTION s.f(a int) RETURNS int LANGUAGE sql AS $$SELECT a+2$$;", "s").Functions.Single();

        Assert.NotEqual(Computer.CanonicalHashOf(v1), Computer.CanonicalHashOf(v2));
    }

    // ---- StableId: rename-stable, structure-sensitive ---------------------------------------------

    [Fact]
    public void Renaming_a_table_preserves_its_StableId()
    {
        var original = TestModel.Build(
            "CREATE TABLE s.orders (id int NOT NULL, total numeric);", "s").Tables.Single();
        var renamed = TestModel.Build(
            "CREATE TABLE s.purchase_orders (id int NOT NULL, total numeric);", "s").Tables.Single();

        Assert.Equal(Computer.StableIdOf(original), Computer.StableIdOf(renamed));
    }

    [Fact]
    public void A_structural_change_to_a_table_changes_its_StableId()
    {
        var original = TestModel.Build(
            "CREATE TABLE s.orders (id int NOT NULL, total numeric);", "s").Tables.Single();
        var altered = TestModel.Build(
            "CREATE TABLE s.orders (id int NOT NULL, total numeric, note text);", "s").Tables.Single();

        Assert.NotEqual(Computer.StableIdOf(original), Computer.StableIdOf(altered));
    }

    [Fact]
    public void Renaming_a_function_preserves_StableId_but_changing_its_signature_changes_it()
    {
        var f = TestModel.Build(
            "CREATE FUNCTION s.calc(a int, b text) RETURNS int LANGUAGE sql AS $$SELECT a$$;", "s").Functions.Single();
        var renamed = TestModel.Build(
            "CREATE FUNCTION s.compute(a int, b text) RETURNS int LANGUAGE sql AS $$SELECT a$$;", "s").Functions.Single();
        var resigned = TestModel.Build(
            "CREATE FUNCTION s.calc(a bigint, b text) RETURNS int LANGUAGE sql AS $$SELECT a$$;", "s").Functions.Single();

        Assert.Equal(Computer.StableIdOf(f), Computer.StableIdOf(renamed));
        Assert.NotEqual(Computer.StableIdOf(f), Computer.StableIdOf(resigned));
    }

    [Fact]
    public void Renaming_an_index_preserves_StableId()
    {
        var i = TestModel.Build(
            "CREATE TABLE s.t (id int, email text); CREATE INDEX ix_a ON s.t (email);", "s").Indexes.Single();
        var renamed = TestModel.Build(
            "CREATE TABLE s.t (id int, email text); CREATE INDEX ix_b ON s.t (email);", "s").Indexes.Single();

        Assert.Equal(Computer.StableIdOf(i), Computer.StableIdOf(renamed));
    }

    // ---- determinism across builds ----------------------------------------------------------------

    [Fact]
    public void Identity_triple_is_deterministic_across_independent_builds()
    {
        const string sql = "CREATE TABLE s.t (id int NOT NULL, name text); CREATE VIEW s.v AS SELECT id FROM s.t;";
        var first = TestModel.Build(sql, "s");
        var second = TestModel.Build(sql, "s");

        var t1 = first.Tables.Single(); var t2 = second.Tables.Single();
        var v1 = first.Views.Single(); var v2 = second.Views.Single();

        Assert.Equal(Computer.StableIdOf(t1), Computer.StableIdOf(t2));
        Assert.Equal(Computer.CanonicalHashOf(t1), Computer.CanonicalHashOf(t2));
        Assert.Equal(Computer.StableIdOf(v1), Computer.StableIdOf(v2));
        Assert.Equal(Computer.CanonicalHashOf(v1), Computer.CanonicalHashOf(v2));
    }

    // ---- project model record vs introspected (live) record: identical triple ---------------------

    [Fact]
    public void Project_and_introspected_table_yield_same_StableId_and_CanonicalHash()
    {
        // Project side: hand-written DDL with project-style spellings (varchar, int).
        var project = TestModel.Build(
            "CREATE TABLE s.customer (id int NOT NULL, name varchar(50), active boolean DEFAULT true);", "s")
            .Tables.Single();

        // Introspected side: the same table as the live reader would build it — catalog spellings
        // (integer, character varying(50)) and a cast-suffixed default ('true' would arrive as is, but
        // numeric/text defaults arrive cast). TypeNormalizer + Canonicalizer must reconcile them.
        var introspected = new TableDefinition
        {
            Schema = "s",
            Name = "customer",
            Columns =
            {
                new ColumnDefinition("id", "integer", IsNullable: false),
                new ColumnDefinition("name", "character varying(50)", IsNullable: true),
                new ColumnDefinition("active", "boolean", IsNullable: true, Default: "true"),
            },
        };

        Assert.Equal(Computer.StableIdOf(project), Computer.StableIdOf(introspected));
        Assert.Equal(Computer.CanonicalHashOf(project), Computer.CanonicalHashOf(introspected));
    }

    [Fact]
    public void Project_and_introspected_function_yield_same_StableId_and_CanonicalHash()
    {
        var project = TestModel.Build(
            "CREATE FUNCTION s.add(a int, b int) RETURNS int LANGUAGE sql AS $$SELECT a+b$$;", "s")
            .Functions.Single();

        // Introspected: catalog arg-type spelling (integer) folds to the same StableId via TypeNormalizer;
        // the body differs only cosmetically (its own $function$ dollar tag + spacing), which NormalizeBody
        // folds away, so CanonicalHash matches. (Type-spelling differences INSIDE a body — int vs integer —
        // are a known Phase-8/#51 canonicalization gap, so the body here keeps the project's `int` spelling.)
        var introspected = new FunctionDefinition(
            "s", "add",
            Signature: "s.add(integer, integer)",
            Body: "CREATE FUNCTION s.add(a int, b int) RETURNS int LANGUAGE sql AS $function$ SELECT a + b $function$;",
            ArgTypes: "integer, integer");

        Assert.Equal(Computer.StableIdOf(project), Computer.StableIdOf(introspected));
        Assert.Equal(Computer.CanonicalHashOf(project), Computer.CanonicalHashOf(introspected));
    }

    // ---- B11 diff classifier ----------------------------------------------------------------------

    [Fact]
    public void Classifier_reports_Unchanged_for_identical_objects()
    {
        var t = TestModel.Build("CREATE TABLE s.t (id int NOT NULL);", "s").Tables.Single();
        var id = Computer.Identify(t);
        var same = Computer.Identify(t);

        var r = IdentityDiff.Classify(id, same);
        Assert.Equal(IdentityChangeKind.Unchanged, r.Kind);
        Assert.False(r.FqnChanged);
    }

    [Fact]
    public void Classifier_reports_Rename_when_only_the_FQN_changed()
    {
        var src = Computer.Identify(
            TestModel.Build("CREATE TABLE s.orders (id int NOT NULL, total numeric);", "s").Tables.Single());
        var tgt = Computer.Identify(
            TestModel.Build("CREATE TABLE s.purchase_orders (id int NOT NULL, total numeric);", "s").Tables.Single());

        var r = IdentityDiff.Classify(src, tgt);
        Assert.Equal(IdentityChangeKind.Rename, r.Kind);
        Assert.True(r.FqnChanged);
    }

    [Fact]
    public void Classifier_reports_Alter_when_meaning_changed_but_StableId_held()
    {
        // Same structure (StableId) — the table shape is unchanged — but the DEFAULT (meaning) differs,
        // which the CanonicalHash captures. Identity preserved, meaning changed → Alter.
        var src = Computer.Identify(
            TestModel.Build("CREATE TABLE s.t (id int NOT NULL, flag boolean DEFAULT true);", "s").Tables.Single());
        var tgt = Computer.Identify(
            TestModel.Build("CREATE TABLE s.t (id int NOT NULL, flag boolean DEFAULT false);", "s").Tables.Single());

        Assert.Equal(src.StableId, tgt.StableId);
        var r = IdentityDiff.Classify(src, tgt);
        Assert.Equal(IdentityChangeKind.Alter, r.Kind);
        Assert.False(r.FqnChanged);
    }

    [Fact]
    public void Classifier_reports_DropAndCreate_when_StableId_differs()
    {
        var src = Computer.Identify(
            TestModel.Build("CREATE TABLE s.t (id int NOT NULL);", "s").Tables.Single());
        var tgt = Computer.Identify(
            TestModel.Build("CREATE TABLE s.t (id int NOT NULL, extra text);", "s").Tables.Single());

        var r = IdentityDiff.Classify(src, tgt);
        Assert.Equal(IdentityChangeKind.DropAndCreate, r.Kind);
    }

    [Fact]
    public void Classifier_Create_and_Drop_are_DropAndCreate()
    {
        Assert.Equal(IdentityChangeKind.DropAndCreate, IdentityDiff.Create().Kind);
        Assert.Equal(IdentityChangeKind.DropAndCreate, IdentityDiff.Drop().Kind);
    }

    // ---- raw object kinds get a populated triple --------------------------------------------------

    [Fact]
    public void Raw_object_kinds_receive_a_populated_identity_triple()
    {
        var model = TestModel.Build(
            "CREATE TYPE s.mood AS ENUM ('happy','sad');", "s");
        var raw = model.Objects.First(o => o.Kind == ObjectKind.Type);
        var id = Computer.Identify(raw);

        Assert.False(id.ObjectId.IsNone);
        Assert.False(string.IsNullOrEmpty(id.StableId.Value));
        Assert.False(string.IsNullOrEmpty(id.CanonicalHash.Value));
        Assert.Equal("raw:type", id.Kind);
    }

    [Fact]
    public void ComputeAll_covers_every_object_in_the_model()
    {
        var model = TestModel.Build(
            "CREATE TABLE s.t (id int NOT NULL); " +
            "CREATE INDEX ix ON s.t (id); " +
            "CREATE VIEW s.v AS SELECT id FROM s.t; " +
            "CREATE SEQUENCE s.seq; " +
            "CREATE FUNCTION s.f() RETURNS int LANGUAGE sql AS $$SELECT 1$$; " +
            "CREATE TYPE s.mood AS ENUM ('a','b');", "s");

        var ids = Computer.ComputeAll(model);
        var expected = model.Schemas.Count + model.Tables.Count + model.Indexes.Count
                     + model.Views.Count + model.Sequences.Count + model.Functions.Count + model.Objects.Count;

        Assert.Equal(expected, ids.Count);
        Assert.All(ids.Values, v => Assert.False(v.ObjectId.IsNone));
    }
}
