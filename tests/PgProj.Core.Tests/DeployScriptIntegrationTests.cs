using System;
using System.IO;
using System.Threading.Tasks;
using Npgsql;
using PgProj.Core.Comparison;
using PgProj.Core.Deployment;
using PgProj.Core.Introspection;
using PgProj.Core.Model;
using PgProj.Core.Project;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// EP-DEPLOYSCRIPTS + EP-VARS end-to-end against real PostgreSQL (reuses the PGPROJ_TEST_CONNECTION
/// admin endpoint, like <see cref="LiveReaderIntegrationTests"/> and <see cref="CorpusDb"/>). Each test
/// provisions a throwaway database and drops it afterward. When PGPROJ_TEST_CONNECTION is unset these
/// no-op (early return) — they still compile so the suite is complete; they were NOT run in this
/// environment (no Docker/Postgres available) and are exercised in CI where the connection is set.
/// </summary>
public sealed class DeployScriptIntegrationTests
{
    private static string? Admin => Environment.GetEnvironmentVariable("PGPROJ_TEST_CONNECTION");

    private static async Task<string> CreateScratchDbAsync()
    {
        var admin = new NpgsqlConnectionStringBuilder(Admin!) { Pooling = false }.ConnectionString;
        var db = "pgproj_deploy_" + Guid.NewGuid().ToString("N")[..12];
        await ExecAsync(admin, $"CREATE DATABASE \"{db}\"");
        return new NpgsqlConnectionStringBuilder(Admin!) { Database = db, Pooling = false }.ConnectionString;
    }

    private static async Task DropAsync(string conn)
    {
        NpgsqlConnection.ClearAllPools();
        var b = new NpgsqlConnectionStringBuilder(conn);
        var db = b.Database!;
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

    private static async Task<long> ScalarAsync(string conn, string sql)
    {
        await using var c = new NpgsqlConnection(conn);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task PostDeploy_seed_rows_are_present_after_publish()
    {
        if (string.IsNullOrWhiteSpace(Admin)) return;   // skip — no live DB
        var conn = await CreateScratchDbAsync();
        try
        {
            var source = TestModel.Build("CREATE TABLE public.lookup (id int PRIMARY KEY, name text);");
            var changes = new SchemaComparer().Compare(source, new DatabaseModel());
            var script = new DeployScriptGenerator().Generate(changes, new DeployOptions
            {
                WrapInTransaction = true,
                Scripts = new DeployScriptBundle(Post: new DeployScript("PostDeploy.sql",
                    "INSERT INTO public.lookup (id, name) VALUES (1, 'alpha'), (2, 'beta');")),
            });

            await new DatabaseDeployer().ExecuteAsync(conn, script);

            Assert.Equal(2, await ScalarAsync(conn, "SELECT count(*) FROM public.lookup"));
        }
        finally { await DropAsync(conn); }
    }

    [Fact]
    public async Task Failing_PostDeploy_rolls_back_the_schema_diff()
    {
        if (string.IsNullOrWhiteSpace(Admin)) return;   // skip — no live DB
        var conn = await CreateScratchDbAsync();
        try
        {
            var source = TestModel.Build("CREATE TABLE public.t (id int PRIMARY KEY);");
            var changes = new SchemaComparer().Compare(source, new DatabaseModel());
            var script = new DeployScriptGenerator().Generate(changes, new DeployOptions
            {
                WrapInTransaction = true,
                // Post-deploy references a column that doesn't exist → errors, rolling the whole txn back.
                Scripts = new DeployScriptBundle(Post: new DeployScript("PostDeploy.sql",
                    "INSERT INTO public.t (nope) VALUES (1);")),
            });

            await Assert.ThrowsAsync<PostgresException>(() => new DatabaseDeployer().ExecuteAsync(conn, script));

            // Transactional mode: the table must NOT exist (the schema diff was rolled back with the seed).
            var exists = await ScalarAsync(conn,
                "SELECT count(*) FROM information_schema.tables WHERE table_schema='public' AND table_name='t'");
            Assert.Equal(0, exists);
        }
        finally { await DropAsync(conn); }
    }

    [Fact]
    public async Task Variable_substitution_lands_object_in_the_prod_schema()
    {
        if (string.IsNullOrWhiteSpace(Admin)) return;   // skip — no live DB
        var conn = await CreateScratchDbAsync();
        try
        {
            // PostDeploy creates a schema named app_$(EnvSuffix); with --var EnvSuffix=prod it must be app_prod.
            var vars = SqlCmdVariableResolver.Build(
                defaults: Dict("EnvSuffix", "dev"),
                cliOverrides: Dict("EnvSuffix", "prod"));
            var script = new DeployScriptGenerator().Generate(Array.Empty<SchemaChange>(), new DeployOptions
            {
                WrapInTransaction = true,
                Variables = vars,
                Scripts = new DeployScriptBundle(Post: new DeployScript("PostDeploy.sql",
                    "CREATE SCHEMA app_$(EnvSuffix); CREATE TABLE app_$(EnvSuffix).widget (id int);")),
            });

            await new DatabaseDeployer().ExecuteAsync(conn, script);

            Assert.Equal(1, await ScalarAsync(conn,
                "SELECT count(*) FROM information_schema.schemata WHERE schema_name='app_prod'"));
            Assert.Equal(0, await ScalarAsync(conn,
                "SELECT count(*) FROM information_schema.schemata WHERE schema_name='app_dev'"));
            Assert.Equal(1, await ScalarAsync(conn,
                "SELECT count(*) FROM information_schema.tables WHERE table_schema='app_prod' AND table_name='widget'"));
        }
        finally { await DropAsync(conn); }
    }

    private static System.Collections.Generic.IReadOnlyDictionary<string, string> Dict(string k, string v) =>
        new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [k] = v };
}
