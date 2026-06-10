using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PgProj.Core.Analysis;

/// <summary>
/// Per-rule overrides applied on top of a rule's built-in default: whether it runs at all and, when it
/// does, what severity its findings carry. A null field means "no override — keep the rule's default".
/// </summary>
public sealed record RuleOverride(bool? Enabled = null, DiagnosticSeverity? Severity = null);

/// <summary>
/// The resolved analysis configuration the <see cref="PgAnalyzer"/> honours (EP-ANALYSIS+). It carries
/// per-rule <c>enabled</c> + <c>severity</c> overrides, layered in precedence order
/// <b>CLI &gt; config file &gt; rule default</b>. A rule with no override keeps the default baked into the
/// analyzer (see <see cref="PgAnalyzer.RuleDefaults"/>).
/// </summary>
/// <remarks>
/// On disk the config is a <c>.pgproj.analysis.json</c> sidecar sitting next to the <c>.pgproj</c>:
/// <code>
/// { "rules": { "PG003": { "enabled": false }, "PG005": { "severity": "error" } } }
/// </code>
/// CLI <c>--rule</c> overrides parse as <c>PG003=off</c> (disable), <c>PG005=error</c> (severity), or
/// <c>PG005=on</c> (force-enable) and win over the file. The config is intentionally permissive: an
/// unknown rule id or an unparsable severity is ignored rather than failing the build (a project should
/// not stop deploying because someone fat-fingered a rule name in a side file).
/// </remarks>
public sealed class AnalysisConfig
{
    /// <summary>The conventional sidecar file name, resolved next to the <c>.pgproj</c>.</summary>
    public const string SidecarFileName = ".pgproj.analysis.json";

    private readonly Dictionary<string, RuleOverride> _overrides;
    private readonly IReadOnlyList<string> _rulePackPaths;

    /// <summary>An empty config: every rule keeps its built-in default. The analyzer's no-arg behaviour.</summary>
    public static AnalysisConfig Empty { get; } = new(new Dictionary<string, RuleOverride>());

    private AnalysisConfig(Dictionary<string, RuleOverride> overrides, IReadOnlyList<string>? rulePackPaths = null)
    {
        _overrides = overrides;
        _rulePackPaths = rulePackPaths ?? Array.Empty<string>();
    }

    /// <summary>The per-rule overrides, keyed by rule id (case-insensitive). For tests/inspection.</summary>
    public IReadOnlyDictionary<string, RuleOverride> Overrides => _overrides;

    /// <summary>
    /// External rule-pack DLL paths declared by the project's <c>rulePacks</c> array (EP-ANALYSIS+ #79),
    /// in declaration order. Resolved relative to the <c>.pgproj</c> directory by the analysis setup.
    /// </summary>
    public IReadOnlyList<string> RulePackPaths => _rulePackPaths;

