namespace PgProj.Core.Versioning;

/// <summary>
/// The version-correct system-catalog introspection SQL, one property per object type. This is the
/// single home for the queries <see cref="Introspection.LiveDatabaseReader"/> issues; the reader asks
/// the active <see cref="PostgresVersionProfile"/> for each text instead of carrying SQL literals.
///
/// The default instance (see <see cref="Default"/>) is the PG18-canonical set; an older profile builds
/// on it via <see cref="With"/> and overrides only the properties whose catalogs differ on that version.
/// Centralising every query — even the ones that are identical across versions — means a future catalog
/// change has exactly one place to fork by version, and the reader provably issues "the profile's SQL".
///
/// The record is split into partials by object kind so a catalog change touches a focused file:
///   CatalogQueries.Relations.cs — schemas, tables/columns, partitioning, indexes, views, sequences,
///                                 constraints, foreign tables
///   CatalogQueries.Types.cs     — enum/composite/range/shell types, domains, collations, casts, conversions
///   CatalogQueries.Routines.cs  — functions, aggregates, triggers, rules, event triggers, languages,
///                                 operators and operator classes/families
///   CatalogQueries.Objects.cs   — extensions, policies, comments, FDW/servers/user mappings, statistics,
///                                 text search, publications
/// </summary>
public sealed partial record CatalogQueries
{
    /// <summary>The PG18-canonical query set. Older profiles fork from this via <see cref="With"/>.</summary>
    public static CatalogQueries Default { get; } = new();

    /// <summary>
    /// Produce a derived set, applying <paramref name="overrides"/> on top of this one. Because the
    /// properties are <c>init</c>-only, an override is written as a record-style <c>with</c> expression
    /// performed by the caller; this helper exists so a profile can express "same as default except X".
    /// </summary>
    public CatalogQueries With(System.Func<CatalogQueries, CatalogQueries> overrides) => overrides(this);
}
