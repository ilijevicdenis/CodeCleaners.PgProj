using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PgProj.Core.Comparison.Risk;
using PgProj.Core.Deployment;

namespace PgProj.Core.Comparison;

/// <summary>A pre/post-deployment script: its display name (for banners/diagnostics) and raw body.</summary>
public sealed record DeployScript(string Name, string Body);

/// <summary>
/// The pre/post-deploy scripts to splice around the schema diff. Bodies are passed through verbatim
/// (only SQLCMD-variable substitution is applied, never reformatting) so dollar-quoted function bodies
/// and embedded semicolons survive untouched.
/// </summary>
public sealed record DeployScriptBundle(DeployScript? Pre = null, DeployScript? Post = null)
{
    public bool IsEmpty => Pre is null && Post is null;
}

public sealed class DeployOptions
{
    /// <summary>Wrap the whole script in BEGIN/COMMIT so a failed step rolls everything back.</summary>
    public bool WrapInTransaction { get; init; } = true;

    /// <summary>Emit a leading comment banner describing the plan (and the resolved variable map).</summary>
    public bool IncludeHeader { get; init; } = true;

    /// <summary>Pre/post-deployment scripts to splice around the schema diff (EP-DEPLOYSCRIPTS).</summary>
    public DeployScriptBundle? Scripts { get; init; }

    /// <summary>
    /// Resolved SQLCMD variables (EP-VARS). When set, <c>$(Name)</c> tokens in the pre/post scripts are
    /// substituted and the resolved map is echoed into the header. Unresolved tokens throw.
    /// </summary>
    public SqlCmdVariableResolver? Variables { get; init; }

    // ---- Phase-18 publish options (issue #58) --------------------------------------------------
    // The block-on-data-loss gate is ENFORCED here (see DeployScriptGenerator.Guard). The remaining
    // options below define the surface only — threading them through script GENERATION is Phase 14 /
    // issue #56's job; they are stored so a profile can round-trip them now. Each default reproduces
    // today's behaviour exactly.

    /// <summary>
    /// Refuse to generate a script when it contains a possible-data-loss change (risk level
    /// <see cref="RiskLevel.DataLoss"/> or higher). Defaults to <c>false</c> (today's behaviour: the script
    /// is always produced). Wired to the Phase-12 risk analyzer (#54).
    /// </summary>
    public bool BlockOnPossibleDataLoss { get; init; }

    /// <summary>Drop objects present in the target but absent from the source. Mirrors the comparer option; off by default.</summary>
    public bool DropObjectsNotInSource { get; init; }

    /// <summary>Drop constraints/indexes present in the target but absent from the source. Off by default.</summary>
    public bool DropConstraintsAndIndexesNotInSource { get; init; }

    /// <summary>Prefer ALTER over drop+recreate when both express the change. On by default (today's behaviour).</summary>
    public bool PreferAlterOverRecreate { get; init; } = true;

    /// <summary>Only recreate an object when an in-place ALTER cannot express the change. On by default.</summary>
    public bool RecreateOnlyWhenNecessary { get; init; } = true;

    /// <summary>Emit idempotent <c>IF [NOT] EXISTS</c> guards where the dialect allows. Off by default.</summary>
    public bool IdempotentIfExists { get; init; }

    /// <summary>Object-type tokens to EXCLUDE from generation at the profile level (e.g. <c>extension</c>). Empty = include all.</summary>
    public IReadOnlyList<string> ExcludeObjectTypes { get; init; } = Array.Empty<string>();

    /// <summary>Object-type tokens to INCLUDE exclusively; when non-empty only these are generated. Empty = include all.</summary>
    public IReadOnlyList<string> IncludeOnlyObjectTypes { get; init; } = Array.Empty<string>();

    /// <summary>Emit a <c>SET statement_timeout</c> (ms) preamble. Null = leave the server default.</summary>
    public int? StatementTimeoutMs { get; init; }

    /// <summary>Emit a <c>SET lock_timeout</c> (ms) preamble. Null = leave the server default.</summary>
    public int? LockTimeoutMs { get; init; }

    /// <summary>Verbose output (extra per-change banners/rationale) vs minimal. Off by default (today's header).</summary>
    public bool Verbose { get; init; }

    /// <summary>Target PostgreSQL major version the script is generated for. Null = the project/profile default.</summary>
    public string? TargetPostgresVersion { get; init; }
}

/// <summary>Thrown by the block-on-data-loss gate when a deploy would apply a possible-data-loss change.</summary>
public sealed class DataLossBlockedException : Exception
{
    /// <summary>The included changes whose risk is <see cref="RiskLevel.DataLoss"/> or higher.</summary>
    public IReadOnlyList<SelectableChange> Offending { get; }

    public DataLossBlockedException(IReadOnlyList<SelectableChange> offending)
        : base($"Deployment blocked: {offending.Count} possible-data-loss change(s) and " +
               $"BlockOnPossibleDataLoss is set. Offending: " +
               string.Join("; ", offending.Select(c => c.Description)))
        => Offending = offending;
}

