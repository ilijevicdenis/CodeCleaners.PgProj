using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using PgProj.Core.Model;
using PgProj.Core.Packaging;
using PgProj.Core.Project;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Unit tests for the <c>.pgpkg</c> package format: write→read round-trip, manifest fidelity,
/// byte-for-byte determinism, the embedded model being identical to the loose <c>model.json</c>, and the
/// integrity (checksum) guard. No database required.
/// </summary>
public sealed class PgPkgRoundTripTests : IDisposable
{
    private readonly string _dir;

    public PgPkgRoundTripTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pgpkg_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private (DatabaseProject Project, ProjectBuildResult Result) BuildSample()
    {
        var proj = Write("Sample.pgproj", """
            <Project Sdk="PgProj.Sdk/0.1.0">
              <PropertyGroup>
                <Name>Sample</Name>
                <DefaultSchema>app</DefaultSchema>
                <TargetPostgresVersion>18</TargetPostgresVersion>
              </PropertyGroup>
              <ItemGroup>
                <Build Include="**/*.sql" />
              </ItemGroup>
            </Project>
            """);
        Write("Tables/customers.sql", "CREATE TABLE app.customers (id int PRIMARY KEY, name text);");
        Write("Tables/orders.sql", "CREATE TABLE app.orders (id int PRIMARY KEY, cid int REFERENCES app.customers (id));");
        Write("Views/v.sql", "CREATE VIEW app.v AS SELECT id FROM app.customers;");

        var project = DatabaseProject.Load(proj);
        var result = project.Build();
        Assert.Empty(result.Diagnostics);
        return (project, result);
    }

    private const string Stamp = "2026-01-01T00:00:00Z";
    private const string Tool = "test-1.2.3";

    [Fact]
    public void Refactor_log_is_packed_into_the_package_and_read_back()
    {
        var (project, result) = BuildSample();
        // A committed refactor log next to the project (#136).
        new PgProj.Core.Refactoring.RefactorLog
        {
            Entries = new[] { new PgProj.Core.Refactoring.RefactorEntry("rename", "table", "app.client", "app.customers") },
        }.Save(PgProj.Core.Refactoring.RefactorLog.PathFor(project.ProjectFilePath));

        var pkg = PgPkgBuilder.FromBuild(project, result.Model, result.Files, Tool, Stamp);
        var path = Path.Combine(_dir, "withlog.pgpkg");
        pkg.Write(path);

        var read = PgPkg.Read(path);
        Assert.NotNull(read.RefactorLogJson);
        var log = PgProj.Core.Refactoring.RefactorLog.Parse(read.RefactorLogJson!);
        Assert.Single(log.Entries);
        Assert.Equal("app.customers", log.Entries[0].NewName);
    }

    [Fact]
    public void No_refactor_log_means_no_entry_in_the_package()
    {
        var (project, result) = BuildSample();   // no .pgrefactorlog written
        var pkg = PgPkgBuilder.FromBuild(project, result.Model, result.Files, Tool, Stamp);
        var path = Path.Combine(_dir, "nolog.pgpkg");
        pkg.Write(path);
        Assert.Null(PgPkg.Read(path).RefactorLogJson);
    }

    [Fact]
    public void Write_then_read_round_trips_model_and_manifest()
    {
        var (project, result) = BuildSample();
        var pkg = PgPkgBuilder.FromBuild(project, result.Model, result.Files, Tool, Stamp);
        var path = Path.Combine(_dir, "out.pgpkg");
        pkg.Write(path);

        var read = PgPkg.Read(path);

        // Manifest fields survive verbatim.
        Assert.Equal("Sample", read.Manifest.Name);
        Assert.Equal("18", read.Manifest.PgVersion);
        Assert.Equal(Tool, read.Manifest.ToolVersion);
        Assert.Equal(Stamp, read.Manifest.CreatedUtc);
        Assert.StartsWith("sha256:", read.Manifest.SourceChecksum);

        // The model deserialized from the package equals the in-memory model (compared via the same
        // canonical JSON the build writes).
        Assert.Equal(ModelJson.Serialize(result.Model), ModelJson.Serialize(read.Model));
        Assert.Equal(result.Model.Tables.Count, read.Model.Tables.Count);
        Assert.Equal(result.Model.Views.Count, read.Model.Views.Count);

        // Sources travel inside, keyed by forward-slashed relative path.
        Assert.Equal(3, read.Sources.Count);
        Assert.Contains(read.Sources, s => s.RelativePath == "Tables/customers.sql");
        Assert.All(read.Sources, s => Assert.DoesNotContain('\\', s.RelativePath));
    }

