using PgProj.Core.Analysis;

namespace PgProj.Core.Versioning;

/// <summary>
/// The capability-flag facet of a <see cref="PostgresVersionProfile"/>: version-gated PostgreSQL
/// features resolved for one target major version. This is a thin, typed view over the existing
/// <see cref="PgVersionCapabilities"/> table (the single source of truth the
/// <see cref="TargetVersionAnalyzer"/> walks) — it does NOT duplicate the minimum-version data, it
/// looks each flag up against the profile's major version. So adding a row to
/// <see cref="PgVersionCapabilities"/> automatically flows here, and the PGV### analysis path keeps
/// using the table directly and is unaffected.
/// </summary>
public sealed class SupportedFeatures
{
    /// <summary>The major version these flags are resolved against (13, 14, …, 18).</summary>
    public int MajorVersion { get; }

    public SupportedFeatures(int majorVersion) => MajorVersion = majorVersion;

    /// <summary>True when the feature behind <paramref name="ruleId"/> is available at this version.</summary>
    public bool Has(string ruleId) =>
        PgVersionCapabilities.For(ruleId).MinMajorVersion <= MajorVersion;

    /// <summary>True when any PostgreSQL feature first shipped no later than this version.</summary>
    public bool AvailableSince(int minMajorVersion) => minMajorVersion <= MajorVersion;

    // ---- named capability flags (each delegates to the PgVersionCapabilities table) ------------
    //
    // These cover the features the issue calls out (MERGE, NULLS NOT DISTINCT, …) plus a couple that
    // have no PGV### syntax rule yet but are real version gates the diff/codegen can consult. The
    // ones backed by a rule id never restate the min version; the few that aren't yet in the table
    // (generated columns PG12, logical partitioning PG10) carry their min version inline and will be
    // folded into the table if/when a PGV### rule is added for them.

    /// <summary>MERGE statement (PG15+, PGV001).</summary>
    public bool Merge => Has(PgVersionCapabilities.MergeStatement);

    /// <summary>MERGE … RETURNING (PG17+, PGV002).</summary>
    public bool MergeReturning => Has(PgVersionCapabilities.MergeReturning);

    /// <summary>MERGE … WHEN [NOT] MATCHED BY SOURCE/TARGET (PG17+, PGV003).</summary>
    public bool MergeByGuard => Has(PgVersionCapabilities.MergeByGuard);

    /// <summary>UNIQUE … NULLS NOT DISTINCT (PG15+, PGV004).</summary>
    public bool NullsNotDistinct => Has(PgVersionCapabilities.NullsNotDistinct);

    /// <summary>JSON_TABLE (PG17+, PGV005).</summary>
    public bool JsonTable => Has(PgVersionCapabilities.JsonTable);

    /// <summary>SQL/JSON query functions JSON_QUERY / JSON_VALUE / JSON_EXISTS (PG17+, PGV006).</summary>
    public bool JsonQueryFunctions => Has(PgVersionCapabilities.JsonQueryFunctions);

    /// <summary>SQL/JSON value constructors JSON() / JSON_SCALAR / JSON_SERIALIZE (PG16+, PGV007).</summary>
    public bool JsonConstructors => Has(PgVersionCapabilities.JsonConstructors);

    /// <summary>IS [NOT] JSON predicate (PG16+, PGV008).</summary>
    public bool IsJsonPredicate => Has(PgVersionCapabilities.IsJsonPredicate);

    // ---- flags without a syntax rule yet (version inline; fold into the table when a rule lands) --

    /// <summary>Stored generated columns — GENERATED ALWAYS AS (…) STORED (PG12+).</summary>
    public bool GeneratedColumns => AvailableSince(12);

    /// <summary>Declarative (logical) partitioning — PARTITION BY / PARTITION OF (PG10+).</summary>
    public bool LogicalPartitioning => AvailableSince(10);

    /// <summary>Multirange types (PG14+).</summary>
    public bool Multirange => AvailableSince(14);
}
