using System.Linq;
using PgProj.Core.Comparison;
using PgProj.Core.Model;
using PgProj.Core.Parsing;
using Xunit;

namespace PgProj.Core.Tests;

public class ComparerTests
{
    private static DatabaseModel Parse(string sql) => new SqlParser().Parse(sql);
    private static readonly SchemaComparer Comparer = new();

    [Fact]
    public void New_table_produces_create_table()
    {
        var source = Parse("CREATE TABLE app.t (id int PRIMARY KEY, name text);");
        var changes = Comparer.Compare(source, new DatabaseModel());

        Assert.Contains(changes, c => c is CreateSchemaChange { Schema: "app" });
        Assert.Contains(changes, c => c is CreateTableChange ct && ct.Table.Name == "t");
    }

    [Fact]
    public void Missing_column_produces_add_column()
    {
        var source = Parse("CREATE TABLE app.t (id int, name text);");
        var target = Parse("CREATE TABLE app.t (id int);");

        var add = Assert.Single(Comparer.Compare(source, target).OfType<AddColumnChange>());
        Assert.Equal("name", add.Column.Name);
    }

    [Fact]
    public void Changed_type_produces_alter_column_with_type_clause()
    {
        var source = Parse("CREATE TABLE app.t (id bigint);");
        var target = Parse("CREATE TABLE app.t (id int);");

        var alter = Assert.Single(Comparer.Compare(source, target).OfType<AlterColumnChange>());
        Assert.Contains("TYPE bigint", alter.ToSql());
    }

    [Fact]
    public void Changed_nullability_produces_set_not_null()
    {
        var source = Parse("CREATE TABLE app.t (id int NOT NULL);");
        var target = Parse("CREATE TABLE app.t (id int);");

        var alter = Assert.Single(Comparer.Compare(source, target).OfType<AlterColumnChange>());
        Assert.Contains("SET NOT NULL", alter.ToSql());
    }

    [Fact]
    public void Extra_column_is_left_alone_unless_drops_allowed()
    {
        var source = Parse("CREATE TABLE app.t (id int);");
        var target = Parse("CREATE TABLE app.t (id int, legacy text);");

        Assert.Empty(Comparer.Compare(source, target).OfType<DropColumnChange>());

        var drop = Assert.Single(Comparer
            .Compare(source, target, new ComparerOptions { DropObjectsNotInSource = true })
            .OfType<DropColumnChange>());
        Assert.Equal("legacy", drop.Column);
    }

    [Fact]
    public void Identical_models_produce_no_changes()
    {
        const string sql = "CREATE TABLE app.t (id int PRIMARY KEY, name text NOT NULL DEFAULT 'x');";
        Assert.Empty(Comparer.Compare(Parse(sql), Parse(sql)));
    }

    [Fact]
    public void New_foreign_key_is_added_after_tables()
    {
        var source = Parse("""
            CREATE TABLE app.parent (id int PRIMARY KEY);
            CREATE TABLE app.child (id int PRIMARY KEY, pid int REFERENCES app.parent (id));
            """);
        var changes = Comparer.Compare(source, new DatabaseModel());

        var fkIndex = changes.FindIndex(c => c is AddForeignKeyChange);
        var lastTableIndex = changes.FindLastIndex(c => c is CreateTableChange);
        Assert.True(fkIndex > lastTableIndex, "foreign keys must be added after all tables are created");
    }
}

file static class ListExtensions
{
    public static int FindIndex(this System.Collections.Generic.IReadOnlyList<SchemaChange> list, System.Func<SchemaChange, bool> pred)
    {
        for (var i = 0; i < list.Count; i++) if (pred(list[i])) return i;
        return -1;
    }

    public static int FindLastIndex(this System.Collections.Generic.IReadOnlyList<SchemaChange> list, System.Func<SchemaChange, bool> pred)
    {
        for (var i = list.Count - 1; i >= 0; i--) if (pred(list[i])) return i;
        return -1;
    }
}
