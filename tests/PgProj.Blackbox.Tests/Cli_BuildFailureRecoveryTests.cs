using PgProj.Blackbox.Tests.Infrastructure;

namespace PgProj.Blackbox.Tests;

/// <summary>
/// Blackbox failure-then-recovery for the static gates that run BEFORE any database is touched:
/// parse/build (exit 3), the analysis gate (exit 4), the target-version gate (exit 4), and reference
/// resolution (exit 5). Each test triggers the failure, then applies the documented recovery and
/// shows it clears. No DB needed — these never reach a server.
/// </summary>
public sealed class Cli_BuildFailureRecoveryTests(BlackboxFixture fx) : BlackboxTestBase(fx)
{

    [CliFact]
    public void Syntax_error_fails_build_then_fixing_it_recovers()
    {

        using var proj = TempProject.Create("SyntaxErr");
        proj.AddSql("Schemas/app.sql", "CREATE SCHEMA app;");
        var bad = proj.AddSql("Tables/app.t.sql", "CREATE TABLE app.t (id integer PRIMARY KEY,);"); // trailing comma

        var fail = Run($"build {Q(proj.ProjectFile)}");
        Assert.Equal(3, fail.ExitCode); // BuildError

        // Recovery: fix the SQL.
        File.WriteAllText(bad, "CREATE TABLE app.t (id integer PRIMARY KEY);");
        var ok = Run($"build {Q(proj.ProjectFile)}");
        Assert.Equal(0, ok.ExitCode);
    }

    [CliFact]
    public void Duplicate_object_definition_fails_build_then_removing_it_recovers()
    {
        using var proj = TempProject.Create("DupObject");
        proj.AddSql("Schemas/app.sql", "CREATE SCHEMA app;");
        proj.AddSql("Tables/app.t1.sql", "CREATE TABLE app.dup (id integer PRIMARY KEY);");
        var dupe = proj.AddSql("Tables/app.t2.sql", "CREATE TABLE app.dup (id integer PRIMARY KEY);"); // same object twice

        var blocked = Run($"build {Q(proj.ProjectFile)}");
        Assert.Equal(3, blocked.ExitCode); // BuildError — duplicate definition
        Assert.True(blocked.Mentions("Duplicate"), blocked.ToString());

        // Recovery: remove the second definition.
        File.Delete(dupe);
        Assert.Equal(0, Run($"build {Q(proj.ProjectFile)}").ExitCode);
    }

    [CliFact]
    public void Analysis_gate_blocks_at_error_severity_then_no_analyze_bypasses_it()
    {
        using var proj = TempProject.Create("AnalysisGate");
        proj.AddSql("Schemas/app.sql", "CREATE SCHEMA app;");
        // A table with no PRIMARY KEY trips PG006 (info by default). Promote it to error so the gate fires.
        proj.AddSql("Tables/app.nopk.sql", "CREATE TABLE app.nopk (id integer, name text);");

        var blocked = Run($"build {Q(proj.ProjectFile)} --rule PG006=error");
        Assert.Equal(4, blocked.ExitCode); // AnalysisBlocked
        Assert.True(blocked.Mentions("PG006"), blocked.ToString());

        // Recovery option A: skip analysis entirely.
        Assert.Equal(0, Run($"build {Q(proj.ProjectFile)} --rule PG006=error --no-analyze").ExitCode);
        // Recovery option B: don't promote the rule (default severity is non-blocking).
        Assert.Equal(0, Run($"build {Q(proj.ProjectFile)}").ExitCode);
    }

    [CliFact]
    public void Target_version_gate_blocks_newer_syntax_then_raising_the_target_recovers()
    {

        // UNIQUE ... NULLS NOT DISTINCT requires PostgreSQL 15.
        const string sql =
            "CREATE TABLE app.t (id integer PRIMARY KEY, code text, " +
            "CONSTRAINT uq_code UNIQUE NULLS NOT DISTINCT (code));";

        using var v14 = TempProject.Create("PgvGate", targetVersion: 14);
        v14.AddSql("Schemas/app.sql", "CREATE SCHEMA app;");
        v14.AddSql("Tables/app.t.sql", sql);
        var blocked = Run($"build {Q(v14.ProjectFile)}");
        Assert.Equal(4, blocked.ExitCode); // AnalysisBlocked (PGV###)

        // Recovery: declare a target version that supports the syntax.
        using var v18 = TempProject.Create("PgvOk", targetVersion: 18);
        v18.AddSql("Schemas/app.sql", "CREATE SCHEMA app;");
        v18.AddSql("Tables/app.t.sql", sql);
        var ok = Run($"build {Q(v18.ProjectFile)}");
        Assert.Equal(0, ok.ExitCode);
    }

    [CliFact]
    public void Missing_project_reference_fails_then_removing_it_recovers()
    {

        var extra =
            """
              <ItemGroup>
                <ProjectReference Include="../Nope/Nope.pgproj" />
              </ItemGroup>
            """;
        using var proj = TempProject.Create("BadRef", extraItemGroup: extra);
        proj.AddSql("Schemas/app.sql", "CREATE SCHEMA app;");
        proj.AddSql("Tables/app.t.sql", "CREATE TABLE app.t (id integer PRIMARY KEY);");

        var fail = Run($"build {Q(proj.ProjectFile)}");
        Assert.Equal(5, fail.ExitCode); // ReferenceError

        // Recovery: drop the dangling reference from the manifest.
        var clean =
            """
            <Project>
              <PropertyGroup>
                <Name>BadRef</Name>
                <DefaultSchema>app</DefaultSchema>
                <TargetPostgresVersion>18</TargetPostgresVersion>
              </PropertyGroup>
              <ItemGroup>
                <Build Include="**/*.sql" />
              </ItemGroup>
            </Project>
            """;
        File.WriteAllText(proj.ProjectFile, clean);
        var ok = Run($"build {Q(proj.ProjectFile)}");
        Assert.Equal(0, ok.ExitCode);
    }

    [CliFact]
    public void Missing_project_file_is_a_usage_error()
    {

        var r = Run($"build {Q(Path.Combine(Path.GetTempPath(), "does-not-exist.pgproj"))}");
        Assert.NotEqual(0, r.ExitCode);
    }
}
