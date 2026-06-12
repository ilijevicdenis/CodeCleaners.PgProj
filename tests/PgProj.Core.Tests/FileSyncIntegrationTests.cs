using System;
using System.IO;
using System.Threading.Tasks;
using Npgsql;
using PgProj.Core.Introspection;
using PgProj.Core.Model;
using PgProj.Core.Project;
using PgProj.Core.Publishing;
using PgProj.Core.Sync;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// EP-SYNC file-level: the engine behind the editors' "Sync with Database" — one project file
/// inspected against a real PostgreSQL and synced in EITHER direction. Drives the full loop the
/// VS command exposes: detect drift caused by a direct DB change, take the database's version into
/// the file, push the file's version back (including a destructive column drop). Skipped (no-op)
/// when PGPROJ_TEST_CONNECTION is unset, like every DB-backed class in this suite.
/// </summary>
public sealed class FileSyncIntegrationTests : IClassFixture<ThrowawayDatabaseFixture>, IDisposable
{
    private readonly ThrowawayDatabaseFixture _fixture;
    private readonly string _dir;

    public FileSyncIntegrationTests(ThrowawayDatabaseFixture fixture)
    {
        _fixture = fixture;
        _dir = Path.Combine(Path.GetTempPath(), "pgproj-filesync-" + Guid.NewGuid().ToString("N")[..8]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string ScaffoldProject()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "app", "Tables"));
        var proj = Path.Combine(_dir, "Db.pgproj");
        File.WriteAllText(proj, """
            <Project DefaultTargets="Build">
              <PropertyGroup>
                <Name>FileSyncDb</Name>
                <DefaultSchema>public</DefaultSchema>
              </PropertyGroup>
              <ItemGroup>
                <Build Include="**/*.sql" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(_dir, "app", "Tables", "widgets.sql"),
            "CREATE TABLE app.widgets (id integer NOT NULL, label text);\n");
        return proj;
    }

    private async Task ExecAsync(string conn, string sql)
    {
        await using var c = new NpgsqlConnection(conn);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    private static Task<DatabaseModel> ReadLiveAsync(string conn) => new LiveDatabaseReader().ReadAsync(conn);

    [Fact]
    public async Task Full_two_way_file_sync_loop_against_a_live_database()
    {
        var conn = _fixture.ConnectionString;
        if (string.IsNullOrWhiteSpace(conn)) return;   // no live DB — treated as a skip

        var projPath = ScaffoldProject();
        var project = DatabaseProject.Load(projPath);
        const string rel = "app/Tables/widgets.sql";

        // seed the DB to match the project
        await ExecAsync(conn, "CREATE SCHEMA app; CREATE TABLE app.widgets (id integer NOT NULL, label text);");

        // 1) in sync: the verdict is semantic, so formatting differences don't count as drift
        var state = await FileSync.InspectAsync(project, await ReadLiveAsync(conn), rel);
        Assert.Equal(FileSync.FileSyncStatus.Identical, state.Status);

        // 2) a hotfix lands directly in the database → the file drifts
        await ExecAsync(conn, "ALTER TABLE app.widgets ADD COLUMN hotfix_flag boolean DEFAULT false;");
        state = await FileSync.InspectAsync(project, await ReadLiveAsync(conn), rel);
        Assert.Equal(FileSync.FileSyncStatus.Differs, state.Status);
        Assert.Contains("hotfix_flag", state.DatabaseText);
        Assert.DoesNotContain("hotfix_flag", state.LocalText);

        // 3) the user takes the database's version → file updated, drift gone
        FileSync.ApplyToLocal(project, state);
        Assert.Contains("hotfix_flag", File.ReadAllText(Path.Combine(_dir, "app", "Tables", "widgets.sql")));
        state = await FileSync.InspectAsync(project, await ReadLiveAsync(conn), rel);
        Assert.Equal(FileSync.FileSyncStatus.Identical, state.Status);

        // 4) the user reverts the file and pushes THEIR version — a destructive drop, so it must
        //    refuse to appear without allowDrops and execute with it
        File.WriteAllText(Path.Combine(_dir, "app", "Tables", "widgets.sql"),
            "CREATE TABLE app.widgets (id integer NOT NULL, label text);\n");
        var (script, count, destructive) = await FileSync.BuildPushScriptAsync(
            project, await ReadLiveAsync(conn), rel, allowDrops: false);
        Assert.Equal(0, count); // the only difference is a drop → suppressed without allowDrops

        (script, count, destructive) = await FileSync.BuildPushScriptAsync(
            project, await ReadLiveAsync(conn), rel, allowDrops: true);
        Assert.True(count > 0);
        Assert.True(destructive);
        Assert.Contains("hotfix_flag", script);

        await new DatabaseDeployer().ExecuteAsync(conn, script);
        state = await FileSync.InspectAsync(project, await ReadLiveAsync(conn), rel);
        Assert.Equal(FileSync.FileSyncStatus.Identical, state.Status);
    }

    [Fact]
    public async Task Push_script_is_scoped_to_the_requested_file_only()
    {
        var conn = _fixture.ConnectionString;
        if (string.IsNullOrWhiteSpace(conn)) return;

        var projPath = ScaffoldProject();
        // second file with its own drift — must NOT leak into widgets.sql's push script
        File.WriteAllText(Path.Combine(_dir, "app", "Tables", "gadgets.sql"),
            "CREATE TABLE app.gadgets (id integer NOT NULL, extra_local_col text);\n");
        var project = DatabaseProject.Load(projPath);

        await ExecAsync(conn,
            "CREATE SCHEMA IF NOT EXISTS app; " +
            "CREATE TABLE IF NOT EXISTS app.widgets (id integer NOT NULL, label text); " +
            "CREATE TABLE app.gadgets (id integer NOT NULL);");

        // widgets drifts locally too
        File.WriteAllText(Path.Combine(_dir, "app", "Tables", "widgets.sql"),
            "CREATE TABLE app.widgets (id integer NOT NULL, label text, widget_only_col integer);\n");

        var (script, count, _) = await FileSync.BuildPushScriptAsync(
            project, await ReadLiveAsync(conn), "app/Tables/widgets.sql");
        Assert.True(count > 0);
        Assert.Contains("widget_only_col", script);
        Assert.DoesNotContain("extra_local_col", script); // the other file's drift stays out
    }
}
