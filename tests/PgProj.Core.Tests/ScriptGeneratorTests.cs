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
        var source = TestModel.Build("CREATE TABLE app.t (id int PRIMARY KEY, name text NOT NULL);");
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
        var source = TestModel.Build("CREATE TABLE app.t (id int);");
        var changes = new SchemaComparer().Compare(source, new DatabaseModel());
        var script = new DeployScriptGenerator().Generate(changes, new DeployOptions { WrapInTransaction = false });
        Assert.DoesNotContain("BEGIN;", script);
    }

    [Fact]
    public void Statements_are_ordered_by_phase()
    {
        // schema (10) must come before table (40) which comes before foreign key (70).
        var source = TestModel.Build("""
            CREATE TABLE app.parent (id int PRIMARY KEY);
            CREATE TABLE app.child (id int, pid int REFERENCES app.parent (id));
            """);
        var script = new DeployScriptGenerator().Generate(new SchemaComparer().Compare(source, new DatabaseModel()));

        var schemaPos = script.IndexOf("CREATE SCHEMA", System.StringComparison.Ordinal);
        var tablePos = script.IndexOf("CREATE TABLE", System.StringComparison.Ordinal);
        var fkPos = script.IndexOf("FOREIGN KEY", System.StringComparison.Ordinal);

        Assert.True(schemaPos < tablePos && tablePos < fkPos);
    }

    [Fact]
    public void Index_on_a_materialized_view_is_created_after_the_view()
    {
        // Regression: an index ON a matview was emitted at the table-index phase (65), before the
        // matview (created with views at 75) existed → 42P01 on deploy. It must now follow the view.
        var source = TestModel.Build("""
            CREATE MATERIALIZED VIEW app.mv AS SELECT 1 AS x;
            CREATE UNIQUE INDEX mv_pk ON app.mv (x);
            """);
        var script = new DeployScriptGenerator().Generate(new SchemaComparer().Compare(source, new DatabaseModel()));

        var mvPos = script.IndexOf("CREATE MATERIALIZED VIEW", System.StringComparison.Ordinal);
        var idxPos = script.IndexOf("CREATE UNIQUE INDEX", System.StringComparison.Ordinal);

        Assert.True(mvPos >= 0 && idxPos >= 0, "expected both the matview and its index in the script");
        Assert.True(mvPos < idxPos, "the materialized view must be created before the index on it");
    }

    [Fact]
    public void Index_on_a_plain_table_still_precedes_views()
    {
        // The matview fix must not push ordinary table indexes (phase 65) past views (75).
        var source = TestModel.Build("""
            CREATE TABLE app.t (id int, name text);
            CREATE INDEX t_name ON app.t (name);
            CREATE VIEW app.v AS SELECT id FROM app.t;
            """);
        var script = new DeployScriptGenerator().Generate(new SchemaComparer().Compare(source, new DatabaseModel()));

        var idxPos = script.IndexOf("CREATE INDEX", System.StringComparison.Ordinal);
        var viewPos = script.IndexOf("CREATE OR REPLACE VIEW", System.StringComparison.Ordinal);
        if (viewPos < 0) viewPos = script.IndexOf("CREATE VIEW", System.StringComparison.Ordinal);

        Assert.True(idxPos >= 0 && viewPos >= 0 && idxPos < viewPos, "a table index should still come before views");
    }
}
