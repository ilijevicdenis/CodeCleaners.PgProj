using System.Collections.Generic;
using AnalysisDiag = PgProj.Core.Analysis.Diagnostic;
using DiagnosticSeverity = PgProj.Core.Analysis.DiagnosticSeverity;

namespace PgProj.Core.Diagnostics;

/// <summary>
/// The one compiler-style diagnostic shape for the whole engine (issue #49 / EP-SEMCORE). Before this,
/// findings travelled as four incompatible shapes — parser <c>ParseDiagnostic</c> (message + line/col),
/// the analyzer <c>Analysis.Diagnostic</c> (ruleId/severity/message/target, no location),
/// <c>SemanticDiagnostic</c> (bare message), and free-form build strings — and the contract layer
/// dropped fields when mapping them. This type carries every field a Problems-panel entry needs:
/// <see cref="Severity"/>, <see cref="Code"/>, <see cref="Message"/>, a <see cref="File"/> +
/// <see cref="Line"/>/<see cref="Column"/> anchor, and zero or more <see cref="Related"/> locations.
/// </summary>
/// <remarks>
/// Severity reuses <see cref="DiagnosticSeverity"/> (Info/Warning/Error). <see cref="File"/> is
/// project-relative when known (null otherwise); <see cref="Line"/>/<see cref="Column"/> are 1-based,
/// 0 when unknown. <see cref="Target"/> is the optional object the finding is about (kept so analyzer
/// findings can still be grouped and resolved to a source position by qualified name).
/// </remarks>
public sealed record Diagnostic
{
    public required DiagnosticSeverity Severity { get; init; }

    /// <summary>Stable code, e.g. <c>PG001</c> (analyzer), <c>BUILD</c> (build), <c>PGREF002</c> (references).</summary>
    public required string Code { get; init; }

    public required string Message { get; init; }

    /// <summary>The object/target the diagnostic is about (e.g. <c>afd.customers</c>), for grouping. Null when none.</summary>
    public string? Target { get; init; }

    /// <summary>Project-relative source file, or null when the finding has no file anchor.</summary>
    public string? File { get; init; }

    /// <summary>1-based line, 0 when unknown.</summary>
    public int Line { get; init; }

    /// <summary>1-based column, 0 when unknown.</summary>
    public int Column { get; init; }

    /// <summary>Secondary source locations the diagnostic refers to (e.g. the prior definition of a duplicate). Never null.</summary>
    public IReadOnlyList<RelatedLocation> Related { get; init; } = System.Array.Empty<RelatedLocation>();

    public override string ToString() =>
        $"[{Severity.ToString().ToUpperInvariant()}] {Code}{(Target is null ? "" : " " + Target)}: {Message}";

    // ---- factories: one per producer, so every call site populates the right fields ----------------

    /// <summary>From an analyzer finding (PGxxx). Carries code/severity/target; location is resolved later from the target.</summary>
    public static Diagnostic FromAnalyzer(AnalysisDiag d) => new()
    {
        Severity = d.Severity,
        Code = d.RuleId,
        Message = d.Message,
        Target = d.Target,
    };

    /// <summary>From an analyzer finding with an already-resolved source anchor.</summary>
    public static Diagnostic FromAnalyzer(AnalysisDiag d, string? file, int line, int column) => new()
    {
        Severity = d.Severity,
        Code = d.RuleId,
        Message = d.Message,
        Target = d.Target,
        File = file,
        Line = line,
        Column = column,
    };

    /// <summary>From a parser diagnostic (always an error; carries line/col and the project-relative file).</summary>
    public static Diagnostic FromParser(string message, string? file, int line, int column) => new()
    {
        Severity = DiagnosticSeverity.Error,
        Code = "BUILD",
        Message = message,
        File = file,
        Line = line,
        Column = column,
    };

    /// <summary>From a semantic diagnostic (always an error; <paramref name="file"/>/<paramref name="line"/> when the caller knows the statement's position).</summary>
    public static Diagnostic FromSemantic(string message, string? file = null, int line = 0, int column = 0) => new()
    {
        Severity = DiagnosticSeverity.Error,
        Code = "SEM",
        Message = message,
        File = file,
        Line = line,
        Column = column,
    };

    /// <summary>From a free-form build message that has no parser-style position (duplicate definitions, file read failures).</summary>
    public static Diagnostic FromBuild(string message, string? file = null) => new()
    {
        Severity = DiagnosticSeverity.Error,
        Code = "BUILD",
        Message = message,
        File = file,
    };
}

/// <summary>A secondary source location a <see cref="Diagnostic"/> refers to, with an optional note.</summary>
public sealed record RelatedLocation(string? File, int Line, int Column, string? Message = null);
