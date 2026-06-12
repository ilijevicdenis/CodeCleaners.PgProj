using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using PgProj.Core.Syntax;

namespace PgProj.Core.Analysis;

/// <summary>
/// Discovers and instantiates external <see cref="IPgRule"/> implementations from rule-pack assemblies
/// (EP-ANALYSIS+ #79). <see cref="FromAssemblies"/> reflects over already-loaded assemblies (used by tests
/// and in-process hosts); <see cref="FromPaths"/> loads DLLs from disk in an isolated
/// <see cref="AssemblyLoadContext"/> that shares <c>PgProj.Core</c> with the host so the discovered rules
/// implement the <em>same</em> <see cref="IPgRule"/> type. Duplicate ids are dropped (first wins).
/// </summary>
public static class RulePackLoader
{
    /// <summary>Discovers every public, parameterless-constructible <see cref="IPgRule"/> in the given assemblies.</summary>
    public static IReadOnlyList<IPgRule> FromAssemblies(IEnumerable<Assembly> assemblies) =>
        Discover<IPgRule>(assemblies, r => r.Id);

    /// <summary>Discovers every public, parameterless-constructible <see cref="IModelRule"/> in the given assemblies.</summary>
    public static IReadOnlyList<IModelRule> ModelRulesFromAssemblies(IEnumerable<Assembly> assemblies) =>
        Discover<IModelRule>(assemblies, r => r.Id);

    private static IReadOnlyList<T> Discover<T>(IEnumerable<Assembly> assemblies, Func<T, string> idOf) where T : class
    {
        var rules = new List<T>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asm in assemblies)
        {
            foreach (var type in SafeGetTypes(asm))
            {
                if (type is null || type.IsAbstract || type.IsInterface) continue;
                if (!typeof(T).IsAssignableFrom(type)) continue;
                if (type.GetConstructor(Type.EmptyTypes) is null) continue;

                T rule;
                try { rule = (T)Activator.CreateInstance(type)!; }
                catch (Exception ex)
                {
                    throw new RulePackException($"Could not instantiate rule '{type.FullName}': {ex.Message}", ex);
                }
                if (string.IsNullOrWhiteSpace(idOf(rule)))
                    throw new RulePackException($"Rule '{type.FullName}' has an empty Id.");
                if (seen.Add(idOf(rule))) rules.Add(rule);   // first wins on a duplicate id
            }
        }
        return rules;
    }

    /// <summary>
    /// Loads each rule-pack DLL at <paramref name="paths"/> (relative paths resolved against
    /// <paramref name="baseDir"/>) and discovers its per-file rules. A missing or unloadable pack throws
    /// <see cref="RulePackException"/>.
    /// </summary>
    public static IReadOnlyList<IPgRule> FromPaths(IEnumerable<string> paths, string? baseDir = null) =>
        FromAssemblies(LoadAssemblies(paths, baseDir));

    /// <summary>
    /// Loads each rule-pack DLL once and discovers BOTH rule shapes: per-file <see cref="IPgRule"/>s and
    /// model-level <see cref="IModelRule"/>s. The preferred entry point for hosts that run the full gate.
    /// </summary>
    public static (IReadOnlyList<IPgRule> FileRules, IReadOnlyList<IModelRule> ModelRules) AllFromPaths(
        IEnumerable<string> paths, string? baseDir = null)
    {
        var assemblies = LoadAssemblies(paths, baseDir);
        return (FromAssemblies(assemblies), ModelRulesFromAssemblies(assemblies));
    }

    private static List<Assembly> LoadAssemblies(IEnumerable<string> paths, string? baseDir)
    {
        var assemblies = new List<Assembly>();
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            var full = Path.IsPathRooted(p) || baseDir is null ? p : Path.Combine(baseDir, p);
            full = Path.GetFullPath(full);
            if (!File.Exists(full))
                throw new RulePackException($"Rule pack not found: {full}");
            try { assemblies.Add(new RulePackLoadContext(full).LoadFromAssemblyPath(full)); }
            catch (Exception ex)
            {
                throw new RulePackException($"Could not load rule pack '{full}': {ex.Message}", ex);
            }
        }
        return assemblies;
    }

    private static IEnumerable<Type?> SafeGetTypes(Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types; }   // skip the ones that failed to load
    }

    /// <summary>
    /// Per-pack load context. Resolves the pack's own private dependencies next to its DLL, but returns null
    /// for <c>PgProj.Core</c> so it falls back to the host's already-loaded copy — otherwise the pack would
    /// get a second <see cref="IPgRule"/> type and nothing would be assignable.
    /// </summary>
    private sealed class RulePackLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;
        public RulePackLoadContext(string mainAssemblyPath) : base(isCollectible: false)
            => _resolver = new AssemblyDependencyResolver(mainAssemblyPath);

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == "PgProj.Core") return null;   // share the contract assembly with the host
            var path = _resolver.ResolveAssemblyToPath(name);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }
}

/// <summary>Runs external rules over a parsed file, applying the project's analysis config (enable + severity) by rule id.</summary>
public static class ExternalRules
{
    /// <summary>
    /// Runs each enabled rule over <paramref name="result"/> and returns its findings with the configured
    /// effective severity (the config override, else the rule's <see cref="IPgRule.DefaultSeverity"/>).
    /// </summary>
    public static IReadOnlyList<Diagnostic> Run(IReadOnlyList<IPgRule> rules, ParseResult result, AnalysisConfig config)
    {
        if (rules.Count == 0) return Array.Empty<Diagnostic>();
        var diags = new List<Diagnostic>();
        foreach (var rule in rules)
        {
            if (!config.IsEnabled(rule.Id)) continue;
            var sev = config.EffectiveSeverity(rule.Id, rule.DefaultSeverity);
            foreach (var d in rule.Analyze(result))
                diags.Add(d.Severity == sev ? d : d with { Severity = sev });
        }
        return diags;
    }
}

/// <summary>Thrown when a rule pack is missing, cannot be loaded, or contains an invalid rule.</summary>
public sealed class RulePackException : Exception
{
    public RulePackException(string message) : base(message) { }
    public RulePackException(string message, Exception inner) : base(message, inner) { }
}
