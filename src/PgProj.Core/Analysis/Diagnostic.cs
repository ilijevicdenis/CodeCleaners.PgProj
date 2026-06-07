namespace PgProj.Core.Analysis;

public enum DiagnosticSeverity { Info, Warning, Error }

/// <summary>A single finding produced by an <see cref="IAnalysisRule"/> over the AST.</summary>
public sealed record Diagnostic(
    string RuleId,
    DiagnosticSeverity Severity,
    string Message,
    string Target)
{
    public override string ToString() =>
        $"[{Severity.ToString().ToUpperInvariant()}] {RuleId} {Target}: {Message}";

    /// <summary>Lift this analyzer finding into the unified compiler-style diagnostic, optionally with a resolved source anchor.</summary>
    public Diagnostics.Diagnostic ToUnified(string? file = null, int line = 0, int column = 0) =>
        Diagnostics.Diagnostic.FromAnalyzer(this, file, line, column);
}
