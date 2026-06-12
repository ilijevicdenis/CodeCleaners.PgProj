using System;
using System.Collections.Generic;
using System.Linq;
using PgProj.Core.Project;

namespace PgProj.Core.Analysis;

/// <summary>
/// EP-BUILD (#135) — the project-level build-warning policy, the SSDT
/// <c>SuppressTSqlWarnings</c> / <c>TreatTSqlWarningsAsErrors</c> analogue. Declared in the
/// <c>.pgproj</c> (<c>&lt;SuppressWarnings&gt;PG005;PGV001&lt;/SuppressWarnings&gt;</c>,
/// <c>&lt;TreatWarningsAsErrors&gt;true&lt;/TreatWarningsAsErrors&gt;</c>) and applied at the BUILD
/// gate — deliberately separate from the per-rule <see cref="AnalysisConfig"/> (which tunes what the
/// <c>analyze</c> verb finds; this tunes what the build gate does about it). One implementation,
/// applied identically by the CLI and by <c>ContractBuilder.Analyze</c> (the in-proc editor path).
/// </summary>
public sealed record BuildWarningPolicy(IReadOnlyCollection<string> SuppressedCodes, bool TreatWarningsAsErrors)
{
    /// <summary>No suppression, no promotion — the behavior of a project that declares nothing.</summary>
    public static BuildWarningPolicy None { get; } = new(Array.Empty<string>(), false);

    /// <summary>The policy a project declares (or <see cref="None"/> for package sources).</summary>
    public static BuildWarningPolicy FromProject(DatabaseProject? project) =>
        project is null
            ? None
            : new(project.SuppressedWarnings, project.TreatWarningsAsErrors);

    /// <summary>Drops findings whose code is suppressed. A suppressed code can never break a build.</summary>
    public IReadOnlyList<Diagnostic> Apply(IEnumerable<Diagnostic> findings) =>
        SuppressedCodes.Count == 0
            ? findings.ToList()
            : findings.Where(f => !SuppressedCodes.Contains(f.RuleId, StringComparer.OrdinalIgnoreCase)).ToList();

    /// <summary>
    /// The blocking verdict for already-<see cref="Apply"/>-filtered findings: errors always block;
    /// warnings block when promoted — by the caller's strict flag OR by the project policy.
    /// </summary>
    public bool IsBlocking(IReadOnlyList<Diagnostic> filtered, bool strictFlag = false) =>
        filtered.Any(f => f.Severity == DiagnosticSeverity.Error)
        || ((strictFlag || TreatWarningsAsErrors) && filtered.Any(f => f.Severity == DiagnosticSeverity.Warning));

    /// <summary>One line for build summaries, so the resolved policy is visible where it acted.</summary>
    public string Describe() =>
        $"warning policy: suppress=[{string.Join(",", SuppressedCodes)}] treatWarningsAsErrors={TreatWarningsAsErrors}";
}
