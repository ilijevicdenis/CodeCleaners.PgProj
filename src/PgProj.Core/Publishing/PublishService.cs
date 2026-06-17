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

    // ---- #140 DacDeployOptions-equivalent family (resolved: CLI > profile > built-in default) ----

    /// <summary>Refuse to apply a possible-data-loss change (the publish default is to block).</summary>
    public bool BlockOnPossibleDataLoss { get; init; }

    /// <summary>Synthesize a default for a new <c>NOT NULL</c> column on a populated table.</summary>
    public bool GenerateSmartDefaults { get; init; }

    /// <summary>Validate new FK/CHECK constraints (default); off ⇒ emit them <c>NOT VALID</c>.</summary>
    public bool ScriptNewConstraintValidation { get; init; } = true;

    /// <summary>Permit object drop-and-recreate (the publish default is off so recreation is a blocked step).</summary>
    public bool AllowTableRecreation { get; init; } = true;

    /// <summary>Lock-minimizing deploy: CONCURRENTLY index ops + NOT VALID/VALIDATE constraints (#137).</summary>
    public bool ConcurrentIndexOperations { get; init; }

    /// <summary>Drop constraints not in source (default true; only relevant when <see cref="AllowDrops"/>).</summary>
    public bool DropConstraintsNotInSource { get; init; } = true;

    /// <summary>Drop indexes not in source (default true; only relevant when <see cref="AllowDrops"/>).</summary>
    public bool DropIndexesNotInSource { get; init; } = true;

    /// <summary>Per-session <c>statement_timeout</c> (ms); null ⇒ server default.</summary>
    public int? CommandTimeoutMs { get; init; }

    /// <summary>Per-session <c>lock_timeout</c> (ms); null ⇒ server default.</summary>
    public int? LockTimeoutMs { get; init; }

    /// <summary>Object-type tokens excluded from the diff entirely.</summary>
    public IReadOnlyList<string> ExcludeObjectTypes { get; init; } = Array.Empty<string>();

    /// <summary>Object-type tokens whose standalone DROP is suppressed.</summary>
    public IReadOnlyList<string> DoNotDropObjectTypes { get; init; } = Array.Empty<string>();

    /// <summary>
    /// An explicit refactor log to consume (#136). When set it wins over loading from the project — used when
    /// the source is a pre-built <c>.pgpkg</c> that carries its own packed log. Null ⇒ load from the project.
    /// </summary>
    public Refactoring.RefactorLog? RefactorLog { get; init; }
}

/// <summary>
/// A computed publish plan: the ordered changes, the generated deploy script, and whether pre/post-deploy
/// scripts are spliced in (which forces whole-script, non-parallel apply). Produced by
/// <see cref="PublishService.PlanAsync"/>; applied by <see cref="PublishService.ApplyAsync"/>.
/// </summary>
public sealed class PublishPlan
{
    public PublishPlan(IReadOnlyList<SchemaChange> changes, string script, bool hasDeployScripts,
        bool hasPreDeployScript = false, bool hasPostDeployScript = false,
        int? statementTimeoutMs = null, int? lockTimeoutMs = null)
    {
        Changes = changes;
        Script = script;
        HasDeployScripts = hasDeployScripts;
        HasPreDeployScript = hasPreDeployScript;
        HasPostDeployScript = hasPostDeployScript;
        StatementTimeoutMs = statementTimeoutMs;
        LockTimeoutMs = lockTimeoutMs;
    }

    /// <summary>Per-session <c>statement_timeout</c> (ms) the phased deployer should SET; null ⇒ server default.</summary>
    public int? StatementTimeoutMs { get; }

    /// <summary>Per-session <c>lock_timeout</c> (ms) the phased deployer should SET; null ⇒ server default.</summary>
    public int? LockTimeoutMs { get; }

    public IReadOnlyList<SchemaChange> Changes { get; }

    /// <summary>The generated deploy script (identical to what the CLI <c>script</c>/<c>publish</c> emit).</summary>
    public string Script { get; }

    /// <summary>True when the project declares pre/post-deploy scripts spliced around the diff.</summary>
    public bool HasDeployScripts { get; }

    /// <summary>Whether a pre-deploy script is spliced before the diff (reported by deploy-report).</summary>
    public bool HasPreDeployScript { get; }

    /// <summary>Whether a post-deploy script is spliced after the diff (reported by deploy-report).</summary>
    public bool HasPostDeployScript { get; }

    public int ChangeCount => Changes.Count;

    public int DestructiveCount => Changes.Count(c => c.IsDestructive);

    /// <summary>Nothing to apply: no schema changes AND no pre/post-deploy scripts to run.</summary>
    public bool NothingToDo => Changes.Count == 0 && !HasDeployScripts;

