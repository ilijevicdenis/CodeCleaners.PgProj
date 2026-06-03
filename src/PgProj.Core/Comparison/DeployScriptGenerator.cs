using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PgProj.Core.Comparison;

public sealed class DeployOptions
{
    /// <summary>Wrap the whole script in BEGIN/COMMIT so a failed step rolls everything back.</summary>
    public bool WrapInTransaction { get; init; } = true;

    /// <summary>Emit a leading comment banner describing the plan.</summary>
    public bool IncludeHeader { get; init; } = true;
}

/// <summary>
/// Renders an ordered list of <see cref="SchemaChange"/>s into a single deployment script.
/// Changes are grouped and sorted by <see cref="SchemaChange.Phase"/> so the result is always
/// dependency-safe and (optionally) transactional.
/// </summary>
public sealed class DeployScriptGenerator
{
    public string Generate(IReadOnlyList<SchemaChange> changes, DeployOptions? options = null)
    {
        options ??= new DeployOptions();
        var ordered = changes.OrderBy(c => c.Phase).ToList();
        var sb = new StringBuilder();

        if (options.IncludeHeader)
        {
            sb.AppendLine("-- ============================================================");
            sb.AppendLine("-- PgProj deployment script");
            sb.AppendLine($"-- {ordered.Count} change(s)" +
                          (ordered.Any(c => c.IsDestructive) ? "  [contains destructive operations]" : ""));
            sb.AppendLine("-- ============================================================");
            sb.AppendLine();
        }

        if (ordered.Count == 0)
        {
            sb.AppendLine("-- No changes. Target already matches source.");
            return sb.ToString();
        }

        if (options.WrapInTransaction)
        {
            sb.AppendLine("BEGIN;");
            sb.AppendLine();
        }

        foreach (var change in ordered)
        {
            sb.AppendLine($"-- {change.Describe()}");
            sb.AppendLine(change.ToSql());
            sb.AppendLine();
        }

        if (options.WrapInTransaction)
            sb.AppendLine("COMMIT;");

        return sb.ToString();
    }
}
