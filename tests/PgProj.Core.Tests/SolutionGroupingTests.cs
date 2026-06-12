using System;
using System.IO;
using System.Linq;
using PgProj.Core.Solutions;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// EP-VS #118 — slngen-style solution grouping: <see cref="SolutionGrouper"/> scans for .pgproj
/// files and groups them into a canonical .slnx whose solution folders mirror the directory tree.
/// </summary>
public sealed class SolutionGroupingTests : IDisposable
{
    private readonly string _dir;

    public SolutionGroupingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pgproj_sln_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string WriteProject(string relative)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<Project><PropertyGroup><Name>X</Name></PropertyGroup></Project>");
        return path;
    }

    // ---- folder derivation ----------------------------------------------------------------

    [Theory]
    [InlineData("Db.pgproj", "")]                                  // file at the solution root
    [InlineData("SampleDb/SampleDb.pgproj", "")]                   // project dir directly under root
    [InlineData("References/Common/Common.pgproj", "/References/")]
    [InlineData("a/b/c/Db.pgproj", "/a/b/")]
    [InlineData(@"References\Sales\Sales.pgproj", "/References/")] // backslashes normalize
    [InlineData("../Outside/Db.pgproj", "")]                       // outside the solution dir → root
    public void Folder_derivation_mirrors_the_directory_tree_minus_the_project_dir(string path, string folder)
        => Assert.Equal(folder, SolutionGrouper.DeriveFolder(path));

    // ---- generate ---------------------------------------------------------------------------

    [Fact]
    public void Generate_groups_every_project_under_root_into_a_canonical_slnx()
    {
        WriteProject("SampleDb/SampleDb.pgproj");
        WriteProject("References/Common/Common.pgproj");
        WriteProject("References/Sales/Sales.pgproj");

        var result = SolutionGrouper.Generate("All", _dir);

        Assert.Equal(Path.Combine(_dir, "All.slnx"), result.SolutionPath);
        Assert.Equal(3, result.AddedProjects.Count);

        var expected =
            """
            <Solution>
              <Project Path="SampleDb/SampleDb.pgproj" />
              <Folder Name="/References/">
                <Project Path="References/Common/Common.pgproj" />
                <Project Path="References/Sales/Sales.pgproj" />
              </Folder>
            </Solution>
            """.ReplaceLineEndings("\n") + "\n";
        Assert.Equal(expected, File.ReadAllText(result.SolutionPath));
    }

    [Fact]
    public void Generate_skips_bin_obj_and_dot_directories()
    {
        WriteProject("Db/Db.pgproj");
        WriteProject("Db/bin/Stale.pgproj");
        WriteProject("Db/obj/Stale.pgproj");
        WriteProject(".git/Hook.pgproj");

        var result = SolutionGrouper.Generate("All", _dir);

        Assert.Equal(["Db/Db.pgproj"], result.Solution.Projects);
    }

    [Fact]
    public void Generate_is_idempotent_and_picks_up_new_projects_on_rerun()
    {
        WriteProject("A/A.pgproj");
        var first = SolutionGrouper.Generate("All", _dir);
        var firstBytes = File.ReadAllText(first.SolutionPath);

        // Re-run with nothing new: byte-identical, nothing added.
        var rerun = SolutionGrouper.Generate("All", _dir);
        Assert.Empty(rerun.AddedProjects);
        Assert.Equal(firstBytes, File.ReadAllText(rerun.SolutionPath));

        // A new project appears: re-run adds exactly it and keeps the old one.
        WriteProject("B/B.pgproj");
        var updated = SolutionGrouper.Generate("All", _dir);
        Assert.Equal(["B/B.pgproj"], updated.AddedProjects);
        Assert.Equal(["A/A.pgproj", "B/B.pgproj"], updated.Solution.Projects);
    }

    [Fact]
    public void Generate_with_separate_root_scans_root_but_writes_relative_to_the_output_dir()
    {
        WriteProject("dbs/One/One.pgproj");
        var outDir = Path.Combine(_dir, "build");

        var result = SolutionGrouper.Generate("All", outDir, rootDirectory: _dir);

        Assert.Equal(["../dbs/One/One.pgproj"], result.Solution.Projects);
        // The folder tree mirrors the scan root, not the "../" walk from the solution directory.
        Assert.Equal(["../dbs/One/One.pgproj"], result.Solution.Folders["/dbs/"]);
    }

    // ---- add --------------------------------------------------------------------------------

    [Fact]
    public void Add_appends_to_an_existing_solution_and_skips_duplicates()
    {
        WriteProject("A/A.pgproj");
        var generated = SolutionGrouper.Generate("All", _dir);
        var extra = WriteProject("Refs/B/B.pgproj");

        var added = SolutionGrouper.Add(generated.SolutionPath, [extra, extra]);

        Assert.Equal(["Refs/B/B.pgproj"], added.AddedProjects);
        Assert.Equal(["A/A.pgproj", "Refs/B/B.pgproj"], added.Solution.Projects);
        Assert.Contains("/Refs/", added.Solution.Folders.Keys);
    }

    [Fact]
    public void Add_throws_for_a_missing_solution_or_project()
    {
        Assert.Throws<FileNotFoundException>(
            () => SolutionGrouper.Add(Path.Combine(_dir, "nope.slnx"), [Path.Combine(_dir, "x.pgproj")]));

        WriteProject("A/A.pgproj");
        var generated = SolutionGrouper.Generate("All", _dir);
        Assert.Throws<FileNotFoundException>(
            () => SolutionGrouper.Add(generated.SolutionPath, [Path.Combine(_dir, "missing.pgproj")]));
    }

    // ---- load / round-trip --------------------------------------------------------------------

    [Fact]
    public void Load_preserves_non_pgproj_projects_and_handwritten_folders()
    {
        var slnx = Path.Combine(_dir, "Mixed.slnx");
        File.WriteAllText(slnx,
            """
            <Solution>
              <Folder Name="/src/">
                <Project Path="src/App/App.csproj" />
              </Folder>
              <Project Path="Loose.pgproj" />
            </Solution>
            """);
        WriteProject("Db/Db.pgproj");

        var result = SolutionGrouper.Add(slnx, [Path.Combine(_dir, "Db/Db.pgproj")]);

        Assert.Equal(["Db/Db.pgproj", "Loose.pgproj", "src/App/App.csproj"], result.Solution.Projects);
        Assert.Equal(["src/App/App.csproj"], result.Solution.Folders["/src/"]);
    }

    [Fact]
    public void Parse_rejects_a_non_solution_document()
        => Assert.Throws<InvalidDataException>(() => SlnxDocument.Parse("<Project></Project>"));

    [Fact]
    public void The_repo_house_style_slnx_round_trips_canonically()
    {
        var solution = SlnxDocument.Parse(
            """
            <Solution>
              <Folder Name="/src/">
                <Project Path="src/PgProj.Cli/PgProj.Cli.csproj" />
              </Folder>
            </Solution>
            """);

        Assert.Equal(["src/PgProj.Cli/PgProj.Cli.csproj"], solution.Projects);
        Assert.Contains("<Folder Name=\"/src/\">", solution.ToXml());
    }
}
