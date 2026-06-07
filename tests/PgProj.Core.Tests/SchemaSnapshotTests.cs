using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PgProj.Core.Cli;
using PgProj.Core.Comparison;
using PgProj.Core.Model;
using PgProj.Core.Snapshot;
using PgProj.Core.Versioning;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// DB-free unit tests for the <c>.schema.snapshot</c> artifact (issue #52): write→read model round-trip,
/// manifest fidelity + checksum integrity, staleness detection on a version/format mismatch, and the
/// snapshot resolving as an offline <see cref="EndpointKind.Snapshot"/> endpoint with no DB connection.
/// </summary>
public sealed class SchemaSnapshotTests : IDisposable
{
    private readonly string _dir;

    public SchemaSnapshotTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pgsnap_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private const string Stamp = "2026-01-01T00:00:00Z";
    private const string Tool = "test-1.2.3";

    private static DatabaseModel Sample() => TestModel.Build(
        """
        CREATE SCHEMA app;
        CREATE TABLE app.customers (id int PRIMARY KEY, name text NOT NULL);
        CREATE TABLE app.orders (id int PRIMARY KEY, cid int REFERENCES app.customers (id));
        CREATE VIEW app.v AS SELECT id FROM app.customers;
        """);

    // ---- round-trip: write a model → read back → identical model -------------------------

    [Fact]
    public void Snapshot_round_trips_the_model_identically()
    {
        var model = Sample();
        var snap = SchemaSnapshot.Create(model, "PostgreSQL 18.0", 18, Tool, Stamp, "mydb");
        var path = Path.Combine(_dir, "db.schema.snapshot");
        snap.Write(path);

        var read = SchemaSnapshot.Read(path);

        // The model deserialized from the snapshot equals the in-memory model, compared via the same
        // canonical JSON the model build writes — the mandated "identical DatabaseModel" check.
        Assert.Equal(ModelJson.Serialize(model), ModelJson.Serialize(read.Model));
        Assert.Equal(model.Tables.Count, read.Model.Tables.Count);
        Assert.Equal(model.Views.Count, read.Model.Views.Count);
    }

    [Fact]
    public void Manifest_fields_survive_a_write_read_round_trip()
    {
        var snap = SchemaSnapshot.Create(Sample(), "PostgreSQL 16.2", 16, Tool, Stamp, "mydb");
        var path = Path.Combine(_dir, "db.schema.snapshot");
        snap.Write(path);

        var m = SchemaSnapshot.Read(path).Manifest;
        Assert.Equal(SchemaSnapshot.CurrentFormatVersion, m.FormatVersion);
        Assert.Equal("PostgreSQL 16.2", m.SourcePgVersion);
        Assert.Equal(16, m.SourcePgMajorVersion);
        Assert.Equal(Tool, m.ToolVersion);
        Assert.Equal(Stamp, m.CreatedUtc);
        Assert.Equal("mydb", m.SourceName);
        Assert.StartsWith("sha256:", m.ModelChecksum);
    }

    [Fact]
    public void ReadManifest_loads_the_header_without_decoding_the_model()
    {
        var snap = SchemaSnapshot.Create(Sample(), "PostgreSQL 18.0", 18, Tool, Stamp, "mydb");
        var path = Path.Combine(_dir, "db.schema.snapshot");
        snap.Write(path);

        var m = SchemaSnapshot.ReadManifest(path);
        Assert.Equal(18, m.SourcePgMajorVersion);
    }

    [Fact]
    public void Same_model_and_stamp_produce_byte_identical_snapshots()
    {
        var model = Sample();
        string Write()
        {
            var s = SchemaSnapshot.Create(model, "PostgreSQL 18.0", 18, Tool, Stamp, "mydb");
            return s.ToJson();
        }
        Assert.Equal(Write(), Write());   // deterministic: no clock read in core
    }

    // ---- integrity guard ----------------------------------------------------------------

