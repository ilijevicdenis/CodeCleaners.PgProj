using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PgProj.Core.Comparison;
using PgProj.Core.Packaging;
using PgProj.Core.Project;
using PgProj.Core.Sync;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Issue #142 — the project-snapshot workflow (a timestamped read-only <c>.pgpkg</c> baseline of the BUILT
/// project model, with no database connection). These exercise the Core operations the
/// <c>pgproj snapshot create/compare/revert</c> CLI orchestrates: a snapshot compared to itself shows no
/// diff, and reverting a project to a snapshot of itself leaves the tree unchanged.
/// </summary>
public sealed class ProjectSnapshotTests : IDisposable
{
    private readonly string _dir;
    public ProjectSnapshotTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pgproj_snap_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Write(string rel, string content)
    {
        var path = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private DatabaseProject Sample()
    {
        var proj = Write("App.pgproj",
            """
            <Project Sdk="PgProj.Sdk/0.1.0">
              <PropertyGroup><Name>App</Name><DefaultSchema>app</DefaultSchema></PropertyGroup>
              <ItemGroup><Build Include="**/*.sql" /></ItemGroup>
            </Project>
            """);
        Write("Tables/customer.sql", "CREATE TABLE app.customer (id int PRIMARY KEY, name text NOT NULL);");
        Write("Tables/orders.sql", "CREATE TABLE app.orders (id int PRIMARY KEY, cid int REFERENCES app.customer (id));");
        return DatabaseProject.Load(proj);
    }

    private async Task<string> CreateSnapshotAsync(DatabaseProject project)
    {
        var result = await project.BuildAsync();
        Assert.Empty(result.Diagnostics);
        var dir = Path.Combine(project.ProjectDirectory, "Snapshots");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "Project_20260101_00-00-00.pgpkg");
        PgPkgBuilder.FromBuild(project, result.Model, result.Files, "test", "2026-01-01T00:00:00Z").Write(path);
        return path;
    }

    [Fact]
    public async Task Create_then_compare_against_itself_shows_no_diff()
    {
        var project = Sample();
        var snapshot = await CreateSnapshotAsync(project);

        var result = await SchemaCompare.RunAsync(snapshot, snapshot);
        Assert.True(result.ChangeSet.InSync, "a snapshot compared to itself must be in sync");
        Assert.Equal(0, result.ChangeSet.Count);
    }

    [Fact]
    public async Task Create_then_compare_against_the_project_shows_no_diff()
    {
        var project = Sample();
        var snapshot = await CreateSnapshotAsync(project);

        // Snapshot (source) vs the live project (target) — built from the same sources → in sync.
        var result = await SchemaCompare.RunAsync(snapshot, project.ProjectFilePath);
        Assert.True(result.ChangeSet.InSync);
    }

    [Fact]
    public async Task Create_then_revert_leaves_the_project_tree_unchanged()
    {
        var project = Sample();
        var built = await project.BuildAsync();
        var snapshotModel = built.Model;   // a snapshot of the current project

        // Reverting the project to a snapshot of itself must find nothing to write.
        var plan = await ReverseSync.PlanAsync(project, snapshotModel, new DriftOptions { AllowDeletes = true });
        Assert.False(plan.HasDrift, "reverting to a snapshot of the unchanged project should be a no-op");
    }

    [Fact]
    public async Task Compare_detects_a_real_difference_between_a_snapshot_and_an_edited_project()
    {
        var project = Sample();
        var snapshot = await CreateSnapshotAsync(project);

        // Edit the project after snapshotting: add a column.
        Write("Tables/customer.sql", "CREATE TABLE app.customer (id int PRIMARY KEY, name text NOT NULL, email text);");
        var edited = DatabaseProject.Load(Path.Combine(_dir, "App.pgproj"));

        // Edited project (source, has the new column) vs the snapshot (target): the diff is the column add.
        var result = await SchemaCompare.RunAsync(edited.ProjectFilePath, snapshot);
        Assert.False(result.ChangeSet.InSync);
        Assert.Contains(result.ChangeSet.Changes, c => c.Change is AddColumnChange);
    }
}
