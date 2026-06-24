using PgProj.Blackbox.Tests.Infrastructure;

namespace PgProj.Blackbox.Tests;

/// <summary>
/// Blackbox for the offline artifacts — project snapshots, package inspect/verify, and the integrity
/// guard that rejects a corrupt .pgpkg (exit 1). No database needed.
/// </summary>
public sealed class Cli_SnapshotPackageTests(BlackboxFixture fx) : BlackboxTestBase(fx)
{

    private static TempProject Sample(string name)
    {
        var p = TempProject.Create(name);
        p.AddSql("Schemas/app.sql", "CREATE SCHEMA app;");
        p.AddSql("Tables/app.t.sql", "CREATE TABLE app.t (id integer PRIMARY KEY, name text);");
        return p;
    }

    [CliFact]
    public void Snapshot_create_then_compare_against_the_unchanged_project_is_in_sync()
    {

        using var proj = Sample("SnapCycle");
        var snapDir = Path.Combine(proj.Dir, "Snapshots");

        var create = Run($"snapshot create {Q(proj.ProjectFile)} -o {Q(snapDir)}");
        Assert.Equal(0, create.ExitCode);
        var snap = Directory.EnumerateFiles(snapDir, "*.pgpkg").Single();

        var compare = Run($"snapshot compare {Q(snap)} {Q(proj.ProjectFile)}");
        Assert.Equal(0, compare.ExitCode);
        Assert.True(compare.Mentions("sync"), compare.ToString());
    }

    [CliFact]
    public void Build_twice_produces_equivalent_packages_that_verify_equal()
    {

        using var proj = Sample("VerifyEqual");
        var a = Path.Combine(proj.Dir, "bin", "a.pgpkg");
        var b = Path.Combine(proj.Dir, "bin", "b.pgpkg");
        Assert.Equal(0, Run($"build {Q(proj.ProjectFile)} --package {Q(a)}").ExitCode);
        Assert.Equal(0, Run($"build {Q(proj.ProjectFile)} --package {Q(b)}").ExitCode);

        var verify = Run($"verify {Q(a)} {Q(b)}");
        Assert.Equal(0, verify.ExitCode);
    }

    [CliFact]
    public void Pkg_inspect_lists_the_objects()
    {

        using var proj = Sample("InspectMe");
        var pkg = Path.Combine(proj.Dir, "bin", "InspectMe.pgpkg");
        Assert.Equal(0, Run($"build {Q(proj.ProjectFile)} --package {Q(pkg)}").ExitCode);

        var r = Run($"pkg inspect {Q(pkg)}");
        Assert.Equal(0, r.ExitCode);
        Assert.True(r.Mentions("app.t"), r.ToString());
    }

    [CliFact]
    public void A_corrupt_package_is_rejected()
    {

        var bad = Path.Combine(Path.GetTempPath(), "pgproj-bb", "corrupt_" + Guid.NewGuid().ToString("N")[..8] + ".pgpkg");
        Directory.CreateDirectory(Path.GetDirectoryName(bad)!);
        File.WriteAllBytes(bad, new byte[] { 0x50, 0x4B, 0x03, 0x04, 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01 });
        try
        {
            var r = Run($"pkg inspect {Q(bad)}");
            Assert.NotEqual(0, r.ExitCode); // integrity / format failure
        }
        finally { try { File.Delete(bad); } catch { } }
    }
}
