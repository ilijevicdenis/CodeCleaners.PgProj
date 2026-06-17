using System;
using System.IO;
using System.Threading.Tasks;
using Npgsql;
using PgProj.Core.Comparison;
using PgProj.Core.Deployment;
using PgProj.Core.Introspection;
using PgProj.Core.Publishing;
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
    public async Task Logged_table_rename_preserves_rows_while_the_no_log_baseline_loses_them()
    {
        // #136 end-to-end: a logged rename deploys as ALTER … RENAME (rows kept); the same diff WITHOUT the
        // log is a destructive DROP+CREATE (rows lost) — proving the log is what prevents the data loss.
        if (string.IsNullOrWhiteSpace(Admin)) return;   // skip — no live DB
        var source = TestModel.Build("CREATE SCHEMA app;\nCREATE TABLE app.client (id int PRIMARY KEY, name text);");
        var target = TestModel.Build("CREATE SCHEMA app;\nCREATE TABLE app.customer (id int PRIMARY KEY, name text);");
        var log = new PgProj.Core.Refactoring.RefactorLog
        {
            Entries = new[] { new PgProj.Core.Refactoring.RefactorEntry("rename", "table", "app.customer", "app.client") },
        };

        // (1) WITH the log → ALTER … RENAME, the row survives.
        var withLog = await CreateScratchDbAsync();
        try
        {
            await ExecAsync(withLog, "CREATE SCHEMA app; CREATE TABLE app.customer (id int PRIMARY KEY, name text); INSERT INTO app.customer VALUES (1, 'alpha');");
            var changes = new SchemaComparer().Compare(source, target,
                new ComparerOptions { DropObjectsNotInSource = true, RefactorLog = log });
            Assert.Contains(changes, c => c is PgProj.Core.Comparison.RenameTableChange);
            await new DatabaseDeployer().ExecuteAsync(withLog, new DeployScriptGenerator().Generate(changes, new DeployOptions { WrapInTransaction = true }));
            Assert.Equal(1, await ScalarAsync(withLog, "SELECT count(*) FROM app.client"));   // row preserved
        }
        finally { await DropAsync(withLog); }

        // (2) WITHOUT the log → DROP+CREATE, the row is lost (the footgun the log fixes).
        var noLog = await CreateScratchDbAsync();
        try
        {
            await ExecAsync(noLog, "CREATE SCHEMA app; CREATE TABLE app.customer (id int PRIMARY KEY, name text); INSERT INTO app.customer VALUES (1, 'alpha');");
            var changes = new SchemaComparer().Compare(source, target, new ComparerOptions { DropObjectsNotInSource = true });
            Assert.Contains(changes, c => c is DropTableChange);
            await new DatabaseDeployer().ExecuteAsync(noLog, new DeployScriptGenerator().Generate(changes, new DeployOptions { WrapInTransaction = true }));
            Assert.Equal(0, await ScalarAsync(noLog, "SELECT count(*) FROM app.client"));     // rows lost
        }
        finally { await DropAsync(noLog); }
    }

    [Fact]
    public async Task Concurrent_lock_minimizing_deploy_reaches_the_same_end_state_as_the_transactional_one()
    {
        // #137 end-to-end: the lock-minimizing plan (CONCURRENTLY index + FK NOT VALID + VALIDATE) applied
        // via the phased deployer must reproduce the source schema exactly — a re-compare shows no drift.
        if (string.IsNullOrWhiteSpace(Admin)) return;   // skip — no live DB
        var conn = await CreateScratchDbAsync();
        try
        {
            var source = TestModel.Build(
                "CREATE TABLE public.p (id int PRIMARY KEY);\n" +
                "CREATE TABLE public.c (id int PRIMARY KEY, pid int, CONSTRAINT fk_c_p FOREIGN KEY (pid) REFERENCES public.p (id));\n" +
                "CREATE INDEX ix_c_pid ON public.c (pid);");

            // Lock-minimizing change list: CONCURRENTLY index + NOT VALID FK + a VALIDATE pass.
            var changes = LockMinimizer.Apply(new SchemaComparer().Compare(source, new DatabaseModel()));
            Assert.Contains(changes, ch => ch is CreateIndexChange { Concurrent: true });
            Assert.Contains(changes, ch => ch is ValidateConstraintChange);

            // The phased deployer runs CONCURRENTLY autocommit and each VALIDATE in its own transaction.
            await new PhasedDeployer(conn).ExecuteAsync(changes);

            // Re-read the live database and diff against the source — a correct deploy leaves zero changes.
            var live = await new LiveDatabaseReader(PgProj.Core.Versioning.PostgresVersionProfile.Latest).ReadAsync(conn);
            var residual = new SchemaComparer().Compare(source, live);
            Assert.Empty(residual);
        }
        finally { await DropAsync(conn); }
    }

    [Fact]
    public async Task Smart_defaults_let_a_not_null_column_add_to_a_populated_table_succeed()
    {
        // #140 GenerateSmartDefaults, end-to-end: adding a NOT NULL column to a populated table fails
        // without a default and succeeds (backfilling existing rows) with the synthesized one.
        if (string.IsNullOrWhiteSpace(Admin)) return;   // skip — no live DB
        var conn = await CreateScratchDbAsync();
        try
        {
            await ExecAsync(conn, "CREATE TABLE public.t (id int PRIMARY KEY); INSERT INTO public.t (id) VALUES (1);");

            var source = TestModel.Build("CREATE TABLE public.t (id int PRIMARY KEY, n text NOT NULL);");
            var target = TestModel.Build("CREATE TABLE public.t (id int PRIMARY KEY);");
            var changes = new SchemaComparer().Compare(source, target);

            // Bare ADD COLUMN ... NOT NULL is rejected on the populated table.
            var bare = new DeployScriptGenerator().Generate(changes, new DeployOptions { WrapInTransaction = true });
            await Assert.ThrowsAnyAsync<PostgresException>(() => new DatabaseDeployer().ExecuteAsync(conn, bare));

            // With smart defaults the synthesized DEFAULT '' backfills the existing row → the add succeeds.
            var smart = new DeployScriptGenerator().Generate(changes,
                new DeployOptions { WrapInTransaction = true, GenerateSmartDefaults = true });
            await new DatabaseDeployer().ExecuteAsync(conn, smart);

            Assert.Equal(1, await ScalarAsync(conn, "SELECT count(*) FROM public.t WHERE n IS NOT NULL"));
        }
        finally { await DropAsync(conn); }
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
