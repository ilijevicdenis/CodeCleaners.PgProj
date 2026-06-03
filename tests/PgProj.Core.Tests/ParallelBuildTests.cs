using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PgProj.Core.Project;
using Xunit;

namespace PgProj.Core.Tests;

public class ParallelBuildTests : IDisposable
{
    private readonly string _dir;

    public ParallelBuildTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pgproj_par_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private DatabaseProject MakeProject(int tableCount)
    {
        var proj = Path.Combine(_dir, "P.pgproj");
        File.WriteAllText(proj, """
            <Project><PropertyGroup><Name>P</Name><DefaultSchema>app</DefaultSchema></PropertyGroup>
            <ItemGroup><Build Include="**/*.sql" /></ItemGroup></Project>
            """);
        File.WriteAllText(Path.Combine(_dir, "schema.sql"), "CREATE SCHEMA app;");
        for (var i = 0; i < tableCount; i++)
            File.WriteAllText(Path.Combine(_dir, $"t{i:D3}.sql"),
                $"CREATE TABLE app.t{i:D3} (id int PRIMARY KEY, name text NOT NULL);");
        return DatabaseProject.Load(proj);
    }

    [Fact]
    public async Task Parallel_build_matches_sequential_build_deterministically()
    {
        var project = MakeProject(40);

        var sequential = project.Build();
        var parallel = await project.BuildAsync();

        Assert.Empty(parallel.Diagnostics);
        // Same set and ORDER of tables — deterministic merge regardless of completion order.
        Assert.Equal(
            sequential.Model.Tables.Select(t => t.QualifiedName),
            parallel.Model.Tables.Select(t => t.QualifiedName));
        Assert.Equal(sequential.Model.Tables.Count, parallel.Model.Tables.Count);
        Assert.Single(parallel.Model.Schemas); // schema deduped at merge
    }

    [Fact]
    public async Task Parallel_build_isolates_a_bad_file()
    {
        var project = MakeProject(5);
        File.WriteAllText(Path.Combine(_dir, "bad.sql"), "CREATE TABLE app.broken ( this is not valid sql");

        var result = await project.BuildAsync();
        // Good tables still parsed; the bad file surfaced as a diagnostic, not a crash.
        Assert.True(result.Model.Tables.Count >= 5);
    }
}
