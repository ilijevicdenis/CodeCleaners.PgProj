using System;
using System.Collections.Generic;
using System.Linq;
using PgProj.Core.Model.Identity;

namespace PgProj.Core.Semantics;

/// <summary>What a <see cref="SymbolEntry"/> denotes. Mirrors the object kinds the catalog tracks.</summary>
public enum SymbolKind
{
    Schema,
    Relation,   // table / view / matview / sequence / foreign table (anything column-bearing or selectable)
    Column,
    Type,       // type / domain
    Function,   // function / procedure / aggregate
}

/// <summary>
/// The overload key for a function/procedure: its ordered, type-normalized argument signature. Postgres
/// resolves overloads on the positional argument-type list, so <c>f(int)</c> and <c>f(text)</c> are two
/// distinct callables sharing a name. The signature is normalized via the same canonicalization the
/// Identity Model uses, so <c>f(INT)</c> and <c>f(integer)</c> key to the same overload.
/// </summary>
public readonly struct FunctionSignature : IEquatable<FunctionSignature>
{
    /// <summary>The normalized, comma-joined argument types (empty string for a no-arg function).</summary>
    public string ArgTypes { get; }

    public FunctionSignature(string argTypes) => ArgTypes = argTypes ?? string.Empty;

    public bool Equals(FunctionSignature other) =>
        string.Equals(ArgTypes, other.ArgTypes, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is FunctionSignature s && Equals(s);
    public override int GetHashCode() => ArgTypes is null ? 0 : StringComparer.Ordinal.GetHashCode(ArgTypes);
    public override string ToString() => $"({ArgTypes})";
}

/// <summary>
/// One symbol the semantic analyzer knows exists, carrying the durable Identity-Model handles plus the
/// metadata Phases 3–6 consume: forward resolution (kind/schema/FQN), incremental closure (StableId),
/// and type-safety (a column's resolved type). Construct via the static factories so the FQN/kind invariants
/// hold. Equality is identity (FQN + signature), not reference, so two builds of the same object compare equal.
/// </summary>
public sealed class SymbolEntry
{
    /// <summary>Build-local opaque handle (ordinal). <see cref="ObjectId.None"/> when not assigned.</summary>
    public ObjectId ObjectId { get; }

    /// <summary>Durable, name-independent structural identity (survives a rename). May be <c>default</c>
    /// for symbols that have no Identity-Model record yet (e.g. a column, an externally-absorbed name).</summary>
    public StableId StableId { get; }

    public SymbolKind Kind { get; }

    /// <summary>The owning schema. For a <see cref="SymbolKind.Schema"/> entry this is the schema's own name.</summary>
    public string Schema { get; }

    /// <summary>The unqualified object name (the column/relation/function/type/schema name).</summary>
    public string Name { get; }

    /// <summary>Schema-qualified name. For a function this is the bare name (the overload key carries args);
    /// for a column it is <c>schema.relation.column</c>.</summary>
    public string Fqn { get; }

    /// <summary>The function's overload key, when <see cref="Kind"/> is <see cref="SymbolKind.Function"/>.</summary>
    public FunctionSignature? Signature { get; }

    /// <summary>For a <see cref="SymbolKind.Column"/>: its declared/normalized type (the seed Phase 4/5
    /// type-safety reads). Null for other kinds or when the type is unknown.</summary>
    public string? ColumnType { get; }

    /// <summary>For a <see cref="SymbolKind.Function"/>: its declared/normalized RETURNS type (e.g.
    /// <c>trigger</c>, <c>integer</c>), when known. Null for other kinds, RETURNS TABLE, or when the
    /// return type was not captured. Read by the semantic validator (#48) for trigger validity + return-type
    /// type-safety; never asserted when null (conservative).</summary>
    public string? ReturnType { get; }

    /// <summary>The source file this symbol was defined in, when available (project builds attribute it).</summary>
    public string? SourceFile { get; }

    /// <summary>True when this symbol comes from a referenced project/artifact (EP-REF) rather than this build.</summary>
    public bool IsExternal { get; }

    private SymbolEntry(ObjectId objectId, StableId stableId, SymbolKind kind, string schema, string name,
        string fqn, FunctionSignature? signature, string? columnType, string? returnType, string? sourceFile, bool isExternal)
    {
        ObjectId = objectId; StableId = stableId; Kind = kind; Schema = schema; Name = name;
        Fqn = fqn; Signature = signature; ColumnType = columnType; ReturnType = returnType; SourceFile = sourceFile; IsExternal = isExternal;
    }

    public static SymbolEntry ForSchema(string name, ObjectId id = default, StableId stableId = default, bool external = false) =>
        new(id, stableId, SymbolKind.Schema, name, name, name, null, null, null, null, external);

    public static SymbolEntry ForRelation(string schema, string name, ObjectId id = default, StableId stableId = default,
        string? sourceFile = null, bool external = false) =>
        new(id, stableId, SymbolKind.Relation, schema, name, $"{schema}.{name}", null, null, null, sourceFile, external);

    public static SymbolEntry ForColumn(string schema, string relation, string name, string? columnType,
        ObjectId id = default, string? sourceFile = null, bool external = false) =>
        new(id, default, SymbolKind.Column, schema, name, $"{schema}.{relation}.{name}", null, columnType, null, sourceFile, external);

    public static SymbolEntry ForType(string schema, string name, ObjectId id = default, StableId stableId = default,
        string? sourceFile = null, bool external = false) =>
        new(id, stableId, SymbolKind.Type, schema, name, $"{schema}.{name}", null, null, null, sourceFile, external);

    public static SymbolEntry ForFunction(string schema, string name, FunctionSignature signature, ObjectId id = default,
        StableId stableId = default, string? sourceFile = null, bool external = false, string? returnType = null) =>
        new(id, stableId, SymbolKind.Function, schema, name, $"{schema}.{name}", signature, null, returnType, sourceFile, external);

    /// <summary>The dictionary key: FQN (case-insensitive) plus, for a function, its overload signature.</summary>
    public string Key => Signature is { } s ? $"{Fqn}({s.ArgTypes})".ToLowerInvariant() : Fqn.ToLowerInvariant();

    public override string ToString() => $"{Kind} {Key}";
}

/// <summary>
/// One observed reference FROM a referencing site TO a symbol. The <see cref="ReferencerKey"/> is the
/// stable key of the object that does the referencing (e.g. a view that selects a table); the analyzer
/// records these so <see cref="SymbolTable.ReferencesTo"/> can answer "who references X" — the basis for
/// Find References and for incremental rebuild closure (when X changes, every referencer is dirty).
/// </summary>
public sealed record SymbolReference(string ReferencerKey, string ReferencerFile, SymbolKind ReferentKind);

/// <summary>
/// A search_path: the ordered schema list Postgres consults to resolve an unqualified name (e.g.
/// <c>"$user", public</c>). <c>"$user"</c> expands to the current user; in static analysis we model the
/// current user as the project's default schema so the path is self-consistent (qualified ↔ unqualified
/// resolution reach the same entry).
/// </summary>
public sealed class SearchPath
{
    private readonly List<string> _schemas;

    /// <summary>The schema substituted for the <c>"$user"</c> token (the project's default schema).</summary>
    public string CurrentUserSchema { get; }

    public SearchPath(IEnumerable<string> schemas, string currentUserSchema)
    {
        CurrentUserSchema = currentUserSchema;
        _schemas = new List<string>();
        foreach (var raw in schemas)
        {
            var s = Expand(raw);
            if (s.Length > 0 && !_schemas.Contains(s, StringComparer.OrdinalIgnoreCase))
                _schemas.Add(s);
        }
        if (_schemas.Count == 0) _schemas.Add(currentUserSchema);
    }

    /// <summary>Default Postgres search_path with <c>"$user"</c> mapped to <paramref name="defaultSchema"/>.</summary>
    public static SearchPath Default(string defaultSchema) =>
        new(new[] { "\"$user\"", "public" }, defaultSchema);

    private string Expand(string token)
    {
        var t = token.Trim();
        if (t.Length >= 2 && t[0] == '"' && t[^1] == '"') t = t[1..^1];
        return t.Equals("$user", StringComparison.OrdinalIgnoreCase) ? CurrentUserSchema : t;
    }

    /// <summary>The resolved schema order (the head is what an unqualified write/create lands in).</summary>
    public IReadOnlyList<string> Schemas => _schemas;

    /// <summary>The first schema on the path — where an unqualified, newly-created object lives.</summary>
    public string Head => _schemas[0];
}

/// <summary>
/// The global symbol table: a real per-build directory of every object the semantic core knows, replacing
/// the catalog's existence-set. It provides
/// <list type="bullet">
/// <item><b>forward lookup</b> — by FQN (qualified) or by name over a <see cref="SearchPath"/> (unqualified),
///   symmetric: both reach the same <see cref="SymbolEntry"/>;</item>
/// <item><b>overload-keyed functions</b> — keyed on name + <see cref="FunctionSignature"/>, so two overloads
///   resolve independently and a name-only probe lists all overloads;</item>
/// <item><b>reverse lookup</b> — <see cref="ReferencesTo"/> ("who references X"), the basis for Find
///   References and incremental closure.</item>
/// </list>
/// It is additive: <see cref="Catalog"/> owns one and mirrors its existence checks onto it; existing
/// existence-set consumers keep working unchanged.
/// </summary>
public sealed class SymbolTable
{
    // Forward index: entry key -> entry. Functions are keyed name(sig); everything else by FQN.
    private readonly Dictionary<string, SymbolEntry> _byKey = new(StringComparer.OrdinalIgnoreCase);

    // Name buckets for search_path resolution & overload enumeration: "schema.name" -> entries.
    private readonly Dictionary<string, List<SymbolEntry>> _byQualifiedName = new(StringComparer.OrdinalIgnoreCase);

    // Reverse index: referent key -> the references pointing at it.
    private readonly Dictionary<string, List<SymbolReference>> _reverse = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>All entries currently registered (deterministic insertion order).</summary>
    public IReadOnlyCollection<SymbolEntry> Entries => _byKey.Values;

    /// <summary>Register (or replace) a symbol. Idempotent on its <see cref="SymbolEntry.Key"/>.</summary>
    public void Add(SymbolEntry entry)
    {
        _byKey[entry.Key] = entry;

        // Functions share a "schema.name" bucket across overloads; relations/types/columns occupy theirs alone.
        var bucketKey = $"{entry.Schema}.{entry.Name}";
        if (!_byQualifiedName.TryGetValue(bucketKey, out var list))
            _byQualifiedName[bucketKey] = list = new List<SymbolEntry>();
        // de-dup by full key (so re-adding the same overload doesn't double-count)
        list.RemoveAll(e => string.Equals(e.Key, entry.Key, StringComparison.OrdinalIgnoreCase));
        list.Add(entry);
    }

    /// <summary>Resolve a QUALIFIED, non-function symbol directly by FQN (e.g. <c>app.users</c>).</summary>
    public SymbolEntry? ResolveQualified(string schema, string name) =>
        _byKey.TryGetValue($"{schema}.{name}".ToLowerInvariant(), out var e) ? e : null;

    /// <summary>Resolve a specific function overload by its qualified name + argument signature.</summary>
    public SymbolEntry? ResolveFunction(string schema, string name, FunctionSignature signature) =>
        _byKey.TryGetValue($"{schema}.{name}({signature.ArgTypes})".ToLowerInvariant(), out var e) ? e : null;

    /// <summary>All overloads of a (possibly qualified) function name, in registration order.</summary>
    public IReadOnlyList<SymbolEntry> FunctionOverloads(string schema, string name) =>
        _byQualifiedName.TryGetValue($"{schema}.{name}", out var list)
            ? list.Where(e => e.Kind == SymbolKind.Function).ToList()
            : Array.Empty<SymbolEntry>();

    /// <summary>
    /// Resolve an UNQUALIFIED name against <paramref name="path"/>: walk the schemas in order and return the
    /// first matching entry of <paramref name="kind"/>. Symmetric with <see cref="ResolveQualified"/> — the
    /// entry reached via the path is the very same object reachable by its full FQN.
    /// </summary>
    public SymbolEntry? ResolveUnqualified(string name, SymbolKind kind, SearchPath path)
    {
        foreach (var schema in path.Schemas)
            if (_byQualifiedName.TryGetValue($"{schema}.{name}", out var list))
                foreach (var e in list)
                    if (e.Kind == kind) return e;
        return null;
    }

    /// <summary>
    /// Resolve a function by unqualified name + signature against the search_path (first overload match wins
    /// per schema in path order).
    /// </summary>
    public SymbolEntry? ResolveUnqualifiedFunction(string name, FunctionSignature signature, SearchPath path)
    {
        foreach (var schema in path.Schemas)
        {
            var hit = ResolveFunction(schema, name, signature);
            if (hit is not null) return hit;
        }
        return null;
    }

    /// <summary>Record that <paramref name="reference"/> points at the symbol whose key is <paramref name="referentKey"/>.</summary>
    public void AddReference(string referentKey, SymbolReference reference)
    {
        var key = referentKey.ToLowerInvariant();
        if (!_reverse.TryGetValue(key, out var list))
            _reverse[key] = list = new List<SymbolReference>();
        list.Add(reference);
    }

    /// <summary>Reverse lookup: every reference pointing AT <paramref name="entry"/> ("who references X").</summary>
    public IReadOnlyList<SymbolReference> ReferencesTo(SymbolEntry entry) => ReferencesTo(entry.Key);

    /// <summary>Reverse lookup by raw symbol key.</summary>
    public IReadOnlyList<SymbolReference> ReferencesTo(string referentKey) =>
        _reverse.TryGetValue(referentKey.ToLowerInvariant(), out var list)
            ? list
            : (IReadOnlyList<SymbolReference>)Array.Empty<SymbolReference>();

    /// <summary>Copy every entry and reference from <paramref name="other"/> into this table (used by
    /// <see cref="Catalog.Extend"/> / <see cref="Catalog.Clone"/>).</summary>
    public void AbsorbFrom(SymbolTable other)
    {
        foreach (var e in other._byKey.Values) Add(e);
        foreach (var kv in other._reverse)
            foreach (var r in kv.Value)
                AddReference(kv.Key, r);
    }
}
