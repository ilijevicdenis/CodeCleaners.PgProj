using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PgProj.Core.Comparison;

namespace PgProj.Core.Packaging;

/// <summary>
/// EP-PKG (#138) — package equivalence verification, the <c>Microsoft.DacpacVerify</c> analogue:
/// are two <c>.pgpkg</c> artifacts the same thing? Stronger than a schema compare because it also
/// asserts SOURCE parity (the embedded .sql set) and OPTION parity (manifest settings), which is
/// what conversion/round-trip and build-determinism proofs need. Read-only over the packages — no
/// database, no project. Shared by the CLI <c>verify</c> verb and any editor host.
/// </summary>
public sealed record PackageVerifyReportDto
{
    /// <summary>Contract schema version (matches the EP-RPC JSON contract major).</summary>
    public string SchemaVersion { get; init; } = "1.0";

    public string Verb { get; init; } = "verify";

    public required string PackageA { get; init; }

    public required string PackageB { get; init; }

    /// <summary>The verdict: no model, source, or option drift.</summary>
    public required bool Equivalent { get; init; }

    /// <summary>Per-object model differences (each names the drifting object), deploy-ordered.</summary>
    public IReadOnlyList<string> ModelDrift { get; init; } = new List<string>();

    /// <summary>Embedded-source differences (path + which side / changed).</summary>
    public IReadOnlyList<PackageSourceDriftDto> SourceDrift { get; init; } = new List<PackageSourceDriftDto>();

    /// <summary>Manifest option/setting differences (stamps — CreatedUtc/ToolVersion — never count).</summary>
    public IReadOnlyList<PackageOptionDriftDto> OptionDrift { get; init; } = new List<PackageOptionDriftDto>();
}

/// <summary>One drifting embedded source file.</summary>
public sealed record PackageSourceDriftDto
{
    public required string Path { get; init; }

    /// <summary><c>onlyInA</c> / <c>onlyInB</c> / <c>changed</c>.</summary>
    public required string Kind { get; init; }
}

/// <summary>One drifting manifest option.</summary>
public sealed record PackageOptionDriftDto
{
    public required string Option { get; init; }

    public string? ValueA { get; init; }

    public string? ValueB { get; init; }
}

public static class PackageVerifier
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Verifies equivalence across the three layers. Identity stamps (<see cref="PgPkgManifest.CreatedUtc"/>,
    /// <see cref="PgPkgManifest.ToolVersion"/>) are deliberately EXCLUDED — two builds of the same sources
    /// at different times must verify equivalent, or the command is useless as a determinism gate.
    /// </summary>
    public static PackageVerifyReportDto Verify(PgPkg a, PgPkg b, string labelA = "A", string labelB = "B")
    {
        // ---- option/settings layer ----------------------------------------------------------
        var options = new List<PackageOptionDriftDto>();
        void Opt(string name, string? va, string? vb)
        {
            if (!string.Equals(va, vb, StringComparison.Ordinal))
                options.Add(new PackageOptionDriftDto { Option = name, ValueA = va, ValueB = vb });
        }
        Opt("name", a.Manifest.Name, b.Manifest.Name);
        Opt("pgVersion", a.Manifest.PgVersion, b.Manifest.PgVersion);
        Opt("formatVersion", a.Manifest.FormatVersion, b.Manifest.FormatVersion);

        // ---- source layer ----------------------------------------------------------------------
        var sourceDrift = new List<PackageSourceDriftDto>();
        var byPathA = a.Sources.ToDictionary(s => s.RelativePath, s => s.Content, StringComparer.OrdinalIgnoreCase);
        var byPathB = b.Sources.ToDictionary(s => s.RelativePath, s => s.Content, StringComparer.OrdinalIgnoreCase);
        foreach (var path in byPathA.Keys.Union(byPathB.Keys, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var inA = byPathA.TryGetValue(path, out var ca);
            var inB = byPathB.TryGetValue(path, out var cb);
            if (inA && inB && string.Equals(ca, cb, StringComparison.Ordinal)) continue;
            sourceDrift.Add(new PackageSourceDriftDto
            {
                Path = path,
                Kind = !inA ? "onlyInB" : !inB ? "onlyInA" : "changed",
            });
        }

        // ---- model layer ------------------------------------------------------------------------
        // Fast path: identical source checksums + no source drift ⇒ identical models by construction
        // (the model is a deterministic function of the sources). Otherwise run the real comparer so
        // the report NAMES the drifting objects instead of saying "hashes differ".
        var modelDrift = new List<string>();
        var checksumsMatch = string.Equals(a.Manifest.SourceChecksum, b.Manifest.SourceChecksum, StringComparison.Ordinal);
        if (!(checksumsMatch && sourceDrift.Count == 0))
        {
            var changes = SchemaChangeSet.Build(a.Model, b.Model,
                new ComparerOptions { DropObjectsNotInSource = true });
            foreach (var c in changes.Changes)
                modelDrift.Add($"{c.ObjectType}: {c.Description}");
        }

        return new PackageVerifyReportDto
        {
            PackageA = labelA,
            PackageB = labelB,
            Equivalent = options.Count == 0 && sourceDrift.Count == 0 && modelDrift.Count == 0,
            ModelDrift = modelDrift,
            SourceDrift = sourceDrift,
            OptionDrift = options,
        };
    }

    /// <summary>Stable JSON form (camelCase, deterministic order — same conventions as compare/deploy-report).</summary>
    public static string Serialize(PackageVerifyReportDto report) => JsonSerializer.Serialize(report, JsonOptions);

    /// <summary>The human-readable verdict the CLI prints.</summary>
    public static string RenderText(PackageVerifyReportDto report)
    {
        var sb = new StringBuilder();
        if (report.Equivalent)
        {
            sb.AppendLine($"PASS — packages are equivalent ({report.PackageA} == {report.PackageB}).");
            return sb.ToString();
        }

        sb.AppendLine($"FAIL — packages differ ({report.PackageA} vs {report.PackageB}):");
        foreach (var o in report.OptionDrift)
            sb.AppendLine($"  option {o.Option}: '{o.ValueA}' vs '{o.ValueB}'");
        foreach (var s in report.SourceDrift)
            sb.AppendLine($"  source {s.Path}: {s.Kind}");
        foreach (var m in report.ModelDrift)
            sb.AppendLine($"  model  {m}");
        sb.AppendLine($"{report.OptionDrift.Count} option, {report.SourceDrift.Count} source, {report.ModelDrift.Count} model difference(s).");
        return sb.ToString();
    }
}
