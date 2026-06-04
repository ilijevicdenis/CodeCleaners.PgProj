using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PgProj.Core.Model;
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
    public async Task Single_file_project_takes_the_small_N_path_and_matches_sequential()
    {
        var project = MakeProject(0);                 // just schema.sql → exactly one .sql file
        Assert.Single(project.ResolveSqlFiles());     // confirm we exercise the count==1 guard

        var sequential = project.Build();
        var parallel = await project.BuildAsync();

        Assert.Equal(ModelJson.Serialize(sequential.Model), ModelJson.Serialize(parallel.Model));
        Assert.Equal(sequential.Diagnostics, parallel.Diagnostics);
        Assert.Equal(sequential.Files, parallel.Files);
    }

    [Fact]
    public async Task Empty_project_takes_the_small_N_path_and_matches_sequential()
    {
        var proj = Path.Combine(_dir, "Empty.pgproj");
        File.WriteAllText(proj, """
            <Project><PropertyGroup><Name>Empty</Name><DefaultSchema>app</DefaultSchema></PropertyGroup>
            <ItemGroup><Build Include="**/*.sql" /></ItemGroup></Project>
            """);
        var project = DatabaseProject.Load(proj);
        Assert.Empty(project.ResolveSqlFiles());      // confirm we exercise the count==0 guard

        var sequential = project.Build();
        var parallel = await project.BuildAsync();

        Assert.Equal(ModelJson.Serialize(sequential.Model), ModelJson.Serialize(parallel.Model));
        Assert.Equal(sequential.Diagnostics, parallel.Diagnostics);
        Assert.Empty(parallel.Model.Tables);
    }

    [Fact]
    public async Task Parallel_build_is_byte_identical_to_sequential_over_a_diverse_corpus()
    {
        // A representative spread of object kinds across many files, plus a re-declared schema and a
        // duplicate table (so FindDuplicates fires) — the merge must reproduce the sequential build
        // exactly: same model (serialized JSON), same diagnostics, same file list, in the same order.
        var project = MakeDiverseProject();

        var sequential = project.Build();
        var parallel = await project.BuildAsync();

        Assert.Equal(ModelJson.Serialize(sequential.Model), ModelJson.Serialize(parallel.Model));
        Assert.Equal(sequential.Diagnostics, parallel.Diagnostics);   // order-sensitive sequence compare
        Assert.Equal(sequential.Files, parallel.Files);
        Assert.NotEmpty(parallel.Diagnostics);                        // the duplicate table was reported
    }

    private DatabaseProject MakeDiverseProject()
    {
        var proj = Path.Combine(_dir, "P.pgproj");
        File.WriteAllText(proj, """
            <Project><PropertyGroup><Name>P</Name><DefaultSchema>app</DefaultSchema></PropertyGroup>
            <ItemGroup><Build Include="**/*.sql" /></ItemGroup></Project>
            """);

        File.WriteAllText(Path.Combine(_dir, "00_schema.sql"), "CREATE SCHEMA app;");
        File.WriteAllText(Path.Combine(_dir, "01_schema_again.sql"), "CREATE SCHEMA app;"); // first-occurrence wins
        File.WriteAllText(Path.Combine(_dir, "02_schema_rep.sql"), "CREATE SCHEMA rep;");

        for (var i = 0; i < 30; i++)
            File.WriteAllText(Path.Combine(_dir, $"10_t{i:D3}.sql"),
                $"CREATE TABLE app.t{i:D3} (id int PRIMARY KEY, parent_id int REFERENCES app.t000 (id), name text NOT NULL DEFAULT 'x');");

        File.WriteAllText(Path.Combine(_dir, "20_view.sql"), "CREATE VIEW app.v_t AS SELECT id, name FROM app.t000;");
        File.WriteAllText(Path.Combine(_dir, "21_matview.sql"), "CREATE MATERIALIZED VIEW app.mv_t AS SELECT id FROM app.t001;");
        File.WriteAllText(Path.Combine(_dir, "30_seq.sql"), "CREATE SEQUENCE app.s_main START 5 INCREMENT 2;");
        File.WriteAllText(Path.Combine(_dir, "40_index.sql"), "CREATE INDEX ix_t000_name ON app.t000 (name);");
        File.WriteAllText(Path.Combine(_dir, "50_fn.sql"),
            "CREATE FUNCTION app.add(a int, b int) RETURNS int LANGUAGE sql AS $$ SELECT a + b; $$;");
        File.WriteAllText(Path.Combine(_dir, "60_comment.sql"), "COMMENT ON TABLE app.t000 IS 'root';");
        File.WriteAllText(Path.Combine(_dir, "61_extension.sql"), "CREATE EXTENSION IF NOT EXISTS pgcrypto;");
        File.WriteAllText(Path.Combine(_dir, "70_dup.sql"), "CREATE TABLE app.t000 (id int PRIMARY KEY);"); // duplicate → diagnostic

        return DatabaseProject.Load(proj);
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
