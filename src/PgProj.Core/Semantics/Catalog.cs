using System;
using System.Collections.Generic;
using System.Linq;

namespace PgProj.Core.Semantics;

/// <summary>
/// What the semantic analyzer knows exists: schemas it "manages" (so an absent object in one is a
/// real error), relations (tables / views / matviews / sequences / foreign tables) with their
/// columns, plus types and functions. Built from PgParser output by <see cref="CatalogBuilder"/>.
/// Name matching is case-insensitive on the unquoted form.
/// </summary>
public sealed class Catalog
{
    private readonly HashSet<string> _schemas = new(StringComparer.OrdinalIgnoreCase) { "public" };
    private readonly Dictionary<string, List<string>> _relations = new(StringComparer.OrdinalIgnoreCase); // "schema.name" -> columns
    private readonly HashSet<string> _types = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _functions = new(StringComparer.OrdinalIgnoreCase);

    // Schemas contributed by EXTERNAL references (EP-REF). A schema may be both managed (defined in
    // this project) and external (also defined in a referenced project) — membership here only widens
    // resolution; it never narrows what the project itself manages.
    private readonly HashSet<string> _externalSchemas = new(StringComparer.OrdinalIgnoreCase);

    public string DefaultSchema { get; init; } = "public";

    public bool SchemaManaged(string schema) => _schemas.Contains(schema);
    public bool HasRelation(string? schema, string name) => _relations.ContainsKey($"{schema ?? DefaultSchema}.{name}");
    public IReadOnlyList<string>? Columns(string? schema, string name)
        => _relations.TryGetValue($"{schema ?? DefaultSchema}.{name}", out var c) ? c : null;
    public bool HasType(string name) => _types.Contains(name) || _types.Contains(StripSchema(name));
    public bool HasFunction(string name) => _functions.Contains(StripSchema(name));

    public void AddSchema(string name) => _schemas.Add(name);
    public void AddRelation(string? schema, string name, IEnumerable<string>? columns = null)
    {
        var s = schema ?? DefaultSchema;
        _schemas.Add(s);
        _relations[$"{s}.{name}"] = columns?.ToList() ?? new List<string>();
    }
    public void AddType(string? schema, string name) { if (schema is not null) { _schemas.Add(schema); _types.Add($"{schema}.{name}"); } _types.Add(name); }
    public void AddFunction(string name) => _functions.Add(StripSchema(name));

    /// <summary>
    /// Marks <paramref name="schema"/> as resolvable via an external reference (EP-REF). External schemas
    /// are <see cref="SchemaManaged"/> (so qualified references into them resolve at build time) but the
    /// objects they hold are never emitted — they live in a referenced project, not this one.
    /// </summary>
    public void AddExternalSchema(string name) { _schemas.Add(name); _externalSchemas.Add(name); }

    /// <summary>True if <paramref name="schema"/> was contributed by a reference (vs defined locally).</summary>
    public bool SchemaIsExternal(string schema) => _externalSchemas.Contains(schema);

    /// <summary>A copy plus everything in <paramref name="other"/> (used for per-script scope).</summary>
    public Catalog Extend(Catalog other)
    {
        var c = Clone();
        c._schemas.UnionWith(other._schemas);
        foreach (var kv in other._relations) c._relations[kv.Key] = kv.Value;
        c._types.UnionWith(other._types);
        c._functions.UnionWith(other._functions);
        c._externalSchemas.UnionWith(other._externalSchemas);
        return c;
    }

    public Catalog Clone()
    {
        var c = new Catalog { DefaultSchema = DefaultSchema };
        c._schemas.UnionWith(_schemas);
        foreach (var kv in _relations) c._relations[kv.Key] = kv.Value;
        c._types.UnionWith(_types);
        c._functions.UnionWith(_functions);
        c._externalSchemas.UnionWith(_externalSchemas);
        return c;
    }

    private static string StripSchema(string n) { var i = n.LastIndexOf('.'); return i >= 0 ? n[(i + 1)..] : n; }
}
