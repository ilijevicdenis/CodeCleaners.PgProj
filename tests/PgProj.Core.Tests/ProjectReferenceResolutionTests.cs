using System;
using System.IO;
using System.Linq;
using PgProj.Core.Comparison;
using PgProj.Core.Model;
using PgProj.Core.Packaging;
using PgProj.Core.Project;
using PgProj.Core.Project.References;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// EP-REF unit tests (no DB). Proves: B-references-A makes A's objects resolvable at build time without
/// emitting them; an unresolved cross-schema reference fails with a positioned diagnostic; a circular
/// reference is reported (never stack-overflows); an artifact (.pgpkg) reference resolves like a project;
/// and a PackageReference yields the documented "not-yet-restored" diagnostic.
/// </summary>
public sealed class ProjectReferenceResolutionTests : IDisposable
{
    private readonly string _root;

    public ProjectReferenceResolutionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pgref_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private const string ProjA = """
        <Project><PropertyGroup><Name>A</Name><DefaultSchema>common</DefaultSchema></PropertyGroup>
          <ItemGroup><Build Include="**/*.sql" /></ItemGroup></Project>
        """;

    // B references A; B's view reads A's common.customer table.
    private string MakeProjB(string referenceXml) => $"""
        <Project><PropertyGroup><Name>B</Name><DefaultSchema>app</DefaultSchema></PropertyGroup>
          <ItemGroup><Build Include="**/*.sql" /></ItemGroup>
          <ItemGroup>{referenceXml}</ItemGroup></Project>
        """;

    private void WriteProjectA()
    {
        Write("A/A.pgproj", ProjA);
        Write("A/customer.sql", "CREATE TABLE common.customer (id int PRIMARY KEY, name text NOT NULL);");
    }

    // ---- project reference: external objects resolve, but are not emitted -----------------

    [Fact]
    public void Project_reference_makes_external_objects_resolvable()
    {
        WriteProjectA();
        var bProj = Write("B/B.pgproj", MakeProjB("""<ProjectReference Include="../A/A.pgproj" />"""));
        Write("B/orders_view.sql",
            "CREATE VIEW app.customer_names AS SELECT c.id, c.name FROM common.customer c;");

        var project = DatabaseProject.Load(bProj);
        Assert.Single(project.References);
        Assert.Equal(ReferenceKind.Project, project.References[0].Kind);

        var resolution = new ReferenceResolver().Resolve(project);
        Assert.False(resolution.HasErrors, string.Join("\n", resolution.Diagnostics));

        // A's common schema + customer table entered the external model.
        Assert.Contains(resolution.ExternalModel.Schemas, s => DatabaseModel.NameEquals(s.Name, "common"));
        Assert.Contains(resolution.ExternalModel.Tables, t => DatabaseModel.NameEquals(t.Schema, "common") && DatabaseModel.NameEquals(t.Name, "customer"));

        // Validation is clean: B's view over A's table resolves.
        var diags = ReferenceValidator.Validate(project, resolution);
        Assert.Empty(diags);
    }

