namespace PgProj.Core.Model.Identity;

/// <summary>
/// Knobs that shape the <em>canonical form</em> an <see cref="ObjectIdentityComputer"/> derives
/// (issue #51, Phase 8). Every option defaults to the <b>behaviour-preserving</b> setting, so the
/// parameterless <see cref="ObjectIdentityComputer"/> — and therefore every existing comparer /
/// golden path — is unaffected; opting in is an explicit, deliberate choice.
/// </summary>
public sealed class CanonicalFormOptions
{
    /// <summary>
    /// When <c>true</c>, a table's columns are sorted (by canonical name) before being folded into the
    /// StableId / CanonicalHash, so two tables that differ ONLY in column declaration order hash equal.
    /// <para>
    /// Defaults to <c>false</c>: Postgres column order is physically meaningful (<c>SELECT *</c>, COPY,
    /// composite-row layout), and the comparer / deploy script compare columns positionally — so the
    /// default keeps current verdicts and the golden artifacts byte-identical. The Phase-18
    /// "ignore column order" option flips this on.
    /// </para>
    /// </summary>
    public bool IgnoreColumnOrder { get; init; }

    /// <summary>The behaviour-preserving defaults (all knobs off). Shared singleton.</summary>
    public static readonly CanonicalFormOptions Default = new();
}
