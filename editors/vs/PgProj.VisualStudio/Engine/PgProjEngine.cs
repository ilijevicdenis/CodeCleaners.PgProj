// EP-VS #25 Route B (modern). The IN-PROCESS seam to the engine. Compare/publish go through the SAME
// PgProj.Core code paths the CLI uses — read-only compare via SchemaCompare, the publish gates via
// ContractBuilder.Analyze + TargetVersionAnalyzer, and the deploy via the shared PublishService — so VS
// publish and CLI publish generate the identical deploy script and use the identical deploy strategy.
using PgProj.Core.Analysis;
using PgProj.Core.Comparison;
using PgProj.Core.Contracts;
using PgProj.Core.Model;
using PgProj.Core.Project;
using PgProj.Core.Publishing;

namespace PgProj.VisualStudio.Engine;

/// <summary>Outcome of the pre-publish gates (static analysis + target-version), mirroring the CLI publish.</summary>
internal readonly record struct PublishGateResult(bool Blocked, IReadOnlyList<string> Messages);

internal static class PgProjEngine
{
    /// <summary>
    /// Read-only compare of a <c>.pgproj</c> (source) against a target spec (connection / project / package /
    /// snapshot). Used by the Schema Compare tool window.
    /// </summary>
    public static Task<SchemaCompareResult> CompareAsync(string projectPath, string targetSpec, CancellationToken cancellationToken)
        => SchemaCompare.RunAsync(projectPath, targetSpec, new ComparerOptions(), excludeObjectTypes: null, cancellationToken);

    /// <summary>Loads + builds the project model. Throws (like the CLI) when the build has problems.</summary>
    public static async Task<(DatabaseProject Project, DatabaseModel Model)> LoadProjectAsync(string projectPath, CancellationToken cancellationToken)
    {
        var project = DatabaseProject.Load(projectPath);
        var build = await project.BuildAsync(cancellationToken);
        if (build.Diagnostics.Count > 0)
            throw new InvalidOperationException("Project has build problems:" + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", build.Diagnostics));

        return (project, build.Model);
    }

    /// <summary>
    /// Runs the same gates the CLI publish runs before touching the database: the static-analysis gate
    /// (PG0xx rules via <see cref="ContractBuilder.Analyze"/>) and the target-version gate (PGV### via
    /// <see cref="TargetVersionAnalyzer"/>). Returns blocked + human-readable messages for the Output window.
    /// </summary>
    public static PublishGateResult RunGates(DatabaseProject project, bool strict = false)
    {
        var (config, rules) = AnalysisSetup.Resolve(project.ProjectFilePath, cliRuleArgs: null);
        var analysis = ContractBuilder.Analyze(project, strict, config, rules);
        if (analysis.Blocked)
        {
            var messages = new List<string> { $"Analysis gate blocked: {analysis.Summary.Errors} error(s), {analysis.Summary.Warnings} warning(s)." };
            messages.AddRange(analysis.Diagnostics.Select(d => $"  {d.Severity}: {d.Message}"));
            return new PublishGateResult(true, messages);
        }

        var targetVersion = TargetVersionAnalyzer.AnalyzeProject(project);
        if (targetVersion.Count > 0)
        {
            var messages = new List<string> { $"Target-version gate blocked: {targetVersion.Count} feature(s) newer than the declared TargetPostgresVersion." };
            messages.AddRange(targetVersion.Select(d => $"  {d}"));
            return new PublishGateResult(true, messages);
        }

        return new PublishGateResult(false, []);
    }

    /// <summary>Builds the deploy plan (compare + deploy script) via the shared <see cref="PublishService"/>.</summary>
    public static Task<PublishPlan> PlanAsync(DatabaseProject project, DatabaseModel model, string connectionString, CancellationToken cancellationToken)
        => PlanAsync(project, model, connectionString, new PublishPlanOptions(), cancellationToken);

    /// <summary>Builds the deploy plan with explicit options (the Publish dialog's allow-drops / transaction / variables).</summary>
    public static Task<PublishPlan> PlanAsync(DatabaseProject project, DatabaseModel model, string connectionString, PublishPlanOptions options, CancellationToken cancellationToken)
        => new PublishService().PlanAsync(project, model, connectionString, options, cancellationToken);

    /// <summary>Applies a plan to the target (whole-script, one transaction).</summary>
    public static Task ApplyAsync(PublishPlan plan, string connectionString, CancellationToken cancellationToken)
        => new PublishService().ApplyAsync(plan, connectionString, parallel: false, cancellationToken);

    /// <summary>The deploy script for the currently-included subset of a Schema Compare change set.</summary>
    public static string ScriptIncluded(SchemaChangeSet changeSet)
        => changeSet.ScriptIncluded(new DeployOptions());

    /// <summary>
    /// Applies the currently-included subset of a Schema Compare change set to a live target — the same
    /// script <see cref="ScriptIncluded"/> shows, executed by the engine's transactional deployer.
    /// </summary>
    public static Task ApplyIncludedAsync(SchemaChangeSet changeSet, string connectionString, CancellationToken cancellationToken)
        => new DatabaseDeployer().ExecuteAsync(connectionString, changeSet.ScriptIncluded(new DeployOptions()), cancellationToken);

    /// <summary>
    /// Lightweight connectivity probe for the Import/Publish dialogs: opens the connection and asks the
    /// server its version. Throws (with Npgsql's message) when unreachable/unauthorized.
    /// </summary>
    public static async Task<string> TestConnectionAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new Npgsql.NpgsqlCommand("SELECT version()", connection);
        return (string?)await command.ExecuteScalarAsync(cancellationToken) ?? "PostgreSQL";
    }

    /// <summary>
    /// Introspects a live database and returns the per-object file units the CLI's <c>extract</c> would
    /// write (relative path → SQL; a table's unit carries its FKs + indexes). The Import dialog lists
    /// these as the selectable objects and writes only the checked ones into the project.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>> ReadDatabaseFileUnitsAsync(string connectionString, CancellationToken cancellationToken)
    {
        var model = await new PgProj.Core.Introspection.LiveDatabaseReader().ReadAsync(connectionString, cancellationToken);
        return DdlExporter.ExportFiles(model);
    }

    /// <summary>
    /// Parses the dialog's <c>Name=Value;Name2=Value2</c> SQLCMD-variable override string (the same shape
    /// the SDK's PgProjPublishVariables property uses). Returns null when there is nothing to override.
    /// </summary>
    public static Dictionary<string, string>? ParseVariableOverrides(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
                throw new InvalidOperationException($"Variable override '{pair}' is not Name=Value.");
            result[pair[..separator].Trim()] = pair[(separator + 1)..].Trim();
        }
        return result.Count == 0 ? null : result;
    }
}
