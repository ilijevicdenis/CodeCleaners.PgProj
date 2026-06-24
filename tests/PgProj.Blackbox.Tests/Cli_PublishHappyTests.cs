using PgProj.Blackbox.Tests.Infrastructure;

namespace PgProj.Blackbox.Tests;

/// <summary>
/// Blackbox happy paths for the deploy engine against a real, isolated TARGET database: greenfield
/// publish, idempotent re-publish, dry-run safety, incremental column add, and shadow validate.
/// Each test gets its own throwaway database and drops it.
/// </summary>
public sealed class Cli_PublishHappyTests(BlackboxFixture fx) : BlackboxTestBase(fx)
{
    private static TempProject Widget(string name, string columns)
    {
        var p = TempProject.Create(name);
        p.AddSql("Schemas/app.sql", "CREATE SCHEMA app;");
        p.AddSql("Tables/app.widget.sql", $"CREATE TABLE app.widget ({columns});");
        return p;
    }

    [LiveFact]
    public async Task Greenfield_publish_creates_all_objects()
    {

        await using var tgt = await Fx.NewTargetDbAsync();
        using var proj = Widget("Greenfield", "id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY, name text NOT NULL");

        var r = Run($"publish {Q(proj.ProjectFile)} --connection {Q(tgt.ConnectionString)}");
        Assert.Equal(0, r.ExitCode);
        Assert.True(await tgt.Db.SchemaExistsAsync("app"), r.ToString());
        Assert.True(await tgt.Db.RelationExistsAsync("app", "widget"), r.ToString());
    }

    [LiveFact]
    public async Task Re_publishing_an_up_to_date_database_is_a_no_op()
    {

        await using var tgt = await Fx.NewTargetDbAsync();
        using var proj = Widget("Idempotent", "id integer PRIMARY KEY, name text");

        Assert.Equal(0, Run($"publish {Q(proj.ProjectFile)} --connection {Q(tgt.ConnectionString)}").ExitCode);
        var second = Run($"publish {Q(proj.ProjectFile)} --connection {Q(tgt.ConnectionString)}");
        Assert.Equal(0, second.ExitCode);
        Assert.True(second.Mentions("Nothing to publish"), second.ToString());
    }

    [LiveFact]
    public async Task Dry_run_leaves_the_database_untouched()
    {

        await using var tgt = await Fx.NewTargetDbAsync();
        using var proj = Widget("DryRun", "id integer PRIMARY KEY, name text");

        var r = Run($"publish {Q(proj.ProjectFile)} --connection {Q(tgt.ConnectionString)} --dry-run");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("CREATE TABLE", r.StdOut, StringComparison.OrdinalIgnoreCase); // the script was printed
        Assert.False(await tgt.Db.RelationExistsAsync("app", "widget"), "dry-run must not create anything");
    }

    [LiveFact]
    public async Task Incremental_publish_adds_a_new_column()
    {

        await using var tgt = await Fx.NewTargetDbAsync();
        using var v1 = Widget("IncrementalV1", "id integer PRIMARY KEY, name text");
        Assert.Equal(0, Run($"publish {Q(v1.ProjectFile)} --connection {Q(tgt.ConnectionString)}").ExitCode);
        Assert.False(await tgt.Db.ColumnExistsAsync("app", "widget", "qty"));

        // V2 adds a column (with a DEFAULT so it is a safe, non-data-loss change).
        using var v2 = Widget("IncrementalV2", "id integer PRIMARY KEY, name text, qty integer NOT NULL DEFAULT 0");
        var r = Run($"publish {Q(v2.ProjectFile)} --connection {Q(tgt.ConnectionString)}");
        Assert.Equal(0, r.ExitCode);
        Assert.True(await tgt.Db.ColumnExistsAsync("app", "widget", "qty"), r.ToString());
    }

    [LiveFact]
    public async Task Validate_applies_to_a_throwaway_db_and_passes()
    {

        using var proj = Widget("ValidateOk", "id integer PRIMARY KEY, name text NOT NULL");
        // validate spins up + drops its OWN scratch DB on the server; point it at the target admin.
        var r = Run($"validate {Q(proj.ProjectFile)} --connection {Q(Fx.TargetAdmin!)}");
        Assert.Equal(0, r.ExitCode);
        Assert.True(r.Mentions("Valid"), r.ToString());
    }

    [LiveFact]
    public async Task Output_script_is_written_and_then_applied()
    {

        await using var tgt = await Fx.NewTargetDbAsync();
        using var proj = Widget("OutScript", "id integer PRIMARY KEY");
        var outSql = Path.Combine(proj.Dir, "bin", "_deploy.sql");

        var r = Run($"publish {Q(proj.ProjectFile)} --connection {Q(tgt.ConnectionString)} -o {Q(outSql)}");
        Assert.Equal(0, r.ExitCode);
        Assert.True(File.Exists(outSql), r.ToString());
        Assert.True(await tgt.Db.RelationExistsAsync("app", "widget"));
    }
}
