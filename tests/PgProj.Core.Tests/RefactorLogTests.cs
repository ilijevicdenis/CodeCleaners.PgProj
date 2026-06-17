using System;
using System.IO;
using System.Linq;
using PgProj.Core.Comparison;
using PgProj.Core.Model;
using PgProj.Core.Refactoring;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Issue #136 — the persisted <c>.pgrefactorlog</c>. The deploy planner consumes it BY DEFAULT (its mere
/// presence is the opt-in) so a logged rename / schema-move / column-rename deploys as a data-preserving
/// <c>ALTER</c> instead of the destructive DROP+CREATE the name-keyed diff would otherwise infer.
/// </summary>
public sealed class RefactorLogTests
{
    private static RefactorLog Log(params RefactorEntry[] entries) => new() { Entries = entries };

    private static System.Collections.Generic.IReadOnlyList<SchemaChange> Diff(
        string sourceSql, string targetSql, RefactorLog? log) =>
        new SchemaComparer().Compare(TestModel.Build(sourceSql), TestModel.Build(targetSql),
            new ComparerOptions { DropObjectsNotInSource = true, RefactorLog = log });

    // ---- table rename ---------------------------------------------------------------------------

    [Fact]
    public void Without_the_log_a_renamed_table_is_drop_plus_create()
    {
        var changes = Diff(
            "CREATE TABLE app.client (id int PRIMARY KEY, name text);",
            "CREATE TABLE app.customer (id int PRIMARY KEY, name text);",
            log: null);
        Assert.Contains(changes, c => c is CreateTableChange);
        Assert.Contains(changes, c => c is DropTableChange);
        Assert.DoesNotContain(changes, c => c is RenameTableChange);
    }

    [Fact]
    public void With_the_log_a_renamed_table_is_a_single_alter_rename()
    {
        var changes = Diff(
            "CREATE TABLE app.client (id int PRIMARY KEY, name text);",
            "CREATE TABLE app.customer (id int PRIMARY KEY, name text);",
            Log(new RefactorEntry("rename", "table", "app.customer", "app.client")));

        Assert.Contains(changes, c => c is RenameTableChange { OldName: "customer", NewName: "client" });
        Assert.DoesNotContain(changes, c => c is CreateTableChange);
        Assert.DoesNotContain(changes, c => c is DropTableChange);
    }

    [Fact]
    public void Stale_log_entry_is_ignored()
    {
        // The log claims a rename but the target has no such old table → the entry is skipped (no ALTER).
        var changes = Diff(
            "CREATE TABLE app.client (id int PRIMARY KEY);",
            "CREATE TABLE app.client (id int PRIMARY KEY);",
            Log(new RefactorEntry("rename", "table", "app.gone", "app.client")));
        Assert.DoesNotContain(changes, c => c is RenameTableChange);
        Assert.Empty(changes);   // source already matches target
    }

    // ---- move to schema -------------------------------------------------------------------------

    [Fact]
    public void With_the_log_a_schema_move_is_alter_set_schema()
    {
        var changes = Diff(
            "CREATE SCHEMA archive;\nCREATE TABLE archive.orders (id int PRIMARY KEY);",
            "CREATE TABLE app.orders (id int PRIMARY KEY);",
            Log(new RefactorEntry("move-schema", "table", "app.orders", "archive.orders")));

        Assert.Contains(changes, c => c is SetTableSchemaChange { OldSchema: "app", NewSchema: "archive", Name: "orders" });
        Assert.DoesNotContain(changes, c => c is DropTableChange);
    }

    // ---- column rename --------------------------------------------------------------------------

    [Fact]
    public void With_the_log_a_renamed_column_is_alter_rename_column()
    {
        var changes = Diff(
            "CREATE TABLE app.t (id int PRIMARY KEY, full_name text);",
            "CREATE TABLE app.t (id int PRIMARY KEY, name text);",
            Log(new RefactorEntry("rename", "column", "app.t.name", "app.t.full_name")));

        Assert.Contains(changes, c => c is RenameColumnChange { OldName: "name", NewName: "full_name" });
        Assert.DoesNotContain(changes, c => c is AddColumnChange);
        Assert.DoesNotContain(changes, c => c is DropColumnChange);
    }

    [Fact]
    public void A_renamed_column_that_also_changed_type_still_alters_in_place()
    {
        var changes = Diff(
            "CREATE TABLE app.t (id int PRIMARY KEY, qty bigint);",
            "CREATE TABLE app.t (id int PRIMARY KEY, quantity int);",
            Log(new RefactorEntry("rename", "column", "app.t.quantity", "app.t.qty")));

        Assert.Contains(changes, c => c is RenameColumnChange { OldName: "quantity", NewName: "qty" });
        Assert.Contains(changes, c => c is AlterColumnChange);     // the type change is still applied
        Assert.DoesNotContain(changes, c => c is DropColumnChange);
    }

