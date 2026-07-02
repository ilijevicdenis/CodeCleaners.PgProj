using System.Linq;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Standalone ALTER TABLE actions must reach the DatabaseModel (P0 audit finding, 2026-07-02): before
/// this, everything except ADD CONSTRAINT (#153) was parsed, validated, then silently DISCARDED — a
/// column added or retyped via ALTER was invisible to the comparer, deploys, and the test generator.
/// Same-file CREATE-before-ALTER (the extractor's shape); a cross-file ALTER stays best-effort.
/// </summary>
public class AlterTableFoldTests
{
    [Fact]
    public void Add_column_reaches_the_model_with_its_inline_constraints()
    {
        var t = TestModel.Build(@"
            CREATE TABLE public.t (id int PRIMARY KEY);
            ALTER TABLE public.t ADD COLUMN email text NOT NULL DEFAULT 'x' UNIQUE;
            ALTER TABLE public.t ADD flags int;").Tables.Single();

        var email = t.Columns.Single(c => c.Name == "email");
        Assert.False(email.IsNullable);
        Assert.Equal("'x'", email.Default);
        Assert.Contains(t.Unique, u => u.Columns.SequenceEqual(new[] { "email" }));

        // the COLUMN keyword is optional — both forms fold
        Assert.Contains(t.Columns, c => c.Name == "flags");
    }

    [Fact]
    public void Drop_column_removes_the_column_and_its_constraints()
    {
        var t = TestModel.Build(@"
            CREATE TABLE public.t (
                id int PRIMARY KEY,
                ref_id int REFERENCES public.t(id),
                code text UNIQUE);
            ALTER TABLE public.t DROP COLUMN ref_id, DROP COLUMN id;").Tables.Single();

        Assert.Equal(new[] { "code" }, t.Columns.Select(c => c.Name));
        Assert.Null(t.PrimaryKey);                                 // pk was on the dropped id
        Assert.Empty(t.ForeignKeys);                               // fk was on the dropped ref_id
        Assert.Single(t.Unique);                                   // code's unique survives
    }

    [Fact]
    public void Alter_column_actions_mutate_the_model_column()
    {
        var t = TestModel.Build(@"
            CREATE TABLE public.t (a varchar(10), b int NOT NULL, c int DEFAULT 5);
            ALTER TABLE public.t
                ALTER COLUMN a TYPE text,
                ALTER COLUMN a SET NOT NULL,
                ALTER COLUMN b DROP NOT NULL,
                ALTER COLUMN b SET DEFAULT 42,
                ALTER COLUMN c DROP DEFAULT;").Tables.Single();

        var a = t.Columns.Single(x => x.Name == "a");
        Assert.Equal("text", a.DataType);
        Assert.False(a.IsNullable);

        var b = t.Columns.Single(x => x.Name == "b");
        Assert.True(b.IsNullable);
        Assert.Equal("42", b.Default);

        Assert.Null(t.Columns.Single(x => x.Name == "c").Default);
    }

    [Fact]
    public void Cross_file_alter_without_the_table_stays_best_effort()
    {
        // The target table is not in this parse unit — must not throw, must not invent a table.
        var model = TestModel.Build("ALTER TABLE public.elsewhere ADD COLUMN x int;");
        Assert.Empty(model.Tables);
    }

    [Fact]
    public void Non_structural_alter_actions_still_pass_through_unfolded()
    {
        // OWNER/SET options carry no model shape — the table must be untouched (and unchanged in count).
        var t = TestModel.Build(@"
            CREATE TABLE public.t (id int);
            ALTER TABLE public.t OWNER TO someone;
            ALTER TABLE public.t SET (fillfactor = 70);").Tables.Single();
        Assert.Single(t.Columns);
    }
}
