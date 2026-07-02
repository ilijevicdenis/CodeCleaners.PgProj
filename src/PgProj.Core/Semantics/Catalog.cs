using System;
using System.Collections.Generic;
using System.Linq;
using PgProj.Core.Model.Identity;

namespace PgProj.Core.Semantics;

/// <summary>
/// What the semantic analyzer knows exists: schemas it "manages" (so an absent object in one is a
/// real error), relations (tables / views / matviews / sequences / foreign tables) with their
/// columns, plus types and functions. Built from PgParser output by <see cref="CatalogBuilder"/>.
/// Name matching is case-insensitive on the unquoted form.
/// <para>
/// As of Phase 2 (issue #46) the catalog owns a real <see cref="SymbolTable"/> (identity-carrying
/// entries, overload-keyed functions, reverse lookup, search_path resolution). The existence-set API
/// below is preserved for existing consumers and mirrors every mutation onto <see cref="Symbols"/>;
/// new code should prefer the richer <see cref="Symbols"/> / <see cref="SearchPath"/> surface.
/// </para>
/// </summary>
public sealed class Catalog
{
    private readonly HashSet<string> _schemas = new(StringComparer.OrdinalIgnoreCase) { "public" };
    private readonly Dictionary<string, List<string>> _relations = new(StringComparer.OrdinalIgnoreCase); // "schema.name" -> columns
    private readonly Dictionary<string, List<ColumnInfo>> _relationColumns = new(StringComparer.OrdinalIgnoreCase); // "schema.name" -> columns+types
    private readonly HashSet<string> _types = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<FunctionSignature>> _functions = new(StringComparer.OrdinalIgnoreCase); // unqualified name -> overload signatures

    // Schemas contributed by EXTERNAL references (EP-REF). A schema may be both managed (defined in
    // this project) and external (also defined in a referenced project) — membership here only widens
    // resolution; it never narrows what the project itself manages.
    private readonly HashSet<string> _externalSchemas = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A column name together with its declared/normalized type (the seed for Phase 4/5 type-safety).</summary>
    public readonly record struct ColumnInfo(string Name, string? Type);

    /// <summary>The real symbol table backing this catalog (identity entries, overloads, reverse lookup).</summary>
    public SymbolTable Symbols { get; } = new();

    public string DefaultSchema { get; init; } = "public";

    private SearchPath? _searchPath;

    /// <summary>
    /// The search_path used to resolve unqualified names. Defaults to <c>"$user", public</c> with
    /// <c>"$user"</c> bound to <see cref="DefaultSchema"/>, so <see cref="DefaultSchema"/> stays the schema an
    /// unqualified create/write lands in (back-compat) while qualified ↔ unqualified resolution is symmetric.
    /// Lazily derived from <see cref="DefaultSchema"/> so an object-initializer-set DefaultSchema is honored.
    /// </summary>
    public SearchPath SearchPath
    {
        get => _searchPath ??= SearchPath.Default(DefaultSchema);
        init => _searchPath = value;
    }

    public Catalog()
    {
        Symbols.Add(SymbolEntry.ForSchema("public"));
    }

    public bool SchemaManaged(string schema) => _schemas.Contains(schema);
    public bool HasRelation(string? schema, string name) => _relations.ContainsKey($"{schema ?? DefaultSchema}.{name}");
    public IReadOnlyList<string>? Columns(string? schema, string name)
        => _relations.TryGetValue($"{schema ?? DefaultSchema}.{name}", out var c) ? c : null;

    /// <summary>Columns with their resolved type metadata (Phase 4/5 type-safety seed); null if unknown.</summary>
    public IReadOnlyList<ColumnInfo>? ColumnsWithTypes(string? schema, string name)
        => _relationColumns.TryGetValue($"{schema ?? DefaultSchema}.{name}", out var c) ? c : null;

    public bool HasType(string name) => _types.Contains(name) || _types.Contains(StripSchema(name));
    public bool HasFunction(string name) => _functions.ContainsKey(StripSchema(name));

    /// <summary>True if a specific overload (name + normalized arg signature) exists.</summary>
    public bool HasFunctionOverload(string name, FunctionSignature signature)
        => _functions.TryGetValue(StripSchema(name), out var sigs) && sigs.Contains(signature);

    public void AddSchema(string name) { _schemas.Add(name); Symbols.Add(SymbolEntry.ForSchema(name)); }

    public void AddRelation(string? schema, string name, IEnumerable<string>? columns = null)
        => AddRelation(schema, name, columns?.Select(c => new ColumnInfo(c, null)), default, null, false);

    /// <summary>Adds a relation with column-type metadata, an optional Identity-Model StableId and source file.</summary>
    public void AddRelation(string? schema, string name, IEnumerable<ColumnInfo>? columns,
        StableId stableId = default, string? sourceFile = null, bool external = false)
    {
        var s = schema ?? DefaultSchema;
        AddSchema(s);
        var cols = columns?.ToList() ?? new List<ColumnInfo>();
        _relations[$"{s}.{name}"] = cols.Select(c => c.Name).ToList();
        _relationColumns[$"{s}.{name}"] = cols;

        Symbols.Add(SymbolEntry.ForRelation(s, name, stableId: stableId, sourceFile: sourceFile, external: external));
        foreach (var c in cols)
            Symbols.Add(SymbolEntry.ForColumn(s, name, c.Name, c.Type, sourceFile: sourceFile, external: external));
    }