    [Fact]
    public void External_objects_are_excluded_from_B_deploy_script()
    {
        WriteProjectA();
        var bProj = Write("B/B.pgproj", MakeProjB("""<ProjectReference Include="../A/A.pgproj" />"""));
        Write("B/orders_view.sql",
            "CREATE VIEW app.customer_names AS SELECT c.id, c.name FROM common.customer c;");

        var project = DatabaseProject.Load(bProj);
        var built = project.Build();
        Assert.False(built.HasErrors);

        // The change set of B against an empty target must contain ONLY B's own objects — never A's.
        var changes = new SchemaComparer().Compare(built.Model, new DatabaseModel());
        var descriptions = changes.Select(c => c.Describe()).ToList();

        Assert.DoesNotContain(descriptions, d => d.Contains("common.customer", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(descriptions, d => d.Contains("common", StringComparison.OrdinalIgnoreCase) && d.Contains("schema", StringComparison.OrdinalIgnoreCase));

        // ...but B's own view IS emitted.
        Assert.Contains(descriptions, d => d.Contains("customer_names", StringComparison.OrdinalIgnoreCase));

        // And A's model never leaked into B's model.
        Assert.DoesNotContain(built.Model.Tables, t => DatabaseModel.NameEquals(t.Schema, "common"));
        Assert.DoesNotContain(built.Model.Schemas, s => DatabaseModel.NameEquals(s.Name, "common"));
    }

    [Fact]
    public void Removing_the_reference_makes_the_same_build_fail_with_a_positioned_diagnostic()
    {
        WriteProjectA();
        // No <ProjectReference> this time.
        var bProj = Write("B/B.pgproj", MakeProjB(""));
        Write("B/orders_view.sql",
            "CREATE VIEW app.customer_names AS\n  SELECT c.id, c.name FROM common.customer c;");

        var project = DatabaseProject.Load(bProj);
        Assert.Empty(project.References);

        var resolution = new ReferenceResolver().Resolve(project);
        var diags = ReferenceValidator.Validate(project, resolution);

        var unresolved = Assert.Single(diags);
        Assert.Contains("common.customer", unresolved.Message);
        Assert.Contains("does not exist", unresolved.Message);
        Assert.Equal("orders_view.sql", unresolved.RelativePath);
        Assert.True(unresolved.Line >= 1);
        Assert.True(unresolved.Column >= 1);
    }

    // ---- circular references ---------------------------------------------------------------

    [Fact]
    public void Circular_reference_is_reported_not_stack_overflowed()
    {
        // A references B and B references A.
        Write("A/A.pgproj", """
            <Project><PropertyGroup><Name>A</Name><DefaultSchema>common</DefaultSchema></PropertyGroup>
              <ItemGroup><Build Include="**/*.sql" /></ItemGroup>
              <ItemGroup><ProjectReference Include="../B/B.pgproj" /></ItemGroup></Project>
            """);
        Write("A/a.sql", "CREATE TABLE common.customer (id int PRIMARY KEY);");
        var bProj = Write("B/B.pgproj", MakeProjB("""<ProjectReference Include="../A/A.pgproj" />"""));
        Write("B/b.sql", "CREATE TABLE app.orders (id int PRIMARY KEY);");

        var project = DatabaseProject.Load(bProj);
        var resolution = new ReferenceResolver().Resolve(project);

        Assert.Contains(resolution.Diagnostics, d => d.Code == ReferenceErrorCodes.Circular);
    }

    [Fact]
    public void Self_reference_is_detected_as_circular()
    {
        var aProj = Write("A/A.pgproj", """
            <Project><PropertyGroup><Name>A</Name></PropertyGroup>
              <ItemGroup><Build Include="**/*.sql" /></ItemGroup>
              <ItemGroup><ProjectReference Include="./A.pgproj" /></ItemGroup></Project>
            """);
        Write("A/a.sql", "CREATE TABLE public.t (id int);");

        var resolution = new ReferenceResolver().Resolve(DatabaseProject.Load(aProj));
        Assert.Contains(resolution.Diagnostics, d => d.Code == ReferenceErrorCodes.Circular);
    }

    // ---- transitive references -------------------------------------------------------------

    [Fact]
    public void Transitive_reference_pulls_in_the_whole_chain()
    {
        // C defines base.t; B references C; A references B → A sees both B's and C's objects.
        Write("C/C.pgproj", """
            <Project><PropertyGroup><Name>C</Name><DefaultSchema>base</DefaultSchema></PropertyGroup>
              <ItemGroup><Build Include="**/*.sql" /></ItemGroup></Project>
            """);
        Write("C/c.sql", "CREATE TABLE base.t (id int PRIMARY KEY);");

        Write("B/B.pgproj", """
            <Project><PropertyGroup><Name>B</Name><DefaultSchema>mid</DefaultSchema></PropertyGroup>
              <ItemGroup><Build Include="**/*.sql" /></ItemGroup>
              <ItemGroup><ProjectReference Include="../C/C.pgproj" /></ItemGroup></Project>
            """);
        Write("B/b.sql", "CREATE TABLE mid.u (id int PRIMARY KEY);");

        var aProj = Write("A/A.pgproj", """
            <Project><PropertyGroup><Name>A</Name><DefaultSchema>app</DefaultSchema></PropertyGroup>
              <ItemGroup><Build Include="**/*.sql" /></ItemGroup>
              <ItemGroup><ProjectReference Include="../B/B.pgproj" /></ItemGroup></Project>
            """);
        Write("A/a.sql", "CREATE VIEW app.v AS SELECT t.id FROM base.t t JOIN mid.u u ON u.id = t.id;");

        var project = DatabaseProject.Load(aProj);
        var resolution = new ReferenceResolver().Resolve(project);
        Assert.False(resolution.HasErrors, string.Join("\n", resolution.Diagnostics));

        Assert.Contains(resolution.ExternalModel.Tables, t => DatabaseModel.NameEquals(t.Schema, "base") && DatabaseModel.NameEquals(t.Name, "t"));
        Assert.Contains(resolution.ExternalModel.Tables, t => DatabaseModel.NameEquals(t.Schema, "mid") && DatabaseModel.NameEquals(t.Name, "u"));

        Assert.Empty(ReferenceValidator.Validate(project, resolution));
    }

    // ---- artifact (.pgpkg) reference -------------------------------------------------------

    [Fact]
    public void Artifact_reference_resolves_like_a_project_reference()
    {
        // Build A into a .pgpkg, then reference the artifact (not the source) from B.
        WriteProjectA();
        var aProject = DatabaseProject.Load(Path.Combine(_root, "A", "A.pgproj"));
        var builtA = aProject.Build();
        Assert.False(builtA.HasErrors);
        var pkg = PgPkgBuilder.FromBuild(aProject, builtA.Model, builtA.Files, "t", "2026-01-01T00:00:00Z");
        var pkgPath = Path.Combine(_root, "A", "bin", "A.pgpkg");
        Directory.CreateDirectory(Path.GetDirectoryName(pkgPath)!);
        pkg.Write(pkgPath);

        var bProj = Write("B/B.pgproj", MakeProjB("""<ArtifactReference Include="../A/bin/A.pgpkg" />"""));
        Write("B/v.sql", "CREATE VIEW app.cust AS SELECT c.id FROM common.customer c;");

        var project = DatabaseProject.Load(bProj);
        Assert.Equal(ReferenceKind.Artifact, project.References[0].Kind);

        var resolution = new ReferenceResolver().Resolve(project);
        Assert.False(resolution.HasErrors, string.Join("\n", resolution.Diagnostics));
        Assert.Contains(resolution.ExternalModel.Tables, t => DatabaseModel.NameEquals(t.Schema, "common") && DatabaseModel.NameEquals(t.Name, "customer"));
        Assert.Empty(ReferenceValidator.Validate(project, resolution));
    }

    [Fact]
    public void Missing_artifact_reports_not_found()
    {
        var bProj = Write("B/B.pgproj", MakeProjB("""<ArtifactReference Include="../A/bin/Nope.pgpkg" />"""));
        Write("B/v.sql", "CREATE TABLE app.t (id int);");

        var resolution = new ReferenceResolver().Resolve(DatabaseProject.Load(bProj));
        Assert.Contains(resolution.Diagnostics, d => d.Code == ReferenceErrorCodes.NotFound);
    }

    [Fact]
    public void Missing_project_reference_reports_not_found()
    {
        var bProj = Write("B/B.pgproj", MakeProjB("""<ProjectReference Include="../Ghost/Ghost.pgproj" />"""));
        Write("B/v.sql", "CREATE TABLE app.t (id int);");

        var resolution = new ReferenceResolver().Resolve(DatabaseProject.Load(bProj));
        Assert.Contains(resolution.Diagnostics, d => d.Code == ReferenceErrorCodes.NotFound);
    }

    // ---- package reference: documented not-yet-restored ------------------------------------

    [Fact]
    public void Package_reference_yields_not_yet_restored_diagnostic()
    {
        var bProj = Write("B/B.pgproj",
            MakeProjB("""<PackageReference Include="Acme.CommonSchema" Version="1.2.3" />"""));
        Write("B/v.sql", "CREATE TABLE app.t (id int);");

        var project = DatabaseProject.Load(bProj);
        Assert.Equal(ReferenceKind.Package, project.References[0].Kind);
        Assert.Equal("1.2.3", project.References[0].Version);

        var resolution = new ReferenceResolver().Resolve(project);
        var diag = Assert.Single(resolution.Diagnostics);
        Assert.Equal(ReferenceErrorCodes.PackageRestoreNotImplemented, diag.Code);
        Assert.Contains("Acme.CommonSchema", diag.Message);
        Assert.Contains("1.2.3", diag.Message);
    }

    // ---- UI-consumable resolver shape ------------------------------------------------------

    [Fact]
    public void Resolution_exposes_a_structured_reference_list()
    {
        WriteProjectA();
        var bProj = Write("B/B.pgproj", MakeProjB("""<ProjectReference Include="../A/A.pgproj" />"""));
        Write("B/v.sql", "CREATE TABLE app.t (id int);");

        var resolution = new ReferenceResolver().Resolve(DatabaseProject.Load(bProj));
        var resolved = Assert.Single(resolution.References);
        Assert.Equal(ReferenceKind.Project, resolved.Item.Kind);
        Assert.Equal("../A/A.pgproj", resolved.Item.Include);
        Assert.NotNull(resolved.Model);
    }
}
