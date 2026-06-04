using System;
using System.IO;
using System.Linq;
using PgProj.Core.Project;
using PgProj.Core.Syntax;
using PgProj.Core.Templates;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Unit coverage for the EP-TEMPLATES scaffolding engine (<see cref="Scaffolder"/> /
/// <see cref="TemplateCatalog"/>): every <c>add &lt;kind&gt;</c> output parses with zero diagnostics,
/// files land in the convention folder with tokens substituted, the <c>--force</c> overwrite guard
/// holds, and a <c>new project</c> output builds clean. No database required.
/// </summary>
public sealed class TemplateTests : IDisposable
{
    private readonly string _dir;

    public TemplateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pgproj_tmpl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    public static TheoryData<TemplateKind> AllKinds()
    {
        var data = new TheoryData<TemplateKind>();
        foreach (var k in Enum.GetValues<TemplateKind>()) data.Add(k);
        return data;
    }

    // ---- every template renders parse-clean SQL -----------------------------------------

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Every_template_renders_parse_clean_sql(TemplateKind kind)
    {
        var template = TemplateCatalog.Get(kind);
        var name = new ObjectName("app", "Thing");
        var sql = template.Render(name);

        var diagnostics = new PgParser().Parse(sql).Diagnostics;
        Assert.True(diagnostics.Count == 0,
            $"{kind} template did not parse clean:\n{sql}\n--\n{string.Join("\n", diagnostics.Select(d => d.ToString()))}");
    }

    [Fact]
    public void Catalog_exposes_all_nine_documented_kinds()
    {
        Assert.Equal(9, TemplateCatalog.All.Count);
        foreach (var kind in new[] { "table", "view", "function", "procedure", "trigger", "sequence", "type", "schema", "policy" })
            Assert.NotNull(TemplateCatalog.Resolve(kind));
    }

    // ---- add: placement + token substitution --------------------------------------------

    [Fact]
    public void Add_table_places_file_in_tables_folder_with_qualified_name()
    {
        var proj = NewProject(defaultSchema: "public");
        var result = Scaffolder.Add(proj, "table", "app.Customer");

        Assert.Equal("Tables/app.Customer.sql", result.RelativePath);
        Assert.True(File.Exists(result.FilePath));
        Assert.Contains("CREATE TABLE app.Customer", result.Content);
    }

    [Theory]
    [InlineData("view", "Views/app.Listing.sql", "CREATE VIEW app.Listing")]
    [InlineData("function", "Functions/app.Listing.sql", "CREATE OR REPLACE FUNCTION app.Listing")]
    [InlineData("procedure", "Procedures/app.Listing.sql", "CREATE OR REPLACE PROCEDURE app.Listing")]
    [InlineData("sequence", "Sequences/app.Listing.sql", "CREATE SEQUENCE app.Listing")]
    [InlineData("type", "Types/app.Listing.sql", "CREATE TYPE app.Listing")]
    [InlineData("trigger", "Triggers/app.Listing.sql", "CREATE TRIGGER Listing")]
    [InlineData("policy", "Policies/app.Listing.sql", "CREATE POLICY Listing")]
    public void Add_places_schema_scoped_object_correctly(string kind, string expectedRel, string expectedSnippet)
    {
        var proj = NewProject(defaultSchema: "public");
        var result = Scaffolder.Add(proj, kind, "app.Listing");

        Assert.Equal(expectedRel, result.RelativePath);
        Assert.Contains(expectedSnippet, result.Content);
        Assert.True(File.Exists(result.FilePath));
    }

    [Fact]
    public void Add_schema_uses_name_only_path_and_token()
    {
        var proj = NewProject(defaultSchema: "public");
        var result = Scaffolder.Add(proj, "schema", "reporting");

        Assert.Equal("Schemas/reporting.sql", result.RelativePath);
        Assert.Contains("CREATE SCHEMA IF NOT EXISTS reporting", result.Content);
    }

    [Fact]
    public void Add_bare_name_falls_back_to_project_default_schema()
    {
        var proj = NewProject(defaultSchema: "sales");
        var result = Scaffolder.Add(proj, "table", "Invoice");

        Assert.Equal("Tables/sales.Invoice.sql", result.RelativePath);
        Assert.Contains("CREATE TABLE sales.Invoice", result.Content);
    }

    [Fact]
    public void Add_unknown_kind_throws_with_kind_list()
    {
        var proj = NewProject();
        var ex = Assert.Throws<ArgumentException>(() => Scaffolder.Add(proj, "widget", "app.X"));
        Assert.Contains("table", ex.Message);
    }

    [Theory]
    [InlineData("app.")]
    [InlineData(".Customer")]
    [InlineData("a.b.c")]
    [InlineData("")]
    public void Add_rejects_malformed_name(string raw)
    {
        var proj = NewProject();
        Assert.Throws<ArgumentException>(() => Scaffolder.Add(proj, "table", raw));
    }

    // ---- add: every kind's written file parses clean ------------------------------------

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Add_writes_a_parse_clean_file(TemplateKind kind)
    {
        var proj = NewProject();
        var word = kind.ToString().ToLowerInvariant();
        var result = Scaffolder.Add(proj, word, "app.Thing");

        var written = File.ReadAllText(result.FilePath);
        var diagnostics = new PgParser().Parse(written).Diagnostics;
        Assert.True(diagnostics.Count == 0, $"{kind} written file did not parse: {string.Join("\n", diagnostics.Select(d => d.ToString()))}");
    }

    // ---- add: --force overwrite guard ---------------------------------------------------

    [Fact]
    public void Add_refuses_to_overwrite_without_force()
    {
        var proj = NewProject();
        Scaffolder.Add(proj, "table", "app.Customer");

        var ex = Assert.Throws<IOException>(() => Scaffolder.Add(proj, "table", "app.Customer"));
        Assert.Contains("--force", ex.Message);
    }

    [Fact]
    public void Add_overwrites_with_force()
    {
        var proj = NewProject();
        var first = Scaffolder.Add(proj, "table", "app.Customer");
        File.WriteAllText(first.FilePath, "-- hand-edited\n");

        var second = Scaffolder.Add(proj, "table", "app.Customer", force: true);
        Assert.DoesNotContain("hand-edited", File.ReadAllText(second.FilePath));
        Assert.Contains("CREATE TABLE app.Customer", File.ReadAllText(second.FilePath));
    }

    // ---- new project --------------------------------------------------------------------

    [Fact]
    public void New_project_writes_a_buildable_manifest()
    {
        var result = Scaffolder.NewProject("Demo", _dir, defaultSchema: "app", targetVersion: "17");

        Assert.True(File.Exists(result.ProjectFilePath));
        var project = DatabaseProject.Load(result.ProjectFilePath);
        Assert.Equal("Demo", project.Name);
        Assert.Equal("app", project.DefaultSchema);
        Assert.Equal("17", project.TargetPostgresVersion);

        // Empty project builds with zero diagnostics.
        var built = project.Build();
        Assert.False(built.HasErrors, string.Join("\n", built.Diagnostics));
    }

    [Fact]
    public void New_project_then_add_table_then_build_is_clean()
    {
        var created = Scaffolder.NewProject("Shop", _dir, defaultSchema: "app");
        Scaffolder.Add(created.ProjectFilePath, "table", "app.Customer");
        Scaffolder.Add(created.ProjectFilePath, "function", "app.touch");

        var project = DatabaseProject.Load(created.ProjectFilePath);
        var built = project.Build();

        Assert.False(built.HasErrors, string.Join("\n", built.Diagnostics));
        Assert.Single(built.Model.Tables);
        Assert.Single(built.Model.Functions);
        Assert.Contains(built.Model.Tables, t => DatabaseModelNameEquals(t.Schema, "app") && DatabaseModelNameEquals(t.Name, "Customer"));
    }

    [Fact]
    public void New_project_pre_creates_convention_folders()
    {
        var created = Scaffolder.NewProject("Layout", _dir);
        foreach (var folder in new[] { "Tables", "Views", "Functions", "Procedures", "Triggers", "Sequences", "Types", "Policies", "Schemas" })
            Assert.True(Directory.Exists(Path.Combine(created.ProjectDirectory, folder)), $"missing folder {folder}");
    }

    [Fact]
    public void New_project_refuses_to_clobber_existing_manifest()
    {
        Scaffolder.NewProject("Dup", _dir);
        Assert.Throws<IOException>(() => Scaffolder.NewProject("Dup", _dir));
    }

    // ---- add resolves project from a directory ------------------------------------------

    [Fact]
    public void Add_resolves_project_from_directory()
    {
        var created = Scaffolder.NewProject("DirProj", _dir, defaultSchema: "x");
        var result = Scaffolder.Add(created.ProjectDirectory, "view", "v");

        Assert.Equal("Views/x.v.sql", result.RelativePath);
    }

    // ---- dotnet new template pack content builds (smoke) --------------------------------

    [Fact]
    public void Dotnet_new_object_templates_render_parse_clean_sql()
    {
        var content = Path.Combine(FindTemplatesDir(), "content");

        // Every object item template's .sql, with the dotnet-new tokens substituted, must parse clean.
        var sqlFiles = Directory.GetFiles(content, "*.sql", SearchOption.AllDirectories);
        Assert.NotEmpty(sqlFiles);

        foreach (var file in sqlFiles)
        {
            var sql = File.ReadAllText(file)
                .Replace("PGPROJ_SCHEMA", "app")
                .Replace("PGPROJ_NAME", "Thing");
            var diagnostics = new PgParser().Parse(sql).Diagnostics;
            Assert.True(diagnostics.Count == 0,
                $"dotnet new template {Path.GetFileName(file)} did not parse:\n{sql}\n--\n{string.Join("\n", diagnostics.Select(d => d.ToString()))}");
        }
    }

    [Fact]
    public void Dotnet_new_empty_project_manifest_builds()
    {
        var src = Path.Combine(FindTemplatesDir(), "content", "pgproj-project");
        var dest = Path.Combine(_dir, "FromTemplate");
        Directory.CreateDirectory(dest);

        // Copy the template content, substitute the dotnet-new tokens, and rename the manifest —
        // emulating what `dotnet new pgproj -n FromTemplate` produces.
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            if (file.Contains(".template.config")) continue;
            var text = File.ReadAllText(file)
                .Replace("PGPROJ_DEFAULT_SCHEMA", "public")
                .Replace("PGPROJ_TARGET_VERSION", "18")
                .Replace("PgProjProject", "FromTemplate");
            var destName = Path.GetFileName(file).Replace("PgProjProject", "FromTemplate");
            File.WriteAllText(Path.Combine(dest, destName), text);
        }

        var project = DatabaseProject.Load(Path.Combine(dest, "FromTemplate.pgproj"));
        var built = project.Build();
        Assert.False(built.HasErrors, string.Join("\n", built.Diagnostics));
    }

    private static string FindTemplatesDir()
    {
        // Walk up from the test bin dir to the repo root, where `templates/` lives.
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "templates");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "PgProj.Templates.csproj")))
                return candidate;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        throw new DirectoryNotFoundException("Could not locate the repo 'templates/' directory from " + AppContext.BaseDirectory);
    }

    // ---- helpers ------------------------------------------------------------------------

    private string NewProject(string defaultSchema = "public")
    {
        var name = "P" + Guid.NewGuid().ToString("N")[..8];
        return Scaffolder.NewProject(name, _dir, defaultSchema).ProjectFilePath;
    }

    private static bool DatabaseModelNameEquals(string a, string b) =>
        PgProj.Core.Model.DatabaseModel.NameEquals(a, b);
}
