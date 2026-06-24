using PgProj.Blackbox.Tests.Infrastructure;

namespace PgProj.Blackbox.Tests;

/// <summary>
/// Blackbox for the refactor log: a `rename` records the intent so the NEXT publish emits a
/// data-preserving ALTER TABLE ... RENAME instead of drop+create. Proven end-to-end by checking the
/// row survives the rename on a live target.
/// </summary>
public sealed class Cli_RefactorTests(BlackboxFixture fx) : BlackboxTestBase(fx)
{
    [LiveFact]
    public async Task Rename_then_publish_preserves_data_via_alter_rename()
    {

        await using var tgt = await Fx.NewTargetDbAsync();
        using var proj = TempProject.Create("RenameRefactor");
        proj.AddSql("Schemas/app.sql", "CREATE SCHEMA app;");
        proj.AddSql("Tables/app.widget.sql", "CREATE TABLE app.widget (id integer PRIMARY KEY, name text);");

        // Deploy v1 and put a row in it.
        Assert.Equal(0, Run($"publish {Q(proj.ProjectFile)} --connection {Q(tgt.ConnectionString)}").ExitCode);
        await tgt.Db.ExecAsync("INSERT INTO app.widget (id, name) VALUES (42, 'keep me');");

        // Rename widget → gadget (rewrites the .sql + records the refactor log).
        var rename = Run($"rename {Q(proj.ProjectFile)} app.widget gadget");
        Assert.Equal(0, rename.ExitCode);

        // Publish again: with the refactor log, this is an ALTER ... RENAME, not drop+create.
        var publish = Run($"publish {Q(proj.ProjectFile)} --connection {Q(tgt.ConnectionString)}");
        Assert.Equal(0, publish.ExitCode);

        Assert.True(await tgt.Db.RelationExistsAsync("app", "gadget"), publish.ToString());
        Assert.False(await tgt.Db.RelationExistsAsync("app", "widget"), "the old table should be gone");
        // The row survived — this is the whole point of recording the rename.
        Assert.Equal("keep me", await tgt.Db.ScalarAsync<string>("SELECT name FROM app.gadget WHERE id = 42"));
    }
}
