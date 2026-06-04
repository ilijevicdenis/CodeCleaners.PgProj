using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace PgProj.Core.Parsing;

/// <summary>
/// A fixed, process-wide canonical vocabulary of the SQL words that dominate token frequency — the
/// reserved keywords and built-in type names — in their two common spellings (UPPER and lower). It
/// complements the per-<see cref="Tokenizer"/> interner: that one dedupes a file's own identifiers but
/// resets per file (and is size-gated), whereas these words recur in <i>every</i> file, so canonicalising
/// them from one immutable, shared table removes their duplication across the whole build with no
/// per-file dictionary and no unbounded growth. Lookups are by source span (no string is built for a hit)
/// and the table is read-only after static init, so concurrent parsing is safe.
///
/// <para>Matching is exact-case (Ordinal): a hit returns the same-case canonical instance, so token text
/// — and round-trip — is unchanged. Mixed-case spellings (rare) simply miss and fall through to the
/// per-parse interner. The list need not be exhaustive; coverage of the high-frequency head is what pays.</para>
/// </summary>
internal static class TokenVocabulary
{
    // NOTE: declared before Map — static field initialisers run top-to-bottom, and Build() reads Words.

    /// <summary>Span-keyed lookup: <c>Canonical.TryGetValue(sourceSpan, out canonical)</c>, allocation-free on a hit.</summary>
    public static FrozenDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> Canonical { get; } =
        Build().GetAlternateLookup<ReadOnlySpan<char>>();

    private static FrozenDictionary<string, string> Build()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string word)
        {
            var u = word.ToUpperInvariant();
            var l = word.ToLowerInvariant();
            d[u] = u;   // value is its own CLR-interned canonical instance
            d[l] = l;
        }
        foreach (var w in Words()) Add(w);
        return d.ToFrozenDictionary(StringComparer.Ordinal);
    }

    // Reserved keywords + built-in type names (listed once, lower-case; both cases are generated). Ordered
    // loosely by how often they appear in DDL/DML so the intent is legible; FrozenDictionary order is moot.
    // A method, not a field, so there is no static-field-initialiser ordering dependency with Build().
    private static string[] Words() => new[]
    {
        // --- statement / clause keywords ---
        "select", "insert", "update", "delete", "merge", "truncate", "values", "into", "from", "where",
        "group", "by", "having", "window", "order", "asc", "desc", "limit", "offset", "fetch", "first",
        "next", "row", "rows", "only", "with", "recursive", "union", "all", "intersect", "except",
        "distinct", "as", "on", "using", "natural", "join", "inner", "left", "right", "full", "outer",
        "cross", "lateral", "and", "or", "not", "null", "true", "false", "is", "isnull", "notnull",
        "in", "exists", "between", "like", "ilike", "similar", "case", "when", "then", "else", "end",
        "cast", "collate", "any", "some", "array", "returning", "over", "partition", "filter", "within",
        // --- DDL ---
        "create", "alter", "drop", "table", "index", "view", "materialized", "sequence", "schema",
        "function", "procedure", "trigger", "type", "domain", "extension", "language", "or", "replace",
        "temp", "temporary", "unlogged", "global", "local", "if", "not", "exists", "cascade", "restrict",
        "add", "column", "constraint", "primary", "key", "foreign", "references", "unique", "check",
        "default", "generated", "always", "identity", "stored", "deferrable", "initially", "deferred",
        "immediate", "match", "partial", "simple", "set", "of", "inherits", "partition", "range", "list",
        "hash", "tablespace", "storage", "compression", "comment", "grant", "revoke", "owner", "rename",
        "to", "for", "do", "begin", "declare", "return", "returns", "setof", "void", "out", "inout",
        "variadic", "language", "immutable", "stable", "volatile", "called", "strict", "input", "cost",
        "rows", "parallel", "safe", "unsafe", "security", "definer", "invoker", "execute",
        // --- built-in types ---
        "integer", "int", "int2", "int4", "int8", "smallint", "bigint", "serial", "bigserial",
        "smallserial", "decimal", "numeric", "real", "double", "precision", "float", "float4", "float8",
        "money", "boolean", "bool", "char", "character", "varying", "varchar", "text", "bytea", "name",
        "date", "time", "timestamp", "timestamptz", "interval", "zone", "without", "uuid", "json", "jsonb",
        "xml", "inet", "cidr", "macaddr", "macaddr8", "bit", "point", "line", "lseg", "box", "path",
        "polygon", "circle", "tsvector", "tsquery", "oid", "int4range", "int8range", "numrange", "tsrange",
        "tstzrange", "daterange",
    };
}
