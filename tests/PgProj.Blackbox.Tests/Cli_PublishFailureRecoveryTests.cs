using PgProj.Blackbox.Tests.Infrastructure;

namespace PgProj.Blackbox.Tests;

/// <summary>
/// Blackbox failure-then-recovery for the DEPLOY step (it actually contacts the target): connection
/// loss (exit 7), the possible-data-loss gate (exit 9) + the --allow-data-loss recovery, and a
/// mid-deploy DDL failure under a transaction (exit 7, clean rollback) + the --smart-defaults
/// recovery. These prove the documented recovery options actually work and that a failed
/// transactional deploy leaves the database unchanged.
/// </summary>
public sealed class Cli_PublishFailureRecoveryTests(BlackboxFixture fx) : BlackboxTestBase(fx)
{
    [LiveFact]
    public async Task Connection_refused_fails_the_publish()
    {
        using var proj = TempProject.Create("ConnRefused");
        proj.AddSql("Schemas/app.sql", "CREATE SCHEMA app;");
        proj.AddSql("Tables/app.t.sql", "CREATE TABLE app.t (id integer PRIMARY KEY);");

        // Nothing is listening on 15999 — introspection can't connect, so the publish fails non-zero
        // (the failure happens while READING the target, before the deploy step; the gates pass first).
        const string dead = "Host=localhost;Port=15999;Username=postgres;Password=pgproj;Database=postgres";
        var r = Run($"publish {Q(proj.ProjectFile)} --connection {Q(dead)}");
        Assert.NotEqual(0, r.ExitCode);
        Assert.True(r.Mentions("error") || r.Mentions("fail") || r.Mentions("connect") || r.Mentions("refused"),
            r.ToString());
    }

    [LiveFact]
    public async Task Drop_column_is_blocked_by_the_data_loss_gate_then_allow_data_loss_recovers()
    {

        // Target already has a column the project no longer declares → DROP COLUMN = data loss.
        await using var tgt = await Fx.NewTargetDbAsync(
            "CREATE SCHEMA app; CREATE TABLE app.widget (id integer PRIMARY KEY, name text, secret text);");
        using var proj = TempProject.Create("DataLoss");
        proj.AddSql("Schemas/app.sql", "CREATE SCHEMA app;");
        proj.AddSql("Tables/app.widget.sql", "CREATE TABLE app.widget (id integer PRIMARY KEY, name text);");

        var blocked = Run($"publish {Q(proj.ProjectFile)} --connection {Q(tgt.ConnectionString)} --allow-drops");
        Assert.Equal(9, blocked.ExitCode); // DataLossBlocked
        Assert.True(await tgt.Db.ColumnExistsAsync("app", "widget", "secret"), "the column must still be there");

        // Recovery: explicitly opt in to the data loss.
        var ok = Run($"publish {Q(proj.ProjectFile)} --connection {Q(tgt.ConnectionString)} --allow-drops --allow-data-loss");
        Assert.Equal(0, ok.ExitCode);
        Assert.False(await tgt.Db.ColumnExistsAsync("app", "widget", "secret"), ok.ToString());
    }

    [LiveFact]
    public async Task Mid_deploy_failure_rolls_back_under_a_transaction_then_smart_defaults_recovers()
    {

        // Populated table; adding a NOT NULL column with no default fails at apply time (existing rows
        // would violate NOT NULL). The default transaction wrapper must roll the whole thing back.
        await using var tgt = await Fx.NewTargetDbAsync(
            "CREATE SCHEMA app; CREATE TABLE app.widget (id integer PRIMARY KEY, name text); " +
            "INSERT INTO app.widget VALUES (1, 'one');");
        using var proj = TempProject.Create("MidDeploy");
        proj.AddSql("Schemas/app.sql", "CREATE SCHEMA app;");
        proj.AddSql("Tables/app.widget.sql",
            "CREATE TABLE app.widget (id integer PRIMARY KEY, name text, qty integer NOT NULL);");

        var fail = Run($"publish {Q(proj.ProjectFile)} --connection {Q(tgt.ConnectionString)}");
        Assert.Equal(7, fail.ExitCode); // DeployError
        Assert.False(await tgt.Db.ColumnExistsAsync("app", "widget", "qty"),
            "the failed deploy was transactional — the column must NOT exist (clean rollback)");

        // Recovery: synthesize a DEFAULT for the new NOT NULL column on the populated table.
        var ok = Run($"publish {Q(proj.ProjectFile)} --connection {Q(tgt.ConnectionString)} --smart-defaults");
        Assert.Equal(0, ok.ExitCode);
        Assert.True(await tgt.Db.ColumnExistsAsync("app", "widget", "qty"), ok.ToString());
        // The existing row survived (recovery preserved data).
        Assert.Equal(1L, await tgt.Db.ScalarAsync<long>("SELECT count(*) FROM app.widget"));
    }

    [LiveFact]
    public async Task A_failed_deploy_can_simply_be_re_run_after_the_fix()
    {

        // Same failure, but the recovery here is "fix the source, re-run" — the idempotent retry path.
        await using var tgt = await Fx.NewTargetDbAsync(
            "CREATE SCHEMA app; CREATE TABLE app.widget (id integer PRIMARY KEY, name text); " +
            "INSERT INTO app.widget VALUES (1, 'one');");
        using var bad = TempProject.Create("RetryBad");
        bad.AddSql("Schemas/app.sql", "CREATE SCHEMA app;");
        bad.AddSql("Tables/app.widget.sql",
            "CREATE TABLE app.widget (id integer PRIMARY KEY, name text, qty integer NOT NULL);");
        Assert.Equal(7, Run($"publish {Q(bad.ProjectFile)} --connection {Q(tgt.ConnectionString)}").ExitCode);

        using var good = TempProject.Create("RetryGood");
        good.AddSql("Schemas/app.sql", "CREATE SCHEMA app;");
        good.AddSql("Tables/app.widget.sql",
            "CREATE TABLE app.widget (id integer PRIMARY KEY, name text, qty integer NOT NULL DEFAULT 0);");
        var ok = Run($"publish {Q(good.ProjectFile)} --connection {Q(tgt.ConnectionString)}");
        Assert.Equal(0, ok.ExitCode);
        Assert.True(await tgt.Db.ColumnExistsAsync("app", "widget", "qty"));
    }
}
