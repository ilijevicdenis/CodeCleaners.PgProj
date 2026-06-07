namespace PgProj.Core.Comparison.Risk;

/// <summary>
/// The risk verdict for a single <see cref="SchemaChange"/>: its <see cref="Level"/>, a one-line human
/// <see cref="Rationale"/> explaining why, and the two operational flags a reviewer/planner cares about —
/// whether applying it forces a full <see cref="RequiresTableRewrite"/> and whether it takes a heavy
/// <see cref="RequiresExclusiveLock"/> (ACCESS EXCLUSIVE) on the relation.
/// </summary>
/// <remarks>
/// Pure data, no behaviour — the classification rules live in <see cref="RiskAnalyzer"/> so the verdict is
/// trivially serializable and testable. <see cref="Unknown"/> is the conservative fallback used when a
/// change kind isn't explicitly mapped (treated as <see cref="RiskLevel.Warning"/>, never silently Safe).
/// </remarks>
public sealed record ChangeRisk(
    RiskLevel Level,
    string Rationale,
    bool RequiresTableRewrite = false,
    bool RequiresExclusiveLock = false)
{
    /// <summary>The conservative default for an unmapped change kind: Warning, no rewrite/lock asserted.</summary>
    public static ChangeRisk Unknown { get; } =
        new(RiskLevel.Warning, "Unclassified change; review manually before applying.");
}
