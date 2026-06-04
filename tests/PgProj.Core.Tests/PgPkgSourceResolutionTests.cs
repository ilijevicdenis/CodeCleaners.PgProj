using System;
using System.IO;
using PgProj.Core.Comparison;
using PgProj.Core.Model;
using PgProj.Core.Packaging;
using PgProj.Core.Project;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Proves the "build once, deploy many" guarantee at the model level: a model loaded from a
/// <c>.pgpkg</c> resolves to the same comparison/script output as building straight from the project.
/// This is exactly what the CLI does when <c>compare</c>/<c>script</c>/<c>publish</c> are given a package.
/// </summary>
public sealed class PgPkgSourceResolutionTests : IDisposable
{
    private readonly string _dir;

    public PgPkgSourceResolutionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pgpkg_res_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private (DatabaseModel FromProject, DatabaseModel FromPackage) BuildBoth()
    {
        var proj = Write("R.pgproj", """
            <Project>
              <PropertyGroup><Name>R</Name><DefaultSchema>app</DefaultSchema></PropertyGroup>
              <ItemGroup><Build Include="**/*.sql" /></ItemGroup>
            </Project>
            """);
        Write("Tables/a.sql", "CREATE TABLE app.a (id int PRIMARY KEY, val text NOT NULL);");
        Write("Tables/b.sql", "CREATE TABLE app.b (id int PRIMARY KEY, aid int REFERENCES app.a (id));");
        Write("Indexes/i.sql", "CREATE INDEX ix_b_aid ON app.b (aid);");

        var project = DatabaseProject.Load(proj);
        var built = project.Build();
        Assert.Empty(built.Diagnostics);

        var pkg = PgPkgBuilder.FromBuild(project, built.Model, built.Files, "t", "2026-01-01T00:00:00Z");
        var pkgPath = Path.Combine(_dir, "R.pgpkg");
        pkg.Write(pkgPath);

        return (built.Model, PgPkg.Read(pkgPath).Model);
    }

    [Fact]
    public void Script_from_package_equals_script_from_project()
    {
        var (fromProject, fromPackage) = BuildBoth();

        string FullCreate(DatabaseModel m) =>
            new DeployScriptGenerator().Generate(
                new SchemaComparer().Compare(m, new DatabaseModel()),
                new DeployOptions { WrapInTransaction = true });

        Assert.Equal(FullCreate(fromProject), FullCreate(fromPackage));
    }

    [Fact]
    public void Compare_from_package_equals_compare_from_project()
    {
        var (fromProject, fromPackage) = BuildBoth();

        // Compare each source model against the same (empty) target → identical change set.
        var target = new DatabaseModel();
        var a = new SchemaComparer().Compare(fromProject, target);
        var b = new SchemaComparer().Compare(fromPackage, target);

        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
            Assert.Equal(a[i].Describe(), b[i].Describe());
    }

    [Fact]
    public void IsPackagePath_detects_extension_case_insensitively()
    {
        Assert.True(PgPkg.IsPackagePath("foo.pgpkg"));
        Assert.True(PgPkg.IsPackagePath("Foo.PGPKG"));
        Assert.True(PgPkg.IsPackagePath(@"C:\bin\Sample.PgPkg"));
        Assert.False(PgPkg.IsPackagePath("foo.pgproj"));
        Assert.False(PgPkg.IsPackagePath("foo.model.json"));
    }
}
