using System.Collections.Generic;

namespace PgProj.Core.Analysis;

/// <summary>
/// One version-gated PostgreSQL capability: a syntax/feature the AST can detect, the major version
/// that first shipped it, the <c>PGV###</c> rule id that reports it, and a human label. This is the
/// single source of truth the <see cref="TargetVersionAnalyzer"/> walks — pure data, no detection
/// logic, so the table is auditable and trivially extendable as PgParser learns to recognize more
/// version-specific syntax.
/// </summary>
public sealed record PgCapability(string RuleId, int MinMajorVersion, string Feature, string Detail);

/// <summary>
/// The capability table: feature/syntax → minimum PostgreSQL major version. Every entry corresponds to
/// a construct the hand-written PgParser surfaces in the typed AST (or, for view/function bodies that
/// PgParser keeps verbatim, in their captured source text), so the gate never guesses. Versions follow
/// the PostgreSQL release in which the standard form first appeared:
/// <list type="bullet">
///   <item>PG15 — <c>MERGE</c>; <c>NULLS NOT DISTINCT</c> on UNIQUE.</item>
///   <item>PG16 — SQL/JSON <c>IS [NOT] JSON</c> predicate; <c>JSON()</c> / <c>JSON_SCALAR</c> / <c>JSON_SERIALIZE</c>.</item>
///   <item>PG17 — <c>MERGE … RETURNING</c>; <c>MERGE … WHEN [NOT] MATCHED BY SOURCE/TARGET</c>;
///         <c>JSON_TABLE</c>; the SQL/JSON query functions <c>JSON_QUERY</c> / <c>JSON_VALUE</c> / <c>JSON_EXISTS</c>.</item>
/// </list>
/// Entries are deliberately conservative — only forms whose minimum version is unambiguous are listed,
/// so the gate produces no false positives on legacy syntax that merely shares a keyword.
/// </summary>
public static class PgVersionCapabilities
{
    // ---- rule ids (stable, append-only — never renumber) ---------------------------------------
    public const string MergeStatement      = "PGV001";   // MERGE                              (PG15)
    public const string MergeReturning      = "PGV002";   // MERGE … RETURNING                  (PG17)
    public const string MergeByGuard        = "PGV003";   // WHEN [NOT] MATCHED BY SOURCE/TARGET (PG17)
    public const string NullsNotDistinct    = "PGV004";   // UNIQUE … NULLS NOT DISTINCT        (PG15)
    public const string JsonTable           = "PGV005";   // JSON_TABLE                         (PG17)
    public const string JsonQueryFunctions  = "PGV006";   // JSON_QUERY / JSON_VALUE / JSON_EXISTS (PG17)
    public const string JsonConstructors    = "PGV007";   // JSON() / JSON_SCALAR / JSON_SERIALIZE (PG16)
    public const string IsJsonPredicate     = "PGV008";   // IS [NOT] JSON                      (PG16)

    /// <summary>The full table, keyed by rule id. Append-only; new rules get the next PGV### number.</summary>
    public static readonly IReadOnlyDictionary<string, PgCapability> ByRuleId = Build();

    /// <summary>The capability for a rule id (the analyzer always passes ids it owns).</summary>
    public static PgCapability For(string ruleId) => ByRuleId[ruleId];

    /// <summary>Total number of version-gating rules (mirrors <c>PgAnalyzer.RuleCount</c> for the gate banner).</summary>
    public static int RuleCount => ByRuleId.Count;

    private static IReadOnlyDictionary<string, PgCapability> Build()
    {
        var rows = new[]
        {
            new PgCapability(MergeStatement,     15, "MERGE statement",
                "MERGE was introduced in PostgreSQL 15."),
            new PgCapability(MergeReturning,     17, "MERGE … RETURNING",
                "RETURNING on MERGE was introduced in PostgreSQL 17."),
            new PgCapability(MergeByGuard,       17, "MERGE … WHEN [NOT] MATCHED BY SOURCE/TARGET",
                "The BY SOURCE / BY TARGET merge guards were introduced in PostgreSQL 17."),
            new PgCapability(NullsNotDistinct,   15, "UNIQUE … NULLS NOT DISTINCT",
                "NULLS NOT DISTINCT on a unique constraint/index was introduced in PostgreSQL 15."),
            new PgCapability(JsonTable,          17, "JSON_TABLE",
                "JSON_TABLE was introduced in PostgreSQL 17."),
            new PgCapability(JsonQueryFunctions, 17, "SQL/JSON query function (JSON_QUERY / JSON_VALUE / JSON_EXISTS)",
                "The SQL/JSON query functions were introduced in PostgreSQL 17."),
            new PgCapability(JsonConstructors,   16, "SQL/JSON value constructor (JSON / JSON_SCALAR / JSON_SERIALIZE)",
                "These SQL/JSON value constructors were introduced in PostgreSQL 16."),
            new PgCapability(IsJsonPredicate,    16, "IS [NOT] JSON predicate",
                "The IS JSON predicate was introduced in PostgreSQL 16."),
        };

        var map = new Dictionary<string, PgCapability>();
        foreach (var r in rows) map[r.RuleId] = r;
        return map;
    }
}
