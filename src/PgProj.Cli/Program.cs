using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Reflection;
using PgProj.Core.Analysis;
using PgProj.Core.Cli;
using PgProj.Core.Comparison;
using PgProj.Core.Contracts;
using PgProj.Core.Deployment;
using PgProj.Core.Introspection;
using PgProj.Core.Model;
using PgProj.Core.Packaging;
using PgProj.Core.Project;
using PgProj.Core.Project.References;
using PgProj.Core.Snapshot;
using PgProj.Core.Templates;

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
            if (args.Length == 0) { PrintUsage(); return ExitCode.Usage; }

            return args[0].ToLowerInvariant() switch
            {
                "new" => New(args),
                "add" => Add(args),
                "build" => await Build(args),
                "compare" => await Compare(args),
                "publish" => await Publish(args),
                "validate" => await Validate(args),
                "extract" => await Extract(args),
                "snapshot" => await Snapshot(args),
                "drift" => await Drift(args),
                "pull" => await Pull(args),
                "analyze" => Analyze(args),
                "model-tree" => await ModelTree(args),
                "describe-table" => DescribeTable(args),
                "emit-table" => EmitTable(args),
                "script" => await Script(args),
                "pkg" => await Pkg(args),
                "profile" => Profile(args),
                "serve" => await Serve(args),
                "help" or "--help" or "-h" => PrintUsageReturn(ExitCode.Success),
                _ => Fail($"Unknown command '{args[0]}'."),
            };
        }
        catch (CliUsageException ex)
        {
            // A user mistake (missing/malformed argument, unknown option) — distinct from a runtime failure.
            Console.Error.WriteLine($"error: {ex.Message}");
            PrintUsage();
            return ExitCode.Usage;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ExitCode.Error;
        }
    }

    // ---- new project (scaffold an empty, buildable project) -----------------------------

    private static int New(string[] args)
    {
        // Only "new project <name>" is defined today; "new <name>" is a friendly alias.
        var positionals = args.Skip(1).Where(a => !a.StartsWith('-')).ToList();
        if (positionals.Count > 0 && positionals[0].Equals("project", StringComparison.OrdinalIgnoreCase))
            positionals.RemoveAt(0);

        var name = positionals.FirstOrDefault()
            ?? throw new InvalidOperationException("Usage: pgproj new project <name> [-o <dir>] [--default-schema public] [--target-version 18]");

        var outDir = GetOption(args, "-o", "--output") ?? ".";
        var schema = GetOption(args, "--default-schema") ?? "public";
        var version = GetOption(args, "--target-version") ?? "18";

        var result = Scaffolder.NewProject(name, outDir, schema, version);
        Console.WriteLine($"Created project '{name}' at {result.ProjectDirectory}");
        Console.WriteLine($"  manifest: {Path.GetFileName(result.ProjectFilePath)} (default schema '{schema}', target PostgreSQL {version})");
        Console.WriteLine($"Next: pgproj add table {schema}.MyTable && pgproj build \"{result.ProjectFilePath}\"");
        return 0;
    }

    // ---- add (scaffold an object file from a template) ----------------------------------

    private static int Add(string[] args)
    {
        var positionals = args.Skip(1).Where(a => !a.StartsWith('-')).ToList();
        if (positionals.Count < 2)
            throw new InvalidOperationException(
                $"Usage: pgproj add <kind> <schema.name> [-p <project|dir>] [--force]   (kinds: {TemplateCatalog.KindWords})");

        var kind = positionals[0];
        var nameArg = positionals[1];
        // Project location: -p/--project, or the current directory (must contain one .pgproj).
        var projectArg = GetOption(args, "-p", "--project") ?? positionals.ElementAtOrDefault(2) ?? ".";
        var force = HasFlag(args, "--force");

        var result = Scaffolder.Add(projectArg, kind, nameArg, force);
        Console.WriteLine($"Added {kind} → {result.RelativePath}");
        return 0;
    }

    // ---- build --------------------------------------------------------------------------

    private static async Task<int> Build(string[] args)
    {
        var project = DatabaseProject.Load(RequirePositional(args, "project file"));

        if (WantsJson(args))
        {
            var report = await ContractBuilder.BuildAsync(project, includeTree: true);
            EmitJson(report);
            return report.Success ? ExitCode.Success : ExitCode.BuildError;
        }

        var result = await project.BuildAsync();

        Console.WriteLine($"Building project '{project.Name}' ({result.Files.Count} file(s), default schema '{project.DefaultSchema}')");
        PrintModelSummary(result.Model);

        if (result.Diagnostics.Count > 0)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Build failed with {result.Diagnostics.Count} problem(s):");
            foreach (var d in result.Diagnostics) Console.Error.WriteLine($"  - {d}");
            return ExitCode.BuildError;
        }

        // Resolve references (EP-REF): build/read each referenced model into the semantic catalog, then
        // validate cross-schema names. External objects never enter the model, so they are never emitted.
        if (ResolveAndValidateReferencesBlocks(project)) return ExitCode.ReferenceError;

        // Static-analysis gate (skip with --no-analyze; escalate warnings with --strict).
        if (AnalysisGateBlocks(project, args)) return ExitCode.AnalysisBlocked;

        // Target-platform gate (EP-TARGET): block syntax newer than <TargetPostgresVersion>.
        if (TargetVersionGateBlocks(project, args)) return ExitCode.AnalysisBlocked;

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

    // ---- compare (EP-SCHEMACOMPARE: first-class two-way Schema Compare) ------------------

    private static async Task<int> Compare(string[] args)
    {
        var cli = new CliArgs(args);

        // Two-way form (--source X --target Y): each endpoint ∈ {project, .pgpkg, .schema.snapshot, live DB},
        // resolved through the shared EndpointResolver so the full {project,pkg,snapshot,live}² matrix is one
        // code path. When --source/--target are absent we fall back to the legacy form below for back-compat.
        if (cli.GetOption("--source") is { } sourceSpec)
        {
            var targetSpec = cli.GetOption("--target")
                ?? throw new CliUsageException("compare --source <X> requires --target <Y> (a .pgproj, .pgpkg, .schema.snapshot, or connection string).");
            return await CompareTwoWay(cli, sourceSpec, targetSpec);
        }

        // Positional two-way form: `compare <source> <target>` where the target positional is itself an
        // endpoint (e.g. `compare app.pgproj db.schema.snapshot`). This is the offline-compare invocation in
        // the snapshot acceptance criteria — routed through the same matrix path as --source/--target.
        if (cli.Positional(0) is { } pos0 && cli.Positional(1) is { } pos1)
            return await CompareTwoWay(cli, pos0, pos1);

        return await CompareLegacy(args);
    }

    /// <summary>The two-way Schema Compare: source &amp; target each a project/package/snapshot/live DB.</summary>
    private static async Task<int> CompareTwoWay(CliArgs cli, string sourceSpec, string targetSpec)
    {
        var options = new ComparerOptions { DropObjectsNotInSource = cli.HasFlag("--allow-drops") };
        var excludes = ParseExcludeObjectTypes(cli);

        var result = await SchemaCompare.RunAsync(sourceSpec, targetSpec, options, excludes);
        var changeSet = result.ChangeSet;

        // Staleness: a snapshot endpoint compared offline is flagged when its captured source PG version
        // (or its format version) mismatches what the other side expects — surfaced to stderr as a warning
        // (a signal, not a failure: the compare still ran against the snapshot's model).
        WarnIfSnapshotStale(result.Source, result.Target);
        WarnIfSnapshotStale(result.Target, result.Source);

        // --output diff.json: write the structured, selectable diff for a UI to render (always JSON, regardless
        // of --format, since the consumer is a tool). --format json mirrors it to stdout for piping.
        var report = SchemaCompareReport.Build(result);
        var outPath = cli.GetOption("-o", "--output");
        if (outPath is not null)
        {
            File.WriteAllText(outPath, SchemaCompareReport.Serialize(report));
            if (!cli.WantsJson) Console.WriteLine($"Schema-compare diff written to {outPath}");
        }

        if (cli.WantsJson)
        {
            Console.WriteLine(SchemaCompareReport.Serialize(report));
            return FailOnChangesGate(cli, changeSet);
        }

        PrintCompareBanner(result);
        if (changeSet.InSync)
        {
            Console.WriteLine("No differences. Source and target are in sync.");
            return FailOnChangesGate(cli, changeSet);
        }

        var shown = changeSet.Included;
        var excluded = changeSet.Count - shown.Count;
        Console.WriteLine($"{shown.Count} change(s) would bring the target in line with the source" +
                          (excluded > 0 ? $" ({excluded} excluded by filter):" : ":"));
        foreach (var c in shown)
            Console.WriteLine($"  [{(c.IsDestructive ? "!" : "+")}] ({c.ObjectType}) {c.Description}  #{c.Id}");

        var destructive = shown.Count(c => c.IsDestructive);
        if (destructive > 0)
            Console.WriteLine($"\n{destructive} included change(s) are destructive (marked with !).");
        return FailOnChangesGate(cli, changeSet);
    }

    /// <summary>The legacy one-way form: <c>compare &lt;project|pkg&gt; --connection &lt;conn&gt;</c> (project → live DB).</summary>
    private static async Task<int> CompareLegacy(string[] args)
    {
        var profile = LoadProfile(args);            // EP-PROFILE: CLI flags override profile values.
        var (source, project) = await BuildSourceOrThrowAsync(args);
        var versionProfile = ProfileFor(project); // PG version profile from TargetPostgresVersion
        var target = await ReadTarget(args, versionProfile);
        var allowDrops = ResolveAllowDrops(args, profile);

        if (WantsJson(args))
        {
            EmitJson(ContractBuilder.Compare(source, target, SourceName(project), allowDrops));
            return 0;
        }

        var changes = new SchemaComparer(versionProfile).Compare(source, target, new ComparerOptions
        {
            DropObjectsNotInSource = allowDrops,
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

    /// <summary>Parses repeatable/comma-joined <c>--exclude</c> object-type tokens for the two-way compare.</summary>
    private static IReadOnlyList<string> ParseExcludeObjectTypes(CliArgs cli) =>
        cli.GetOptionValues("--exclude")
            .SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToList();

    /// <summary>Optional drift gate: with <c>--fail-on-changes</c> (alias <c>--fail-on-drift</c>), a
    /// non-empty diff returns <see cref="ExitCode.Drift"/>.</summary>
    private static int FailOnChangesGate(CliArgs cli, SchemaChangeSet changeSet) =>
        (cli.HasFlag("--fail-on-changes") || cli.HasFlag("--fail-on-drift")) && changeSet.IncludedCount > 0
            ? ExitCode.Drift : ExitCode.Success;

    private static void PrintCompareBanner(SchemaCompareResult result)
    {
        static string Label(PgProj.Core.Cli.ResolvedEndpoint e) =>
            $"{e.DisplayName} [{e.Kind.ToString().ToLowerInvariant()}]";
        Console.WriteLine($"Schema compare: {Label(result.Source)}  →  {Label(result.Target)}");
        foreach (var d in result.Source.BuildDiagnostics) Console.Error.WriteLine($"  source build: {d}");
        foreach (var d in result.Target.BuildDiagnostics) Console.Error.WriteLine($"  target build: {d}");
    }

    /// <summary>
    /// If <paramref name="endpoint"/> is a snapshot, checks it for staleness against the version
    /// <paramref name="other"/> expects (a project's TargetPostgresVersion; otherwise just the format check)
    /// and prints any reasons to stderr. Staleness is a signal, not a failure — the compare still ran.
    /// </summary>
    private static void WarnIfSnapshotStale(PgProj.Core.Cli.ResolvedEndpoint endpoint, PgProj.Core.Cli.ResolvedEndpoint other)
    {
        if (endpoint.Kind != PgProj.Core.Cli.EndpointKind.Snapshot || endpoint.SnapshotManifest is not { } manifest)
            return;

        var staleness = new SchemaSnapshot { Manifest = manifest, Model = endpoint.Model }
            .CheckStaleness(ExpectedMajorOf(other));
        if (!staleness.IsStale) return;

        Console.Error.WriteLine($"warning: snapshot '{endpoint.DisplayName}' may be stale:");
        foreach (var reason in staleness.Reasons) Console.Error.WriteLine($"  - {reason}");
    }

    /// <summary>The PostgreSQL major version an endpoint asserts: a project's TargetPostgresVersion (when set),
    /// else null (no version expectation to check the snapshot against).</summary>
    private static int? ExpectedMajorOf(PgProj.Core.Cli.ResolvedEndpoint endpoint) =>
        endpoint.Project is { } p
            ? PgProj.Core.Analysis.TargetVersionAnalyzer.ParseMajorVersion(p.TargetPostgresVersion)
            : null;

    // ---- publish ------------------------------------------------------------------------

    private static async Task<int> Publish(string[] args)
    {
        var profile = LoadProfile(args);            // EP-PROFILE: CLI flags override profile values.
        var (source, project) = await BuildSourceOrThrowAsync(args);
        var allowDrops = ResolveAllowDrops(args, profile);
        var wrapInTransaction = ResolveWrapInTransaction(args, profile);

        // JSON dry-run: emit the plan + script, no server mutation, no text gate output to pollute stdout.
        if (WantsJson(args) && HasFlag(args, "--dry-run"))
        {
            var target0 = await ReadTarget(args);
            EmitJson(ContractBuilder.PublishPlan(source, target0, SourceName(project),
                allowDrops, wrapInTransaction: wrapInTransaction));
            return 0;
        }

        // Gate before touching the database: a failing analysis must not reach the server.
        if (AnalysisGateBlocks(project, args)) return ExitCode.AnalysisBlocked;

        // Target-platform gate (EP-TARGET): never publish syntax newer than <TargetPostgresVersion>.
        if (TargetVersionGateBlocks(project, args)) return ExitCode.AnalysisBlocked;

        var versionProfile = ProfileFor(project); // PG version profile from TargetPostgresVersion
        var target = await ReadTarget(args, versionProfile);

        var changes = new SchemaComparer(versionProfile).Compare(source, target, new ComparerOptions
        {
            DropObjectsNotInSource = allowDrops,
        });

        var variables = BuildVariableResolver(project, args, profile);
        var script = new DeployScriptGenerator().Generate(changes, new DeployOptions
        {
            WrapInTransaction = wrapInTransaction,
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

        // A server-side failure while applying the script is a distinct CI failure class (EP-CICD):
        // map it to ExitCode.DeployError so a pipeline can alert specifically on a failed deploy
        // (vs a build/analysis problem that never touched the target).
        try
        {
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
        }
        catch (Npgsql.PostgresException ex)
        {
            // Enrich the server's bare SQLSTATE with its symbolic condition name + class (PgErrorCodes).
            Console.Error.WriteLine($"Deploy failed: {ex.Message}  [{PgProj.Core.Diagnostics.PgErrorCodes.Describe(ex.SqlState)}]");
            return ExitCode.DeployError;
        }
        catch (Npgsql.NpgsqlException ex)
        {
            Console.Error.WriteLine($"Deploy failed: {ex.Message}");
            return ExitCode.DeployError;
        }
        return 0;
    }

    // ---- validate (apply to a throwaway temp DB in a rolled-back txn) --------------------

    private static async Task<int> Validate(string[] args)
    {
        var (source, project) = await BuildSourceOrThrowAsync(args);

        // Layer 1 (static, instant): the analysis gate. Layer 2 (below) runs it against real Postgres.
        if (AnalysisGateBlocks(project, args)) return ExitCode.AnalysisBlocked;

        // Target-platform gate (EP-TARGET): a project that uses syntax newer than <TargetPostgresVersion>
        // fails validation statically, before the (possibly mismatched) shadow database is spun up.
        if (TargetVersionGateBlocks(project, args)) return ExitCode.AnalysisBlocked;

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

        Console.Error.WriteLine($"Invalid: {outcome.Error}" +
            (outcome.SqlState is null ? "" : $"  [{PgProj.Core.Diagnostics.PgErrorCodes.Describe(outcome.SqlState)}]"));
        if (outcome.Position > 0) Console.Error.WriteLine($"  near script position {outcome.Position}");
        return ExitCode.ValidationFailed;
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

    // ---- snapshot (capture a live DB's canonical model to a portable .schema.snapshot) ---

    /// <summary>
    /// Introspects a live database ONCE and persists its canonical model to a <c>.schema.snapshot</c> so a
    /// later <c>compare</c> can run against the database's schema offline — no re-introspection, no DB
    /// connection on the compare step. The manifest stamps the source PostgreSQL version (basis for
    /// staleness), so a snapshot captured against the wrong server version is flagged when it is compared.
    /// </summary>
    private static async Task<int> Snapshot(string[] args)
    {
        var conn = RequireConnection(args);
        var outPath = GetOption(args, "-o", "--output")
                      ?? throw new CliUsageException("snapshot requires -o <file.schema.snapshot> (the output path).");
        if (!SchemaSnapshot.IsSnapshotPath(outPath))
            Console.Error.WriteLine($"warning: '{Path.GetFileName(outPath)}' does not end in {SchemaSnapshot.Extension} " +
                                    "— it will not be recognised as a snapshot by compare.");

        var snapshot = await new SchemaSnapshotReader().CaptureAsync(conn, ToolVersion, UtcStamp());
        var dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        snapshot.Write(outPath);

        var m = snapshot.Manifest;
        Console.WriteLine($"Snapshot written to {outPath}");
        Console.WriteLine($"  source PostgreSQL {m.SourcePgMajorVersion} ({m.SourcePgVersion})");
        Console.WriteLine($"  formatVersion {m.FormatVersion}  created {m.CreatedUtc}  checksum {m.ModelChecksum}");
        PrintModelSummary(snapshot.Model);
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
        // Pure report by default; with --fail-on-drift the detected drift becomes a CI gate (EP-CICD).
        return HasFlag(args, "--fail-on-drift") ? ExitCode.Drift : ExitCode.Success;
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
        var cli = new CliArgs(args);
        var project = DatabaseProject.Load(RequirePositional(args, "project file"));
        var (config, rules) = ResolveAnalysis(project, cli);   // config + external rule packs (#79)
        var strict = HasFlag(args, "--strict");

        switch (cli.Format)
        {
            case OutputFormat.Json:
            {
                var report = ContractBuilder.Analyze(project, strict, config, rules);
                EmitJson(report);
                return report.Blocked ? ExitCode.AnalysisBlocked : ExitCode.Success;
            }
            case OutputFormat.Sarif:
            {
                var positions = SourcePositionIndex.Build(project);
                var findings = RunAnalysis(project, config, rules, out _);
                Console.WriteLine(new SarifWriter().Write(findings, positions));
                var blocked = findings.Any(f => f.Severity == DiagnosticSeverity.Error)
                              || (strict && findings.Any(f => f.Severity == DiagnosticSeverity.Warning));
                return blocked ? ExitCode.AnalysisBlocked : ExitCode.Success;
            }
            default:
            {
                var findings = RunAnalysis(project, config, rules, out var ruleCount);
                Console.WriteLine($"Analyzed '{project.Name}': {ruleCount} rule(s).");
                return ReportFindings(findings, strict, alwaysReport: true) ? ExitCode.AnalysisBlocked : ExitCode.Success;
            }
        }
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

    // ---- describe-table / emit-table (EP-DESIGNER: the table-designer round-trip) ---------

    /// <summary>
    /// Parses a single table's <c>.sql</c> file and prints its structured model as JSON — the input the
    /// graphical table designer (issue #26) binds its form to. Reuses the production parser + model builder,
    /// so the JSON is exactly what the deploy engine sees. Pairs with <see cref="EmitTable"/>: the JSON
    /// printed here round-trips back to <c>.sql</c> via the engine's single <c>SqlEmitter</c>.
    /// </summary>
    private static int DescribeTable(string[] args)
    {
        var cli = new CliArgs(args);
        var sqlPath = cli.RequirePositional("table .sql file");
        var defaultSchema = cli.GetOption("--default-schema") ?? "public";
        var table = cli.GetOption("--table");   // optional schema.name; default = first table in the file

        var dto = TableDesigner.Describe(File.ReadAllText(sqlPath), table, defaultSchema);

        // Always JSON: the consumer is a tool (the designer webview). --format json is accepted but implied.
        Console.WriteLine(JsonContract.Serialize(dto));
        return ExitCode.Success;
    }

    /// <summary>
    /// Reads a table model JSON (the shape <see cref="DescribeTable"/> emits, as edited by the designer) and
    /// emits the <c>.sql</c> for it through the engine's <c>SqlEmitter</c> — the same emitter the deploy
    /// engine uses, so the designer can never drift from what deploy writes. The JSON comes from a file
    /// argument or, when that is "-", from stdin. With <c>-o</c> the SQL is written to a file; otherwise it
    /// goes to stdout.
    /// </summary>
    private static int EmitTable(string[] args)
    {
        var cli = new CliArgs(args);
        var jsonPath = cli.RequirePositional("table model .json file (or - for stdin)");
        var json = jsonPath == "-" ? Console.In.ReadToEnd() : File.ReadAllText(jsonPath);

        var dto = System.Text.Json.JsonSerializer.Deserialize<TableModelDto>(json, JsonContract.Options)
                  ?? throw new InvalidOperationException("Could not parse the table model JSON.");
        var sql = TableDesigner.Emit(dto);

        var outPath = cli.GetOption("-o", "--output");
        if (outPath is not null)
        {
            File.WriteAllText(outPath, sql);
            Console.WriteLine($"Table SQL written to {outPath}");
        }
        else
        {
            Console.Write(sql);
        }
        return ExitCode.Success;
    }

    // ---- serve (EP-LSP: resident language service over STDIO) ----------------------------

    /// <summary>
    /// Runs the resident language service: a long-running LSP host speaking JSON-RPC over STDIO with the
    /// standard <c>Content-Length</c> framing (issue #31). stdout is the LSP wire, so this verb writes NOTHING
    /// else to it — any stray <see cref="Console.Out"/> use is redirected to stderr for the lifetime of the
    /// loop. All parsing/analysis is reused from the engine via <c>PgProj.Lsp</c> (handlers are a separate,
    /// unit-tested library; this is only the transport boundary). The optional first positional may name the
    /// workspace root so the server can resolve the <c>.pgproj</c> without waiting for an <c>initialize</c>
    /// rootUri; otherwise the root comes from the LSP handshake.
    /// </summary>
    private static async Task<int> Serve(string[] args)
    {
        // Protect the wire: redirect any accidental Console.Out writes (deep in the engine) to stderr.
        var realOut = Console.OpenStandardOutput();
        Console.SetOut(Console.Error);

        var debounce = int.TryParse(GetOption(args, "--debounce"), out var d) ? d : 150;
        using var input = Console.OpenStandardInput();
        using var server = new PgProj.Lsp.Server.LspServer(input, realOut, debounce);
        return await server.RunAsync();
    }

    // ---- shared analysis gate -----------------------------------------------------------

    private static IReadOnlyList<Diagnostic> RunAnalysis(DatabaseProject project, AnalysisConfig config,
        IReadOnlyList<IPgRule> externalRules, out int ruleCount)
    {
        ruleCount = PgAnalyzer.RuleCount + externalRules.Count;
        var analyzer = new PgAnalyzer(config);
        var findings = new List<Diagnostic>();
        foreach (var file in project.ResolveSqlFiles())
        {
            var parsed = new PgProj.Core.Syntax.PgParser().Parse(PgProj.Core.Project.SourceReader.ReadAllText(file));
            findings.AddRange(analyzer.Analyze(parsed));
            findings.AddRange(ExternalRules.Run(externalRules, parsed, config));   // EP-ANALYSIS+ #79
        }
        return findings;
    }

    /// <summary>
    /// Resolves the analysis configuration + external rule packs for a verb: the <c>.pgproj.analysis.json</c>
    /// sidecar next to the project (incl. its <c>rulePacks</c>), with CLI <c>--rule RULEID=off|on|severity</c>
    /// overrides layered on top (CLI wins). A malformed <c>--rule</c> or an unloadable rule pack surfaces as a
    /// usage error.
    /// </summary>
    private static (AnalysisConfig Config, IReadOnlyList<IPgRule> Rules) ResolveAnalysis(DatabaseProject project, CliArgs cli)
    {
        try
        {
            return AnalysisSetup.Resolve(project.ProjectFilePath, cli.GetKeyValues("--rule"));
        }
        catch (CliRuleException ex) { throw new CliUsageException(ex.Message); }
        catch (RulePackException ex) { throw new CliUsageException(ex.Message); }
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
        var (config, rules) = ResolveAnalysis(project, new CliArgs(args));
        var findings = RunAnalysis(project, config, rules, out _);
        var blocked = ReportFindings(findings, HasFlag(args, "--strict"), alwaysReport: false);
        if (blocked) Console.Error.WriteLine("Aborted by analysis gate (pass --no-analyze to skip).");
        return blocked;
    }

    // ---- target-platform gate (EP-TARGET) -----------------------------------------------

    /// <summary>
    /// The target-version enforcement gate. When the project declares a <c>TargetPostgresVersion</c>, flags
    /// any syntax newer than that target (<c>PGV###</c> findings) and returns true if the operation must
    /// abort. With no target set, or a pre-built package source, or <c>--no-analyze</c>, it is a no-op.
    /// Runs alongside (not inside) <see cref="AnalysisGateBlocks"/> — a separate analyzer from PgAnalyzer.
    /// </summary>
    private static bool TargetVersionGateBlocks(DatabaseProject? project, string[] args)
    {
        if (project is null) return false;                       // package source — gated at build time
        if (HasFlag(args, "--no-analyze")) return false;         // same skip switch as the static gate
        if (TargetVersionAnalyzer.ParseMajorVersion(project.TargetPostgresVersion) is not { } target)
            return false;                                        // no/blank target → default behavior unchanged

        var findings = TargetVersionAnalyzer.AnalyzeProject(project);
        if (findings.Count == 0) return false;

        Console.Error.WriteLine($"Target PostgreSQL {target}: {findings.Count} feature(s) newer than the target:");
        foreach (var d in findings) Console.Error.WriteLine($"  {d}");
        Console.Error.WriteLine("Aborted by target-version gate (raise <TargetPostgresVersion>, or pass --no-analyze to skip).");
        return true;
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
        var profile = LoadProfile(args);            // EP-PROFILE: CLI flags override profile values.
        var (source, project) = await BuildSourceOrThrowAsync(args);
        var changes = new SchemaComparer().Compare(source, new DatabaseModel());
        var script = new DeployScriptGenerator().Generate(changes, new DeployOptions
        {
            WrapInTransaction = ResolveWrapInTransaction(args, profile),
            Scripts = LoadDeployScripts(project),
            Variables = BuildVariableResolver(project, args, profile),
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

    // ---- profile (EP-PROFILE: create a .pgpublish.json from current CLI flags) -----------

    private static int Profile(string[] args)
    {
        var sub = args.Skip(1).FirstOrDefault(a => !a.StartsWith('-'))?.ToLowerInvariant();
        return sub switch
        {
            "create" => ProfileCreate(args),
            null => Fail("Expected a 'profile' subcommand (create)."),
            _ => Fail($"Unknown 'profile' subcommand '{sub}'."),
        };
    }

    private static int ProfileCreate(string[] args)
    {
        // Positional after the "create" subcommand: the output .pgpublish.json path.
        var outPath = args.Skip(2).FirstOrDefault(a => !a.StartsWith('-'))
                      ?? throw new CliUsageException("Expected an output .pgpublish.json path argument.");

        var a = new CliArgs(args);

        // Build the profile purely from the current flags. The connection STRING is never captured (secret);
        // only an optional non-secret --connection-name label is recorded.
        var profile = new PublishProfile
        {
            TargetPostgresVersion = a.GetOption("--target-version"),
            ConnectionName = a.GetOption("--connection-name"),
            Variables = a.GetKeyValues("--var"),
            Options = new PublishProfileOptions
            {
                // Only record an option the user explicitly set, so the profile asserts nothing it wasn't told.
                AllowDrops = a.HasFlag("--allow-drops") ? true : null,
                WrapInTransaction = a.HasFlag("--no-transaction") ? false : null,
            },
        };

        profile.Save(outPath);
        Console.WriteLine($"Wrote publish profile to {outPath}");
        Console.WriteLine("  (the connection string is never stored — pass --connection / PGPROJ_CONNECTION at publish time)");
        return ExitCode.Success;
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
    /// Builds the resolved SQLCMD-variable map: project DefaultValues overlaid by the <c>--profile</c>
    /// variable block overlaid by CLI <c>--var N=V</c> (repeatable). Precedence: CLI &gt; publish profile &gt;
    /// project default. When the source was a pre-built package (no project), only profile + CLI apply.
    /// </summary>
    private static SqlCmdVariableResolver BuildVariableResolver(DatabaseProject? project, string[] args) =>
        BuildVariableResolver(project, args, LoadProfile(args));

    /// <summary>Overload taking an already-loaded profile (so a verb loads the profile once).</summary>
    private static SqlCmdVariableResolver BuildVariableResolver(DatabaseProject? project, string[] args, PublishProfile? profile) =>
        SqlCmdVariableResolver.Build(
            defaults: project?.SqlCmdVariableDefaults ?? new Dictionary<string, string>(),
            profile: profile?.Variables,            // EP-PROFILE — profile variable overrides
            cliOverrides: ParseCliVars(args));

    /// <summary>
    /// Loads the <c>--profile &lt;file&gt;</c> publish profile, or null when none was supplied (EP-PROFILE).
    /// A malformed/missing profile surfaces as a <see cref="PublishProfileException"/> (→ exit 1).
    /// </summary>
    private static PublishProfile? LoadProfile(string[] args)
    {
        var path = GetOption(args, "--profile");
        return path is null ? null : PublishProfile.Load(path);
    }

    /// <summary>
    /// Resolves the allow-drops publish option with EP-PROFILE precedence: an explicit CLI <c>--allow-drops</c>
    /// flag wins; otherwise the profile's value; otherwise the built-in default (false).
    /// </summary>
    private static bool ResolveAllowDrops(string[] args, PublishProfile? profile) =>
        HasFlag(args, "--allow-drops") || (profile?.Options.AllowDrops ?? false);

    /// <summary>
    /// Resolves wrap-in-transaction with EP-PROFILE precedence: an explicit CLI <c>--no-transaction</c> wins
    /// (→ false); otherwise the profile's value; otherwise the built-in default (true).
    /// </summary>
    private static bool ResolveWrapInTransaction(string[] args, PublishProfile? profile) =>
        !HasFlag(args, "--no-transaction") && (profile?.Options.WrapInTransaction ?? true);

    /// <summary>Parses every <c>--var Name=Value</c> occurrence into a case-insensitive override map.</summary>
    private static IReadOnlyDictionary<string, string> ParseCliVars(string[] args) =>
        new CliArgs(args).GetKeyValues("--var");

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

        // EP-REF: resolve/validate references before the model is used for compare/publish/script. A broken
        // cross-schema reference must fail here, not silently produce an invalid deploy.
        if (ResolveAndValidateReferencesBlocks(project))
            throw new InvalidOperationException("Fix the reference problems before continuing.");

        return (result.Model, project);
    }

    /// <summary>Display name for the source: the project name, or "(package)" when the source was a pre-built .pgpkg.</summary>
    private static string SourceName(DatabaseProject? project) => project?.Name ?? "(package)";

    private static async Task<DatabaseModel> ReadTarget(string[] args) =>
        await new LiveDatabaseReader().ReadAsync(RequireConnection(args));

    // Introspect the live target using the version profile selected from the project's
    // TargetPostgresVersion, so the catalog SQL issued matches the declared target (blank → latest).
    private static async Task<DatabaseModel> ReadTarget(string[] args, PgProj.Core.Versioning.PostgresVersionProfile profile) =>
        await new LiveDatabaseReader(profile).ReadAsync(RequireConnection(args));

    /// <summary>The PostgreSQL version profile a project targets (from <c>TargetPostgresVersion</c>; latest when unset).</summary>
    private static PgProj.Core.Versioning.PostgresVersionProfile ProfileFor(DatabaseProject? project) =>
        PgProj.Core.Versioning.PostgresVersionProfile.ForTarget(project?.TargetPostgresVersion);

    private static void PrintModelSummary(DatabaseModel m) =>
        Console.WriteLine($"  schemas={m.Schemas.Count} tables={m.Tables.Count} " +
                          $"indexes={m.Indexes.Count} views={m.Views.Count} " +
                          $"sequences={m.Sequences.Count} functions={m.Functions.Count}");

    // ---- argument parsing -----------------------------------------------------------------
    // These delegate to the shared, unit-tested CliArgs/OutputFormat primitives in PgProj.Core.Cli so the
    // whole CLI (and a future `pgproj serve` host) parses one grammar. Verbs call these thin wrappers or
    // `new CliArgs(args)` directly — new options should not re-implement parsing.

    private static string RequireConnection(string[] args) => new CliArgs(args).RequireConnection();

    private static string RequirePositional(string[] args, string what) => new CliArgs(args).RequirePositional(what);

    private static string? GetOption(string[] args, params string[] names) => new CliArgs(args).GetOption(names);

    private static bool HasFlag(string[] args, string name) => new CliArgs(args).HasFlag(name);

    /// <summary>True when the caller asked for machine-readable output (<c>--format json</c>).</summary>
    private static bool WantsJson(string[] args) => new CliArgs(args).WantsJson;

    /// <summary>Writes a contract payload to stdout as stable JSON.</summary>
    private static void EmitJson<T>(T payload) => Console.WriteLine(JsonContract.Serialize(payload));

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        PrintUsage();
        return ExitCode.Usage;
    }

    private static int PrintUsageReturn(int code) { PrintUsage(); return code; }

    private static string DefaultProjectFile(string name) =>
        $"""
        <Project Sdk="PgProj.Sdk/0.1.0" DefaultTargets="Build">
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
          pgproj new project <name> [-o <dir>] [--default-schema public] [--target-version 18]   (scaffold an empty buildable project)
          pgproj add <kind> <schema.name> [-p <project|dir>] [--force]                            (scaffold an object file from a template)
          pgproj build   <project.pgproj> [-o model.json] [--package <out.pgpkg> | --no-package] [--strict] [--no-analyze] [--var N=V] [--substitute-objects]
                         (resolves <ProjectReference>/<ArtifactReference> into the build's catalog so
                          cross-schema names resolve; referenced objects are never emitted)
          pgproj script  <project.pgproj|.pgpkg> [-o create.sql] [--no-transaction] [--var N=V] [--profile <file>]
          pgproj compare <project.pgproj|.pgpkg> --connection <conn> [--allow-drops] [--profile <file>]                 (one-way: project/package → live DB)
          pgproj compare --source <X> --target <Y> [-o diff.json] [--format json] [--exclude <type,...>] [--allow-drops] [--fail-on-changes]
                         (two-way Schema Compare; X and Y each a .pgproj, .pgpkg, .schema.snapshot, or connection string)
          pgproj publish <project.pgproj|.pgpkg> --connection <conn> [--dry-run] [-o script.sql] [--allow-drops] [--no-transaction] [--parallel] [--strict] [--no-analyze] [--var N=V] [--substitute-objects] [--profile <file>]
          pgproj validate <project.pgproj|.pgpkg> --connection <conn> [--strict] [--no-analyze]   (apply to a throwaway temp DB, rolled back)
          pgproj pkg inspect <file.pgpkg>                                              (dump the manifest + object inventory)
          pgproj profile create <out.pgpublish.json> [--target-version 18] [--connection-name <label>] [--var N=V] [--allow-drops] [--no-transaction]
                         (write a reusable publish profile from the current flags; the connection string is never stored)
          pgproj extract --connection <conn> -o <outDir>
          pgproj snapshot --connection <conn> -o <db.schema.snapshot>                    (introspect a live DB once → a portable schema.snapshot for offline compare)
          pgproj drift   <project.pgproj> --connection <conn> [--fail-on-drift]         (preview project files that differ from the DB; --fail-on-drift exits 6 on drift)
          pgproj pull    <project.pgproj> --connection <conn> [--dry-run] [--allow-deletes]   (rewrite project files FROM the DB — scenario 3)
          pgproj analyze <project.pgproj> [--strict]    (static safety analysis over the AST)
          pgproj model-tree <project.pgproj> [--format json]   (objects + source positions, for editors)
          pgproj describe-table <table.sql> [--table schema.name] [--default-schema public]   (one table's model as JSON, for the graphical designer)
          pgproj emit-table <table.json | -> [-o table.sql]     (round-trip the designer's table JSON back to .sql via the engine emitter)
          pgproj serve [<workspace-dir>] [--debounce <ms>]     (resident LSP language server over STDIO — live diagnostics/definition/hover/completion)

        Options:
          --format json      Machine-readable, versioned JSON (build/analyze/compare/publish --dry-run/model-tree)
          -c, --connection   Postgres connection string (or set PGPROJ_CONNECTION)
          -o, --output       Output file or directory
          --dry-run          Generate the deploy script but do not execute it
          --allow-drops      Allow destructive changes (drop tables/columns/etc. not in the project)
          --source           compare (two-way): the left/source endpoint (.pgproj, .pgpkg, or connection string)
          --target           compare (two-way): the right/target endpoint (.pgproj, .pgpkg, or connection string)
          --exclude          compare (two-way): object-type(s) to skip, comma-separated/repeatable (e.g. extension,permission)
          --fail-on-changes  compare (two-way): exit with the Drift code (6) when any change remains
          --allow-deletes    pull: delete project files for objects dropped from the database
          --package          build: write the .pgpkg to this path (default bin/<Name>.pgpkg)
          --no-package       build: skip writing the portable .pgpkg artifact
          --no-transaction   Do not wrap the deploy script in BEGIN/COMMIT
          --parallel         Publish with intra-phase parallelism (phase-level atomicity)
          --strict           Analysis gate: treat warnings as errors (build/publish fail on warnings)
          --no-analyze       Skip the static-analysis gate on build/publish
          --var Name=Value   Override a SqlCmdVariable (repeatable; CLI beats the profile, profile beats the project DefaultValue)
          --profile <file>   compare/script/publish: load options + variable overrides from a .pgpublish.json (CLI flags win)
          --connection-name  profile create: a non-secret connection label/hint to record (never a connection string)
          --substitute-objects  Also expand $(Var) tokens in object .sql files (default: deploy-scripts only)
          --force            add: overwrite an existing object file
          --default-schema   new project: default schema for the manifest (default 'public')
          --target-version   new project: target PostgreSQL major version (default 18)
          -p, --project      add: the project (.pgproj) or directory to scaffold into (default: current dir)

        Object kinds for 'add': table, view, function, procedure, trigger, sequence, type, schema, policy

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
