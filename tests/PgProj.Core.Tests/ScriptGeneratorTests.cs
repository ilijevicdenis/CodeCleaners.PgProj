using System.Linq;
using PgProj.Core.Comparison;
using PgProj.Core.Model;
using PgProj.Core.Parsing;
using Xunit;

namespace PgProj.Core.Tests;

public class ScriptGeneratorTests
{
    [Fact]
    public void Wraps_in_transaction_and_emits_create_table()
    {
        var source = new SqlParser().Parse("CREATE TABLE app.t (id int PRIMARY KEY, name text NOT NULL);");
        var changes = new SchemaComparer().Compare(source, new DatabaseModel());
        var script = new DeployScriptGenerator().Generate(changes);

        Assert.StartsWith("--", script.TrimStart());
        Assert.Contains("BEGIN;", script);
        Assert.Contains("COMMIT;", script);
        Assert.Contains("CREATE SCHEMA IF NOT EXISTS \"app\";", script);
        Assert.Contains("CREATE TABLE \"app\".\"t\"", script);
        Assert.Contains("\"name\" text NOT NULL", script);
        Assert.Contains("PRIMARY KEY (\"id\")", script);
    }

    [Fact]
    public void Empty_diff_reports_no_changes()
    {
        var script = new DeployScriptGenerator().Generate(System.Array.Empty<SchemaChange>());
        Assert.Contains("No changes", script);
    }

    [Fact]
    public void Can_disable_transaction_wrapper()
    {
        var source = new SqlParser().Parse("CREATE TABLE app.t (id int);");
        var changes = new SchemaComparer().Compare(source, new DatabaseModel());
        var script = new DeployScriptGenerator().Generate(changes, new DeployOptions { WrapInTransaction = false });
        Assert.DoesNotContain("BEGIN;", script);
    }

    [Fact]
    public void Statements_are_ordered_by_phase()
    {
        // schema (10) must come before table (40) which comes before foreign key (70).
        var source = new SqlParser().Parse("""
            CREATE TABLE app.parent (id int PRIMARY KEY);
            CREATE TABLE app.child (id int, pid int REFERENCES app.parent (id));
            """);
        var script = new DeployScriptGenerator().Generate(new SchemaComparer().Compare(source, new DatabaseModel()));

        var schemaPos = script.IndexOf("CREATE SCHEMA", System.StringComparison.Ordinal);
        var tablePos = script.IndexOf("CREATE TABLE", System.StringComparison.Ordinal);
        var fkPos = script.IndexOf("FOREIGN KEY", System.StringComparison.Ordinal);

        Assert.True(schemaPos < tablePos && tablePos < fkPos);
    }
}
