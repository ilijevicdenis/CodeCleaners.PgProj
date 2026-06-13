using System;
using System.Linq;
using PgProj.Core.Comparison;
using PgProj.Core.Model;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Issue #137 — lock-minimizing deploy: CONCURRENTLY index ops + NOT VALID/VALIDATE constraints, emitted
/// outside the deploy transaction. DB-free, exact-string assertions on the generated script.
/// </summary>
public sealed class DeployScriptConcurrent137Tests
{
    // A greenfield create with a named FK and an index — exercises index create + constraint add.
    private static System.Collections.Generic.IReadOnlyList<SchemaChange> SampleChanges() =>
        new SchemaComparer().Compare(
            TestModel.Build(
                "CREATE TABLE app.p (id int PRIMARY KEY);\n" +
                "CREATE TABLE app.c (id int PRIMARY KEY, pid int, CONSTRAINT fk_c_p FOREIGN KEY (pid) REFERENCES app.p (id));\n" +
                "CREATE INDEX ix_c_pid ON app.c (pid);"),
            new DatabaseModel());

    private static string Gen(DeployOptions opts) =>
        new DeployScriptGenerator().Generate(SampleChanges(), opts).Replace("\r\n", "\n");

    [Fact]
    public void Off_by_default_emits_blocking_ddl_inside_the_transaction()
    {
        var sql = Gen(new DeployOptions { WrapInTransaction = true });
        Assert.DoesNotContain("CONCURRENTLY", sql);
        Assert.DoesNotContain("NOT VALID", sql);
        Assert.DoesNotContain("VALIDATE CONSTRAINT", sql);
        Assert.Contains("CREATE INDEX \"ix_c_pid\"", sql);
    }

    [Fact]
    public void Concurrent_index_create_is_emitted_concurrently_and_after_commit()
    {
        var sql = Gen(new DeployOptions { WrapInTransaction = true, ConcurrentIndexOperations = true });

        Assert.Contains("CREATE INDEX CONCURRENTLY \"ix_c_pid\"", sql);

        // The concurrent build must sit OUTSIDE the transaction — after COMMIT.
        var commit = sql.IndexOf("COMMIT;", StringComparison.Ordinal);
        var concurrent = sql.IndexOf("CREATE INDEX CONCURRENTLY", StringComparison.Ordinal);
        Assert.True(commit >= 0 && commit < concurrent, "CONCURRENTLY index must be emitted after COMMIT");
        Assert.Contains("non-transactional steps", sql);
    }

    [Fact]
    public void Named_fk_becomes_not_valid_in_txn_plus_a_validate_pass_after_commit()
    {
        var sql = Gen(new DeployOptions { WrapInTransaction = true, ConcurrentIndexOperations = true });

        var commit = sql.IndexOf("COMMIT;", StringComparison.Ordinal);
        var addFk = sql.IndexOf("FOREIGN KEY", StringComparison.Ordinal);
        var validate = sql.IndexOf("VALIDATE CONSTRAINT \"fk_c_p\"", StringComparison.Ordinal);

        // The ADD ... NOT VALID is inside the transaction; the VALIDATE is a separate pass after COMMIT.
        Assert.Contains("NOT VALID;", sql);
        Assert.True(addFk >= 0 && addFk < commit, "ADD ... NOT VALID runs inside the transaction");
        Assert.True(validate > commit, "VALIDATE CONSTRAINT runs after COMMIT");
    }

    [Fact]
    public void Drop_index_is_concurrent_when_the_option_is_on()
    {
        var changes = new SchemaComparer().Compare(
            TestModel.Build("CREATE TABLE app.t (id int PRIMARY KEY);"),
            TestModel.Build("CREATE TABLE app.t (id int PRIMARY KEY);\nCREATE INDEX ix ON app.t (id);"),
            new ComparerOptions { DropObjectsNotInSource = true });

        var sql = new DeployScriptGenerator()
            .Generate(changes, new DeployOptions { WrapInTransaction = true, ConcurrentIndexOperations = true })
            .Replace("\r\n", "\n");
        Assert.Contains("DROP INDEX CONCURRENTLY IF EXISTS", sql);
    }

    // ---- LockMinimizer unit behavior -----------------------------------------------------------

    [Fact]
    public void LockMinimizer_is_idempotent_and_leaves_unnamed_constraints_alone()
    {
        // Unnamed FK (PostgreSQL auto-names it) → cannot be VALIDATE'd by name, so it is left in place.
        var unnamed = new SchemaComparer().Compare(
            TestModel.Build("CREATE TABLE app.p (id int PRIMARY KEY);\nCREATE TABLE app.c (id int PRIMARY KEY, pid int REFERENCES app.p (id));"),
            new DatabaseModel());

        var once = LockMinimizer.Apply(unnamed);
        var twice = LockMinimizer.Apply(once);

        // No VALIDATE step for the unnamed FK; idempotent (same count both times).
        Assert.DoesNotContain(once, c => c is ValidateConstraintChange);
        Assert.Equal(once.Count, twice.Count);

        // Named FK does get a single VALIDATE, and re-applying does not duplicate it.
        var named = LockMinimizer.Apply(SampleChanges());
        var namedTwice = LockMinimizer.Apply(named);
        Assert.Single(named, c => c is ValidateConstraintChange);
        Assert.Equal(named.Count, namedTwice.Count);
    }

    [Fact]
    public void Concurrent_steps_are_flagged_run_outside_transaction()
    {
        var transformed = LockMinimizer.Apply(SampleChanges());
        Assert.Contains(transformed, c => c is CreateIndexChange { Concurrent: true });
        Assert.All(transformed.OfType<CreateIndexChange>(), c => Assert.True(c.RunsOutsideTransaction));
        Assert.All(transformed.OfType<ValidateConstraintChange>(), c => Assert.True(c.RunsOutsideTransaction));
        Assert.All(transformed.OfType<AddForeignKeyChange>(), c => Assert.False(c.RunsOutsideTransaction));
    }
}
