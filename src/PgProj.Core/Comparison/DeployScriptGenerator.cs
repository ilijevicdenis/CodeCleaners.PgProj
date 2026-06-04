using System.Collections.Generic;
using System.Linq;
using System.Text;
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
