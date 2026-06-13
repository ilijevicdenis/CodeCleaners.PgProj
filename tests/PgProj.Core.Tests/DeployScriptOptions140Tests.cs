using System;
using PgProj.Core.Comparison;
using PgProj.Core.Model;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Issue #140 — the DacDeployOptions-equivalent publish-options family. Each new option defaults to
/// today's behaviour and toggling it produces the documented effect. DB-free, exact-string assertions
/// on the generated script (the same generator the publish path drives).
/// </summary>
public sealed class DeployScriptOptions140Tests
{
    private static string Gen(System.Collections.Generic.IReadOnlyList<SchemaChange> changes, DeployOptions opts) =>
        new DeployScriptGenerator().Generate(changes, opts).Replace("\r\n", "\n");

    // ---- GenerateSmartDefaults -----------------------------------------------------------------

    private static System.Collections.Generic.IReadOnlyList<SchemaChange> AddNotNullColumn() =>
        // Source declares an extra NOT NULL column the target lacks → ADD COLUMN ... NOT NULL.
        new SchemaComparer().Compare(
            TestModel.Build("CREATE TABLE app.t (id int PRIMARY KEY, n text NOT NULL);"),
            TestModel.Build("CREATE TABLE app.t (id int PRIMARY KEY);"));

    [Fact]
    public void Smart_defaults_off_emits_bare_not_null_add()
    {
        var sql = Gen(AddNotNullColumn(), new DeployOptions());
        Assert.Contains("ADD COLUMN \"n\" text NOT NULL;", sql);
        Assert.DoesNotContain("DEFAULT", sql);
    }

    [Fact]
    public void Smart_defaults_on_injects_a_type_appropriate_default()
    {
        var sql = Gen(AddNotNullColumn(), new DeployOptions { GenerateSmartDefaults = true });
        // text → '' so the NOT NULL add succeeds on a populated table.
        Assert.Contains("ADD COLUMN \"n\" text NOT NULL DEFAULT '';", sql);
    }

    [Fact]
    public void Smart_defaults_leaves_nullable_and_already_defaulted_columns_alone()
    {
        var nullable = new SchemaComparer().Compare(
            TestModel.Build("CREATE TABLE app.t (id int PRIMARY KEY, n text);"),
            TestModel.Build("CREATE TABLE app.t (id int PRIMARY KEY);"));
        Assert.DoesNotContain("DEFAULT", Gen(nullable, new DeployOptions { GenerateSmartDefaults = true }));

        var withDefault = new SchemaComparer().Compare(
            TestModel.Build("CREATE TABLE app.t (id int PRIMARY KEY, n text NOT NULL DEFAULT 'x');"),
            TestModel.Build("CREATE TABLE app.t (id int PRIMARY KEY);"));
        var sql = Gen(withDefault, new DeployOptions { GenerateSmartDefaults = true });
        Assert.Contains("DEFAULT 'x'", sql);          // the declared default is kept
        Assert.DoesNotContain("DEFAULT ''", sql);     // no synthesized one piled on
    }

    // ---- ScriptNewConstraintValidation (NOT VALID) ---------------------------------------------

    [Fact]
    public void Constraint_validation_off_emits_foreign_key_not_valid()
    {
        var changes = new SchemaComparer().Compare(
            TestModel.Build("CREATE TABLE app.p (id int PRIMARY KEY);\nCREATE TABLE app.c (id int PRIMARY KEY, pid int REFERENCES app.p(id));"),
            TestModel.Build("CREATE TABLE app.p (id int PRIMARY KEY);\nCREATE TABLE app.c (id int PRIMARY KEY);"));

        Assert.DoesNotContain("NOT VALID", Gen(changes, new DeployOptions()));
        var notValid = Gen(changes, new DeployOptions { ScriptNewConstraintValidation = false });
        Assert.Contains("FOREIGN KEY", notValid);
        Assert.Contains("NOT VALID;", notValid);
    }

    [Fact]
    public void Constraint_validation_off_emits_check_not_valid()
    {
        var changes = new SchemaComparer().Compare(
            TestModel.Build("CREATE TABLE app.t (id int PRIMARY KEY, age int, CONSTRAINT chk CHECK (age > 0));"),
            TestModel.Build("CREATE TABLE app.t (id int PRIMARY KEY, age int);"));

        var notValid = Gen(changes, new DeployOptions { ScriptNewConstraintValidation = false });
        Assert.Contains("CHECK", notValid);
        Assert.Contains("NOT VALID;", notValid);
    }

    // ---- DoNotDropObjectTypes / granular drop suppression --------------------------------------

    [Fact]
    public void Do_not_drop_object_types_suppresses_only_the_listed_kind_drop()
    {
        var changes = new SchemaComparer().Compare(
            TestModel.Build("CREATE TABLE app.t (id int PRIMARY KEY);"),
            TestModel.Build("CREATE TABLE app.t (id int PRIMARY KEY);\nCREATE INDEX ix ON app.t (id);"),
            new ComparerOptions { DropObjectsNotInSource = true });

        Assert.Contains("DROP INDEX", Gen(changes, new DeployOptions()));
        Assert.DoesNotContain("DROP INDEX", Gen(changes, new DeployOptions { DoNotDropObjectTypes = new[] { "index" } }));
    }

    [Fact]
    public void Do_not_drop_does_not_suppress_other_kinds()
    {
        // Drop a whole table; suppressing "index" must NOT save the table drop.
        var changes = new SchemaComparer().Compare(
            TestModel.Build("CREATE TABLE app.keep (id int PRIMARY KEY);"),
            TestModel.Build("CREATE TABLE app.keep (id int PRIMARY KEY);\nCREATE TABLE app.gone (id int PRIMARY KEY);"),
            new ComparerOptions { DropObjectsNotInSource = true });

        Assert.Contains("DROP TABLE", Gen(changes, new DeployOptions { DoNotDropObjectTypes = new[] { "index" } }));
    }

    // ---- AllowTableRecreation ------------------------------------------------------------------

    private static System.Collections.Generic.IReadOnlyList<SchemaChange> RecreateChange() =>
        // A domain whose body changed → a destructive drop-and-recreate (RecreateRawObjectChange).
        new SchemaComparer().Compare(
            TestModel.Build("CREATE DOMAIN app.d AS int CHECK (VALUE > 5);"),
            TestModel.Build("CREATE DOMAIN app.d AS int CHECK (VALUE > 0);"),
            new ComparerOptions { DropObjectsNotInSource = true });

    [Fact]
    public void Allow_table_recreation_on_by_default_emits_the_recreate_live()
    {
        var changes = RecreateChange();
        Assert.Contains(changes, c => c is RecreateRawObjectChange);   // the scenario does produce one
        var sql = Gen(changes, new DeployOptions());
        Assert.DoesNotContain("[blocked: object recreation", sql);
    }

    [Fact]
    public void Allow_table_recreation_off_comments_the_recreate_out()
    {
        var sql = Gen(RecreateChange(), new DeployOptions { AllowTableRecreation = false });
        Assert.Contains("-- [blocked: object recreation; set AllowTableRecreation to apply]", sql);
    }
}
