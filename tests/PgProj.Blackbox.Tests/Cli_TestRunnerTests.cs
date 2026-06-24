using PgProj.Blackbox.Tests.Infrastructure;

namespace PgProj.Blackbox.Tests;

/// <summary>
/// Blackbox for `pgproj test --deploy`: it builds the project, deploys it into a throwaway shadow
/// database, runs the *.test.sql PL/pgSQL unit tests there, and drops the shadow DB. Passing tests
/// exit 0; a failing assertion makes the run exit 10 (TestFailed).
/// </summary>
public sealed class Cli_TestRunnerTests(BlackboxFixture fx) : BlackboxTestBase(fx)
{
    private static TempProject WithTable(string name)
    {
        var p = TempProject.Create(name);
        p.AddSql("Schemas/app.sql", "CREATE SCHEMA app;");
        p.AddSql("Tables/app.widget.sql", "CREATE TABLE app.widget (id integer PRIMARY KEY, name text NOT NULL);");
        return p;
    }

    [LiveFact]
    public void Deploy_and_run_passing_tests_exits_zero()
    {

        using var proj = WithTable("TestPass");
        proj.AddSql("tests/rows.test.sql",
            "INSERT INTO app.widget (id, name) VALUES (1, 'x');\n" +
            "SELECT pgproj_assert_rowcount('SELECT * FROM app.widget', 1);");

        var r = Run($"test {Q(proj.ProjectFile)} --connection {Q(Fx.TargetAdmin!)} --deploy");
        Assert.Equal(0, r.ExitCode);
        Assert.True(r.Mentions("PASS"), r.ToString());
    }

    [LiveFact]
    public void A_failing_assertion_makes_the_run_exit_ten()
    {

        using var proj = WithTable("TestFail");
        proj.AddSql("tests/bad.test.sql", "SELECT pgproj_assert(1 = 2, 'deliberately false');");

        var r = Run($"test {Q(proj.ProjectFile)} --connection {Q(Fx.TargetAdmin!)} --deploy");
        Assert.Equal(10, r.ExitCode); // TestFailed
        Assert.True(r.Mentions("FAIL"), r.ToString());
    }

    [LiveFact]
    public void A_negative_test_passes_when_the_expected_sqlstate_is_raised()
    {

        using var proj = WithTable("TestNegative");
        // Unique-violation is 23505: inserting the same PK twice must raise exactly that.
        proj.AddSql("tests/dup.test.sql",
            "-- @expect-sqlstate: 23505\n" +
            "INSERT INTO app.widget (id, name) VALUES (1, 'a');\n" +
            "INSERT INTO app.widget (id, name) VALUES (1, 'b');");

        var r = Run($"test {Q(proj.ProjectFile)} --connection {Q(Fx.TargetAdmin!)} --deploy");
        Assert.Equal(0, r.ExitCode);
        Assert.True(r.Mentions("PASS"), r.ToString());
    }
}
