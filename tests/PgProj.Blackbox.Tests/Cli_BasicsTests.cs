using PgProj.Blackbox.Tests.Infrastructure;

namespace PgProj.Blackbox.Tests;

/// <summary>
/// Blackbox: invocation surface, scaffolding, and the build artifacts — no database required for most
/// of these (they exercise the CLI as a pure binary). Run regardless of DB availability except where
/// noted, but still gated on the CLI being built.
/// </summary>
public sealed class Cli_BasicsTests(BlackboxFixture fx) : BlackboxTestBase(fx)
{
    [CliFact]
    public void No_args_prints_usage_and_exits_usage_code()
    {

        var r = Run("");
        Assert.Equal(2, r.ExitCode);
        Assert.True(r.Mentions("Usage"), r.ToString());
    }

    [CliFact]
    public void Help_exits_zero()
    {

        var r = Run("help");
        Assert.Equal(0, r.ExitCode);
        Assert.True(r.Mentions("pgproj"), r.ToString());
    }

    [CliFact]
    public void Unknown_command_is_usage_error()
    {

        var r = Run("frobnicate");
        Assert.Equal(2, r.ExitCode);
        Assert.True(r.Mentions("Unknown command"), r.ToString());
    }

    [CliFact]
    public void New_project_scaffolds_a_buildable_project()
    {

        var root = Path.Combine(Path.GetTempPath(), "pgproj-bb", "new_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            var created = Run($"new project Acme -o {Q(root)} --default-schema app --target-version 18");
            Assert.Equal(0, created.ExitCode);
            var projFile = Path.Combine(root, "Acme", "Acme.pgproj");
            Assert.True(File.Exists(projFile), created.ToString());

            // Scaffolded-then-built: add one object via the CLI, then build it.
            var added = Run($"add table app.Widget -p {Q(projFile)}");
            Assert.Equal(0, added.ExitCode);
            var built = Run($"build {Q(projFile)}");
            Assert.Equal(0, built.ExitCode);
            Assert.True(built.Mentions("Build succeeded"), built.ToString());
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [CliFact]
    public void Build_emits_model_and_package()
    {

        using var proj = TempProject.Create("BuildArtifacts");
        proj.AddSql("Schemas/app.sql", "CREATE SCHEMA app;");
        proj.AddSql("Tables/app.widget.sql",
            "CREATE TABLE app.widget (id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY, name text NOT NULL);");

        var r = Run($"build {Q(proj.ProjectFile)}");
        Assert.Equal(0, r.ExitCode);
        Assert.True(File.Exists(Path.Combine(proj.Dir, "bin", "BuildArtifacts.model.json")), r.ToString());
        Assert.True(File.Exists(Path.Combine(proj.Dir, "bin", "BuildArtifacts.pgpkg")), r.ToString());
    }

    [CliFact]
    public void Build_format_json_emits_json_contract()
    {

        using var proj = TempProject.Create("BuildJson");
        proj.AddSql("Schemas/app.sql", "CREATE SCHEMA app;");
        proj.AddSql("Tables/app.t.sql", "CREATE TABLE app.t (id integer PRIMARY KEY);");

        var r = Run($"build {Q(proj.ProjectFile)} --format json");
        Assert.Equal(0, r.ExitCode);
        var trimmed = r.StdOut.TrimStart();
        Assert.True(trimmed.StartsWith('{') || trimmed.StartsWith('['), $"expected JSON on stdout, got:\n{r}");
    }

    [CliFact]
    public void Script_writes_a_create_script_without_a_database()
    {

        using var proj = TempProject.Create("ScriptOnly");
        proj.AddSql("Schemas/app.sql", "CREATE SCHEMA app;");
        proj.AddSql("Tables/app.t.sql", "CREATE TABLE app.t (id integer PRIMARY KEY, name text);");

        var outSql = Path.Combine(proj.Dir, "bin", "_create.sql");
        var r = Run($"script {Q(proj.ProjectFile)} -o {Q(outSql)}");
        Assert.Equal(0, r.ExitCode);
        Assert.True(File.Exists(outSql), r.ToString());
        var sql = File.ReadAllText(outSql);
        Assert.Contains("CREATE TABLE", sql, StringComparison.OrdinalIgnoreCase);
    }
}
