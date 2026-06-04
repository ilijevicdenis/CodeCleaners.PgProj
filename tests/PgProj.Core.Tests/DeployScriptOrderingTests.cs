using System;
using PgProj.Core.Comparison;
using PgProj.Core.Deployment;
using PgProj.Core.Model;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>EP-DEPLOYSCRIPTS: pre/diff/post ordering, transaction wrapping, verbatim pass-through.</summary>
public class DeployScriptOrderingTests
{
    private static (string Script, int Pre, int Diff, int Post) Generate(DeployOptions opts)
    {
        var source = TestModel.Build("CREATE TABLE app.t (id int PRIMARY KEY);");
        var changes = new SchemaComparer().Compare(source, new DatabaseModel());
        var script = new DeployScriptGenerator().Generate(changes, opts);
        return (script,
            script.IndexOf("__PRE__", StringComparison.Ordinal),
            script.IndexOf("CREATE TABLE", StringComparison.Ordinal),
            script.IndexOf("__POST__", StringComparison.Ordinal));
    }

    [Fact]
    public void Orders_pre_then_diff_then_post()
    {
        var (_, pre, diff, post) = Generate(new DeployOptions
        {
            Scripts = new DeployScriptBundle(
                new DeployScript("PreDeploy.sql", "SELECT '__PRE__';"),
                new DeployScript("PostDeploy.sql", "SELECT '__POST__';")),
        });

        Assert.True(pre >= 0 && diff >= 0 && post >= 0, "all three sections present");
        Assert.True(pre < diff, "pre-deploy must precede the schema diff");
        Assert.True(diff < post, "schema diff must precede post-deploy");
    }

    [Fact]
    public void All_three_sections_live_inside_one_transaction()
    {
        var (script, pre, _, post) = Generate(new DeployOptions
        {
            WrapInTransaction = true,
            Scripts = new DeployScriptBundle(
                new DeployScript("PreDeploy.sql", "SELECT '__PRE__';"),
                new DeployScript("PostDeploy.sql", "SELECT '__POST__';")),
        });

        var begin = script.IndexOf("BEGIN;", StringComparison.Ordinal);
        var commit = script.IndexOf("COMMIT;", StringComparison.Ordinal);
        Assert.True(begin >= 0 && commit >= 0);
        Assert.True(begin < pre && post < commit, "pre and post must sit between BEGIN and COMMIT");
        // Exactly one transaction wrapper (no nested BEGIN per script).
        Assert.Equal(begin, script.LastIndexOf("BEGIN;", StringComparison.Ordinal));
        Assert.Equal(commit, script.LastIndexOf("COMMIT;", StringComparison.Ordinal));
    }

    [Fact]
    public void No_transaction_wrapper_when_disabled()
    {
        var (script, _, _, _) = Generate(new DeployOptions
        {
            WrapInTransaction = false,
            Scripts = new DeployScriptBundle(Post: new DeployScript("PostDeploy.sql", "SELECT '__POST__';")),
        });
        Assert.DoesNotContain("BEGIN;", script);
        Assert.Contains("__POST__", script);
    }

    [Fact]
    public void Preserves_dollar_quoted_body_and_embedded_semicolons()
    {
        // A function body with embedded semicolons inside $$...$$ must pass through verbatim — no
        // statement-splitting, no reformatting.
        const string body =
            "CREATE FUNCTION app.seed() RETURNS void AS $func$\n" +
            "BEGIN\n" +
            "  INSERT INTO app.t VALUES (1); INSERT INTO app.t VALUES (2);\n" +
            "  RAISE NOTICE 'done; ok';\n" +
            "END;\n" +
            "$func$ LANGUAGE plpgsql;";

        var source = TestModel.Build("CREATE TABLE app.t (id int PRIMARY KEY);");
        var changes = new SchemaComparer().Compare(source, new DatabaseModel());
        var script = new DeployScriptGenerator().Generate(changes, new DeployOptions
        {
            Scripts = new DeployScriptBundle(Post: new DeployScript("PostDeploy.sql", body)),
        });

        Assert.Contains(body, script);   // exact, byte-for-byte
        Assert.Contains("$func$", script);
        Assert.Contains("RAISE NOTICE 'done; ok'", script);
    }

    [Fact]
    public void Deploy_scripts_run_even_with_an_empty_diff()
    {
        var script = new DeployScriptGenerator().Generate(Array.Empty<SchemaChange>(), new DeployOptions
        {
            Scripts = new DeployScriptBundle(Post: new DeployScript("PostDeploy.sql", "SELECT '__POST__';")),
        });
        Assert.Contains("__POST__", script);
        Assert.DoesNotContain("No changes", script);
    }

    [Fact]
    public void Header_names_the_deploy_scripts()
    {
        var script = new DeployScriptGenerator().Generate(Array.Empty<SchemaChange>(), new DeployOptions
        {
            IncludeHeader = true,
            Scripts = new DeployScriptBundle(
                new DeployScript("PreDeploy.sql", "SELECT 1;"),
                new DeployScript("PostDeploy.sql", "SELECT 2;")),
        });
        Assert.Contains("pre-deploy:  PreDeploy.sql", script);
        Assert.Contains("post-deploy: PostDeploy.sql", script);
    }
}
