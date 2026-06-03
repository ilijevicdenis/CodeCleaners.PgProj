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
}
