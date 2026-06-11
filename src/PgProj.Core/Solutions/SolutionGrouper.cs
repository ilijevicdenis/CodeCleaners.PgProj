using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PgProj.Core.Solutions;

/// <summary>
/// The engine behind <c>pgproj sln</c> — slngen-style solution grouping so multiple <c>.pgproj</c>s
/// manage as one solution (the SSDT-matrix <em>Solution management</em> row). Scans a root directory
/// for projects and groups them into a <see cref="SlnxDocument"/> whose solution folders mirror the
/// directory tree: a project's own directory becomes the project node, its ancestor directories
/// become solution folders (a project sitting directly under the root gets no folder). Pure
/// file-system work — deterministic output, unit-testable without the CLI.
/// </summary>
public static class SolutionGrouper
{
    /// <summary>The result of generating or updating a solution.</summary>
    public sealed record Result(string SolutionPath, SlnxDocument Solution, IReadOnlyList<string> AddedProjects);

    /// <summary>Directory names never scanned for projects.</summary>
    private static readonly string[] SkippedDirectories = ["bin", "obj", ".git", ".vs", "node_modules"];

    /// <summary>
    /// Finds every <c>*.pgproj</c> under <paramref name="rootDirectory"/> (skipping bin/obj/.git/.vs),
    /// returned sorted by relative path for deterministic grouping.
    /// </summary>
    public static IReadOnlyList<string> FindProjects(string rootDirectory)
    {
        var root = Path.GetFullPath(rootDirectory);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Directory not found: {root}");

        var results = new List<string>();
        Walk(root);
        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;

        void Walk(string dir)
        {
            results.AddRange(Directory.EnumerateFiles(dir, "*.pgproj", SearchOption.TopDirectoryOnly));
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (!SkippedDirectories.Contains(name, StringComparer.OrdinalIgnoreCase))
                    Walk(sub);
            }
        }
    }

    /// <summary>
    /// Generates <c>&lt;name&gt;.slnx</c> in <paramref name="outputDirectory"/> grouping every
    /// <c>.pgproj</c> under <paramref name="rootDirectory"/> (defaults to the output directory).
    /// Re-running over an existing solution is additive and idempotent: projects already listed are
    /// kept (with their folders), new ones are added, and the file is rewritten in canonical form.
    /// </summary>
    public static Result Generate(string name, string outputDirectory, string? rootDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A solution name is required.");

        var outDir = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outDir);
        var solutionPath = Path.Combine(outDir, name + ".slnx");
        var root = Path.GetFullPath(rootDirectory ?? outDir);

        var solution = File.Exists(solutionPath) ? SlnxDocument.Load(solutionPath) : SlnxDocument.Empty();
        // Project paths are solution-relative (what the loader needs); the folder tree mirrors the
        // SCAN root, so generating into a sibling/build directory doesn't leak "../" walks into names.
        var added = AddProjects(solution, outDir, folderBaseDir: root, FindProjects(root));

        solution.Save(solutionPath);
        return new Result(solutionPath, solution, added);
    }

    /// <summary>
    /// Adds explicit project paths to an existing solution file, deriving each project's solution
    /// folder from its directory relative to the solution. Returns the paths actually added
    /// (duplicates are skipped).
    /// </summary>
    public static Result Add(string solutionPath, IEnumerable<string> projectPaths)
    {
        var fullSolutionPath = Path.GetFullPath(solutionPath);
        if (!File.Exists(fullSolutionPath))
            throw new FileNotFoundException($"Solution not found: {fullSolutionPath}");

        var solutionDir = Path.GetDirectoryName(fullSolutionPath)!;
        var projects = projectPaths.Select(Path.GetFullPath).ToList();
        foreach (var p in projects.Where(p => !File.Exists(p)))
            throw new FileNotFoundException($"Project not found: {p}");

        var solution = SlnxDocument.Load(fullSolutionPath);
        var added = AddProjects(solution, solutionDir, folderBaseDir: solutionDir, projects);

        solution.Save(fullSolutionPath);
        return new Result(fullSolutionPath, solution, added);
    }

    private static IReadOnlyList<string> AddProjects(
        SlnxDocument solution, string solutionDir, string folderBaseDir, IEnumerable<string> projectFullPaths)
    {
        var added = new List<string>();
        foreach (var projectPath in projectFullPaths)
        {
            var relative = SlnxDocument.NormalizePath(Path.GetRelativePath(solutionDir, projectPath));
            var folderRelative = SlnxDocument.NormalizePath(Path.GetRelativePath(folderBaseDir, projectPath));
            if (solution.AddProject(relative, DeriveFolder(folderRelative)))
                added.Add(relative);
        }
        return added;
    }

    /// <summary>
    /// slngen-style folder derivation: the directories above the project's own directory become the
    /// solution folder. <c>References/Common/Common.pgproj</c> → <c>/References/</c>;
    /// <c>SampleDb/SampleDb.pgproj</c> → solution root.
    /// </summary>
    public static string DeriveFolder(string relativeProjectPath)
    {
        var segments = SlnxDocument.NormalizePath(relativeProjectPath).Split('/');
        if (segments.Length <= 2) return "";
        // segments = [folder..., projectDir, file]; everything before projectDir is the folder chain.
        // ".."/"." segments (a project outside the solution directory) make no folder name — drop them.
        var chain = segments[..^2].Where(s => s is not ("." or "..")).ToArray();
        return chain.Length == 0 ? "" : SlnxDocument.NormalizeFolderName(string.Join('/', chain));
    }
}
