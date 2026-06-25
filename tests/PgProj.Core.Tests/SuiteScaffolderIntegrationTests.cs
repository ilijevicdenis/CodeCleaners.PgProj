using System;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using PgProj.Core.Comparison;
using PgProj.Core.Deployment;
using PgProj.Core.Model;
using PgProj.Core.Publishing;
using PgProj.Core.Testing;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Issue #156 — the whole-project suite generator end-to-end against real PostgreSQL (reuses the
/// PGPROJ_TEST_CONNECTION admin endpoint like <see cref="DeployScriptIntegrationTests"/>). Provisions a
/// throwaway database, greenfield-deploys a model, generates the suite, runs it through <see cref="PgUnitRunner"/>,
/// and asserts the buckets: NO failures, the constraint/FK/CRUD/view/existence assertions PASS, and the cases
/// that cannot be synthesised (enum/UDT columns, CHECK predicates) land as INCONCLUSIVE rather than false PASS/FAIL.
/// When PGPROJ_TEST_CONNECTION is unset this no-ops (early return) — it still compiles so the suite is complete.
/// </summary>
public sealed class SuiteScaffolderIntegrationTests
{
    private static string? Admin => Environment.GetEnvironmentVariable("PGPROJ_TEST_CONNECTION");

    private const string Sql = """
        CREATE SCHEMA app;
        CREATE TYPE app.mood AS ENUM ('ok', 'bad');
        CREATE TABLE app.customers (
            id    int GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            name  text NOT NULL,
            email text NOT NULL UNIQUE);
        CREATE TABLE app.orders (
            id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            customer_id int NOT NULL REFERENCES app.customers (id),
            total       numeric NOT NULL CHECK (total >= 0));
        CREATE TABLE app.feelings (
            id int PRIMARY KEY,
            m  app.mood NOT NULL);
        CREATE VIEW app.v_orders AS SELECT * FROM app.orders;
        CREATE SEQUENCE app.counter;
        """;

    [Fact]
    public async Task Generated_suite_runs_with_no_failures_and_correct_buckets()
    {
        if (string.IsNullOrWhiteSpace(Admin)) return;   // skip — no live DB

        var model = TestModel.Build(Sql);
        var conn = await CreateScratchDbAsync();
        try
        {
            // Greenfield-deploy the model so the generated tests run against a matching schema.
            var script = new DeployScriptGenerator().Generate(
                new SchemaComparer().Compare(model, new DatabaseModel()), new DeployOptions { WrapInTransaction = true });
            await new DatabaseDeployer().ExecuteAsync(conn, script);

            var suite = SuiteScaffolder.GenerateSuite(model, SuiteOptions.All);
            var tests = suite
                .Select(r => new TestCase(r.FileName, r.Content, PgUnitRunner.ParseExpectedSqlState(r.Content)))
                .ToList();

            var result = await PgUnitRunner.RunAsync(conn, tests);
            var byName = result.Results.ToDictionary(r => r.Name);

            // The headline guarantee: a freshly generated suite never FAILS against the schema it was generated from.
            Assert.True(result.Failed == 0,
                "generated suite had failures:\n" + string.Join("\n",
                    result.Results.Where(r => r.Status == TestStatus.Failed).Select(r => $"  {r.Name}: {r.Message}")));
            Assert.True(result.Passed > 0);

            // Runnable assertions PASS.
            AssertStatus(byName, "_gen.app.customers.notnull.name.test.sql", TestStatus.Passed);
            AssertStatus(byName, "_gen.app.customers.pk.test.sql", TestStatus.Passed);
            AssertStatus(byName, "_gen.app.customers.unique.email.test.sql", TestStatus.Passed);
            AssertStatus(byName, "_gen.app.orders.fk.customer_id.test.sql", TestStatus.Passed);
            AssertStatus(byName, "_gen.app.orders.crud.test.sql", TestStatus.Passed);
            AssertStatus(byName, "_gen.app.v_orders.view.test.sql", TestStatus.Passed);
            AssertStatus(byName, "_gen.app.counter.exists.test.sql", TestStatus.Passed);
            AssertStatus(byName, "_gen.app.mood.exists.test.sql", TestStatus.Passed);

            // Unsynthesisable cases DOWNGRADE to inconclusive (enum column, CHECK predicate) — never a false verdict.
            AssertStatus(byName, "_gen.app.feelings.crud.test.sql", TestStatus.Inconclusive);
            Assert.Contains(result.Results, r =>
                r.Name.StartsWith("_gen.app.orders.check", StringComparison.Ordinal) && r.Status == TestStatus.Inconclusive);
        }
        finally
        {
            await DropAsync(conn);
        }
    }

    private static void AssertStatus(System.Collections.Generic.IReadOnlyDictionary<string, TestResult> byName,
        string file, TestStatus expected)
    {
        Assert.True(byName.TryGetValue(file, out var r), $"missing generated test '{file}'");
        Assert.True(r!.Status == expected, $"{file}: expected {expected}, got {r.Status} ({r.Message})");
    }

    private static async Task<string> CreateScratchDbAsync()
    {
        var admin = new NpgsqlConnectionStringBuilder(Admin!) { Pooling = false }.ConnectionString;
        var db = "pgproj_suite_" + Guid.NewGuid().ToString("N")[..12];
        await ExecAsync(admin, $"CREATE DATABASE \"{db}\"");
        return new NpgsqlConnectionStringBuilder(Admin!) { Database = db, Pooling = false }.ConnectionString;
    }

    private static async Task DropAsync(string conn)
    {
        NpgsqlConnection.ClearAllPools();
        var db = new NpgsqlConnectionStringBuilder(conn).Database!;
        var admin = new NpgsqlConnectionStringBuilder(Admin!) { Pooling = false }.ConnectionString;
        await ExecAsync(admin, $"DROP DATABASE IF EXISTS \"{db}\" WITH (FORCE)");
    }

    private static async Task ExecAsync(string conn, string sql)
    {
        await using var c = new NpgsqlConnection(conn);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }
}
