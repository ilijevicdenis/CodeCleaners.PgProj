using System;
using System.IO;
using System.Linq;
using PgProj.Core.Project;
using PgProj.Core.Syntax;

namespace PgProj.Core.Templates;

/// <summary>
/// The engine behind <c>pgproj new project</c> and <c>pgproj add</c>: scaffolds an empty buildable
/// project, and scaffolds correctly-named/correctly-placed object <c>.sql</c> files from
/// <see cref="TemplateCatalog"/>. Pure file-system + parser work — no database — so it is unit-testable
/// without shelling to the CLI.
/// </summary>
public static class Scaffolder
{
    /// <summary>The result of scaffolding an object file.</summary>
    public sealed record AddResult(string FilePath, string RelativePath, string Content);

    /// <summary>The result of scaffolding a new project.</summary>
    public sealed record NewProjectResult(string ProjectFilePath, string ProjectDirectory);

    /// <summary>
    /// Scaffolds an empty, immediately buildable project under <paramref name="outputDirectory"/>:
    /// a <c>&lt;name&gt;.pgproj</c> manifest (README one-liner shape) plus the standard object folders so
    /// the layout matches what <c>extract</c> produces. <c>pgproj build</c> succeeds with zero diagnostics.
    /// </summary>
    public static NewProjectResult NewProject(
        string name,
        string outputDirectory,
        string defaultSchema = "public",
        string? targetVersion = "18")
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A project name is required.");

        var projectDir = Path.GetFullPath(Path.Combine(outputDirectory, name));
        Directory.CreateDirectory(projectDir);

        var projPath = Path.Combine(projectDir, name + ".pgproj");
        if (File.Exists(projPath))
            throw new IOException($"A project already exists at {projPath}.");

        File.WriteAllText(projPath, ProjectManifest(name, defaultSchema, targetVersion));

        // Pre-create the conventional folders so the project mirrors the extract layout from day one
        // and so `add` drops files into a familiar tree. Empty folders are harmless to the build.
        foreach (var folder in TemplateCatalog.All.Select(t => t.Folder).Distinct())
            Directory.CreateDirectory(Path.Combine(projectDir, folder));

        return new NewProjectResult(projPath, projectDir);
    }

    /// <summary>
    /// Scaffolds an object file from a template into the project that owns <paramref name="projectFileOrDir"/>.
    /// Resolves the project (a <c>.pgproj</c> path or a directory containing one) to pick up its default
    /// schema, computes the file path per the extract layout, substitutes the schema/name tokens, refuses
    /// to overwrite without <paramref name="force"/>, then parse-verifies the rendered SQL and throws if it
    /// does not parse clean — a template must never produce a non-building file.
    /// </summary>
    public static AddResult Add(string projectFileOrDir, string kindWord, string nameArg, bool force = false)
    {
        var project = LocateProject(projectFileOrDir);
        var template = TemplateCatalog.Resolve(kindWord)
            ?? throw new ArgumentException($"Unknown object kind '{kindWord}'. Known kinds: {TemplateCatalog.KindWords}.");

        var objectName = ObjectName.Parse(nameArg, project.DefaultSchema);
        var content = template.Render(objectName);

        // Parse-verify BEFORE writing: a template that doesn't parse is a bug, not the user's problem.
        var diagnostics = new PgParser().Parse(content).Diagnostics;
        if (diagnostics.Count > 0)
            throw new InvalidOperationException(
                $"Generated {kindWord} did not parse clean (template bug):\n  " +
                string.Join("\n  ", diagnostics.Select(d => d.ToString())));

        var relative = FileName(template, objectName);
        var path = Path.Combine(project.ProjectDirectory, relative);

        if (File.Exists(path) && !force)
            throw new IOException($"{relative} already exists. Pass --force to overwrite.");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return new AddResult(path, relative.Replace('\\', '/'), content);
    }

    /// <summary>The relative path (Folder/file.sql) an object scaffolds to — public so tests/UIs can predict it.</summary>
    public static string FileName(ObjectTemplate template, ObjectName name)
    {
        // Match RawObjectMeta/DdlExporter: schema-scoped objects → "schema.name.sql"; schema-only → "name.sql".
        var basis = template.SchemaScoped ? name.Qualified : name.Name;
        return $"{template.Folder}/{basis}.sql";
    }

    /// <summary>Resolves a .pgproj path or a directory containing exactly one .pgproj.</summary>
    private static DatabaseProject LocateProject(string projectFileOrDir)
    {
        var full = Path.GetFullPath(projectFileOrDir);

        if (Directory.Exists(full))
        {
            var found = Directory.GetFiles(full, "*.pgproj", SearchOption.TopDirectoryOnly);
            if (found.Length == 0)
                throw new FileNotFoundException($"No .pgproj found in {full}. Run 'pgproj new project' first.");
            if (found.Length > 1)
                throw new InvalidOperationException(
                    $"Multiple .pgproj files in {full}; pass the specific one.");
            return DatabaseProject.Load(found[0]);
        }

        return DatabaseProject.Load(full);
    }

    /// <summary>
    /// The standalone <c>.pgproj</c> manifest. Intentionally the README one-liner shape (no SDK import):
    /// outside-repo SDK resolution depends on EP-PKG #13 / EP-VS #25, so we keep the manifest the tool's
    /// own loader fully understands (it ignores the Sdk attribute) and add the import as a follow-up.
    /// </summary>
    public static string ProjectManifest(string name, string defaultSchema, string? targetVersion)
    {
        var versionLine = string.IsNullOrWhiteSpace(targetVersion)
            ? string.Empty
            : $"\n    <TargetPostgresVersion>{targetVersion}</TargetPostgresVersion>";
        return
            $"""
            <Project Sdk="PgProj.Sdk/0.1.0" DefaultTargets="Build">
              <PropertyGroup>
                <Name>{name}</Name>
                <DefaultSchema>{defaultSchema}</DefaultSchema>{versionLine}
              </PropertyGroup>
              <ItemGroup>
                <Build Include="**/*.sql" />
              </ItemGroup>
            </Project>
            """ + "\n";
    }
}