    [Fact]
    public void Tampered_model_payload_is_detected()
    {
        var snap = SchemaSnapshot.Create(Sample(), "PostgreSQL 18.0", 18, Tool, Stamp);
        var path = Path.Combine(_dir, "tampered.schema.snapshot");
        snap.Write(path);

        // Corrupt the embedded model payload without fixing the checksum → integrity failure.
        var text = File.ReadAllText(path).Replace("app.customers", "app.tampered");
        File.WriteAllText(path, text);

        var ex = Assert.Throws<SchemaSnapshotFormatException>(() => SchemaSnapshot.Read(path));
        Assert.Contains("integrity check failed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Malformed_json_reports_a_clear_error()
    {
        var path = Path.Combine(_dir, "bad.schema.snapshot");
        File.WriteAllText(path, "{ not valid json");
        var ex = Assert.Throws<SchemaSnapshotFormatException>(() => SchemaSnapshot.Read(path));
        Assert.Contains("malformed JSON", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- staleness ----------------------------------------------------------------------

    [Fact]
    public void Snapshot_is_fresh_when_version_matches()
    {
        var snap = SchemaSnapshot.Create(Sample(), "PostgreSQL 18.0", 18, Tool, Stamp);
        var staleness = snap.CheckStaleness(expectedMajorVersion: 18);
        Assert.False(staleness.IsStale);
        Assert.Empty(staleness.Reasons);
    }

    [Fact]
    public void Snapshot_is_stale_on_a_source_version_mismatch()
    {
        var snap = SchemaSnapshot.Create(Sample(), "PostgreSQL 15.6", 15, Tool, Stamp);
        var staleness = snap.CheckStaleness(expectedMajorVersion: 18);
        Assert.True(staleness.IsStale);
        Assert.Contains(staleness.Reasons, r => r.Contains("PostgreSQL 15") && r.Contains("18"));
    }

    [Fact]
    public void Snapshot_version_check_is_skipped_when_no_expectation_is_given()
    {
        var snap = SchemaSnapshot.Create(Sample(), "PostgreSQL 15.6", 15, Tool, Stamp);
        Assert.False(snap.CheckStaleness(expectedMajorVersion: null).IsStale);
        // The profile overload uses the profile's major version, so a mismatch is flagged.
        Assert.True(snap.CheckStaleness(PostgresVersionProfile.ForMajor(18)).IsStale);
    }

    [Fact]
    public void Snapshot_is_stale_on_a_format_version_mismatch()
    {
        var manifest = new SchemaSnapshotManifest(
            "PostgreSQL 18.0", 18, Tool, Stamp, "sha256:irrelevant")
        {
            FormatVersion = "0.9-legacy",
        };
        var snap = new SchemaSnapshot { Manifest = manifest, Model = Sample() };
        var staleness = snap.CheckStaleness(expectedMajorVersion: 18);   // version matches
        Assert.True(staleness.IsStale);                                  // …but the format does not
        Assert.Contains(staleness.Reasons, r => r.Contains("format version"));
    }

    // ---- endpoint resolution (offline, no DB) -------------------------------------------

    [Fact]
    public void Classify_recognises_the_snapshot_suffix()
    {
        Assert.Equal(EndpointKind.Snapshot, EndpointResolver.Classify("db.schema.snapshot"));
        Assert.Equal(EndpointKind.Snapshot, EndpointResolver.Classify(@"C:\path\DB.SCHEMA.SNAPSHOT"));
        // It must not be misclassified as a project just because the file exists.
        var path = Path.Combine(_dir, "exists.schema.snapshot");
        SchemaSnapshot.Create(Sample(), "PostgreSQL 18.0", 18, Tool, Stamp).Write(path);
        Assert.Equal(EndpointKind.Snapshot, EndpointResolver.Classify(path));
    }

    [Fact]
    public async Task Snapshot_endpoint_resolves_to_the_same_model_offline()
    {
        var model = Sample();
        var path = Path.Combine(_dir, "db.schema.snapshot");
        SchemaSnapshot.Create(model, "PostgreSQL 18.0", 18, Tool, Stamp, "mydb").Write(path);

        var resolved = await EndpointResolver.ResolveAsync(path);

        Assert.Equal(EndpointKind.Snapshot, resolved.Kind);
        Assert.Equal("mydb", resolved.DisplayName);
        Assert.NotNull(resolved.SnapshotManifest);
        Assert.Equal(18, resolved.SnapshotManifest!.SourcePgMajorVersion);
        // The resolved model equals the captured one — no re-introspection.
        Assert.Equal(ModelJson.Serialize(model), ModelJson.Serialize(resolved.Model));
    }

    [Fact]
    public async Task Comparing_a_project_against_its_own_snapshot_is_in_sync()
    {
        // A snapshot of a model, compared to that same model, yields no changes — proving the offline
        // snapshot endpoint feeds the identical model into the one compare code path.
        var model = Sample();
        var path = Path.Combine(_dir, "db.schema.snapshot");
        SchemaSnapshot.Create(model, "PostgreSQL 18.0", 18, Tool, Stamp).Write(path);

        var resolved = await EndpointResolver.ResolveAsync(path);
        var set = SchemaCompare.Of(model, resolved.Model);
        Assert.True(set.InSync);
    }
}