/// <summary>
/// Renders an ordered list of <see cref="SchemaChange"/>s into a single deployment script.
/// Changes are grouped and sorted by <see cref="SchemaChange.Phase"/> so the result is always
/// dependency-safe. Pre/post-deploy scripts (when supplied) are spliced as <c>pre → diff → post</c>,
/// and with <see cref="DeployOptions.WrapInTransaction"/> all three live inside one BEGIN/COMMIT so a
/// failing seed rolls the whole publish back.
/// </summary>
public sealed class DeployScriptGenerator
{
    public string Generate(IReadOnlyList<SchemaChange> changes, DeployOptions? options = null)
    {
        options ??= new DeployOptions();

        // Block-on-data-loss gate (#54 risk + #58 option). Enforced BEFORE any output is produced so a
        // blocked deploy yields nothing. Default-off, so existing callers are unaffected.
        GuardAgainstDataLoss(changes, options);

        var ordered = changes.OrderBy(c => c.Phase).ToList();
        var scripts = options.Scripts ?? new DeployScriptBundle();

        // Substitute SQLCMD variables in the deploy scripts up front so an unresolved token fails fast
        // (and before any header is emitted). Object-file substitution is intentionally NOT applied here
        // — default scope is deploy-scripts only (object-file substitution is a documented opt-in, gated
        // in the CLI). See SqlCmdVariableResolver remarks for the $$( escaping rule.
        var preBody = ResolveBody(scripts.Pre, options.Variables);
        var postBody = ResolveBody(scripts.Post, options.Variables);

        var sb = new StringBuilder();

        if (options.IncludeHeader)
        {
            sb.AppendLine("-- ============================================================");
            sb.AppendLine("-- PgProj deployment script");
            sb.AppendLine($"-- {ordered.Count} change(s)" +
                          (ordered.Any(c => c.IsDestructive) ? "  [contains destructive operations]" : ""));
            if (scripts.Pre is not null) sb.AppendLine($"-- pre-deploy:  {scripts.Pre.Name}");
            if (scripts.Post is not null) sb.AppendLine($"-- post-deploy: {scripts.Post.Name}");
            if (options.Variables is not null)
                foreach (var line in options.Variables.BannerLines())
                    sb.AppendLine(line);
            sb.AppendLine("-- ============================================================");
            sb.AppendLine();
        }

        var nothingToDo = ordered.Count == 0 && scripts.IsEmpty;
        if (nothingToDo)
        {
            sb.AppendLine("-- No changes. Target already matches the source.");
            return sb.ToString();
        }

        if (options.WrapInTransaction)
        {
            sb.AppendLine("BEGIN;");
            sb.AppendLine();
        }

        // pre → schema diff → post
        AppendScriptSection(sb, "pre-deployment", scripts.Pre?.Name, preBody);

        foreach (var change in ordered)
        {
            sb.AppendLine($"-- {change.Describe()}");
            sb.AppendLine(change.ToSql());
            sb.AppendLine();
        }

        AppendScriptSection(sb, "post-deployment", scripts.Post?.Name, postBody);

        if (options.WrapInTransaction)
            sb.AppendLine("COMMIT;");

        return sb.ToString();
    }

    /// <summary>
    /// The block-on-data-loss enforcement point (#58). When <see cref="DeployOptions.BlockOnPossibleDataLoss"/>
    /// is set and any change classifies at <see cref="RiskLevel.DataLoss"/> or higher (#54), throws
    /// <see cref="DataLossBlockedException"/>. A no-op when the option is off (the default) — so behaviour is
    /// unchanged for existing callers. Public so the planner/CLI can run the same check ahead of generation.
    /// </summary>
    public static void GuardAgainstDataLoss(IReadOnlyList<SchemaChange> changes, DeployOptions options)
    {
        if (!options.BlockOnPossibleDataLoss) return;

        var offending = new List<SelectableChange>();
        var i = 0;
        foreach (var change in changes)
        {
            if (RiskAnalyzer.Default.Classify(change).Level >= RiskLevel.DataLoss)
            {
                // Wrap so the exception carries the human description; id is positional+stable hash.
                offending.Add(new SelectableChange(
                    SelectableChange.HashOf(SelectableChange.Signature(change)) + "#" + i,
                    change, included: true));
            }
            i++;
        }

        if (offending.Count > 0) throw new DataLossBlockedException(offending);
    }

    private static string? ResolveBody(DeployScript? script, SqlCmdVariableResolver? variables)
    {
        if (script is null) return null;
        return variables is null ? script.Body : variables.Substitute(script.Body, script.Name);
    }

    private static void AppendScriptSection(StringBuilder sb, string label, string? name, string? body)
    {
        if (body is null) return;
        sb.AppendLine($"-- ---- {label} script: {name} ----");
        // Verbatim pass-through: append the (already variable-substituted) body untouched so dollar-quoted
        // bodies and embedded semicolons survive. Guarantee a trailing newline and a blank separator.
        sb.AppendLine(body.TrimEnd('\r', '\n'));
        sb.AppendLine();
    }
}
