using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Reflection;
using PgProj.Core.Analysis;
using PgProj.Core.Comparison;
using PgProj.Core.Introspection;
using PgProj.Core.Model;
using PgProj.Core.Packaging;
using PgProj.Core.Project;
using PgProj.Core.Project.References;

namespace PgProj.Cli;

/// <summary>
/// The <c>pgproj</c> command-line tool: build a project into a model, compare it to a live server,
/// publish (generate + run a deploy script), or extract a live server back into a project.
/// This is the headless engine; a Visual Studio project-system/VSIX front-end can layer on top.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0) { PrintUsage(); return 1; }

            return args[0].ToLowerInvariant() switch
            {
                "build" => await Build(args),
                "compare" => await Compare(args),
                "publish" => await Publish(args),
                "validate" => await Validate(args),
                "extract" => await Extract(args),
                "drift" => await Drift(args),
                "pull" => await Pull(args),
                "analyze" => Analyze(args),
                "script" => await Script(args),
                "pkg" => await Pkg(args),
                "help" or "--help" or "-h" => PrintUsageReturn(0),
                _ => Fail($"Unknown command '{args[0]}'."),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
    }

    // ---- build --------------------------------------------------------------------------

    private static async Task<int> Build(string[] args)
    {
        var project = DatabaseProject.Load(RequirePositional(args, "project file"));
        var result = await project.BuildAsync();

        Console.WriteLine($"Building project '{project.Name}' ({result.Files.Count} file(s), default schema '{project.DefaultSchema}')");
        PrintModelSummary(result.Model);

        if (result.Diagnostics.Count > 0)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Build failed with {result.Diagnostics.Count} problem(s):");
            foreach (var d in result.Diagnostics) Console.Error.WriteLine($"  - {d}");
            return 1;
        }

        // Resolve references (EP-REF): build/read each referenced model into the semantic catalog, then
        // validate cross-schema names. External objects never enter the model, so they are never emitted.
        if (ResolveAndValidateReferencesBlocks(project)) return 1;

        // Static-analysis gate (skip with --no-analyze; escalate warnings with --strict).
        if (AnalysisGateBlocks(project, args)) return 1;

        var outPath = GetOption(args, "-o", "--output")
                      ?? Path.Combine(project.ProjectDirectory, "bin", $"{project.Name}.model.json");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllText(outPath, ModelJson.Serialize(result.Model));
        Console.WriteLine($"Model written to {outPath}");

        // Emit the portable .pgpkg artifact alongside the model (default on; --no-package to skip).
        if (!HasFlag(args, "--no-package"))
        {
            var pkgPath = GetOption(args, "--package")
                          ?? Path.Combine(Path.GetDirectoryName(outPath)!, $"{project.Name}{PgPkg.Extension}");
            Directory.CreateDirectory(Path.GetDirectoryName(pkgPath)!);
            var pkg = PgPkgBuilder.FromBuild(project, result.Model, result.Files, ToolVersion, UtcStamp());
            pkg.Write(pkgPath);
            Console.WriteLine($"Package written to {pkgPath} ({pkg.Sources.Count} source(s), checksum {pkg.Manifest.SourceChecksum}).");
        }

        Console.WriteLine("Build succeeded.");
        return 0;
    }

    // ---- compare ------------------------------------------------------------------------

    private static async Task<int> Compare(string[] args)
    {
        var (source, _) = await BuildSourceOrThrowAsync(args);
        var target = await ReadTarget(args);
        var changes = new SchemaComparer().Compare(source, target, new ComparerOptions
        {
            DropObjectsNotInSource = HasFlag(args, "--allow-drops"),
        });

        if (changes.Count == 0)
        {
            Console.WriteLine("No differences. The target already matches the project.");
            return 0;
        }

        Console.WriteLine($"{changes.Count} change(s) needed to bring the target in line with the project:");
        foreach (var c in changes)
            Console.WriteLine($"  [{(c.IsDestructive ? "!" : "+")}] {c.Describe()}");

        var destructive = changes.Count(c => c.IsDestructive);
        if (destructive > 0)
            Console.WriteLine($"\n{destructive} change(s) are destructive (marked with !).");
        return 0;
    }

    // ---- publish ------------------------------------------------------------------------

    private static async Task<int> Publish(string[] args)
    {
        var (source, project) = await BuildSourceOrThrowAsync(args);

        // Gate before touching the database: a failing analysis must not reach the server.
        if (AnalysisGateBlocks(project, args)) return 1;

        var target = await ReadTarget(args);

        var changes = new SchemaComparer().Compare(source, target, new ComparerOptions
        {
            DropObjectsNotInSource = HasFlag(args, "--allow-drops"),
        });

        var script = new DeployScriptGenerator().Generate(changes, new DeployOptions
        {
            WrapInTransaction = !HasFlag(args, "--no-transaction"),
        });

        var outPath = GetOption(args, "-o", "--output");
        if (outPath is not null)
        {
            File.WriteAllText(outPath, script);
            Console.WriteLine($"Deploy script written to {outPath}");
        }

        if (HasFlag(args, "--dry-run"))
        {
            Console.WriteLine(outPath is null ? script : "(dry run — not executed)");
            return 0;
        }

        if (changes.Count == 0)
        {
            Console.WriteLine("Nothing to publish — target already matches the project.");
            return 0;
        }

        if (HasFlag(args, "--parallel"))
        {
            // Intra-phase parallelism with phase barriers (phase-level atomicity).
            await new PhasedDeployer(RequireConnection(args)).ExecuteAsync(changes);
            Console.WriteLine($"Published {changes.Count} change(s) successfully (parallel, phased).");
        }
        else
        {
            // Default: whole script in one transaction (strict all-or-nothing).
            await new DatabaseDeployer().ExecuteAsync(RequireConnection(args), script);
            Console.WriteLine($"Published {changes.Count} change(s) successfully.");
        }
        return 0;
    }

    // ---- validate (apply to a throwaway temp DB in a rolled-back txn) --------------------

    private static async Task<int> Validate(string[] args)
    {
        var (source, project) = await BuildSourceOrThrowAsync(args);

        // Layer 1 (static, instant): the analysis gate. Layer 2 (below) runs it against real Postgres.
        if (AnalysisGateBlocks(project, args)) return 1;

        // Full create script with no BEGIN/COMMIT — ShadowValidator wraps it in its own transaction.
        var changes = new SchemaComparer().Compare(source, new DatabaseModel());
        var script = new DeployScriptGenerator().Generate(changes, new DeployOptions { WrapInTransaction = false });

        Console.WriteLine($"Validating '{project?.Name ?? source.Schemas.Count + " schema(s) from package"}' against a throwaway database…");
        var outcome = await new ShadowValidator().ValidateAsync(RequireConnection(args), script);
        if (outcome.Ok)
        {
            Console.WriteLine("Valid. ✓ The project applies cleanly to PostgreSQL (changes rolled back, scratch DB dropped).");
            return 0;
        }

        Console.Error.WriteLine($"Invalid: {outcome.Error}" + (outcome.SqlState is null ? "" : $"  [{outcome.SqlState}]"));
        if (outcome.Position > 0) Console.Error.WriteLine($"  near script position {outcome.Position}");
        return 1;
    }

    // ---- extract ------------------------------------------------------------------------

    private static async Task<int> Extract(string[] args)
    {
        var outDir = GetOption(args, "-o", "--output") ?? "extracted";
        var model = await new LiveDatabaseReader().ReadAsync(RequireConnection(args));
        var files = DdlExporter.ExportFiles(model);

        foreach (var (rel, content) in files)
        {
            var path = Path.Combine(outDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        // Drop a .pgproj so the extracted folder is immediately buildable.
        var projName = new Npgsql.NpgsqlConnectionStringBuilder(RequireConnection(args)).Database ?? "Extracted";
        File.WriteAllText(Path.Combine(outDir, $"{projName}.pgproj"), DefaultProjectFile(projName));

        Console.WriteLine($"Extracted {files.Count} object file(s) to {Path.GetFullPath(outDir)}");
        PrintModelSummary(model);
        return 0;
    }

    // ---- drift / pull (scenario 3: reverse-sync the project FROM the database) -----------

    private static async Task<int> Drift(string[] args)
    {
        var project = DatabaseProject.Load(RequirePositional(args, "project file"));
        var live = await ReadTarget(args);
        // Report everything, including would-be deletes, so the user sees the full picture.
        var plan = await PgProj.Core.Sync.ReverseSync.PlanAsync(project, live, new PgProj.Core.Sync.DriftOptions { AllowDeletes = true });

        if (!plan.HasDrift)
        {
            Console.WriteLine("No drift. The project already matches the database.");
            return 0;
        }

        Console.WriteLine($"{plan.FileChanges.Count} project file(s) drift from the database:");
        foreach (var fc in plan.FileChanges)
            Console.WriteLine($"  [{Mark(fc)}] {fc.Kind.ToString().ToLowerInvariant()} {fc.RelativePath} — {fc.Summary}");
        if (plan.DestructiveCount > 0)
            Console.WriteLine($"\n{plan.DestructiveCount} change(s) delete files (apply with: pull --allow-deletes).");
        Console.WriteLine("\nRun 'pull' to write these changes into the project.");
        return 0;
    }

    private static async Task<int> Pull(string[] args)
    {
        var project = DatabaseProject.Load(RequirePositional(args, "project file"));
        var live = await ReadTarget(args);
        var allowDeletes = HasFlag(args, "--allow-deletes");
        var plan = await PgProj.Core.Sync.ReverseSync.PlanAsync(project, live, new PgProj.Core.Sync.DriftOptions { AllowDeletes = allowDeletes });

        if (!plan.HasDrift)
        {
            Console.WriteLine("Nothing to pull — the project already matches the database.");
            return 0;
        }

        Console.WriteLine($"{plan.FileChanges.Count} project file(s) will be {(HasFlag(args, "--dry-run") ? "changed" : "written")}:");
        foreach (var fc in plan.FileChanges)
            Console.WriteLine($"  [{Mark(fc)}] {fc.Kind.ToString().ToLowerInvariant()} {fc.RelativePath} — {fc.Summary}");

        if (HasFlag(args, "--dry-run"))
        {
            Console.WriteLine("\n(dry run — no files written)");
            return 0;
        }

        var touched = PgProj.Core.Sync.ReverseSync.Apply(project, plan);
        Console.WriteLine($"\nPulled {touched.Count} file(s) from the database into the project.");
        if (!allowDeletes && plan.SchemaChanges.Any(c => c.IsDestructive))
            Console.WriteLine("Note: objects dropped in the database were left in place (re-run with --allow-deletes to remove their files).");
        return 0;
    }

    private static string Mark(PgProj.Core.Sync.ProjectFileChange fc) =>
        fc.IsDestructive ? "!" : fc.Kind == PgProj.Core.Sync.ProjectFileChangeKind.Create ? "+" : "~";

    // ---- analyze (static analysis over the AST) -----------------------------------------

    private static int Analyze(string[] args)
    {
        var project = DatabaseProject.Load(RequirePositional(args, "project file"));
        var findings = RunAnalysis(project, out var ruleCount);
        Console.WriteLine($"Analyzed '{project.Name}': {ruleCount} rule(s).");
        return ReportFindings(findings, HasFlag(args, "--strict"), alwaysReport: true) ? 1 : 0;
    }

    // ---- shared analysis gate -----------------------------------------------------------

    private static IReadOnlyList<Diagnostic> RunAnalysis(DatabaseProject project, out int ruleCount)
    {
        ruleCount = PgAnalyzer.RuleCount;
        var analyzer = new PgAnalyzer();
        var findings = new List<Diagnostic>();
        foreach (var file in project.ResolveSqlFiles())
            findings.AddRange(analyzer.Analyze(new PgProj.Core.Syntax.PgParser().Parse(File.ReadAllText(file))));
        return findings;
    }

    /// <summary>Prints findings and returns true if the gate should block (errors, or warnings under --strict).</summary>
    private static bool ReportFindings(IReadOnlyList<Diagnostic> findings, bool strict, bool alwaysReport)
    {
        var errors = findings.Count(f => f.Severity == DiagnosticSeverity.Error);
        var warnings = findings.Count(f => f.Severity == DiagnosticSeverity.Warning);
        var infos = findings.Count(f => f.Severity == DiagnosticSeverity.Info);
        var blocked = errors > 0 || (strict && warnings > 0);

        if (findings.Count == 0)
        {
            if (alwaysReport) Console.WriteLine("No findings. ✓");
            return false;
        }

        foreach (var d in findings.OrderByDescending(f => f.Severity))
            Console.WriteLine($"  {d}");
        Console.WriteLine($"analysis: {errors} error, {warnings} warning, {infos} info" +
                          (blocked ? "  — blocking (treat warnings as errors via --strict)" : ""));
        return blocked;
    }

    /// <summary>The build/publish gate. Returns true if the operation must abort.</summary>
    private static bool AnalysisGateBlocks(DatabaseProject? project, string[] args)
    {
        // No project (source was a pre-built .pgpkg) → nothing to re-analyze; it was gated at build time.
        if (project is null) return false;
        if (HasFlag(args, "--no-analyze")) return false;
        var findings = RunAnalysis(project, out _);
        var blocked = ReportFindings(findings, HasFlag(args, "--strict"), alwaysReport: false);
        if (blocked) Console.Error.WriteLine("Aborted by analysis gate (pass --no-analyze to skip).");
        return blocked;
    }

    // ---- references (EP-REF) ------------------------------------------------------------

    /// <summary>
    /// Resolves the project's references and validates cross-schema names against them. Returns true if the
    /// build/operation must abort (a reference failed to resolve, or a name in a managed schema is
    /// unresolved). Referenced objects are catalog-only — they are never added to the model, so the
    /// comparer never emits them into the deploy script.
    /// </summary>
    private static bool ResolveAndValidateReferencesBlocks(DatabaseProject project)
    {
        var resolution = new ReferenceResolver().Resolve(project);
        if (project.References.Count > 0)
            Console.WriteLine($"Resolved {resolution.References.Count} reference(s) " +
                              $"({project.References.Count} declared) → external schemas: " +
                              string.Join(", ", resolution.ExternalModel.Schemas.Select(s => s.Name).DefaultIfEmpty("(none)")));

        var blocked = false;
        foreach (var d in resolution.Diagnostics)
        {
            // A not-yet-restored PackageReference is a warning, not a hard failure — it's an explicitly
            // documented follow-up. Every other reference diagnostic is an error.
            var isError = d.Code != ReferenceErrorCodes.PackageRestoreNotImplemented;
            (isError ? Console.Error : Console.Out).WriteLine($"  {(isError ? "error" : "warning")} {d}");
            blocked |= isError;
        }

        var refDiags = ReferenceValidator.Validate(project, resolution);
        foreach (var d in refDiags)
        {
            Console.Error.WriteLine($"  error {d}");
            blocked = true;
        }

        if (blocked) Console.Error.WriteLine("Aborted: unresolved or invalid references.");
        return blocked;
    }

    // ---- script (full create from project, no server) -----------------------------------

    private static async Task<int> Script(string[] args)
    {
        var (source, _) = await BuildSourceOrThrowAsync(args);
        var changes = new SchemaComparer().Compare(source, new DatabaseModel());
        var script = new DeployScriptGenerator().Generate(changes, new DeployOptions
        {
            WrapInTransaction = !HasFlag(args, "--no-transaction"),
        });

        var outPath = GetOption(args, "-o", "--output");
        if (outPath is not null)
        {
            File.WriteAllText(outPath, script);
            Console.WriteLine($"Create script written to {outPath}");
        }
        else
        {
            Console.WriteLine(script);
        }
        return 0;
    }

    // ---- pkg (inspect a .pgpkg) ---------------------------------------------------------

    private static async Task<int> Pkg(string[] args)
    {
        var sub = args.Skip(1).FirstOrDefault(a => !a.StartsWith('-'))?.ToLowerInvariant();
        return sub switch
        {
            "inspect" => PkgInspect(args),
            null => Fail("Expected a 'pkg' subcommand (inspect)."),
            _ => Fail($"Unknown 'pkg' subcommand '{sub}'."),
        };
        // (async signature kept uniform with the other verbs; inspect is synchronous.)
    }

    private static int PkgInspect(string[] args)
    {
        // Positional after the "inspect" subcommand.
        var path = args.Skip(2).FirstOrDefault(a => !a.StartsWith('-'))
                   ?? throw new InvalidOperationException("Expected a .pgpkg path argument.");
        path = Path.GetFullPath(path);

        // Read() verifies the integrity checksum; a tampered/corrupt package throws PgPkgFormatException.
        var pkg = PgPkg.Read(path);
        var m = pkg.Manifest;

        Console.WriteLine($"Package: {path}");
        Console.WriteLine("Manifest:");
        Console.WriteLine($"  name           {m.Name}");
        Console.WriteLine($"  formatVersion  {m.FormatVersion}");
        Console.WriteLine($"  pgVersion      {m.PgVersion ?? "(unspecified)"}");
        Console.WriteLine($"  toolVersion    {m.ToolVersion}");
        Console.WriteLine($"  createdUtc     {m.CreatedUtc}");
        Console.WriteLine($"  sourceChecksum {m.SourceChecksum}");
        Console.WriteLine($"  sources        {pkg.Sources.Count} file(s)");

        var inventory = PgPkgInventory.Of(pkg.Model);
        Console.WriteLine($"Objects ({inventory.Count}):");
        foreach (var item in inventory)
            Console.WriteLine($"  [{item.Kind}] {item.Identity}");
        return 0;
    }

    // ---- helpers ------------------------------------------------------------------------

    /// <summary>The tool version stamped into packages. Injected here at the CLI boundary so core build
    /// code stays deterministic (never reads version/clock itself).</summary>
    private static string ToolVersion =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        is { Length: > 0 } v
            ? v
            : Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>The build timestamp, formatted ISO-8601 UTC. Read once here (the only DateTime.Now in the
    /// package pipeline) and injected into the manifest; override with PGPROJ_BUILD_STAMP for reproducible
    /// builds.</summary>
    private static string UtcStamp() =>
        Environment.GetEnvironmentVariable("PGPROJ_BUILD_STAMP") is { Length: > 0 } s
            ? s
            : DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Resolves the source model from EITHER a <c>.pgproj</c> (build it) OR a <c>.pgpkg</c> (load the
    /// embedded model — no re-parse). When the source is a package, <c>Project</c> is null: there is no
    /// project to run the static-analysis gate against (the package was already gated at build time), so
    /// callers must treat a null project as "skip the gate".
    /// </summary>
    private static async Task<(DatabaseModel Model, DatabaseProject? Project)> BuildSourceOrThrowAsync(string[] args)
    {
        var sourcePath = RequirePositional(args, "project file or package");
        if (PgPkg.IsPackagePath(sourcePath))
        {
            var pkg = PgPkg.Read(Path.GetFullPath(sourcePath));   // verifies integrity (checksum) on read
            Console.WriteLine($"Using package '{pkg.Manifest.Name}' (built {pkg.Manifest.CreatedUtc} by pgproj {pkg.Manifest.ToolVersion}).");
            return (pkg.Model, null);
        }

        var project = DatabaseProject.Load(sourcePath);
        var result = await project.BuildAsync();
        if (result.Diagnostics.Count > 0)
        {
            Console.Error.WriteLine("Project has build problems:");
            foreach (var d in result.Diagnostics) Console.Error.WriteLine($"  - {d}");
            throw new InvalidOperationException("Fix the build problems before continuing.");
        }

        // EP-REF: resolve/validate references before the model is used for compare/publish/script. A broken
        // cross-schema reference must fail here, not silently produce an invalid deploy.
        if (ResolveAndValidateReferencesBlocks(project))
            throw new InvalidOperationException("Fix the reference problems before continuing.");

        return (result.Model, project);
    }

    private static async Task<DatabaseModel> ReadTarget(string[] args) =>
        await new LiveDatabaseReader().ReadAsync(RequireConnection(args));

    private static void PrintModelSummary(DatabaseModel m) =>
        Console.WriteLine($"  schemas={m.Schemas.Count} tables={m.Tables.Count} " +
                          $"indexes={m.Indexes.Count} views={m.Views.Count} " +
                          $"sequences={m.Sequences.Count} functions={m.Functions.Count}");

    private static string RequireConnection(string[] args) =>
        GetOption(args, "-c", "--connection")
        ?? Environment.GetEnvironmentVariable("PGPROJ_CONNECTION")
        ?? throw new InvalidOperationException("A connection string is required (--connection or PGPROJ_CONNECTION).");

    private static string RequirePositional(string[] args, string what)
    {
        var value = args.Skip(1).FirstOrDefault(a => !a.StartsWith('-'));
        return value ?? throw new InvalidOperationException($"Expected a {what} argument.");
    }

    private static string? GetOption(string[] args, params string[] names)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (names.Contains(args[i], StringComparer.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static bool HasFlag(string[] args, string name) =>
        args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        PrintUsage();
        return 1;
    }

    private static int PrintUsageReturn(int code) { PrintUsage(); return code; }

    private static string DefaultProjectFile(string name) =>
        $"""
        <Project Sdk="PgProj.Sdk/0.1.0">
          <PropertyGroup>
            <Name>{name}</Name>
            <DefaultSchema>public</DefaultSchema>
          </PropertyGroup>
          <ItemGroup>
            <Build Include="**/*.sql" />
          </ItemGroup>
        </Project>
        """;

    private static void PrintUsage()
    {
        Console.WriteLine("""
        pgproj — PostgreSQL database project tool

        Usage:
          pgproj build   <project.pgproj> [-o model.json] [--package <out.pgpkg> | --no-package] [--strict] [--no-analyze]
                         (resolves <ProjectReference>/<ArtifactReference> into the build's catalog so
                          cross-schema names resolve; referenced objects are never emitted)
          pgproj script  <project.pgproj|.pgpkg> [-o create.sql] [--no-transaction]
          pgproj compare <project.pgproj|.pgpkg> --connection <conn> [--allow-drops]
          pgproj publish <project.pgproj|.pgpkg> --connection <conn> [--dry-run] [-o script.sql] [--allow-drops] [--no-transaction] [--parallel] [--strict] [--no-analyze]
          pgproj validate <project.pgproj|.pgpkg> --connection <conn> [--strict] [--no-analyze]   (apply to a throwaway temp DB, rolled back)
          pgproj pkg inspect <file.pgpkg>                                              (dump the manifest + object inventory)
          pgproj extract --connection <conn> -o <outDir>
          pgproj drift   <project.pgproj> --connection <conn>                          (preview project files that differ from the DB)
          pgproj pull    <project.pgproj> --connection <conn> [--dry-run] [--allow-deletes]   (rewrite project files FROM the DB — scenario 3)
          pgproj analyze <project.pgproj> [--strict]    (static safety analysis over the AST)

        Options:
          -c, --connection   Postgres connection string (or set PGPROJ_CONNECTION)
          -o, --output       Output file or directory
          --dry-run          Generate the deploy script but do not execute it
          --allow-drops      Allow destructive changes (drop tables/columns/etc. not in the project)
          --allow-deletes    pull: delete project files for objects dropped from the database
          --package          build: write the .pgpkg to this path (default bin/<Name>.pgpkg)
          --no-package       build: skip writing the portable .pgpkg artifact
          --no-transaction   Do not wrap the deploy script in BEGIN/COMMIT
          --parallel         Publish with intra-phase parallelism (phase-level atomicity)
          --strict           Analysis gate: treat warnings as errors (build/publish fail on warnings)
          --no-analyze       Skip the static-analysis gate on build/publish
        """);
    }
}
