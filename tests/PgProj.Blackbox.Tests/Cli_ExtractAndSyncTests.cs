using PgProj.Blackbox.Tests.Infrastructure;

namespace PgProj.Blackbox.Tests;

/// <summary>
/// Blackbox source→project→target round-trips: reverse-engineer the seeded SOURCE database into a
/// project (and rebuild it), and the compare / drift / pull cycle that keeps a project and a live
/// database in step. Uses the seeded sample DB on the source server as a real, populated input.
/// </summary>
public sealed class Cli_ExtractAndSyncTests(BlackboxFixture fx) : BlackboxTestBase(fx)
{
    [LiveFact]
    public void Extract_then_build_round_trips_the_seeded_source_database()
    {

        var outDir = Path.Combine(Path.GetTempPath(), "pgproj-bb", "extract_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var ex = Run($"extract --connection {Q(Fx.SourceSampleDb!)} -o {Q(outDir)}");
            Assert.Equal(0, ex.ExitCode);

            // It produced a buildable project manifest…
            var manifest = Path.Combine(outDir, "sampledb.pgproj");
            Assert.True(File.Exists(manifest), $"expected {manifest}\n{ex}");

            // …covering the seeded schemas…
            var files = Directory.EnumerateFiles(outDir, "*.sql", SearchOption.AllDirectories).ToList();
            Assert.Contains(files, f => f.Contains("orders", StringComparison.OrdinalIgnoreCase));

            // …and it rebuilds cleanly (the round-trip the parser/model must satisfy).
            var build = Run($"build {Q(manifest)}");
            Assert.Equal(0, build.ExitCode);
        }
        finally { try { Directory.Delete(outDir, true); } catch { } }
    }

    [LiveFact]
    public void Extract_with_table_data_emits_a_post_deploy_seed()
    {

        var outDir = Path.Combine(Path.GetTempPath(), "pgproj-bb", "extractdata_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var ex = Run($"extract --connection {Q(Fx.SourceSampleDb!)} -o {Q(outDir)} --all-table-data");
            Assert.Equal(0, ex.ExitCode);
            var seed = Path.Combine(outDir, "Scripts", "PostDeploy.sql");
            Assert.True(File.Exists(seed), ex.ToString());
            Assert.Contains("INSERT INTO", File.ReadAllText(seed), StringComparison.OrdinalIgnoreCase);
        }
        finally { try { Directory.Delete(outDir, true); } catch { } }
    }

    [LiveFact]
    public async Task Compare_reports_changes_then_in_sync_after_publish()
    {

        await using var tgt = await Fx.NewTargetDbAsync();
        using var proj = TempProject.Create("CompareCycle");
        proj.AddSql("Schemas/app.sql", "CREATE SCHEMA app;");
        proj.AddSql("Tables/app.t.sql", "CREATE TABLE app.t (id integer PRIMARY KEY, name text);");

        // Before publish: the project differs from the empty target, and --fail-on-changes gates.
        var diff = Run($"compare --source {Q(proj.ProjectFile)} --target {Q(tgt.ConnectionString)} --fail-on-changes");
        Assert.Equal(6, diff.ExitCode); // Drift

        Assert.Equal(0, Run($"publish {Q(proj.ProjectFile)} --connection {Q(tgt.ConnectionString)}").ExitCode);

        // After publish: in sync, gate passes.
        var same = Run($"compare --source {Q(proj.ProjectFile)} --target {Q(tgt.ConnectionString)} --fail-on-changes");
        Assert.Equal(0, same.ExitCode);
        Assert.True(same.Mentions("sync"), same.ToString());
    }

    [LiveFact]
    public async Task Drift_is_detected_and_pull_writes_the_db_change_into_the_project()
    {

        await using var tgt = await Fx.NewTargetDbAsync();
        using var proj = TempProject.Create("DriftPull");
        proj.AddSql("Schemas/app.sql", "CREATE SCHEMA app;");
        var widgetFile = proj.AddSql("Tables/app.widget.sql", "CREATE TABLE app.widget (id integer PRIMARY KEY, name text);");
        Assert.Equal(0, Run($"publish {Q(proj.ProjectFile)} --connection {Q(tgt.ConnectionString)}").ExitCode);

        // The database drifts ahead of the project (a column added out-of-band).
        await tgt.Db.ExecAsync("ALTER TABLE app.widget ADD COLUMN qty integer NOT NULL DEFAULT 0;");

        var drift = Run($"drift {Q(proj.ProjectFile)} --connection {Q(tgt.ConnectionString)} --fail-on-drift");
        Assert.Equal(6, drift.ExitCode); // Drift gate fires

        // pull rewrites the project file to match the database.
        var pull = Run($"pull {Q(proj.ProjectFile)} --connection {Q(tgt.ConnectionString)}");
        Assert.Equal(0, pull.ExitCode);
        Assert.Contains("qty", File.ReadAllText(widgetFile), StringComparison.OrdinalIgnoreCase);

        // …and now there is no drift.
        Assert.Equal(0, Run($"drift {Q(proj.ProjectFile)} --connection {Q(tgt.ConnectionString)} --fail-on-drift").ExitCode);
    }
}