    /// <summary>
    /// Amend an already-added relation for a standalone <c>ALTER TABLE</c> (audit P1: the catalog used to
    /// ignore ALTERs entirely, so a view over an ALTER-added column false-positived "column does not
    /// exist"). Adds/drops/retypes columns so binding sees the post-ALTER shape. A relation not yet
    /// absorbed is left unchanged — best effort, same contract as ModelBuilder's fold. A dropped column
    /// keeps its symbol entry (the symbol table is append-only): a reference to it resolves instead of
    /// erroring — a missed diagnostic, never a false positive.
    /// </summary>
    public void AmendRelation(string? schema, string name,
        IEnumerable<ColumnInfo> addedColumns,
        IEnumerable<string> droppedColumns,
        IEnumerable<(string Column, string NewType)> retypedColumns)
    {
        var s = schema ?? DefaultSchema;
        var key = $"{s}.{name}";
        if (!_relationColumns.TryGetValue(key, out var cols) || !_relations.TryGetValue(key, out var names))
            return;

        foreach (var add in addedColumns)
        {
            cols.Add(add);
            names.Add(add.Name);
            Symbols.Add(SymbolEntry.ForColumn(s, name, add.Name, add.Type));
        }
        foreach (var dropped in droppedColumns)
        {
            cols.RemoveAll(c => string.Equals(c.Name, dropped, StringComparison.OrdinalIgnoreCase));
            names.RemoveAll(n => string.Equals(n, dropped, StringComparison.OrdinalIgnoreCase));
        }
        foreach (var (column, newType) in retypedColumns)
        {
            var idx = cols.FindIndex(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) continue;
            cols[idx] = cols[idx] with { Type = newType };
            Symbols.Add(SymbolEntry.ForColumn(s, name, cols[idx].Name, newType));   // Add overwrites by key
        }
    }

    public void AddType(string? schema, string name)
    {
        if (schema is not null) { AddSchema(schema); _types.Add($"{schema}.{name}"); Symbols.Add(SymbolEntry.ForType(schema, name)); }
        _types.Add(name);
    }

    /// <summary>Adds a function with the empty (unknown) overload signature (back-compat existence add).</summary>
    public void AddFunction(string name) => AddFunction(null, StripSchema(name), new FunctionSignature(""));

    /// <summary>Adds a specific function overload keyed on its normalized argument signature.</summary>
    public void AddFunction(string? schema, string name, FunctionSignature signature,
        StableId stableId = default, string? sourceFile = null, bool external = false, string? returnType = null)
    {
        var bare = StripSchema(name);
        if (!_functions.TryGetValue(bare, out var sigs)) _functions[bare] = sigs = new List<FunctionSignature>();
        if (!sigs.Contains(signature)) sigs.Add(signature);

        var s = schema ?? DefaultSchema;
        if (schema is not null) AddSchema(schema);
        Symbols.Add(SymbolEntry.ForFunction(s, bare, signature, stableId: stableId, sourceFile: sourceFile, external: external, returnType: returnType));
    }

    /// <summary>
    /// Marks <paramref name="schema"/> as resolvable via an external reference (EP-REF). External schemas
    /// are <see cref="SchemaManaged"/> (so qualified references into them resolve at build time) but the
    /// objects they hold are never emitted — they live in a referenced project, not this one.
    /// </summary>
    public void AddExternalSchema(string name) { _schemas.Add(name); _externalSchemas.Add(name); Symbols.Add(SymbolEntry.ForSchema(name, external: true)); }

    /// <summary>True if <paramref name="schema"/> was contributed by a reference (vs defined locally).</summary>
    public bool SchemaIsExternal(string schema) => _externalSchemas.Contains(schema);

    /// <summary>A copy plus everything in <paramref name="other"/> (used for per-script scope).</summary>
    public Catalog Extend(Catalog other)
    {
        var c = Clone();
        c._schemas.UnionWith(other._schemas);
        foreach (var kv in other._relations) c._relations[kv.Key] = kv.Value;
        foreach (var kv in other._relationColumns) c._relationColumns[kv.Key] = kv.Value;
        c._types.UnionWith(other._types);
        foreach (var kv in other._functions) MergeFunctionSigs(c._functions, kv.Key, kv.Value);
        c._externalSchemas.UnionWith(other._externalSchemas);
        c.Symbols.AbsorbFrom(other.Symbols);
        return c;
    }

    public Catalog Clone()
    {
        var c = new Catalog { DefaultSchema = DefaultSchema, SearchPath = SearchPath };
        c._schemas.UnionWith(_schemas);
        foreach (var kv in _relations) c._relations[kv.Key] = kv.Value;
        foreach (var kv in _relationColumns) c._relationColumns[kv.Key] = kv.Value;
        c._types.UnionWith(_types);
        foreach (var kv in _functions) MergeFunctionSigs(c._functions, kv.Key, kv.Value);
        c._externalSchemas.UnionWith(_externalSchemas);
        c.Symbols.AbsorbFrom(Symbols);
        return c;
    }

    private static void MergeFunctionSigs(Dictionary<string, List<FunctionSignature>> dst, string name, List<FunctionSignature> sigs)
    {
        if (!dst.TryGetValue(name, out var existing)) dst[name] = existing = new List<FunctionSignature>();
        foreach (var sig in sigs) if (!existing.Contains(sig)) existing.Add(sig);
    }

    private static string StripSchema(string n) { var i = n.LastIndexOf('.'); return i >= 0 ? n[(i + 1)..] : n; }
}
