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

    // ── DefaultSqlItems (issue #39) ──────────────────────────────────────────

    /// <summary>
    /// A project with no &lt;Build Include&gt; at all should still pick up every *.sql file via
    /// the SDK default-include behaviour.  This mirrors what Sdk.props injects at the MSBuild
    /// level and verifies the engine-level fallback produces the same result.
    /// </summary>
    [Fact]
    public void No_Build_items_declared_auto_includes_all_sql_files()
    {
        var proj = Write("Auto.pgproj", """
            <Project>
              <PropertyGroup><Name>Auto</Name><DefaultSchema>app</DefaultSchema></PropertyGroup>
            </Project>
            """);
        Write("Tables/customers.sql", "CREATE TABLE app.customers (id int PRIMARY KEY);");
        Write("Tables/orders.sql",    "CREATE TABLE app.orders (id int PRIMARY KEY);");

        var project = DatabaseProject.Load(proj);
        // Engine must derive the default glob when none are declared.
        Assert.Single(project.IncludePatterns);
        Assert.Equal("**/*.sql", project.IncludePatterns[0]);

        var result = project.Build();
        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.Model.Tables.Count);
        Assert.Equal(2, result.Files.Count);
    }

    /// <summary>
    /// Setting EnableDefaultSqlItems=false and omitting &lt;Build Include&gt; should produce an
    /// empty file set — the user has explicitly opted out of auto-include.
    /// </summary>
    [Fact]
    public void EnableDefaultSqlItems_false_suppresses_auto_include()
    {
        var proj = Write("NoAuto.pgproj", """
            <Project>
              <PropertyGroup>
                <Name>NoAuto</Name>
                <EnableDefaultSqlItems>false</EnableDefaultSqlItems>
              </PropertyGroup>
            </Project>
            """);
        Write("Tables/t.sql", "CREATE TABLE public.t (id int);");

        var project = DatabaseProject.Load(proj);
        // With auto-include disabled and no explicit items, IncludePatterns must be empty.
        Assert.Empty(project.IncludePatterns);

        var result = project.Build();
        Assert.Empty(result.Files);
        Assert.Empty(result.Model.Tables);
    }

    /// <summary>
    /// Double-include guard: a project that declares &lt;Build Include="**/*.sql" /&gt; explicitly
    /// (matching what older projects had) must resolve each file exactly once even though
    /// EnableDefaultSqlItems=true would also match the same files.  MSBuild deduplicates at the
    /// item level; ResolveSqlFiles deduplicates at the path level — no file is parsed twice.
    /// </summary>
    [Fact]
    public void Explicit_glob_same_as_default_does_not_double_include_files()
    {
        // Project declares the same catch-all that the SDK would inject automatically.
        // IncludePatterns will contain ["**/*.sql", "**/*.sql"] after Load, which is what
        // would happen if both the SDK default AND the user declaration were present.
        var proj = Write("Dup.pgproj", """
            <Project>
              <PropertyGroup><Name>Dup</Name></PropertyGroup>
              <ItemGroup><Build Include="**/*.sql" /></ItemGroup>
            </Project>
            """);
        Write("Tables/t.sql", "CREATE TABLE public.t (id int);");

        var result = DatabaseProject.Load(proj).Build();
        // ResolveSqlFiles must deduplicate: exactly one file, no duplicate-definition diagnostic.
        Assert.Single(result.Files);
        Assert.Empty(result.Diagnostics);
        Assert.Single(result.Model.Tables);
    }
}
