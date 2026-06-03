using System.Linq;
using PgProj.Core.Comparison;
using PgProj.Core.Model;
using PgProj.Core.Parsing;
using Xunit;

namespace PgProj.Core.Tests;

public class RawObjectTests
{
    private static DatabaseModel Parse(string sql) => new SqlParser().Parse(sql);

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
        var source = Parse("CREATE TYPE app.tier AS ENUM ('a','b');");
        var target = Parse("CREATE TYPE app.tier AS ENUM ('a');");

        // Destructive recreate suppressed by default...
        Assert.Empty(new SchemaComparer().Compare(source, target).OfType<RecreateRawObjectChange>());
        // ...allowed when drops are permitted.
        Assert.Single(new SchemaComparer()
            .Compare(source, target, new ComparerOptions { DropObjectsNotInSource = true })
            .OfType<RecreateRawObjectChange>());
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