    /// <summary>Builds a config from an explicit override map (the in-memory form; used by tests + merging).</summary>
    public static AnalysisConfig FromOverrides(IReadOnlyDictionary<string, RuleOverride> overrides)
    {
        var map = new Dictionary<string, RuleOverride>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in overrides) map[kv.Key] = kv.Value;
        return new AnalysisConfig(map);
    }

    /// <summary>
    /// Loads the <c>.pgproj.analysis.json</c> sidecar that sits next to <paramref name="projectFilePath"/>,
    /// or returns <see cref="Empty"/> when the file is absent. A malformed file yields an empty config
    /// (analysis stays best-effort); unknown rule ids and unparsable severities are skipped silently.
    /// </summary>
    public static AnalysisConfig LoadForProject(string projectFilePath, IReadOnlySet<string>? extraKnownRuleIds = null)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(projectFilePath));
        if (dir is null) return Empty;
        var sidecar = Path.Combine(dir, SidecarFileName);
        return File.Exists(sidecar) ? LoadFile(sidecar, extraKnownRuleIds) : Empty;
    }

    /// <summary>Parses a sidecar file at an explicit path (used by <see cref="LoadForProject"/> and tests).</summary>
    public static AnalysisConfig LoadFile(string path, IReadOnlySet<string>? extraKnownRuleIds = null)
    {
        try { return Parse(File.ReadAllText(path), extraKnownRuleIds); }
        catch (IOException) { return Empty; }
        catch (UnauthorizedAccessException) { return Empty; }
    }

    /// <summary>
    /// Parses the sidecar JSON text into a config (malformed JSON → <see cref="Empty"/>). <paramref name="extraKnownRuleIds"/>
    /// (the loaded external rule-pack ids) are accepted in the <c>rules</c> block alongside the built-in ids,
    /// so an external rule can be enabled/re-severitied; any other unknown id is ignored.
    /// </summary>
    public static AnalysisConfig Parse(string json, IReadOnlySet<string>? extraKnownRuleIds = null)
    {
        var map = new Dictionary<string, RuleOverride>(StringComparer.OrdinalIgnoreCase);
        var packPaths = new List<string>();
        if (string.IsNullOrWhiteSpace(json)) return Empty;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return Empty; }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Empty;

            // rulePacks: external rule-pack DLL paths (EP-ANALYSIS+ #79).
            if (doc.RootElement.TryGetProperty("rulePacks", out var packs) && packs.ValueKind == JsonValueKind.Array)
                foreach (var p in packs.EnumerateArray())
                    if (p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } path)
                        packPaths.Add(path);

            if (doc.RootElement.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Object)
                foreach (var rule in rules.EnumerateObject())
                {
                    var id = rule.Name.Trim();
                    if (id.Length == 0) continue;
                    if (!PgAnalyzer.IsKnownRule(id) && !ModelAnalyzer.IsKnownRule(id) && !(extraKnownRuleIds?.Contains(id) ?? false)) continue; // ignore unknown ids
                    if (rule.Value.ValueKind != JsonValueKind.Object) continue;

                    bool? enabled = null;
                    DiagnosticSeverity? severity = null;

                    if (rule.Value.TryGetProperty("enabled", out var en) &&
                        (en.ValueKind == JsonValueKind.True || en.ValueKind == JsonValueKind.False))
                        enabled = en.GetBoolean();

                    if (rule.Value.TryGetProperty("severity", out var sev) && sev.ValueKind == JsonValueKind.String &&
                        TryParseSeverity(sev.GetString(), out var parsed))
                        severity = parsed;

                    if (enabled is not null || severity is not null)
                        map[id] = new RuleOverride(enabled, severity);
                }
        }

        return map.Count == 0 && packPaths.Count == 0 ? Empty : new AnalysisConfig(map, packPaths);
    }

    /// <summary>
    /// Layers CLI <c>--rule</c> overrides on top of this config (CLI wins). Each entry is
    /// <c>RULEID=value</c> where value ∈ {<c>off</c>/<c>false</c>/<c>disabled</c>,
    /// <c>on</c>/<c>true</c>/<c>enabled</c>, or a severity <c>info</c>/<c>warning</c>/<c>error</c>}.
    /// A severity value also implies enabled. Unknown rule ids and unparsable values throw
    /// <see cref="CliRuleException"/> so the CLI reports a usage error (unlike the lenient file).
    /// </summary>
    public AnalysisConfig WithCliOverrides(IReadOnlyDictionary<string, string> ruleArgs, IReadOnlySet<string>? extraKnownRuleIds = null)
    {
        if (ruleArgs.Count == 0) return this;
        var map = new Dictionary<string, RuleOverride>(_overrides, StringComparer.OrdinalIgnoreCase);

        foreach (var (rawId, rawVal) in ruleArgs)
        {
            var id = rawId.Trim();
            if (!PgAnalyzer.IsKnownRule(id) && !ModelAnalyzer.IsKnownRule(id) && !(extraKnownRuleIds?.Contains(id) ?? false))
                throw new CliRuleException($"Unknown analysis rule '{id}'. Known rules: {string.Join(", ", PgAnalyzer.RuleIds.Concat(ModelAnalyzer.RuleIds))}.");

            var val = (rawVal ?? string.Empty).Trim().ToLowerInvariant();
            var existing = map.TryGetValue(id, out var e) ? e : new RuleOverride();

            switch (val)
            {
                case "off" or "false" or "disabled" or "disable" or "no":
                    map[id] = existing with { Enabled = false };
                    break;
                case "on" or "true" or "enabled" or "enable" or "yes":
                    map[id] = existing with { Enabled = true };
                    break;
                default:
                    if (!TryParseSeverity(val, out var sev))
                        throw new CliRuleException(
                            $"Invalid --rule value '{rawVal}' for {id}. Expected off/on or a severity (info, warning, error).");
                    // A severity override also implies the rule is enabled.
                    map[id] = existing with { Enabled = true, Severity = sev };
                    break;
            }
        }

        return new AnalysisConfig(map, _rulePackPaths);
    }

    /// <summary>True when <paramref name="ruleId"/> should run (its override enabled flag, else its default).</summary>
    public bool IsEnabled(string ruleId) =>
        _overrides.TryGetValue(ruleId, out var o) && o.Enabled is { } en ? en : true;

    /// <summary>
    /// The effective severity for a finding of <paramref name="ruleId"/>: the configured override if present,
    /// otherwise the rule's natural severity (<paramref name="defaultSeverity"/>).
    /// </summary>
    public DiagnosticSeverity EffectiveSeverity(string ruleId, DiagnosticSeverity defaultSeverity) =>
        _overrides.TryGetValue(ruleId, out var o) && o.Severity is { } sev ? sev : defaultSeverity;

    /// <summary>Parses a severity token (case-insensitive), accepting common synonyms.</summary>
    public static bool TryParseSeverity(string? value, out DiagnosticSeverity severity)
    {
        switch ((value ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "info" or "information" or "note" or "hint": severity = DiagnosticSeverity.Info; return true;
            case "warning" or "warn": severity = DiagnosticSeverity.Warning; return true;
            case "error" or "err": severity = DiagnosticSeverity.Error; return true;
            default: severity = default; return false;
        }
    }
}

/// <summary>
/// Thrown when a CLI <c>--rule</c> override is malformed (unknown rule id or unparsable value). The CLI
/// surfaces it as a usage error. Kept separate from the Core CLI namespace so the analyzer assembly stays
/// free of the host-CLI dependency.
/// </summary>
public sealed class CliRuleException : Exception
{
    public CliRuleException(string message) : base(message) { }
}
