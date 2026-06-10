using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using PgProj.Core.Contracts;

namespace PgProj.Core.Analysis;

/// <summary>
/// Renders analyzer <see cref="Diagnostic"/>s as a SARIF 2.1.0 document (EP-ANALYSIS+), the static-analysis
/// interchange format GitHub / Azure DevOps code scanning ingest. Each finding becomes a
/// <c>runs[].results[]</c> entry carrying its <c>ruleId</c>, mapped <c>level</c>, message, and — when the
/// source position is resolvable via the <see cref="SourcePositionIndex"/> — a physical location with
/// file uri + 1-based line/column region. The tool driver advertises every rule the analyzer can emit
/// (id, name, short description, default level) so a viewer can render rule metadata for suppressed/clean
/// runs too. Output is deterministic: rules in declaration order, results in input order.
/// </summary>
public sealed class SarifWriter
{
    /// <summary>The SARIF schema this writer targets.</summary>
    public const string SchemaUri = "https://json.schemastore.org/sarif-2.1.0.json";

    /// <summary>The SARIF spec version string (the value of the top-level <c>version</c> field).</summary>
    public const string SarifVersion = "2.1.0";

    /// <summary>The tool name advertised in <c>runs[].tool.driver.name</c>.</summary>
    public const string ToolName = "pgproj";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>Builds the SARIF document object for a set of findings (positions optional).</summary>
    public SarifLog Build(IReadOnlyList<Diagnostic> findings, SourcePositionIndex? positions, string? toolVersion = null)
    {
        var driver = new SarifDriver
        {
            Name = ToolName,
            Version = toolVersion ?? typeof(SarifWriter).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            InformationUri = "https://github.com/codecleaners/pgproj",
            Rules = PgAnalyzer.RuleDefaults.Concat(ModelAnalyzer.RuleDefaults).Select(r => new SarifRule
            {
                Id = r.Id,
                Name = r.Id,
                ShortDescription = new SarifText { Text = r.Title },
                DefaultConfiguration = new SarifConfig { Level = ToLevel(r.DefaultSeverity) },
            }).ToList(),
        };

        var results = findings.Select(f => ToResult(f, positions)).ToList();

        return new SarifLog
        {
            Schema = SchemaUri,
            Version = SarifVersion,
            Runs = new List<SarifRun>
            {
                new() { Tool = new SarifTool { Driver = driver }, Results = results },
            },
        };
    }

    /// <summary>Serializes the SARIF document for the findings to its canonical indented JSON form.</summary>
    public string Write(IReadOnlyList<Diagnostic> findings, SourcePositionIndex? positions, string? toolVersion = null) =>
        JsonSerializer.Serialize(Build(findings, positions, toolVersion), Json);

    private static SarifResult ToResult(Diagnostic d, SourcePositionIndex? positions)
    {
        var result = new SarifResult
        {
            RuleId = d.RuleId,
            Level = ToLevel(d.Severity),
            Message = new SarifText { Text = d.Message },
        };

        var pos = ResolveTarget(d.Target, positions);
        if (pos is { } p)
        {
            result.Locations = new List<SarifLocation>
            {
                new()
                {
                    PhysicalLocation = new SarifPhysicalLocation
                    {
                        ArtifactLocation = new SarifArtifactLocation { Uri = p.File },
                        Region = new SarifRegion { StartLine = p.Line, StartColumn = p.Col > 0 ? p.Col : null },
                    },
                },
            };
        }
        return result;
    }

    /// <summary>Maps an analyzer severity to a SARIF result level (info→note, warning→warning, error→error).</summary>
    public static string ToLevel(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Error => "error",
        DiagnosticSeverity.Warning => "warning",
        _ => "note",
    };

    // Mirrors ContractMappers.ResolveAnalyzerTarget: analyzer targets are schema-qualified function / view /
    // table names (or a bare raw name). Kept here so SARIF position resolution matches the JSON contract.
    private static SourcePosition? ResolveTarget(string target, SourcePositionIndex? positions)
    {
        if (positions is null || string.IsNullOrWhiteSpace(target)) return null;
        foreach (var prefix in new[] { "function:", "view:", "table:" })
        {
            if (prefix == "function:")
            {
                var hit = positions.Find($"function:{target}()".ToLowerInvariant());
                if (hit is not null) return hit;
            }
            var p = positions.Find($"{prefix}{target}".ToLowerInvariant());
            if (p is not null) return p;
        }
        return positions.FindRaw("", target);
    }
}

// ---- SARIF 2.1.0 document shape (minimal subset pgproj emits) -----------------------------------
// Property names are SARIF's own camelCase (the serializer's CamelCase policy reproduces them); the
// only special case is the "$schema" key, pinned via [JsonPropertyName].

public sealed class SarifLog
{
    [JsonPropertyName("$schema")] public string Schema { get; set; } = SarifWriter.SchemaUri;
    public string Version { get; set; } = SarifWriter.SarifVersion;
    public List<SarifRun> Runs { get; set; } = new();
}

public sealed class SarifRun
{
    public SarifTool Tool { get; set; } = new();
    public List<SarifResult> Results { get; set; } = new();
}

public sealed class SarifTool
{
    public SarifDriver Driver { get; set; } = new();
}

public sealed class SarifDriver
{
    public string Name { get; set; } = SarifWriter.ToolName;
    public string? Version { get; set; }
    public string? InformationUri { get; set; }
    public List<SarifRule> Rules { get; set; } = new();
}

public sealed class SarifRule
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public SarifText? ShortDescription { get; set; }
    public SarifConfig? DefaultConfiguration { get; set; }
}

public sealed class SarifConfig
{
    public string Level { get; set; } = "warning";
}

public sealed class SarifResult
{
    public string RuleId { get; set; } = "";
    public string Level { get; set; } = "warning";
    public SarifText Message { get; set; } = new();
    public List<SarifLocation>? Locations { get; set; }
}

public sealed class SarifLocation
{
    public SarifPhysicalLocation? PhysicalLocation { get; set; }
}

public sealed class SarifPhysicalLocation
{
    public SarifArtifactLocation? ArtifactLocation { get; set; }
    public SarifRegion? Region { get; set; }
}

public sealed class SarifArtifactLocation
{
    public string Uri { get; set; } = "";
}

public sealed class SarifRegion
{
    public int StartLine { get; set; }
    public int? StartColumn { get; set; }
}

public sealed class SarifText
{
    public string Text { get; set; } = "";
}
