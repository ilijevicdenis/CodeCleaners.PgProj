using System;
using System.Linq;
using PgProj.Core.Comparison;
using PgProj.Core.Model;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Phase 14 (issue #56) — option- &amp; version-aware script generation. Each option defaults to today's
/// behaviour (asserted against the unmodified default output), and toggling it produces the documented
/// effect. DB-free, exact-string assertions.
/// </summary>
public sealed class DeployScriptOptionTests
{
    // A greenfield create of a single table + a function (so we have a CREATE TABLE, a CREATE INDEX path,
    // and a CREATE OR REPLACE FUNCTION to exercise).
    private static System.Collections.Generic.IReadOnlyList<SchemaChange> SampleChanges() =>
        new SchemaComparer().Compare(
            TestModel.Build(
                "CREATE TABLE app.t (id int PRIMARY KEY, name text);\n" +
                "CREATE INDEX t_name_idx ON app.t (name);"),
            new DatabaseModel());

    private static string Gen(DeployOptions opts) =>
        new DeployScriptGenerator().Generate(SampleChanges(), opts).Replace("\r\n", "\n");

    // ---- DEFAULTS: every new option, left at its default, reproduces today's output -------------

    [Fact]
    public void Default_options_reproduce_the_baseline_script()
    {
        var baseline = Gen(new DeployOptions { WrapInTransaction = true });

        // A DeployOptions built fresh (all #56 options at default) must produce the identical script.
        var withAllDefaults = Gen(new DeployOptions
        {
            WrapInTransaction = true,
            IdempotentIfExists = false,
            Verbose = false,
            StatementTimeoutMs = null,
            LockTimeoutMs = null,
            DataLossHandling = DataLossHandling.Include,
            ExcludeObjectTypes = Array.Empty<string>(),
            IncludeOnlyObjectTypes = Array.Empty<string>(),
        });

        Assert.Equal(baseline, withAllDefaults);
        // Sanity: the baseline does NOT carry any of the opt-in artifacts.
        Assert.DoesNotContain("SET statement_timeout", baseline);
        Assert.DoesNotContain("IF NOT EXISTS", baseline.Replace("CREATE SCHEMA IF NOT EXISTS", ""));  // only schema/seq carry it by default
    }

    // ---- IDEMPOTENT IF [NOT] EXISTS -------------------------------------------------------------

    [Fact]
    public void Idempotent_option_wraps_create_table_and_index_in_if_not_exists()
    {
        var off = Gen(new DeployOptions { WrapInTransaction = true });
        Assert.Contains("CREATE TABLE \"app\".\"t\"", off);
        Assert.DoesNotContain("CREATE TABLE IF NOT EXISTS", off);
        Assert.DoesNotContain("CREATE INDEX IF NOT EXISTS", off);

        var on = Gen(new DeployOptions { WrapInTransaction = true, IdempotentIfExists = true });
        Assert.Contains("CREATE TABLE IF NOT EXISTS \"app\".\"t\"", on);
        Assert.Contains("CREATE INDEX IF NOT EXISTS \"t_name_idx\"", on);
    }

    [Fact]
    public void Idempotent_option_adds_if_exists_to_a_drop_column()
    {
        var changes = new SchemaComparer().Compare(
            TestModel.Build("CREATE TABLE app.t (id int);"),
            TestModel.Build("CREATE TABLE app.t (id int, gone text);"),
            new ComparerOptions { DropObjectsNotInSource = true });
        // The diff drops the extra "gone" column.
        var on = new DeployScriptGenerator()
            .Generate(changes, new DeployOptions { IdempotentIfExists = true })
            .Replace("\r\n", "\n");
        Assert.Contains("DROP COLUMN IF EXISTS \"gone\"", on);
    }

    // ---- TIMEOUT INJECTION ---------------------------------------------------------------------

    [Fact]
    public void Statement_and_lock_timeout_preamble_is_injected_when_set()
    {
        var script = Gen(new DeployOptions
        {
            WrapInTransaction = true,
            StatementTimeoutMs = 30000,
            LockTimeoutMs = 5000,
        });

        Assert.Contains("SET statement_timeout = 30000;", script);
        Assert.Contains("SET lock_timeout = 5000;", script);
        // Inside the transaction: after BEGIN, before the first CREATE.
        var begin = script.IndexOf("BEGIN;", StringComparison.Ordinal);
        var setSt = script.IndexOf("SET statement_timeout", StringComparison.Ordinal);
        var firstCreate = script.IndexOf("CREATE ", StringComparison.Ordinal);
        Assert.True(begin < setSt && setSt < firstCreate);
    }

    // ---- VERBOSE -------------------------------------------------------------------------------

    [Fact]
    public void Verbose_option_adds_target_version_and_per_change_rationale()
    {
        var minimal = Gen(new DeployOptions { WrapInTransaction = true });
        Assert.DoesNotContain("risk:", minimal);
        Assert.DoesNotContain("target PostgreSQL:", minimal);

        var verbose = Gen(new DeployOptions { WrapInTransaction = true, Verbose = true });
        Assert.Contains("-- target PostgreSQL:", verbose);
        Assert.Contains("risk:", verbose);
    }

    // ---- INCLUDE / EXCLUDE OBJECT TYPES --------------------------------------------------------

    [Fact]
    public void Exclude_object_types_drops_those_changes_from_generation()
    {
        var with = Gen(new DeployOptions { WrapInTransaction = true });
        Assert.Contains("CREATE INDEX", with);

        var without = Gen(new DeployOptions { WrapInTransaction = true, ExcludeObjectTypes = new[] { "index" } });
        Assert.DoesNotContain("CREATE INDEX", without);
        Assert.Contains("CREATE TABLE", without);   // other types remain
    }

    [Fact]
    public void Include_only_object_types_keeps_only_those_changes()
    {
        var only = Gen(new DeployOptions { WrapInTransaction = true, IncludeOnlyObjectTypes = new[] { "index" } });
        Assert.Contains("CREATE INDEX", only);
        Assert.DoesNotContain("CREATE TABLE", only);
    }

    // ---- DATA-LOSS HANDLING (block / comment / omit) -------------------------------------------

    private static System.Collections.Generic.IReadOnlyList<SchemaChange> DataLossChanges() =>
        // Dropping a table is a DataLoss change (#54). Source lacks the table the target has.
        new SchemaComparer()
            .Compare(TestModel.Build("CREATE TABLE app.keep (id int);"),
                     TestModel.Build("CREATE TABLE app.keep (id int);\nCREATE TABLE app.gone (id int);"),
                     new ComparerOptions { DropObjectsNotInSource = true });

    [Fact]
    public void Block_on_data_loss_throws_before_any_output()
    {
        var ex = Assert.Throws<DataLossBlockedException>(() =>
            new DeployScriptGenerator().Generate(DataLossChanges(),
                new DeployOptions { BlockOnPossibleDataLoss = true }));
        Assert.NotEmpty(ex.Offending);
    }

    [Fact]
    public void Data_loss_handling_comment_emits_the_drop_commented_out()
    {
        var script = new DeployScriptGenerator()
            .Generate(DataLossChanges(), new DeployOptions { DataLossHandling = DataLossHandling.Comment })
            .Replace("\r\n", "\n");

        Assert.Contains("-- [commented: possible data loss]", script);
        // The DROP TABLE statement itself is present only as a comment, never as a live statement.
        Assert.Contains("-- DROP TABLE IF EXISTS \"app\".\"gone\"", script);
        Assert.DoesNotContain("\nDROP TABLE IF EXISTS \"app\".\"gone\"", script);
    }

    [Fact]
    public void Data_loss_handling_omit_drops_the_statement_with_a_marker()
    {
        var script = new DeployScriptGenerator()
            .Generate(DataLossChanges(), new DeployOptions { DataLossHandling = DataLossHandling.Omit })
            .Replace("\r\n", "\n");

        Assert.Contains("-- [omitted: possible data loss]", script);
        Assert.DoesNotContain("DROP TABLE IF EXISTS \"app\".\"gone\"", script);
    }

    [Fact]
    public void Data_loss_handling_include_is_the_default_and_emits_the_live_drop()
    {
        var script = new DeployScriptGenerator()
            .Generate(DataLossChanges(), new DeployOptions())
            .Replace("\r\n", "\n");

        Assert.Contains("DROP TABLE IF EXISTS \"app\".\"gone\"", script);
        Assert.DoesNotContain("[commented: possible data loss]", script);
        Assert.DoesNotContain("[omitted: possible data loss]", script);
    }

    // ---- VERSION-AWARE DDL via ObjectCapabilities ----------------------------------------------

    [Fact]
    public void Version_profile_lacking_alter_column_type_falls_back_to_a_skip_comment()
    {
        // A column type change (int -> bigint). On the default profile this is a live ALTER; on a profile
        // whose ObjectCapabilities cannot ALTER a column type in place, the generator must NOT emit invalid
        // SQL — it emits a skip comment instead (version-aware DDL, #43/#56).
        var changes = new SchemaComparer().Compare(
            TestModel.Build("CREATE TABLE app.t (id bigint);"),
            TestModel.Build("CREATE TABLE app.t (id int);"));

        var defaultScript = new DeployScriptGenerator()
            .Generate(changes, new DeployOptions()).Replace("\r\n", "\n");
        Assert.Contains("ALTER COLUMN \"id\" TYPE bigint", defaultScript);

        Assert.DoesNotContain("[skipped on PostgreSQL", defaultScript);

        // Now drive the version-aware seam with a profile whose ObjectCapabilities lack the in-place
        // ALTER-COLUMN-TYPE path: the generator must emit a skip comment instead of the invalid ALTER.
        var degraded = PgProj.Core.Versioning.PostgresVersionProfile.Latest
            .With(capabilities: new PgProj.Core.Versioning.ObjectCapabilities { CanAlterColumnType = false });

        var degradedScript = new DeployScriptGenerator()
            .Generate(changes, new DeployOptions(), degraded).Replace("\r\n", "\n");

        Assert.DoesNotContain("ALTER COLUMN \"id\" TYPE bigint", degradedScript);
        Assert.Contains("[skipped on PostgreSQL", degradedScript);
    }
}
