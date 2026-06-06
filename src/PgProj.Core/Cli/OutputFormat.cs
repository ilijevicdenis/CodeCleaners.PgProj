using System;

namespace PgProj.Core.Cli;

/// <summary>
/// The output mode a verb should render in, selected by <c>--format</c>. <see cref="Text"/> is the
/// human-readable default; <see cref="Json"/> is the stable editor contract (EP-RPC); <see cref="Sarif"/>
/// is the code-analysis interchange format consumed by GitHub / Azure code scanning (EP-ANALYSIS+).
/// </summary>
public enum OutputFormat
{
    /// <summary>Human-readable console text (the default when <c>--format</c> is absent).</summary>
    Text,

    /// <summary>Machine-readable, versioned JSON (EP-RPC editor contract).</summary>
    Json,

    /// <summary>SARIF 2.1.0 static-analysis results (EP-ANALYSIS+; only meaningful for <c>analyze</c>).</summary>
    Sarif,
}

/// <summary>Parsing helpers for the <c>--format</c> option.</summary>
public static class OutputFormats
{
    /// <summary>
    /// Parses a <c>--format</c> value, case-insensitively. A null/empty/absent value maps to
    /// <see cref="OutputFormat.Text"/>; an unrecognized value throws <see cref="CliUsageException"/> so the
    /// caller fails with a usage (not crash) exit code.
    /// </summary>
    public static OutputFormat Parse(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "" or "text" => OutputFormat.Text,
        "json" => OutputFormat.Json,
        "sarif" => OutputFormat.Sarif,
        _ => throw new CliUsageException($"Unknown --format '{value}'. Expected one of: text, json, sarif."),
    };
}
