using System;
using System.IO;
using PgProj.Core.Project;
using Xunit;

namespace PgProj.Core.Tests;

public class ProjectLoaderTests : IDisposable
{
    private readonly string _dir;

    public ProjectLoaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pgproj_test_" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void Builds_model_from_globbed_sql_files()
    {
        var proj = Write("Test.pgproj", """
            <Project Sdk="PgProj.Sdk/0.1.0">
              <PropertyGroup>
                <Name>Test</Name>
                <DefaultSchema>app</DefaultSchema>
              </PropertyGroup>
              <ItemGroup>
                <Build Include="**/*.sql" />
              </ItemGroup>
            </Project>
            """);
        Write("Tables/customers.sql", "CREATE TABLE app.customers (id int PRIMARY KEY);");
        Write("Tables/orders.sql", "CREATE TABLE app.orders (id int PRIMARY KEY, cid int REFERENCES app.customers (id));");

        var project = DatabaseProject.Load(proj);
        Assert.Equal("Test", project.Name);
        Assert.Equal("app", project.DefaultSchema);

        var result = project.Build();
        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.Model.Tables.Count);
        Assert.Equal(2, result.Files.Count);
    }

    [Fact]
    public void Reports_duplicate_table_definitions()
    {
        var proj = Write("Dup.pgproj", """
            <Project>
              <PropertyGroup><Name>Dup</Name></PropertyGroup>
              <ItemGroup><Build Include="**/*.sql" /></ItemGroup>
            </Project>
            """);
        Write("a.sql", "CREATE TABLE public.t (id int);");
        Write("b.sql", "CREATE TABLE public.t (id int);");

        var result = DatabaseProject.Load(proj).Build();
        Assert.Contains(result.Diagnostics, d => d.Contains("Duplicate table", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Loads_pre_and_post_deploy_scripts_and_excludes_them_from_the_build()
    {
        var proj = Write("D.pgproj", """
            <Project>
              <PropertyGroup><Name>D</Name></PropertyGroup>
              <ItemGroup>
                <Build Include="**/*.sql" />
                <None Include="Scripts/PreDeploy.sql"><BuildAction>PreDeploy</BuildAction></None>
                <None Include="Scripts/PostDeploy.sql"><BuildAction>PostDeploy</BuildAction></None>
              </ItemGroup>
            </Project>
            """);
        Write("Tables/t.sql", "CREATE TABLE public.t (id int);");
        Write("Scripts/PreDeploy.sql", "SELECT 'pre';");
        Write("Scripts/PostDeploy.sql", "SELECT 'post';");

        var project = DatabaseProject.Load(proj);
        Assert.NotNull(project.PreDeployScriptPath);
        Assert.NotNull(project.PostDeployScriptPath);
        Assert.EndsWith("PreDeploy.sql", project.PreDeployScriptPath);
        Assert.EndsWith("PostDeploy.sql", project.PostDeployScriptPath);

        // Even though "**/*.sql" would glob them, deploy scripts must not be parsed as object sources.
        var result = project.Build();
        Assert.Single(result.Files);
        Assert.Single(result.Model.Tables);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Rejects_more_than_one_pre_deploy_script()
    {
        var proj = Write("Dup.pgproj", """
            <Project>
              <PropertyGroup><Name>Dup</Name></PropertyGroup>
              <ItemGroup>
                <None Include="a.sql"><BuildAction>PreDeploy</BuildAction></None>
                <None Include="b.sql"><BuildAction>PreDeploy</BuildAction></None>
              </ItemGroup>
            </Project>
            """);
        var ex = Assert.Throws<InvalidOperationException>(() => DatabaseProject.Load(proj));
        Assert.Contains("PreDeploy", ex.Message);
    }

    [Fact]
    public void Rejects_more_than_one_post_deploy_script()
    {
        var proj = Write("Dup2.pgproj", """
            <Project>
              <PropertyGroup><Name>Dup2</Name></PropertyGroup>
              <ItemGroup>
                <None Include="a.sql"><BuildAction>PostDeploy</BuildAction></None>
                <None Include="b.sql"><BuildAction>PostDeploy</BuildAction></None>
              </ItemGroup>
            </Project>
            """);
        var ex = Assert.Throws<InvalidOperationException>(() => DatabaseProject.Load(proj));
        Assert.Contains("PostDeploy", ex.Message);
    }

    [Fact]
    public void Loads_sqlcmd_variable_defaults()
    {
        var proj = Write("V.pgproj", """
            <Project>
              <PropertyGroup><Name>V</Name></PropertyGroup>
              <ItemGroup>
                <SqlCmdVariable Include="EnvSuffix"><DefaultValue>dev</DefaultValue></SqlCmdVariable>
                <SqlCmdVariable Include="NoDefault" />
              </ItemGroup>
            </Project>
            """);
        var project = DatabaseProject.Load(proj);
        Assert.Equal("dev", project.SqlCmdVariableDefaults["EnvSuffix"]);
        Assert.True(project.SqlCmdVariableDefaults.ContainsKey("NoDefault"));
        Assert.Equal("", project.SqlCmdVariableDefaults["NoDefault"]);
    }

    [Fact]
    public void Underscore_prefixed_files_are_excluded_from_the_build()
    {
        var proj = Write("U.pgproj", """
            <Project>
              <PropertyGroup><Name>U</Name></PropertyGroup>
              <ItemGroup><Build Include="**/*.sql" /></ItemGroup>
            </Project>
            """);
        Write("Tables/t.sql", "CREATE TABLE public.t (id int);");
        // A generated concatenation of the sources; must NOT be parsed again (no duplicate).
        Write("_full_create.sql", "CREATE TABLE public.t (id int);");

        var result = DatabaseProject.Load(proj).Build();
        Assert.Single(result.Files);
        Assert.Empty(result.Diagnostics);
        Assert.Single(result.Model.Tables);
    }
}
