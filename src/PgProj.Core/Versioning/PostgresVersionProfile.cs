using System.Collections.Generic;
using PgProj.Core.Analysis;

namespace PgProj.Core.Versioning;

/// <summary>
/// One PostgreSQL target version, abstracting everything the tooling needs to know about how that
/// version differs from others. Three facets:
/// <list type="bullet">
///   <item><see cref="SupportedFeatures"/> — capability flags (MERGE, NULLS NOT DISTINCT, …), a typed
///     view over the existing <see cref="PgVersionCapabilities"/> table (no duplicate version data).</item>
///   <item><see cref="CatalogQueries"/> — version-correct system-catalog introspection SQL, one per
///     object type; <see cref="Introspection.LiveDatabaseReader"/> reads its SQL from here.</item>
///   <item><see cref="ObjectCapabilities"/> — per object kind, which ALTER paths exist vs must-recreate;
///     consumed by the diff/generator's ALTER-vs-recreate decision.</item>
/// </list>
/// Profiles are selected from a project's <c>TargetPostgresVersion</c> via <see cref="ForTarget"/>;
/// an unknown/blank/unparseable target falls back to <see cref="Latest"/>.
/// </summary>
public sealed class PostgresVersionProfile
{
    /// <summary>The newest major version this build ships a profile for.</summary>
    public const int LatestMajorVersion = 18;

    /// <summary>The lowest major version this build ships a dedicated profile for.</summary>
    public const int EarliestMajorVersion = 13;

    public int MajorVersion { get; }
    public SupportedFeatures SupportedFeatures { get; }
    public CatalogQueries CatalogQueries { get; }
    public ObjectCapabilities ObjectCapabilities { get; }

    private PostgresVersionProfile(int major, CatalogQueries queries, ObjectCapabilities capabilities)
    {
        MajorVersion = major;
        SupportedFeatures = new SupportedFeatures(major);
        CatalogQueries = queries;
        ObjectCapabilities = capabilities;
    }

    /// <summary>
    /// A derived profile that keeps this profile's version and SupportedFeatures but swaps in different
    /// <see cref="CatalogQueries"/> and/or <see cref="ObjectCapabilities"/>. Used to express a one-off
    /// version-specific override (and by tests to drive the ALTER-vs-recreate seam) without registering a
    /// new entry.
    /// </summary>
    public PostgresVersionProfile With(CatalogQueries? queries = null, ObjectCapabilities? capabilities = null)
        => new(MajorVersion, queries ?? CatalogQueries, capabilities ?? ObjectCapabilities);

    // ---- the registry ----------------------------------------------------------------------------
    //
    // Every shipped major (13–18) gets an entry. They currently share the PG18-canonical CatalogQueries
    // and the default ObjectCapabilities — the abstraction's value is that a version-specific catalog or
    // ALTER difference now has exactly one place to fork (override CatalogQueries.With(...) or set an
    // ObjectCapabilities flag) without touching the reader or comparer.

    private static readonly IReadOnlyDictionary<int, PostgresVersionProfile> Registry = Build();

    private static IReadOnlyDictionary<int, PostgresVersionProfile> Build()
    {
        // PG13/14: pg_index has no indnullsnotdistinct (NULLS NOT DISTINCT is PG15+) — referencing the
        // column fails at prepare even inside COALESCE, so those majors read a constant FALSE instead.
        var pre15 = CatalogQueries.Default with
        {
            Constraints = CatalogQueries.Default.Constraints.Replace(
                "COALESCE((SELECT ix.indnullsnotdistinct FROM pg_index ix WHERE ix.indexrelid = con.conindid), false)",
                "false"),
        };

        var map = new Dictionary<int, PostgresVersionProfile>();
        for (var major = EarliestMajorVersion; major <= LatestMajorVersion; major++)
            map[major] = new PostgresVersionProfile(major, major < 15 ? pre15 : CatalogQueries.Default, ObjectCapabilities.Default);
        return map;
    }

    /// <summary>The latest profile — the default when no target is set or the target is unrecognised.</summary>
    public static PostgresVersionProfile Latest => Registry[LatestMajorVersion];

    /// <summary>
    /// Select the profile for a project's <c>TargetPostgresVersion</c> string (e.g. "16", "PostgreSQL 17",
    /// "pg15", "16.2"). Blank/unparseable → <see cref="Latest"/>. A parsed version outside the shipped
    /// range clamps to the nearest shipped profile (newer-than-latest → latest; older-than-earliest →
    /// earliest), so a forward-looking or legacy target still resolves to a usable profile.
    /// </summary>
    public static PostgresVersionProfile ForTarget(string? targetPostgresVersion)
    {
        var major = TargetVersionAnalyzer.ParseMajorVersion(targetPostgresVersion);
        return ForMajor(major);
    }

    /// <summary>As <see cref="ForTarget"/> but from an already-parsed major (null → latest, out-of-range clamps).</summary>
    public static PostgresVersionProfile ForMajor(int? major)
    {
        if (major is null) return Latest;
        var m = major.Value;
        if (Registry.TryGetValue(m, out var exact)) return exact;
        if (m > LatestMajorVersion) return Latest;
        return Registry[EarliestMajorVersion];
    }
}