    // ---- risk -----------------------------------------------------------------------------------

    [Fact]
    public void Renames_and_moves_are_classified_data_safe()
    {
        var r = PgProj.Core.Comparison.Risk.RiskAnalyzer.Default;
        Assert.Equal(PgProj.Core.Comparison.Risk.RiskLevel.Safe, r.Classify(new RenameTableChange("app", "a", "b")).Level);
        Assert.Equal(PgProj.Core.Comparison.Risk.RiskLevel.Safe, r.Classify(new RenameColumnChange("app", "t", "a", "b")).Level);
        Assert.Equal(PgProj.Core.Comparison.Risk.RiskLevel.Safe, r.Classify(new SetTableSchemaChange("app", "t", "archive")).Level);
        Assert.False(new RenameTableChange("app", "a", "b").IsDestructive);
    }

    // ---- artifact (de)serialization -------------------------------------------------------------

    [Fact]
    public void Log_round_trips_through_json_and_append_is_append_only()
    {
        var log = Log(new RefactorEntry("rename", "table", "app.a", "app.b"))
            .Append(new RefactorEntry("rename", "column", "app.b.x", "app.b.y"));

        var reloaded = RefactorLog.Parse(log.ToJson());
        Assert.Equal(2, reloaded.Entries.Count);
        Assert.Equal("table", reloaded.Entries[0].ObjectType);
        Assert.Equal("app.b.y", reloaded.Entries[1].NewName);
        // camelCase + deterministic.
        Assert.Contains("\"operation\"", log.ToJson());
        Assert.Equal(log.ToJson(), reloaded.ToJson());
    }

    [Fact]
    public void Missing_log_file_loads_as_empty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nope_{Guid.NewGuid():N}{RefactorLog.Extension}");
        Assert.True(RefactorLog.Load(path).IsEmpty);
    }

    // ---- authoring: rename rewrites .sql (definition + qualified refs) AND appends the log -------

    [Fact]
    public void RenameTable_rewrites_definition_and_references_and_appends_the_log()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"pgproj_refactor_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var proj = Path.Combine(dir, "App.pgproj");
            File.WriteAllText(proj,
                """
                <Project Sdk="PgProj.Sdk/0.1.0">
                  <PropertyGroup><Name>App</Name><DefaultSchema>app</DefaultSchema></PropertyGroup>
                  <ItemGroup><Build Include="**/*.sql" /></ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(dir, "customer.sql"),
                "CREATE SCHEMA app;\nCREATE TABLE app.customer (id int PRIMARY KEY, name text);");
            File.WriteAllText(Path.Combine(dir, "orders.sql"),
                "CREATE TABLE app.orders (id int PRIMARY KEY, customer_id int REFERENCES app.customer (id));");

            var result = PgProj.Core.Project.DatabaseProject.Load(proj) is var project
                ? RefactorEngine.RenameTable(project, "app.customer", "client")
                : throw new InvalidOperationException();

            // Definition file rewritten; the FK reference in the other file rewritten too.
            Assert.Contains("CREATE TABLE app.client", File.ReadAllText(Path.Combine(dir, "customer.sql")));
            Assert.Contains("REFERENCES app.client", File.ReadAllText(Path.Combine(dir, "orders.sql")));
            Assert.True(result.Replacements >= 2);

            // The column "customer_id" must NOT have been mangled (word-boundary safety).
            Assert.Contains("customer_id", File.ReadAllText(Path.Combine(dir, "orders.sql")));

            // The log was appended with the rename entry.
            var log = RefactorLog.Load(RefactorLog.PathFor(proj));
            Assert.Single(log.Entries);
            Assert.Equal(("rename", "table", "app.customer", "app.client"),
                (log.Entries[0].Operation, log.Entries[0].ObjectType, log.Entries[0].OldName, log.Entries[0].NewName));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void RenameTable_on_a_missing_object_throws()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"pgproj_refactor_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var proj = Path.Combine(dir, "App.pgproj");
            File.WriteAllText(proj, "<Project Sdk=\"PgProj.Sdk/0.1.0\"><ItemGroup><Build Include=\"**/*.sql\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(dir, "t.sql"), "CREATE TABLE app.t (id int PRIMARY KEY);");
            var project = PgProj.Core.Project.DatabaseProject.Load(proj);
            Assert.Throws<RefactorException>(() => RefactorEngine.RenameTable(project, "app.missing", "x"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Save_then_load_round_trips_on_disk()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"pgproj_rlog_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "App" + RefactorLog.Extension);
            Log(new RefactorEntry("move-schema", "table", "app.o", "archive.o")).Save(path);
            var loaded = RefactorLog.Load(path);
            Assert.Single(loaded.Entries);
            Assert.Equal("archive.o", loaded.Entries[0].NewName);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
