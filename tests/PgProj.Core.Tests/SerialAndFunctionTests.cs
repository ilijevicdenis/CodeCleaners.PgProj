using System.Linq;
using PgProj.Core.Comparison;
using PgProj.Core.Model;
using PgProj.Core.Parsing;
using Xunit;

namespace PgProj.Core.Tests;

public class SerialAndFunctionTests
{
    private static DatabaseModel Parse(string sql) => new SqlParser().Parse(sql);

    [Fact]
    public void Serial_column_is_flagged_not_null_and_scripts_as_serial()
    {
        var t = Parse("CREATE TABLE app.t (id serial PRIMARY KEY, big bigserial);").FindTable("app", "t")!;
        var id = t.FindColumn("id")!;
        Assert.True(id.IsSerial);
        Assert.False(id.IsNullable);
        Assert.Equal("integer", id.DataType);

        var ddl = SqlEmitter.CreateTable(t);
        Assert.Contains("\"id\" serial", ddl);
        Assert.Contains("\"big\" bigserial", ddl);
    }

    [Fact]
    public void Serial_matches_introspected_integer_with_nextval_default()
    {
        var source = Parse("CREATE TABLE app.t (id serial);");
        // Simulate what introspection produces for a serial column.
        var target = new DatabaseModel();
        var tbl = new TableDefinition { Schema = "app", Name = "t" };
        tbl.Columns.Add(new ColumnDefinition("id", "integer", IsNullable: false, IsSerial: true));
        target.Tables.Add(tbl);
        target.Schemas.Add(new SchemaDefinition("app"));

        Assert.Empty(new SchemaComparer().Compare(source, target).OfType<AlterColumnChange>());
    }

    [Fact]
    public void Function_arg_types_are_extracted()
    {
        var f = Parse("CREATE FUNCTION app.add(a integer, b integer DEFAULT 0) RETURNS integer LANGUAGE sql AS $$ SELECT a+b $$;")
            .Functions.Single();
        Assert.Equal("integer, integer", f.ArgTypes);
        Assert.Equal("app.add(integer, integer)", f.Signature);
    }

    [Fact]
    public void Overloads_are_disambiguated_by_arg_types()
    {
        var source = Parse("CREATE FUNCTION app.f(a integer) RETURNS int LANGUAGE sql AS $$ SELECT 1 $$;");
        // Target has two overloads of app.f; only the text(...) one differs in body.
        var target = Parse("""
            CREATE FUNCTION app.f(a integer) RETURNS int LANGUAGE sql AS $$ SELECT 1 $$;
            CREATE FUNCTION app.f(a text) RETURNS int LANGUAGE sql AS $$ SELECT 2 $$;
            """);
        // The integer overload matches exactly -> no change emitted.
        Assert.Empty(new SchemaComparer().Compare(source, target).OfType<CreateOrReplaceFunctionChange>());
    }
}
