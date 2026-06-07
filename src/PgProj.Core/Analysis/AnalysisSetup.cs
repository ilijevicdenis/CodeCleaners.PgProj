using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PgProj.Core.Analysis;

/// <summary>
/// Resolves a project's effective analysis configuration <em>and</em> its loaded external rule packs in one
/// place (EP-ANALYSIS+ #79), so the CLI and the JSON-contract builder share identical setup. Two-pass: read
/// the sidecar to discover <c>rulePacks</c>, load them, then re-read the sidecar (and apply CLI <c>--rule</c>
/// overrides) with the external rule ids treated as known so they can be enabled/re-severitied like built-ins.
/// </summary>
public static class AnalysisSetup
{
    /// <summary>The resolved analysis config plus the external rules to run alongside the built-in analyzer.</summary>
    public static (AnalysisConfig Config, IReadOnlyList<IPgRule> Rules) Resolve(
        string projectFilePath, IReadOnlyDictionary<string, string>? cliRuleArgs = null)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(projectFilePath));

        // Pass 1: just to learn the rulePacks declared by the sidecar.
        var pre = AnalysisConfig.LoadForProject(projectFilePath);
        var rules = pre.RulePackPaths.Count == 0
            ? (IReadOnlyList<IPgRule>)Array.Empty<IPgRule>()
            : RulePackLoader.FromPaths(pre.RulePackPaths, dir);

        // Pass 2: build the config knowing the external ids, then layer CLI overrides on top.
        var ids = new HashSet<string>(rules.Select(r => r.Id), StringComparer.OrdinalIgnoreCase);
        var config = AnalysisConfig.LoadForProject(projectFilePath, ids);
        if (cliRuleArgs is { Count: > 0 })
            config = config.WithCliOverrides(cliRuleArgs, ids);

        return (config, rules);
    }
}
