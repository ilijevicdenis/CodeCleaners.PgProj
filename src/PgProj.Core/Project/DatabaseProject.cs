using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using PgProj.Core.Model;
using PgProj.Core.Parsing;
using Refs = PgProj.Core.Project.References;

namespace PgProj.Core.Project;

/// <summary>
/// A Postgres database project: an MSBuild-style <c>.pgproj</c> manifest plus the set of
/// <c>.sql</c> files it globs in. This is the Postgres analogue of an SSDT <c>.sqlproj</c> — the
/// unit you "build" into a model and then compare/publish against a live server.
/// </summary>
public sealed class DatabaseProject
{
    public required string ProjectFilePath { get; init; }
    public required string ProjectDirectory { get; init; }
    public string Name { get; init; } = "Database";
    public string DefaultSchema { get; init; } = "public";
    public string? TargetPostgresVersion { get; init; }
    public IReadOnlyList<string> IncludePatterns { get; init; } = new[] { "**/*.sql" };

    /// <summary>
    /// The project / artifact / package references declared in the <c>.pgproj</c> ItemGroups (EP-REF).
    /// Their Include paths are resolved relative to <see cref="ProjectDirectory"/>; resolution into
    /// external models is the job of <see cref="References.ReferenceResolver"/>, not the loader.
    /// </summary>
    public IReadOnlyList<Refs.ProjectReferenceItem> References { get; init; } =
        System.Array.Empty<Refs.ProjectReferenceItem>();

