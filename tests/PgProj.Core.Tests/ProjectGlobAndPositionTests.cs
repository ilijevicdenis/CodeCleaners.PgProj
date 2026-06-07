using System;
using System.IO;
using System.Linq;
using PgProj.Core.Contracts;
using PgProj.Core.Project;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Issue #45 — project model loading hardening: real glob semantics (<c>**</c>, top-level <c>*</c>),
/// <c>&lt;Exclude&gt;</c>/<c>Remove</c> patterns, and source-file/line persistence on every object so
/// the model-tree resolves file:line WITHOUT a second parse pass.
/// </summary>
public sealed class ProjectGlobAndPositionTests : IDisposable
{
    private readonly string _dir;

    public ProjectGlobAndPositionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pgproj_glob_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string RelOf(DatabaseProject p, string full) =>
        Path.GetRelativePath(p.ProjectDirectory, full).Replace('\\', '/');

    // -------------------------------------------------------------------------------------------
    // <Exclude> / Remove
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Exclude_glob_removes_matching_files_from_the_build_set()
    {
        Write("Glob.pgproj", """
            <Project>
              <PropertyGroup><Name>Glob</Name></PropertyGroup>
              <ItemGroup>
                <Build Include="**/*.sql" />
                <Exclude Include="**/*.generated.sql" />
              </ItemGroup>
            </Project>
            """);
        Write("Tables/customers.sql", "CREATE TABLE public.customers (id int PRIMARY KEY);");
        Write("Tables/orders.generated.sql", "CREATE TABLE public.orders (id int PRIMARY KEY);");

        var project = DatabaseProject.Load(Path.Combine(_dir, "Glob.pgproj"));
        Assert.Contains("**/*.generated.sql", project.ExcludePatterns);

        var files = project.ResolveSqlFiles().Select(f => RelOf(project, f)).ToList();
        Assert.Contains("Tables/customers.sql", files);
        Assert.DoesNotContain("Tables/orders.generated.sql", files);

        var built = project.Build();
        Assert.Single(built.Model.Tables);
        Assert.Equal("customers", built.Model.Tables[0].Name);
    }

    [Fact]
    public void Build_remove_attribute_is_honoured_as_an_exclude()
    {
        Write("Rm.pgproj", """
            <Project>
              <PropertyGroup><Name>Rm</Name></PropertyGroup>
              <ItemGroup>
                <Build Include="**/*.sql" />
                <Build Remove="scratch/**/*.sql" />
              </ItemGroup>
            </Project>
            """);
        Write("Tables/a.sql", "CREATE TABLE public.a (id int);");
        Write("scratch/throwaway.sql", "CREATE TABLE public.a (id int);"); // would be a duplicate if included

        var project = DatabaseProject.Load(Path.Combine(_dir, "Rm.pgproj"));
        var built = project.Build();

        Assert.Single(built.Files);
        Assert.Empty(built.Diagnostics); // no "Duplicate table" — scratch was removed
    }

    // -------------------------------------------------------------------------------------------
    // glob semantics
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Double_star_glob_matches_root_and_nested_files()
    {
        Write("Deep.pgproj", """
            <Project>
              <PropertyGroup><Name>Deep</Name></PropertyGroup>
              <ItemGroup><Build Include="**/*.sql" /></ItemGroup>
            </Project>
            """);
        Write("root.sql", "CREATE TABLE public.root (id int);");
        Write("a/mid.sql", "CREATE TABLE public.mid (id int);");
        Write("a/b/c/deep.sql", "CREATE TABLE public.deep (id int);");

        var project = DatabaseProject.Load(Path.Combine(_dir, "Deep.pgproj"));
        var files = project.ResolveSqlFiles().Select(f => RelOf(project, f)).ToList();

        Assert.Equal(new[] { "a/b/c/deep.sql", "a/mid.sql", "root.sql" }, files.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void Top_level_glob_does_not_recurse_into_subdirectories()
    {
        Write("Top.pgproj", """
            <Project>
              <PropertyGroup><Name>Top</Name></PropertyGroup>
              <ItemGroup><Build Include="Tables/*.sql" /></ItemGroup>
            </Project>
            """);
        Write("Tables/top.sql", "CREATE TABLE public.top (id int);");
        Write("Tables/nested/deep.sql", "CREATE TABLE public.deep (id int);");

        var project = DatabaseProject.Load(Path.Combine(_dir, "Top.pgproj"));
        var files = project.ResolveSqlFiles().Select(f => RelOf(project, f)).ToList();

        Assert.Equal(new[] { "Tables/top.sql" }, files.ToArray());
    }

    // -------------------------------------------------------------------------------------------
    // source-file / line persistence (no second parse pass)
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Objects_carry_source_file_and_line_resolvable_from_the_build_without_reparsing()
    {
        Write("Pos.pgproj", """
            <Project>
              <PropertyGroup><Name>Pos</Name><DefaultSchema>app</DefaultSchema></PropertyGroup>
              <ItemGroup><Build Include="**/*.sql" /></ItemGroup>
            </Project>
            """);
        // Blank lines so the table CREATE is on line 3, not line 1.
        Write("Tables/customers.sql", "-- header\n\nCREATE TABLE app.customers (id int PRIMARY KEY);");

        var project = DatabaseProject.Load(Path.Combine(_dir, "Pos.pgproj"));
        var built = project.Build();

        // The build itself carries the position index — no SourcePositionIndex.Build re-parse.
        var pos = built.Positions.Find("table:app.customers");
        Assert.NotNull(pos);
        Assert.Equal("Tables/customers.sql", pos!.Value.File);
        Assert.Equal(3, pos.Value.Line);

        // And the model-tree resolves file:line straight from the build's index.
        var tree = ModelTreeBuilder.Build(built.Model, project.Name, built.Positions);
        var node = tree.Nodes.Single(n => n.Kind == "table" && n.Name == "customers");
        Assert.Equal("Tables/customers.sql", node.File);
        Assert.Equal(3, node.Line);
    }

    [Fact]
    public void Parallel_build_persists_positions_in_deterministic_file_order()
    {
        Write("Par.pgproj", """
            <Project>
              <PropertyGroup><Name>Par</Name></PropertyGroup>
              <ItemGroup><Build Include="**/*.sql" /></ItemGroup>
            </Project>
            """);
        for (var i = 0; i < 6; i++)
            Write($"Tables/t{i}.sql", $"\nCREATE TABLE public.t{i} (id int);");

        var project = DatabaseProject.Load(Path.Combine(_dir, "Par.pgproj"));
        var built = project.BuildAsync().GetAwaiter().GetResult();

        for (var i = 0; i < 6; i++)
        {
            var pos = built.Positions.Find($"table:public.t{i}");
            Assert.NotNull(pos);
            Assert.Equal($"Tables/t{i}.sql", pos!.Value.File);
            Assert.Equal(2, pos.Value.Line); // leading "\n" → CREATE on line 2
        }
    }
}
