using System.Linq;
using PgProj.Core.Comparison;
using PgProj.Core.Model;
using PgProj.Core.Parsing;
using Xunit;

namespace PgProj.Core.Tests;

public class ConstraintAndColumnTests
{
    private static DatabaseModel Parse(string sql) => TestModel.Build(sql);

    [Fact]
    public void Alter_table_add_foreign_key_is_folded_into_the_table_model()
    {
        // #153 follow-up: `pgproj extract` (and many real projects) emit FKs as a standalone ALTER TABLE
        // ADD CONSTRAINT, not inline. ModelBuilder must fold it into TableDefinition.ForeignKeys so the
        // comparer/deploy and the test-suite generator see it.
        var child = Parse(
            "CREATE TABLE app.parent (id int PRIMARY KEY);\n" +
            "CREATE TABLE app.child (id int PRIMARY KEY, parent_id int NOT NULL);\n" +
            "ALTER TABLE app.child ADD CONSTRAINT child_parent_fk FOREIGN KEY (parent_id) REFERENCES app.parent (id);")
            .FindTable("app", "child")!;
        var fk = Assert.Single(child.ForeignKeys);
        Assert.Equal("child_parent_fk", fk.Name);
        Assert.Equal(new[] { "parent_id" }, fk.Columns);
        Assert.Equal("parent", fk.ReferencedTable);
        Assert.Equal(new[] { "id" }, fk.ReferencedColumns);
    }

    [Fact]
    public void Alter_table_add_primary_key_unique_and_check_are_folded_in()
    {
        var t = Parse(
            "CREATE TABLE app.t (id int, email text, age int);\n" +
            "ALTER TABLE app.t ADD CONSTRAINT t_pk PRIMARY KEY (id);\n" +
            "ALTER TABLE app.t ADD CONSTRAINT t_email_uq UNIQUE (email);\n" +
            "ALTER TABLE app.t ADD CONSTRAINT t_age_ck CHECK (age >= 0);").FindTable("app", "t")!;
        Assert.Equal(new[] { "id" }, t.PrimaryKey!.Columns);
        Assert.Equal("t_email_uq", Assert.Single(t.Unique).Name);
        Assert.Equal("t_age_ck", Assert.Single(t.Checks).Name);
    }

    [Fact]
    public void Column_check_is_captured()
    {
        var t = Parse("CREATE TABLE app.t (qty int CHECK (qty > 0));").FindTable("app", "t")!;
        var check = Assert.Single(t.Checks);
        Assert.Contains("qty", check.Expression);
    }

    [Fact]
    public void Named_table_check_is_captured_and_scripted()
    {
        var t = Parse("CREATE TABLE app.t (a int, b int, CONSTRAINT ck_ab CHECK (a < b));").FindTable("app", "t")!;
        var check = Assert.Single(t.Checks);
        Assert.Equal("ck_ab", check.Name);
        Assert.Contains("CONSTRAINT \"ck_ab\" CHECK", SqlEmitter.CreateTable(t));
    }

    [Fact]
    public void Generated_column_expression_is_retained_and_emitted()
    {
        var t = Parse("CREATE TABLE app.t (price numeric, tax numeric GENERATED ALWAYS AS (price * 0.2) STORED);")
            .FindTable("app", "t")!;
        var tax = t.FindColumn("tax")!;
        Assert.False(string.IsNullOrEmpty(tax.GeneratedExpression));
        Assert.Contains("price", tax.GeneratedExpression!);
        var rendered = SqlEmitter.Column(tax);
        Assert.Contains("GENERATED ALWAYS AS", rendered);
        Assert.Contains("STORED", rendered);
        Assert.Contains("price", rendered);
    }

    [Fact]
    public void Identity_kind_is_preserved()
    {
        var t = Parse("CREATE TABLE app.t (id int GENERATED ALWAYS AS IDENTITY);").FindTable("app", "t")!;
        var id = t.FindColumn("id")!;
        Assert.True(id.IsIdentity);
        Assert.Equal("ALWAYS", id.IdentityKind);
        Assert.Contains("GENERATED ALWAYS AS IDENTITY", SqlEmitter.Column(id));
    }

    [Fact]
    public void Exclude_constraint_is_captured_verbatim()
    {
        const string sql = "CREATE TABLE app.room (id int, during tsrange, EXCLUDE USING gist (during WITH &&));";
        var t = Parse(sql).FindTable("app", "room")!;
        var ex = Assert.Single(t.OtherConstraints);
        Assert.Contains("EXCLUDE USING gist", ex);
        Assert.Contains("EXCLUDE USING gist", SqlEmitter.CreateTable(t));
    }

    [Fact]
    public void Adding_a_check_to_existing_table_emits_alter_add()
    {
        var source = Parse("CREATE TABLE app.t (qty int CHECK (qty >= 0));");
        var target = Parse("CREATE TABLE app.t (qty int);");
        var add = Assert.Single(new SchemaComparer().Compare(source, target).OfType<AddCheckConstraintChange>());
        Assert.Contains("ADD CHECK", add.ToSql());
    }

    [Fact]
    public void Changed_generated_expression_triggers_alter_column()
    {
        var source = Parse("CREATE TABLE app.t (a int, b int GENERATED ALWAYS AS (a + 1) STORED);");
        var target = Parse("CREATE TABLE app.t (a int, b int GENERATED ALWAYS AS (a + 2) STORED);");
        Assert.Single(new SchemaComparer().Compare(source, target).OfType<AlterColumnChange>());
    }
}
