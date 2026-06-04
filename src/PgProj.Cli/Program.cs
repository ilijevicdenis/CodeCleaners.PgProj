using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Reflection;
using PgProj.Core.Analysis;
using PgProj.Core.Comparison;
using PgProj.Core.Contracts;
using PgProj.Core.Deployment;
using PgProj.Core.Introspection;
using PgProj.Core.Model;
using PgProj.Core.Packaging;
using PgProj.Core.Project;

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
                "model-tree" => await ModelTree(args),
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

        if (WantsJson(args))
        {
            var report = await ContractBuilder.BuildAsync(project, includeTree: true);
            EmitJson(report);
            return report.Success ? 0 : 1;
        }

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
        var (source, project) = await BuildSourceOrThrowAsync(args);
        var target = await ReadTarget(args);

        if (WantsJson(args))
        {
            EmitJson(ContractBuilder.Compare(source, target, SourceName(project), HasFlag(args, "--allow-drops")));
            return 0;
        }

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

        // JSON dry-run: emit the plan + script, no server mutation, no text gate output to pollute stdout.
        if (WantsJson(args) && HasFlag(args, "--dry-run"))
        {
            var target0 = await ReadTarget(args);
            EmitJson(ContractBuilder.PublishPlan(source, target0, SourceName(project),
                HasFlag(args, "--allow-drops"), wrapInTransaction: !HasFlag(args, "--no-transaction")));
            return 0;
        }

        // Gate before touching the database: a failing analysis must not reach the server.
        if (AnalysisGateBlocks(project, args)) return 1;

        var target = await ReadTarget(args);

        var changes = new SchemaComparer().Compare(source, target, new ComparerOptions
        {
            DropObjectsNotInSource = HasFlag(args, "--allow-drops"),
        });

        var variables = BuildVariableResolver(project, args);
        var script = new DeployScriptGenerator().Generate(changes, new DeployOptions
        {
            WrapInTransaction = !HasFlag(args, "--no-transaction"),
            Scripts = LoadDeployScripts(project),
            Variables = variables,
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

        var scripts = LoadDeployScripts(project);
        if (changes.Count == 0 && (scripts is null || scripts.IsEmpty))
        {
            Console.WriteLine("Nothing to publish — target already matches the project.");
            return 0;
        }

        // --parallel runs the diff phase-by-phase, but pre/post deploy scripts have no phase model and
        // must bracket the diff inside one transaction — so fall back to the whole-script deployer when
        // deploy scripts are present (still strict all-or-nothing).
        if (HasFlag(args, "--parallel") && (scripts is null || scripts.IsEmpty))
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

        if (WantsJson(args))
        {
            var report = ContractBuilder.Analyze(project, HasFlag(args, "--strict"));
            EmitJson(report);
            return report.Blocked ? 1 : 0;
        }

        var findings = RunAnalysis(project, out var ruleCount);
        Console.WriteLine($"Analyzed '{project.Name}': {ruleCount} rule(s).");
        return ReportFindings(findings, HasFlag(args, "--strict"), alwaysReport: true) ? 1 : 0;
    }

    // ---- model-tree (editor endpoint: objects + source positions) -----------------------

    private static async Task<int> ModelTree(string[] args)
    {
        var project = DatabaseProject.Load(RequirePositional(args, "project file"));
        var tree = await ContractBuilder.ModelTreeAsync(project);

        if (WantsJson(args))
        {
            EmitJson(tree);
            return 0;
        }

        // Human fallback: a flat outline. The JSON form is the contract; this is a convenience.
        Console.WriteLine($"Model tree for '{project.Name}' ({tree.Nodes.Count} object(s)):");
        foreach (var n in tree.Nodes)
        {
            var loc = n.Line > 0 ? $"  ({n.File}:{n.Line})" : "";
            Console.WriteLine($"  [{n.Kind}] {n.QualifiedName}{loc}");
        }
        return 0;
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

    // ---- script (full create from project, no server) -----------------------------------

    private static async Task<int> Script(string[] args)
    {
        var (source, project) = await BuildSourceOrThrowAsync(args);
        var changes = new SchemaComparer().Compare(source, new DatabaseModel());
        var script = new DeployScriptGenerator().Generate(changes, new DeployOptions
        {
            WrapInTransaction = !HasFlag(args, "--no-transaction"),
            Scripts = LoadDeployScripts(project),
            Variables = BuildVariableResolver(project, args),
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

    // ---- deploy-script + variable helpers (EP-DEPLOYSCRIPTS / EP-VARS) -------------------

    /// <summary>Loads the project's pre/post-deploy script bodies (verbatim) into a bundle, or null if none
    /// (or if the source was a pre-built package with no project to read scripts from).</summary>
    private static DeployScriptBundle? LoadDeployScripts(DatabaseProject? project)
    {
        if (project is null) return null;

        DeployScript? Read(string? path)
        {
            if (path is null) return null;
            if (!File.Exists(path))
                throw new FileNotFoundException($"Deploy script not found: {path}");
            return new DeployScript(Path.GetFileName(path), File.ReadAllText(path));
        }

        var pre = Read(project.PreDeployScriptPath);
        var post = Read(project.PostDeployScriptPath);
        return pre is null && post is null ? null : new DeployScriptBundle(pre, post);
    }

    /// <summary>
    /// Builds the resolved SQLCMD-variable map: project DefaultValues overlaid by CLI <c>--var N=V</c>
    /// (repeatable). Precedence: CLI &gt; publish profile (future) &gt; project default. When the source was a
    /// pre-built package (no project), only the CLI overrides apply.
    /// </summary>
    private static SqlCmdVariableResolver BuildVariableResolver(DatabaseProject? project, string[] args) =>
        SqlCmdVariableResolver.Build(
            defaults: project?.SqlCmdVariableDefaults ?? new Dictionary<string, string>(),
            profile: null,                          // EP-PROFILE — not yet wired
            cliOverrides: ParseCliVars(args));

    /// <summary>Parses every <c>--var Name=Value</c> occurrence into a case-insensitive override map.</summary>
    private static IReadOnlyDictionary<string, string> ParseCliVars(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (!args[i].Equals("--var", StringComparison.OrdinalIgnoreCase)) continue;
            var pair = args[i + 1];
            var eq = pair.IndexOf('=');
            if (eq <= 0)
                throw new InvalidOperationException($"--var expects Name=Value (got '{pair}').");
            map[pair[..eq].Trim()] = pair[(eq + 1)..];
        }
        return map;
    }

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

        // Opt-in: substitute $(Var) tokens into object files too (default scope is deploy-scripts only).
        // Documented under --substitute-objects; unresolved tokens fail the build with a file:line diagnostic.
        if (HasFlag(args, "--substitute-objects"))
        {
            var resolver = BuildVariableResolver(project, args);
            project = project with
            {
                ObjectContentTransform = (rel, text) => resolver.Substitute(text, rel),
            };
        }

        var result = await project.BuildAsync();
        if (result.Diagnostics.Count > 0)
        {
            Console.Error.WriteLine("Project has build problems:");
            foreach (var d in result.Diagnostics) Console.Error.WriteLine($"  - {d}");
            throw new InvalidOperationException("Fix the build problems before continuing.");
        }
        return (result.Model, project);
    }

    /// <summary>Display name for the source: the project name, or "(package)" when the source was a pre-built .pgpkg.</summary>
    private static string SourceName(DatabaseProject? project) => project?.Name ?? "(package)";

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

    /// <summary>True when the caller asked for machine-readable output (<c>--format json</c>).</summary>
    private static bool WantsJson(string[] args) =>
        string.Equals(GetOption(args, "--format"), "json", StringComparison.OrdinalIgnoreCase);

    /// <summary>Writes a contract payload to stdout as stable JSON.</summary>
    private static void EmitJson<T>(T payload) => Console.WriteLine(JsonContract.Serialize(payload));

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
          pgproj build   <project.pgproj> [-o model.json] [--package <out.pgpkg> | --no-package] [--strict] [--no-analyze] [--var N=V] [--substitute-objects]
          pgproj script  <project.pgproj|.pgpkg> [-o create.sql] [--no-transaction] [--var N=V]
          pgproj compare <project.pgproj|.pgpkg> --connection <conn> [--allow-drops]
          pgproj publish <project.pgproj|.pgpkg> --connection <conn> [--dry-run] [-o script.sql] [--allow-drops] [--no-transaction] [--parallel] [--strict] [--no-analyze] [--var N=V] [--substitute-objects]
          pgproj validate <project.pgproj|.pgpkg> --connection <conn> [--strict] [--no-analyze]   (apply to a throwaway temp DB, rolled back)
          pgproj pkg inspect <file.pgpkg>                                              (dump the manifest + object inventory)
          pgproj extract --connection <conn> -o <outDir>
          pgproj drift   <project.pgproj> --connection <conn>                          (preview project files that differ from the DB)
          pgproj pull    <project.pgproj> --connection <conn> [--dry-run] [--allow-deletes]   (rewrite project files FROM the DB — scenario 3)
          pgproj analyze <project.pgproj> [--strict]    (static safety analysis over the AST)
          pgproj model-tree <project.pgproj> [--format json]   (objects + source positions, for editors)

        Options:
          --format json      Machine-readable, versioned JSON (build/analyze/compare/publish --dry-run/model-tree)
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
          --var Name=Value   Override a SqlCmdVariable (repeatable; CLI beats the project DefaultValue)
          --substitute-objects  Also expand $(Var) tokens in object .sql files (default: deploy-scripts only)

        Pre/post-deploy scripts & variables:
          Declare in the .pgproj:
            <None Include="Scripts/PreDeploy.sql"><BuildAction>PreDeploy</BuildAction></None>
            <None Include="Scripts/PostDeploy.sql"><BuildAction>PostDeploy</BuildAction></None>
            <SqlCmdVariable Include="EnvSuffix"><DefaultValue>dev</DefaultValue></SqlCmdVariable>
          The deploy script is spliced pre -> schema-diff -> post (all in one BEGIN/COMMIT unless
          --no-transaction). $(Name) tokens in the deploy scripts are substituted; an unresolved token
          fails with a file:line diagnostic. Write a literal "$(" as "$$(".
        """);
    }
}
