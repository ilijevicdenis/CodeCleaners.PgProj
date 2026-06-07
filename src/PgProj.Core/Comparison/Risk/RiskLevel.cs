namespace PgProj.Core.Comparison.Risk;

/// <summary>
/// How impactful applying a <see cref="SchemaChange"/> is, in increasing order of severity. The diff
/// engine emits the change; the <see cref="RiskAnalyzer"/> classifies it; downstream the planner badges
/// it (Phase 17) and the "block on data loss" publish option (Phase 18) gates on it.
/// </summary>
/// <remarks>
/// The order is meaningful — higher numeric value = more severe — so callers can compare/clamp
/// (<c>Max</c>) and threshold ("block when level &gt;= DataLoss"). It supersedes the binary
/// <c>SelectableChange.IsDestructive</c>, which remains as a coarse drop/no-drop signal.
/// </remarks>
public enum RiskLevel
{
    /// <summary>No data or availability impact: additive, in-place, reversible (e.g. add a nullable column).</summary>
    Safe = 0,

    /// <summary>Succeeds without losing data but warrants attention: a widening type change, a lock, or a table rewrite.</summary>
    Warning = 1,

    /// <summary>Likely to fail or to require manual intervention: a narrowing/lossy in-place conversion, a rewrite that can error.</summary>
    Dangerous = 2,

    /// <summary>Destroys data or objects: DROP COLUMN/TABLE/SEQUENCE, or a narrowing that silently truncates.</summary>
    DataLoss = 3,

    /// <summary>Cannot be applied as scripted against the target (unsupported on this version / needs a rewrite the engine won't do).</summary>
    Blocking = 4,
}
