using System;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using PgProj.Core.Testing;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Issue #139 — the PL/pgSQL unit-test runner. Live-DB tests cover the predefined conditions, the
/// expected-SQLSTATE (negative) path, the inconclusive signal, and single-transaction-scope isolation
/// (a test's writes never persist). Plus a DB-free check of the directive parser.
/// </summary>
public sealed class PgUnitRunnerTests
{
    private static string? Admin => Environment.GetEnvironmentVariable("PGPROJ_TEST_CONNECTION");

    private static async Task<string> CreateDbAsync()
    {
        var admin = new NpgsqlConnectionStringBuilder(Admin!) { Pooling = false }.ConnectionString;
        var db = "pgproj_unit_" + Guid.NewGuid().ToString("N")[..12];
        await Exec(admin, $"CREATE DATABASE \"{db}\"");
        return new NpgsqlConnectionStringBuilder(Admin!) { Database = db, Pooling = false }.ConnectionString;
    }

    private static async Task DropAsync(string conn)
    {
        NpgsqlConnection.ClearAllPools();
        var db = new NpgsqlConnectionStringBuilder(conn).Database!;
        var admin = new NpgsqlConnectionStringBuilder(Admin!) { Pooling = false }.ConnectionString;
        await Exec(admin, $"DROP DATABASE IF EXISTS \"{db}\" WITH (FORCE)");
    }

    private static async Task Exec(string conn, string sql)
    {
        await using var c = new NpgsqlConnection(conn);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Predefined_conditions_pass_or_fail_as_expected()
    {
        if (string.IsNullOrWhiteSpace(Admin)) return;   // skip — no live DB
        var conn = await CreateDbAsync();
        try
        {
            var result = await PgUnitRunner.RunAsync(conn, new[]
            {
                new TestCase("assert-true", "SELECT pgproj_assert(true);"),
                new TestCase("assert-false", "SELECT pgproj_assert(1 = 2, 'one is not two');"),
                new TestCase("rowcount-ok", "SELECT pgproj_assert_rowcount('SELECT * FROM (VALUES (1),(2),(3)) v(x)', 3);"),
                new TestCase("rowcount-bad", "SELECT pgproj_assert_rowcount('SELECT 1', 5);"),
                new TestCase("scalar-ok", "SELECT pgproj_assert_scalar('SELECT 2 + 2', '4');"),
                new TestCase("not-empty-ok", "SELECT pgproj_assert_not_empty('SELECT 1');"),
                new TestCase("empty-bad", "SELECT pgproj_assert_empty('SELECT 1');"),
            });

            TestStatus S(string n) => result.Results.Single(r => r.Name == n).Status;
            Assert.Equal(TestStatus.Passed, S("assert-true"));
            Assert.Equal(TestStatus.Failed, S("assert-false"));
            Assert.Equal(TestStatus.Passed, S("rowcount-ok"));
            Assert.Equal(TestStatus.Failed, S("rowcount-bad"));
            Assert.Equal(TestStatus.Passed, S("scalar-ok"));
            Assert.Equal(TestStatus.Passed, S("not-empty-ok"));
            Assert.Equal(TestStatus.Failed, S("empty-bad"));
            Assert.False(result.AllPassed);
        }
        finally { await DropAsync(conn); }
    }

    [Fact]
    public async Task Expected_sqlstate_negative_tests()
    {
        if (string.IsNullOrWhiteSpace(Admin)) return;   // skip — no live DB
        var conn = await CreateDbAsync();
        try
        {
            const string dupe = "CREATE TABLE _t (id int PRIMARY KEY); INSERT INTO _t VALUES (1); INSERT INTO _t VALUES (1);";
            var result = await PgUnitRunner.RunAsync(conn, new[]
            {
                new TestCase("expects-unique-violation", dupe, ExpectedSqlState: "23505"),   // raised → pass
                new TestCase("expects-wrong-state", dupe, ExpectedSqlState: "23502"),         // wrong state → fail
                new TestCase("expects-error-but-none", "SELECT 1;", ExpectedSqlState: "23505"), // no error → fail
            });

            TestStatus S(string n) => result.Results.Single(r => r.Name == n).Status;
            Assert.Equal(TestStatus.Passed, S("expects-unique-violation"));
            Assert.Equal(TestStatus.Failed, S("expects-wrong-state"));
            Assert.Equal(TestStatus.Failed, S("expects-error-but-none"));
        }
        finally { await DropAsync(conn); }
    }

    [Fact]
    public async Task Inconclusive_signal_is_neither_pass_nor_fail()
    {
        if (string.IsNullOrWhiteSpace(Admin)) return;   // skip — no live DB
        var conn = await CreateDbAsync();
        try
        {
            var result = await PgUnitRunner.RunAsync(conn, new[]
            {
                new TestCase("skip", "SELECT pgproj_inconclusive('not ready');"),
            });
            Assert.Equal(TestStatus.Inconclusive, result.Results.Single().Status);
            Assert.True(result.AllPassed);   // an inconclusive test does not fail the run
        }
        finally { await DropAsync(conn); }
    }

    [Fact]
    public async Task Each_test_runs_in_its_own_rolled_back_transaction()
    {
        if (string.IsNullOrWhiteSpace(Admin)) return;   // skip — no live DB
        var conn = await CreateDbAsync();
        try
        {
            // The test creates a table and inserts a row — all of which must be discarded by the rollback.
            var result = await PgUnitRunner.RunAsync(conn, new[]
            {
                new TestCase("writes-then-rolls-back", "CREATE TABLE public.residue (x int); INSERT INTO public.residue VALUES (1); SELECT pgproj_assert_rowcount('SELECT * FROM public.residue', 1);"),
            });
            Assert.Equal(TestStatus.Passed, result.Results.Single().Status);

            // After the run the table must not exist: single-transaction scope left the DB untouched.
            await using var c = new NpgsqlConnection(conn);
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT to_regclass('public.residue') IS NULL", c);
            Assert.True((bool)(await cmd.ExecuteScalarAsync())!);
        }
        finally { await DropAsync(conn); }
    }

    [Theory]
    [InlineData("-- @expect-sqlstate: 23505\nINSERT ...", "23505")]
    [InlineData("--   @expect-sqlstate:  P0001  \nSELECT 1", "P0001")]
    [InlineData("SELECT pgproj_assert(true);", null)]
    [InlineData("SELECT 1; -- @expect-sqlstate: 23505", null)]   // directive after code is ignored
    public void ParseExpectedSqlState_reads_the_leading_directive(string sql, string? expected)
    {
        Assert.Equal(expected, PgUnitRunner.ParseExpectedSqlState(sql));
    }
}