    public static DatabaseProject Load(string projectFilePath)
    {
        var fullPath = Path.GetFullPath(projectFilePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Project file not found: {fullPath}");

        var dir = Path.GetDirectoryName(fullPath)!;
        var doc = XDocument.Load(fullPath);
        var root = doc.Root ?? throw new InvalidOperationException("Empty project file.");

        string Prop(string name, string fallback) =>
            root.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value?.Trim()
            is { Length: > 0 } v ? v : fallback;

        var includes = root.Descendants()
            .Where(e => e.Name.LocalName.Equals("Build", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Attribute("Include")?.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .ToList();
        if (includes.Count == 0)
            includes.Add("**/*.sql");

        return new DatabaseProject
        {
            ProjectFilePath = fullPath,
            ProjectDirectory = dir,
            Name = Prop("Name", Path.GetFileNameWithoutExtension(fullPath)),
            DefaultSchema = Prop("DefaultSchema", "public"),
            TargetPostgresVersion = root.Descendants().Any(e => e.Name.LocalName.Equals("TargetPostgresVersion", StringComparison.OrdinalIgnoreCase))
                ? Prop("TargetPostgresVersion", "") : null,
            IncludePatterns = includes,
            References = ParseReferences(root, dir),
        };
    }

    /// <summary>
    /// Reads <c>&lt;ProjectReference/&gt;</c>, <c>&lt;ArtifactReference/&gt;</c> and
    /// <c>&lt;PackageReference/&gt;</c> items from any ItemGroup. The reference KIND is the element's local
    /// name; <c>Include</c> is the path (project/artifact) or package id (package). Package references also
    /// carry an optional <c>Version</c> attribute.
    /// </summary>
    private static IReadOnlyList<Refs.ProjectReferenceItem> ParseReferences(XElement root, string projectDir)
    {
        var refs = new List<Refs.ProjectReferenceItem>();
        foreach (var e in root.Descendants())
        {
            var kind = e.Name.LocalName switch
            {
                var n when n.Equals("ProjectReference", StringComparison.OrdinalIgnoreCase) => Refs.ReferenceKind.Project,
                var n when n.Equals("ArtifactReference", StringComparison.OrdinalIgnoreCase) => Refs.ReferenceKind.Artifact,
                var n when n.Equals("PackageReference", StringComparison.OrdinalIgnoreCase) => Refs.ReferenceKind.Package,
                _ => (Refs.ReferenceKind?)null,
            };
            if (kind is null) continue;

            var include = e.Attribute("Include")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(include)) continue;

            var version = e.Attribute("Version")?.Value?.Trim();
            refs.Add(new Refs.ProjectReferenceItem(kind.Value, include, version, projectDir));
        }
        return refs;
    }

    /// <summary>Resolves all .sql files the project includes, de-duplicated and ordered deterministically.</summary>
    public IReadOnlyList<string> ResolveSqlFiles()
    {
        var files = new List<string>();
        foreach (var pattern in IncludePatterns)
        {
            var norm = pattern.Replace('\\', '/');
            if (norm.Contains("**"))
            {
                files.AddRange(Directory.GetFiles(ProjectDirectory, "*.sql", SearchOption.AllDirectories));
            }
            else if (norm.Contains('*'))
            {
                var subDir = Path.Combine(ProjectDirectory, Path.GetDirectoryName(norm) ?? string.Empty);
                var glob = Path.GetFileName(norm);
                if (Directory.Exists(subDir))
                    files.AddRange(Directory.GetFiles(subDir, glob, SearchOption.TopDirectoryOnly));
            }
            else
            {
                var literal = Path.Combine(ProjectDirectory, norm);
                if (File.Exists(literal)) files.Add(literal);
            }
        }

        return files
            .Select(Path.GetFullPath)
            // Files whose name starts with '_' are treated as non-source (generated artifacts,
            // scratch, dependency-order manifests). Lets a project keep e.g. a generated
            // _full_create.sql concatenation in-tree without it being parsed twice.
            .Where(f => !Path.GetFileName(f).StartsWith('_'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Parses every included .sql file into one model and reports build diagnostics.</summary>
    public ProjectBuildResult Build()
    {
        var model = new DatabaseModel();
        var diagnostics = new List<string>();
        var files = ResolveSqlFiles();
        var builder = new Syntax.ModelBuilder(DefaultSchema);

        foreach (var file in files)
        {
            var parsed = new Syntax.PgParser().Parse(File.ReadAllText(file));
            var rel = Path.GetRelativePath(ProjectDirectory, file);
            foreach (var d in parsed.Diagnostics) diagnostics.Add($"{rel}: {d}");   // attribute to the project file to fix
            builder.Build(parsed, model);
        }

        diagnostics.AddRange(FindDuplicates(model));
        return new ProjectBuildResult(model, diagnostics, files);
    }

    /// <summary>
    /// Parallel analogue of <see cref="Build"/>: parses each .sql file in isolation (a private
    /// parser + model per file, since neither is thread-safe) with bounded concurrency, then merges
    /// the partial models deterministically in sorted-file order so the result is byte-identical to
    /// the sequential build. Per-file errors are isolated as diagnostics, never aborting the build.
    /// (Design: concurrency-orchestrator agent, 2026-06-03.)
    /// </summary>
    public async Task<ProjectBuildResult> BuildAsync(CancellationToken ct = default)
    {
        var files = ResolveSqlFiles();                 // already sorted → defines merge order

        // Small-N short-circuit: for 0–1 files the Parallel.ForEachAsync machinery is pure overhead
        // (crossover ≈10 files — see PgProj.Benchmarks). Accumulate directly into one model — exactly
        // like the serial Build() (no PartialParse/Merge copy) — so a single-file build is as cheap as
        // serial, while keeping BuildAsync's per-file error isolation (a bad file → diagnostic, not throw).
        if (files.Count <= 1)
        {
            ct.ThrowIfCancellationRequested();
            var model = new DatabaseModel();
            var diagnostics = new List<string>();
            if (files.Count == 1)
            {
                try
                {
                    var parsed = new Syntax.PgParser().Parse(File.ReadAllText(files[0]));
                    var rel = Path.GetRelativePath(ProjectDirectory, files[0]);
                    foreach (var d in parsed.Diagnostics) diagnostics.Add($"{rel}: {d}");
                    new Syntax.ModelBuilder(DefaultSchema).Build(parsed, model);
                }
                catch (Exception ex)
                {
                    diagnostics.Add($"Failed to read/parse '{Path.GetFileName(files[0])}': {ex.Message}");
                }
            }
            diagnostics.AddRange(FindDuplicates(model));
            return new ProjectBuildResult(model, diagnostics, files);
        }

        var parts = new PartialParse[files.Count];     // one slot per file; index = order

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            CancellationToken = ct,
        };

        await Parallel.ForEachAsync(EnumerateIndexed(files), options, (item, _) =>
        {
            parts[item.Index] = ParseOne(item.Path);   // total: never throws a domain error
            return ValueTask.CompletedTask;            // CPU-bound body — no real awaiting
        });

        return Merge(parts, files);
    }

    private PartialParse ParseOne(string path)
    {
        try
        {
            var parsed = new Syntax.PgParser().Parse(File.ReadAllText(path)); // fresh instance → isolated per worker
            var model = new Syntax.ModelBuilder(DefaultSchema).Build(parsed);
            var rel = Path.GetRelativePath(ProjectDirectory, path);
            return new PartialParse(model, parsed.Diagnostics.Select(d => $"{rel}: {d}").ToList());
        }
        catch (Exception ex) // unreadable file / catastrophic parser failure → isolate to this file
        {
            return new PartialParse(new DatabaseModel(),
                new List<string> { $"Failed to read/parse '{Path.GetFileName(path)}': {ex.Message}" });
        }
    }

    private static ProjectBuildResult Merge(PartialParse[] parts, IReadOnlyList<string> files)
    {
        var model = new DatabaseModel();
        var diagnostics = new List<string>();

        foreach (var part in parts) // ordered walk → deterministic, matches sequential Build()
        {
            foreach (var s in part.Model.Schemas)
                if (!model.HasSchema(s.Name)) model.Schemas.Add(s); // first-occurrence wins
            model.Tables.AddRange(part.Model.Tables);
            model.Indexes.AddRange(part.Model.Indexes);
            model.Views.AddRange(part.Model.Views);
            model.Sequences.AddRange(part.Model.Sequences);
            model.Functions.AddRange(part.Model.Functions);
            model.Objects.AddRange(part.Model.Objects);
            diagnostics.AddRange(part.Diagnostics);
        }

        diagnostics.AddRange(FindDuplicates(model)); // same post-merge dup scan as Build()
        return new ProjectBuildResult(model, diagnostics, files);
    }

    private static IEnumerable<(int Index, string Path)> EnumerateIndexed(IReadOnlyList<string> files)
    {
        for (var i = 0; i < files.Count; i++) yield return (i, files[i]);
    }

    private readonly record struct PartialParse(DatabaseModel Model, List<string> Diagnostics);

    private static IEnumerable<string> FindDuplicates(DatabaseModel model)
    {
        foreach (var dup in model.Tables.GroupBy(t => $"{t.Schema}.{t.Name}".ToLowerInvariant()).Where(g => g.Count() > 1))
            yield return $"Duplicate table definition: {dup.Key} (defined {dup.Count()} times).";

        foreach (var dup in model.Views.GroupBy(v => $"{v.Schema}.{v.Name}".ToLowerInvariant()).Where(g => g.Count() > 1))
            yield return $"Duplicate view definition: {dup.Key} (defined {dup.Count()} times).";

        foreach (var dup in model.Functions.GroupBy(f => f.Signature.ToLowerInvariant()).Where(g => g.Count() > 1))
            yield return $"Duplicate function definition: {dup.Key} (defined {dup.Count()} times).";

        foreach (var dup in model.Indexes.GroupBy(i => $"{i.Schema}.{i.Name}".ToLowerInvariant()).Where(g => g.Count() > 1))
            yield return $"Duplicate index definition: {dup.Key} (defined {dup.Count()} times).";
    }
}

public sealed record ProjectBuildResult(
    DatabaseModel Model,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<string> Files)
{
    public bool HasErrors => Diagnostics.Count > 0;
}