    /// <summary>
    /// True when any step must run outside the deploy transaction (CONCURRENTLY / VALIDATE, #137). Such a
    /// plan MUST be applied statement-by-statement (the phased deployer) — a single whole-script command
    /// would wrap the concurrent step in an implicit transaction and PostgreSQL would reject it.
    /// </summary>
    public bool HasNonTransactionalSteps => Changes.Any(c => c.RunsOutsideTransaction);
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

        // #136: consume the project's persisted refactor log BY DEFAULT (its presence is the opt-in) so a
        // logged rename/move deploys as a data-preserving ALTER instead of DROP+CREATE. No project (a
        // pre-built .pgpkg source) ⇒ no log here.
        var refactorLog = options.RefactorLog
            ?? (project is not null ? Refactoring.RefactorLog.LoadForProject(project.ProjectFilePath) : null);

        var changes = new SchemaComparer(versionProfile).Compare(
            sourceModel, target,
            new ComparerOptions { DropObjectsNotInSource = options.AllowDrops, RefactorLog = refactorLog });

        // #137 lock-minimizing rewrite applied here (not just in the generator) so the changes the phased
        // deployer applies carry the same CONCURRENTLY/NOT VALID flags the generated script shows.
        if (options.ConcurrentIndexOperations)
            changes = LockMinimizer.Apply(changes);

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
            BlockOnPossibleDataLoss = options.BlockOnPossibleDataLoss,
            GenerateSmartDefaults = options.GenerateSmartDefaults,
            ScriptNewConstraintValidation = options.ScriptNewConstraintValidation,
            AllowTableRecreation = options.AllowTableRecreation,
            StatementTimeoutMs = options.CommandTimeoutMs,
            LockTimeoutMs = options.LockTimeoutMs,
            ExcludeObjectTypes = options.ExcludeObjectTypes,
            DoNotDropObjectTypes = ResolveDropSuppression(options),
        }, versionProfile);

        return new PublishPlan(changes, script, hasDeployScripts: bundle is { IsEmpty: false },
            hasPreDeployScript: bundle?.Pre is not null, hasPostDeployScript: bundle?.Post is not null,
            statementTimeoutMs: options.CommandTimeoutMs, lockTimeoutMs: options.LockTimeoutMs);
    }

    /// <summary>
    /// Applies a plan to the target. With <paramref name="parallel"/> AND no pre/post-deploy scripts, runs
    /// the diff phase-by-phase with intra-phase parallelism (<see cref="PhasedDeployer"/>, phase-level
    /// atomicity); otherwise runs the whole script in one transaction (<see cref="DatabaseDeployer"/>,
    /// strict all-or-nothing). Pre/post-deploy scripts have no phase model, so they force the whole-script path.
    /// </summary>
    public async Task ApplyAsync(PublishPlan plan, string connectionString, bool parallel, CancellationToken cancellationToken = default)
    {
        // #137: a plan with non-transactional steps (CONCURRENTLY / VALIDATE) MUST be applied
        // statement-by-statement (the phased deployer detects CONCURRENTLY and runs it autocommit). The
        // whole-script DatabaseDeployer would wrap the batch in one implicit transaction and PostgreSQL
        // would reject the concurrent step.
        if (plan.HasNonTransactionalSteps && plan.HasDeployScripts)
            throw new InvalidOperationException(
                "Lock-minimizing index operations (CONCURRENTLY) cannot be combined with pre/post-deploy " +
                "scripts in a single apply — the concurrent steps run outside a transaction while the scripts " +
                "need the whole-script path. Deploy without --concurrent-indexes, or remove the deploy scripts.");

        if ((parallel || plan.HasNonTransactionalSteps) && !plan.HasDeployScripts)
            // The whole-script path gets its timeouts from the generated SET preamble; the phased path runs
            // changes directly, so the timeouts (#140) are passed to the deployer to SET per session.
            await new PhasedDeployer(connectionString,
                    statementTimeoutMs: plan.StatementTimeoutMs, lockTimeoutMs: plan.LockTimeoutMs)
                .ExecuteAsync(plan.Changes, cancellationToken);
        else
            await new DatabaseDeployer().ExecuteAsync(connectionString, plan.Script, cancellationToken);
    }

    /// <summary>
    /// Builds the set of object-type tokens whose DROP is suppressed, combining the explicit
    /// <see cref="PublishPlanOptions.DoNotDropObjectTypes"/> list with the granular SqlPackage-style
    /// <c>Drop*NotInSource</c> toggles (off ⇒ that kind's DROP is suppressed).
    /// </summary>
    public static IReadOnlyList<string> ResolveDropSuppression(PublishPlanOptions options)
    {
        var set = new HashSet<string>(options.DoNotDropObjectTypes, StringComparer.OrdinalIgnoreCase);
        if (!options.DropConstraintsNotInSource) { set.Add("constraint"); set.Add("primarykey"); set.Add("foreignkey"); }
        if (!options.DropIndexesNotInSource) set.Add("index");
        return set.Count == 0 ? Array.Empty<string>() : set.ToArray();
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
