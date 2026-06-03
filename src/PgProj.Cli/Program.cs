using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PgProj.Core.Comparison;
using PgProj.Core.Introspection;
using PgProj.Core.Model;
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
                "build" => Build(args),
                "compare" => await Compare(args),
                "publish" => await Publish(args),
                "extract" => await Extract(args),
                "script" => Script(args),
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

    private static int Build(string[] args)
    {
        var project = DatabaseProject.Load(RequirePositional(args, "project file"));
        var result = project.Build();

        Console.WriteLine($"Building project '{project.Name}' ({result.Files.Count} file(s), default schema '{project.DefaultSchema}')");
        PrintModelSummary(result.Model);

        var outPath = GetOption(args, "-o", "--output")
                      ?? Path.Combine(project.ProjectDirectory, "bin", $"{project.Name}.model.json");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllText(outPath, ModelJson.Serialize(result.Model));
        Console.WriteLine($"Model written to {outPath}");

        if (result.Diagnostics.Count > 0)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Build finished with {result.Diagnostics.Count} problem(s):");
            foreach (var d in result.Diagnostics) Console.Error.WriteLine($"  - {d}");
            return 1;
        }

        Console.WriteLine("Build succeeded.");
        return 0;
    }

    // ---- compare ------------------------------------------------------------------------

    private static async Task<int> Compare(string[] args)
    {
        var (source, _) = BuildSourceOrThrow(args);
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
        var (source, _) = BuildSourceOrThrow(args);
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

    // ---- script (full create from project, no server) -----------------------------------

    private static int Script(string[] args)
    {
        var (source, _) = BuildSourceOrThrow(args);
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

    // ---- helpers ------------------------------------------------------------------------

    private static (DatabaseModel Model, DatabaseProject Project) BuildSourceOrThrow(string[] args)
    {
        var project = DatabaseProject.Load(RequirePositional(args, "project file"));
        var result = project.Build();
        if (result.Diagnostics.Count > 0)
        {
            Console.Error.WriteLine("Project has build problems:");
            foreach (var d in result.Diagnostics) Console.Error.WriteLine($"  - {d}");
            throw new InvalidOperationException("Fix the build problems before continuing.");
        }
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
          pgproj build   <project.pgproj> [-o model.json]
          pgproj script  <project.pgproj> [-o create.sql] [--no-transaction]
          pgproj compare <project.pgproj> --connection <conn> [--allow-drops]
          pgproj publish <project.pgproj> --connection <conn> [--dry-run] [-o script.sql] [--allow-drops] [--no-transaction]
          pgproj extract --connection <conn> -o <outDir>

        Options:
          -c, --connection   Postgres connection string (or set PGPROJ_CONNECTION)
          -o, --output       Output file or directory
          --dry-run          Generate the deploy script but do not execute it
          --allow-drops      Allow destructive changes (drop tables/columns/etc. not in the project)
          --no-transaction   Do not wrap the deploy script in BEGIN/COMMIT
          --parallel         Publish with intra-phase parallelism (phase-level atomicity)
        """);
    }
}
