using System.Linq;
using PgProj.Core.Semantics;
using PgProj.Core.Semantics.Binding;
using PgProj.Core.Syntax;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Audit P1 pair (2026-07-02): (a) ONE AlterStatement anywhere in a file used to disable
/// View/Trigger/CHECK/query validation for the WHOLE file — including the extractor's routine
/// ADD CONSTRAINT, so extracted projects got far weaker validation than hand-written ones;
/// (b) the semantic catalog ignored ALTERs entirely, so a view over an ALTER-added column
/// false-positived "column does not exist". The two fixes only work together.
/// </summary>
public class AlterAwareValidationTests
{
    private static System.Collections.Generic.IReadOnlyList<PgProj.Core.Diagnostics.Diagnostic> Validate(string sql)
    {
        var parsed = new PgParser().Parse(sql);
        var catalog = CatalogBuilder.Build(parsed);
        var validator = new SemanticValidator(catalog);
        validator.IndexFile("f.sql", sql, parsed);
        var diags = validator.Validate("f.sql", sql, parsed);
        parsed.ReleaseTokens();
        return diags;
    }

    [Fact]
    public void Add_constraint_no_longer_disables_view_validation_for_the_file()
    {
        // The extractor's shape: CREATE TABLE + standalone ADD CONSTRAINT. The bad view must be caught.
        var diags = Validate(@"
            CREATE TABLE public.t (id int, email text);
            ALTER TABLE public.t ADD CONSTRAINT t_pk PRIMARY KEY (id);
            CREATE VIEW public.v AS SELECT missing_col FROM public.t;");
        Assert.Contains(diags, d => d.Message.Contains("missing_col"));
    }

    [Fact]
    public void Alter_added_column_resolves_in_a_view()
    {
        // (b): before the catalog fold, this view false-positived "column does not exist".
        var diags = Validate(@"
            CREATE TABLE public.t (id int);
            ALTER TABLE public.t ADD COLUMN email text;
            CREATE VIEW public.v AS SELECT email FROM public.t;");
        Assert.Empty(diags);
    }

    [Fact]
    public void Rename_still_disables_validation_conservatively()
    {
        // Names changed mid-file — resolution is unreliable, the blanket must stay for THIS shape.
        var diags = Validate(@"
            CREATE TABLE public.t (id int);
            ALTER TABLE public.t RENAME TO t2;
            CREATE VIEW public.v AS SELECT nonexistent FROM public.t2;");
        Assert.Empty(diags);
    }

    [Fact]
    public void Invalidating_actions_are_classified_correctly()
    {
        static AlterStatement ParseAlter(string sql)
        {
            var parsed = new PgParser().Parse(sql);
            var a = parsed.Statements.OfType<AlterStatement>().Single();
            parsed.ReleaseTokens();
            return a;
        }

        Assert.False(ParseAlter("ALTER TABLE t ADD CONSTRAINT c PRIMARY KEY (id);").InvalidatesBinding);
        Assert.False(ParseAlter("ALTER TABLE t ADD COLUMN x int;").InvalidatesBinding);
        Assert.False(ParseAlter("ALTER TABLE t ALTER COLUMN x TYPE text;").InvalidatesBinding);
        Assert.False(ParseAlter("ALTER TABLE t OWNER TO someone;").InvalidatesBinding);
        Assert.True(ParseAlter("ALTER TABLE t RENAME COLUMN a TO b;").InvalidatesBinding);
        Assert.True(ParseAlter("ALTER TABLE t SET SCHEMA other;").InvalidatesBinding);
        Assert.True(ParseAlter("ALTER TABLE t INHERIT parent;").InvalidatesBinding);
    }

    [Fact]
    public void Catalog_folds_add_drop_and_retype()
    {
        var parsed = new PgParser().Parse(@"
            CREATE TABLE public.t (id int, old_col text);
            ALTER TABLE public.t ADD COLUMN email text;
            ALTER TABLE public.t DROP COLUMN old_col;
            ALTER TABLE public.t ALTER COLUMN id TYPE bigint;");
        var catalog = CatalogBuilder.Build(parsed);
        parsed.ReleaseTokens();

        var cols = catalog.ColumnsWithTypes("public", "t")!;
        Assert.Contains(cols, c => c.Name == "email");
        Assert.DoesNotContain(cols, c => c.Name == "old_col");
        Assert.Equal("bigint", cols.Single(c => c.Name == "id").Type);
    }
}
