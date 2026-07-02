using System.Linq;
using PgProj.Core.Comparison;
using PgProj.Core.Model;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// DEFERRABLE / INITIALLY DEFERRED / NOT VALID / INCLUDE / NULLS NOT DISTINCT / NO INHERIT / MATCH FULL
/// must survive parse → model → emit → compare (P0 audit finding, 2026-07-02: the parser validated all
/// of them, then discarded them before the model — deploys silently lost the attributes).
/// </summary>
public class ConstraintAttributesTests
{
    [Fact]
    public void Attributes_fold_into_the_model()
    {
        var t = TestModel.Build(@"
            CREATE TABLE public.t (
                id int,
                sku text,
                qty int,
                other int,
                CONSTRAINT t_pk PRIMARY KEY (id) INCLUDE (qty) DEFERRABLE INITIALLY DEFERRED,
                CONSTRAINT t_uq UNIQUE NULLS NOT DISTINCT (sku) INCLUDE (other) DEFERRABLE,
                CONSTRAINT t_ck CHECK (qty > 0) NO INHERIT
            );
            ALTER TABLE public.t ADD CONSTRAINT t_fk FOREIGN KEY (other) REFERENCES public.t (id)
                MATCH FULL ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED NOT VALID;").Tables.Single();

        var pk = t.PrimaryKey!;
        Assert.Equal(new[] { "qty" }, pk.Include);
        Assert.True(pk.Deferrable);
        Assert.True(pk.InitiallyDeferred);

        var uq = t.Unique.Single();
        Assert.True(uq.NullsNotDistinct);
        Assert.Equal(new[] { "other" }, uq.Include);
        Assert.True(uq.Deferrable);
        Assert.False(uq.InitiallyDeferred);

        Assert.True(t.Checks.Single().NoInherit);

        var fk = t.ForeignKeys.Single();
        Assert.Equal("FULL", fk.Match);
        Assert.True(fk.Deferrable);
        Assert.True(fk.InitiallyDeferred);
        Assert.True(fk.NotValid);
        Assert.Equal("CASCADE", fk.OnDelete);
    }

    [Fact]
    public void Attributes_render_in_emitted_sql()
    {
        var t = TestModel.Build(@"
            CREATE TABLE public.t (
                id int,
                sku text,
                qty int,
                CONSTRAINT t_pk PRIMARY KEY (id) INCLUDE (qty) DEFERRABLE INITIALLY DEFERRED,
                CONSTRAINT t_uq UNIQUE NULLS NOT DISTINCT (sku) DEFERRABLE
            );").Tables.Single();

        var sql = SqlEmitter.CreateTable(t);
        Assert.Contains("PRIMARY KEY (\"id\") INCLUDE (\"qty\") DEFERRABLE INITIALLY DEFERRED", sql);
        Assert.Contains("UNIQUE NULLS NOT DISTINCT (\"sku\") DEFERRABLE", sql);
        Assert.DoesNotContain("INITIALLY DEFERRED\n", sql.Replace("DEFERRABLE INITIALLY DEFERRED", ""));
    }

    [Fact]
    public void Foreign_key_emits_match_deferrable_and_not_valid()
    {
        var t = TestModel.Build(@"
            CREATE TABLE public.p (id int PRIMARY KEY);
            CREATE TABLE public.t (pid int);
            ALTER TABLE public.t ADD CONSTRAINT t_fk FOREIGN KEY (pid) REFERENCES public.p (id)
                MATCH FULL DEFERRABLE INITIALLY DEFERRED NOT VALID;").Tables.Single(x => x.Name == "t");

        var sql = SqlEmitter.ForeignKey(t.Schema, t.Name, t.ForeignKeys.Single());
        Assert.Contains("MATCH FULL", sql);
        Assert.Contains("DEFERRABLE INITIALLY DEFERRED", sql);
        Assert.EndsWith("NOT VALID;", sql);
    }

    [Fact]
    public void Attribute_free_constraints_emit_byte_identically_to_before()
    {
        var t = TestModel.Build(@"
            CREATE TABLE public.t (id int, sku text,
                CONSTRAINT t_pk PRIMARY KEY (id),
                CONSTRAINT t_uq UNIQUE (sku),
                CONSTRAINT t_ck CHECK (id > 0));").Tables.Single();
        var sql = SqlEmitter.CreateTable(t);
        Assert.Contains("CONSTRAINT \"t_pk\" PRIMARY KEY (\"id\")", sql);
        Assert.Contains("CONSTRAINT \"t_uq\" UNIQUE (\"sku\")", sql);
        Assert.DoesNotContain("DEFERRABLE", sql);
        Assert.DoesNotContain("INCLUDE", sql);
        Assert.DoesNotContain("NULLS NOT DISTINCT", sql);
    }

    [Fact]
    public void Comparer_flags_attribute_flips_as_changes()
    {
        var deferred = TestModel.Build("CREATE TABLE public.t (id int, CONSTRAINT t_pk PRIMARY KEY (id) DEFERRABLE);");
        var plain = TestModel.Build("CREATE TABLE public.t (id int, CONSTRAINT t_pk PRIMARY KEY (id));");
        var changes = new SchemaComparer().Compare(deferred, plain);
        Assert.Contains(changes, c => c is DropPrimaryKeyChange);
        Assert.Contains(changes, c => c is AddPrimaryKeyChange);

        var nnd = TestModel.Build("CREATE TABLE public.t (sku text, CONSTRAINT t_uq UNIQUE NULLS NOT DISTINCT (sku));");
        var plainUq = TestModel.Build("CREATE TABLE public.t (sku text, CONSTRAINT t_uq UNIQUE (sku));");
        Assert.Contains(new SchemaComparer().Compare(nnd, plainUq), c => c is AddUniqueConstraintChange);
    }

    [Fact]
    public void Comparer_treats_identical_attributes_as_equal()
    {
        const string ddl = @"
            CREATE TABLE public.t (id int, sku text,
                CONSTRAINT t_pk PRIMARY KEY (id) DEFERRABLE INITIALLY DEFERRED,
                CONSTRAINT t_uq UNIQUE NULLS NOT DISTINCT (sku));";
        Assert.Empty(new SchemaComparer().Compare(TestModel.Build(ddl), TestModel.Build(ddl)));
    }

    [Fact]
    public void Fk_referential_action_flip_now_surfaces_as_a_change()
    {
        // Bonus from the same audit family: ON DELETE was excluded from the FK signature entirely.
        var cascade = TestModel.Build(@"
            CREATE TABLE public.p (id int PRIMARY KEY);
            CREATE TABLE public.t (pid int REFERENCES public.p(id) ON DELETE CASCADE);");
        var plain = TestModel.Build(@"
            CREATE TABLE public.p (id int PRIMARY KEY);
            CREATE TABLE public.t (pid int REFERENCES public.p(id));");
        Assert.Contains(new SchemaComparer().Compare(cascade, plain), c => c is AddForeignKeyChange);
        // …and NO ACTION is the same as absent (parser-explicit vs catalog-omitted must not churn).
        var noAction = TestModel.Build(@"
            CREATE TABLE public.p (id int PRIMARY KEY);
            CREATE TABLE public.t (pid int REFERENCES public.p(id) ON DELETE NO ACTION);");
        Assert.Empty(new SchemaComparer().Compare(noAction, plain));
    }

    [Fact]
    public void Not_valid_is_validation_state_not_shape()
    {
        // A validated live CHECK/FK vs a project declaring NOT VALID must not churn.
        var declared = TestModel.Build(@"
            CREATE TABLE public.t (qty int);
            ALTER TABLE public.t ADD CONSTRAINT t_ck CHECK (qty > 0) NOT VALID;");
        var validated = TestModel.Build(@"
            CREATE TABLE public.t (qty int, CONSTRAINT t_ck CHECK (qty > 0));");
        Assert.Empty(new SchemaComparer().Compare(declared, validated));
    }
}
