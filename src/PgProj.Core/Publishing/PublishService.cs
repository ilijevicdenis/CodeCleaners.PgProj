using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PgProj.Core.Comparison;
using PgProj.Core.Deployment;
using PgProj.Core.Introspection;
using PgProj.Core.Model;
using PgProj.Core.Project;
using PgProj.Core.Versioning;

namespace PgProj.Core.Publishing;

/// <summary>
/// The publish/deploy options that vary by caller (CLI flags / publish profile / an editor's UI). These
/// feed the comparer and the deploy-script generator; everything else is derived from the project.
/// </summary>
public sealed record PublishPlanOptions
{
    /// <summary>Drop objects present in the target but absent from the source (CLI <c>--allow-drops</c>).</summary>
    public bool AllowDrops { get; init; }

    /// <summary>Wrap the whole deploy in BEGIN/COMMIT (CLI default true; <c>--no-transaction</c> → false).</summary>
    public bool WrapInTransaction { get; init; } = true;

    /// <summary>Publish-profile SQLCMD variable overrides (EP-PROFILE), layered over project defaults.</summary>
    public IReadOnlyDictionary<string, string>? ProfileVariables { get; init; }

    /// <summary>Highest-precedence SQLCMD variable overrides (CLI <c>--var N=V</c> / editor input).</summary>
    public IReadOnlyDictionary<string, string>? VariableOverrides { get; init; }
}

/// <summary>
/// A computed publish plan: the ordered changes, the generated deploy script, and whether pre/post-deploy
/// scripts are spliced in (which forces whole-script, non-parallel apply). Produced by
/// <see cref="PublishService.PlanAsync"/>; applied by <see cref="PublishService.ApplyAsync"/>.
/// </summary>
public sealed class PublishPlan
{
    public PublishPlan(IReadOnlyList<SchemaChange> changes, string script, bool hasDeployScripts)
    {
        Changes = changes;
        Script = script;
        HasDeployScripts = hasDeployScripts;
    }

    public IReadOnlyList<SchemaChange> Changes { get; }

    /// <summary>The generated deploy script (identical to what the CLI <c>script</c>/<c>publish</c> emit).</summary>
    public string Script { get; }

    /// <summary>True when the project declares pre/post-deploy scripts spliced around the diff.</summary>
    public bool HasDeployScripts { get; }

    public int ChangeCount => Changes.Count;

    public int DestructiveCount => Changes.Count(c => c.IsDestructive);

    /// <summary>Nothing to apply: no schema changes AND no pre/post-deploy scripts to run.</summary>
    public bool NothingToDo => Changes.Count == 0 && !HasDeployScripts;
}

/// <summary>
/// The single publish code path — the comparer → deploy-script generator → deployer pipeline that turns a
/// built source model + a live target into an applied migration. Extracted from the CLI's <c>publish</c>
/// verb so the CLI, the Visual Studio extension, and any future editor all generate the SAME deploy script
/// and use the SAME deploy strategy. Callers own everything around it: building the source model, the
/// analysis / target-version gates (see <c>ContractBuilder.Analyze</c> and <c>TargetVersionAnalyzer</c>),
/// dry-run presentation, and exit-code / UI mapping.
/// </summary>
public sealed class PublishService
{
    /// <summary>
    /// Reads the live target through the project's PostgreSQL version profile, diffs the source against it,
    /// and generates the deploy script (with the project's pre/post-deploy scripts and resolved SQLCMD
    /// variables spliced in). A <paramref name="project"/> of <c>null</c> (e.g. a pre-built <c>.pgpkg</c>
    /// source) means no deploy scripts and no project variable defaults; the latest version profile is used.
    /// </summary>
    public async Task<PublishPlan> PlanAsync(
        DatabaseProject? project,
        DatabaseModel sourceModel,
        string connectionString,
        PublishPlanOptions options,
        CancellationToken cancellationToken = default)
    {
        var versionProfile = PostgresVersionProfile.ForTarget(project?.TargetPostgresVersion);
        var target = await new LiveDatabaseReader(versionProfile).ReadAsync(connectionString, cancellationToken);

        var changes = new SchemaComparer(versionProfile).Compare(
            sourceModel, target, new ComparerOptions { DropObjectsNotInSource = options.AllowDrops });

        var bundle = project is null ? null : LoadDeployScripts(project);
        var variables = SqlCmdVariableResolver.Build(
            defaults: project?.SqlCmdVariableDefaults,
            profile: options.ProfileVariables,
            cliOverrides: options.VariableOverrides);

        var script = new DeployScriptGenerator().Generate(changes, new DeployOptions
        {
            WrapInTransaction = options.WrapInTransaction,
            Scripts = bundle,
            Variables = variables,
        });

        return new PublishPlan(changes, script, hasDeployScripts: bundle is { IsEmpty: false });
    }

    /// <summary>
    /// Applies a plan to the target. With <paramref name="parallel"/> AND no pre/post-deploy scripts, runs
    /// the diff phase-by-phase with intra-phase parallelism (<see cref="PhasedDeployer"/>, phase-level
    /// atomicity); otherwise runs the whole script in one transaction (<see cref="DatabaseDeployer"/>,
    /// strict all-or-nothing). Pre/post-deploy scripts have no phase model, so they force the whole-script path.
    /// </summary>
    public async Task ApplyAsync(PublishPlan plan, string connectionString, bool parallel, CancellationToken cancellationToken = default)
    {
        if (parallel && !plan.HasDeployScripts)
            await new PhasedDeployer(connectionString).ExecuteAsync(plan.Changes, cancellationToken);
        else
            await new DatabaseDeployer().ExecuteAsync(connectionString, plan.Script, cancellationToken);
    }

    /// <summary>Reads the project's single pre/post-deploy scripts into a bundle (null when it declares none).</summary>
    private static DeployScriptBundle? LoadDeployScripts(DatabaseProject project)
    {
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
}
