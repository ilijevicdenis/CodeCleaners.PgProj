using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PgProj.Core.Cli;
using PgProj.Core.Comparison;
using PgProj.Core.Introspection;
using PgProj.Core.Publishing;
using PgProj.Core.Model;
using PgProj.Core.Snapshot;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// DB-gated integration tests for the <c>.schema.snapshot</c> artifact (issue #52). Greenfield-deploys a
/// small schema to a throwaway database, captures a snapshot, then proves that comparing a project against
/// the OFFLINE snapshot yields the SAME change set as comparing against the LIVE database directly — and
/// that the snapshot-side compare makes no DB connection. Skipped unless PGPROJ_TEST_CONNECTION is set
/// (each run gets its own isolated database via <see cref="ThrowawayDatabaseFixture"/>).
/// </summary>
public sealed class SchemaSnapshotIntegrationTests : IClassFixture<ThrowawayDatabaseFixture>, IDisposable
{
    private readonly ThrowawayDatabaseFixture _fixture;
    private readonly string _dir;

    public SchemaSnapshotIntegrationTests(ThrowawayDatabaseFixture fixture)
    {
        _fixture = fixture;
        _dir = Path.Combine(Path.GetTempPath(), "pgsnap_it_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [DbFact]
    public async Task Snapshot_then_compare_offline_matches_comparing_live()
    {
        var conn = _fixture.ConnectionString;
        if (string.IsNullOrWhiteSpace(conn)) return;   // no live DB — treated as a skip

        // Deploy a small schema to the throwaway DB so there is something to snapshot.
        var live0 = TestModel.Build(
            """
            CREATE SCHEMA snap_it;
            CREATE TABLE snap_it.t (id int PRIMARY KEY, name text NOT NULL);
            """);
        var create = new SchemaComparer().Compare(live0, new DatabaseModel());
        var script = new DeployScriptGenerator().Generate(create, new DeployOptions { WrapInTransaction = true });
        await new DatabaseDeployer().ExecuteAsync(conn, script);

        // Capture a snapshot of the live DB (introspect once).
        var snapshot = await new SchemaSnapshotReader().CaptureAsync(conn, "test", "2026-01-01T00:00:00Z");
        var path = Path.Combine(_dir, "db.schema.snapshot");
        snapshot.Write(path);

        // The source project differs from the live DB (adds a column) so the diff is non-trivial.
        var projectModel = TestModel.Build(
            """
            CREATE SCHEMA snap_it;
            CREATE TABLE snap_it.t (id int PRIMARY KEY, name text NOT NULL, email text);
            """);

        // Compare against the LIVE DB (re-introspects).
        var live = await new LiveDatabaseReader().ReadAsync(conn);
        var liveChanges = SchemaCompare.Of(projectModel, live);

        // Compare against the OFFLINE snapshot (no DB connection on this step).
        var resolved = await EndpointResolver.ResolveAsync(path);
        Assert.Equal(EndpointKind.Snapshot, resolved.Kind);
        var snapChanges = SchemaCompare.Of(projectModel, resolved.Model);

        // SAME change set: same ids, in the same order.
        Assert.Equal(
            liveChanges.Changes.Select(c => c.Id).ToList(),
            snapChanges.Changes.Select(c => c.Id).ToList());
        Assert.Contains(snapChanges.Changes, c => c.ObjectType == "column");

        // The snapshot's captured source major version is real (>= 13) and feeds staleness detection.
        Assert.True(snapshot.Manifest.SourcePgMajorVersion >= 13);
        Assert.False(snapshot.CheckStaleness(snapshot.Manifest.SourcePgMajorVersion).IsStale);
        Assert.True(snapshot.CheckStaleness(snapshot.Manifest.SourcePgMajorVersion + 1).IsStale);
    }
}
