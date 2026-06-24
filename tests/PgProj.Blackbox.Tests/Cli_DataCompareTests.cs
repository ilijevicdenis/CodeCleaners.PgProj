using PgProj.Blackbox.Tests.Infrastructure;

namespace PgProj.Blackbox.Tests;

/// <summary>
/// Blackbox row-level data compare + sync between two SEPARATE servers — the source database lives on
/// the SOURCE server, the target on the TARGET server, exercising the genuine two-endpoint path.
/// </summary>
public sealed class Cli_DataCompareTests(BlackboxFixture fx) : BlackboxTestBase(fx)
{
    private const string Schema = "CREATE SCHEMA app; CREATE TABLE app.t (id integer PRIMARY KEY, name text, qty integer);";

    [LiveFact]
    public async Task Data_compare_reports_differences_and_apply_brings_target_in_sync()
    {

        await using var src = await Fx.NewSourceDbAsync(Schema + "INSERT INTO app.t VALUES (1,'a',10),(2,'b',20),(3,'c',30);");
        await using var tgt = await Fx.NewTargetDbAsync(Schema + "INSERT INTO app.t VALUES (1,'a',10),(2,'b',99),(4,'d',40);");

        var diff = Run($"data-compare --source {Q(src.ConnectionString)} --target {Q(tgt.ConnectionString)} --fail-on-changes");
        Assert.Equal(6, diff.ExitCode); // Drift — data differs
        Assert.True(diff.Mentions("different"), diff.ToString());

        var apply = Run($"data-compare --source {Q(src.ConnectionString)} --target {Q(tgt.ConnectionString)} --apply");
        Assert.Equal(0, apply.ExitCode);

        // The target now matches the source: row 2 corrected, row 3 added, row 4 removed.
        Assert.Equal(20L, await tgt.Db.ScalarAsync<long>("SELECT qty FROM app.t WHERE id = 2"));
        Assert.Equal(3L, await tgt.Db.ScalarAsync<long>("SELECT count(*) FROM app.t"));

        var recheck = Run($"data-compare --source {Q(src.ConnectionString)} --target {Q(tgt.ConnectionString)} --fail-on-changes");
        Assert.Equal(0, recheck.ExitCode);
    }

    [LiveFact]
    public async Task Identical_data_compares_in_sync()
    {

        const string data = Schema + "INSERT INTO app.t VALUES (1,'a',10),(2,'b',20);";
        await using var src = await Fx.NewSourceDbAsync(data);
        await using var tgt = await Fx.NewTargetDbAsync(data);

        var r = Run($"data-compare --source {Q(src.ConnectionString)} --target {Q(tgt.ConnectionString)} --fail-on-changes");
        Assert.Equal(0, r.ExitCode);
        Assert.True(r.Mentions("in sync"), r.ToString());
    }
}