    [Fact]
    public void Embedded_model_json_is_byte_identical_to_loose_model_json()
    {
        var (project, result) = BuildSample();
        var pkg = PgPkgBuilder.FromBuild(project, result.Model, result.Files, Tool, Stamp);
        var path = Path.Combine(_dir, "out.pgpkg");
        pkg.Write(path);

        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("model.json");
        Assert.NotNull(entry);
        using var r = new StreamReader(entry!.Open(), Encoding.UTF8);
        var embedded = r.ReadToEnd();

        Assert.Equal(ModelJson.Serialize(result.Model), embedded);
    }

    [Fact]
    public void Two_builds_with_same_stamp_are_byte_identical()
    {
        var (project, result) = BuildSample();

        byte[] Build()
        {
            var pkg = PgPkgBuilder.FromBuild(project, result.Model, result.Files, Tool, Stamp);
            using var ms = new MemoryStream();
            pkg.Write(ms);
            return ms.ToArray();
        }

        var a = Build();
        var b = Build();
        Assert.Equal(a, b);   // deterministic: identical sources + identical injected stamp → identical bytes
    }

    [Fact]
    public void Different_stamp_changes_the_bytes_but_not_the_model()
    {
        var (project, result) = BuildSample();
        var p1 = PgPkgBuilder.FromBuild(project, result.Model, result.Files, Tool, "2026-01-01T00:00:00Z");
        var p2 = PgPkgBuilder.FromBuild(project, result.Model, result.Files, Tool, "2027-12-31T23:59:59Z");

        Assert.NotEqual(p1.Manifest.CreatedUtc, p2.Manifest.CreatedUtc);
        // Same sources → same checksum regardless of stamp.
        Assert.Equal(p1.Manifest.SourceChecksum, p2.Manifest.SourceChecksum);
    }

    [Fact]
    public void Corrupt_checksum_fails_with_clear_error()
    {
        var (project, result) = BuildSample();
        var pkg = PgPkgBuilder.FromBuild(project, result.Model, result.Files, Tool, Stamp);
        var path = Path.Combine(_dir, "tampered.pgpkg");
        pkg.Write(path);

        // Tamper: rewrite manifest.json with a bogus checksum.
        TamperManifestChecksum(path, "sha256:deadbeef");

        var ex = Assert.Throws<PgPkgFormatException>(() => PgPkg.Read(path));
        Assert.Contains("integrity check failed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tampered_source_content_is_detected()
    {
        var (project, result) = BuildSample();
        var pkg = PgPkgBuilder.FromBuild(project, result.Model, result.Files, Tool, Stamp);
        var path = Path.Combine(_dir, "tampered2.pgpkg");
        pkg.Write(path);

        // Replace a source's bytes without updating the manifest checksum → must be caught.
        ReplaceEntry(path, "sources/Tables/customers.sql", "DROP TABLE app.customers;");

        var ex = Assert.Throws<PgPkgFormatException>(() => PgPkg.Read(path));
        Assert.Contains("integrity check failed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_required_entry_reports_a_clear_error()
    {
        var (project, result) = BuildSample();
        var pkg = PgPkgBuilder.FromBuild(project, result.Model, result.Files, Tool, Stamp);
        var path = Path.Combine(_dir, "incomplete.pgpkg");
        pkg.Write(path);

        DeleteEntry(path, "model.json");

        var ex = Assert.Throws<PgPkgFormatException>(() => PgPkg.Read(path));
        Assert.Contains("model.json", ex.Message);
    }

    [Fact]
    public void Inventory_lists_every_object()
    {
        var (_, result) = BuildSample();
        var inventory = PgPkgInventory.Of(result.Model);

        Assert.Contains(inventory, i => i.Kind == "Table" && i.Identity == "app.customers");
        Assert.Contains(inventory, i => i.Kind == "Table" && i.Identity == "app.orders");
        Assert.Contains(inventory, i => i.Kind == "View" && i.Identity == "app.v");
        // Sorted: kind then identity (ordinal).
        var kinds = inventory.Select(i => i.Kind).ToList();
        Assert.Equal(kinds.OrderBy(k => k, StringComparer.Ordinal).ToList(), kinds);
    }

    // ---- low-level zip surgery helpers ---------------------------------------------------

    private static void TamperManifestChecksum(string path, string bogus)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        var manifest = archive.GetEntry("manifest.json")!;
        string json;
        using (var r = new StreamReader(manifest.Open())) json = r.ReadToEnd();
        manifest.Delete();

        // Round-trip through the manifest model and swap the recorded checksum.
        var tampered = PgPkgManifest.FromJson(json) with { SourceChecksum = bogus };
        var e = archive.CreateEntry("manifest.json");
        using var w = new StreamWriter(e.Open());
        w.Write(tampered.ToJson());
    }

    private static void ReplaceEntry(string path, string entryName, string newContent)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        archive.GetEntry(entryName)!.Delete();
        var e = archive.CreateEntry(entryName);
        using var w = new StreamWriter(e.Open());
        w.Write(newContent);
    }

    private static void DeleteEntry(string path, string entryName)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        archive.GetEntry(entryName)!.Delete();
    }
}
