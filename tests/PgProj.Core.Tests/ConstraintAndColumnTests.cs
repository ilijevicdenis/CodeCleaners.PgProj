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
