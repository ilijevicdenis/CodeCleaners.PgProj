namespace PgProj.Core.Versioning;

/// <summary>Type-system catalog queries: enum/composite/range/shell types, domains, collations,
/// casts, and conversions.</summary>
public sealed partial record CatalogQueries
{
    public string EnumTypes { get; init; } = @"
            SELECT n.nspname, t.typname, e.enumlabel
            FROM pg_type t
            JOIN pg_namespace n ON n.oid = t.typnamespace
            JOIN pg_enum e ON e.enumtypid = t.oid
            WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, t.typname, e.enumsortorder;";

    public string CompositeTypes { get; init; } = @"
            SELECT n.nspname, t.typname, a.attname, format_type(a.atttypid, a.atttypmod)
            FROM pg_type t
            JOIN pg_namespace n ON n.oid = t.typnamespace
            JOIN pg_class c ON c.oid = t.typrelid
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
            WHERE t.typtype = 'c' AND c.relkind = 'c'
              AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, t.typname, a.attnum;";

    public string RangeTypes { get; init; } = @"
            SELECT n.nspname, t.typname, format_type(r.rngsubtype, NULL) AS subtype
            FROM pg_range r
            JOIN pg_type t ON t.oid = r.rngtypid
            JOIN pg_namespace n ON n.oid = t.typnamespace
            WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, t.typname;";

    public string ShellTypes { get; init; } = @"
            SELECT n.nspname, t.typname
            FROM pg_type t
            JOIN pg_namespace n ON n.oid = t.typnamespace
            WHERE NOT t.typisdefined AND t.typtype <> 'b'
              AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, t.typname;";

    public string Collations { get; init; } = @"
            SELECT n.nspname, c.collname, c.collprovider, c.collisdeterministic,
                   c.collcollate, c.collctype, c.colllocale
            FROM pg_collation c
            JOIN pg_namespace n ON n.oid = c.collnamespace
            WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, c.collname;";

    public string Domains { get; init; } = @"
            SELECT n.nspname, t.typname, format_type(t.typbasetype, t.typtypmod) AS basetype,
                   t.typnotnull, t.typdefault,
                   (SELECT string_agg(pg_get_constraintdef(c.oid), ' ')
                      FROM pg_constraint c WHERE c.contypid = t.oid AND c.contype = 'c') AS checks
            FROM pg_type t
            JOIN pg_namespace n ON n.oid = t.typnamespace
            WHERE t.typtype = 'd'
              AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, t.typname;";

    public string Casts { get; init; } = @"
            SELECT format_type(c.castsource, NULL) AS src,
                   format_type(c.casttarget, NULL) AS tgt,
                   CASE WHEN c.castfunc <> 0 THEN c.castfunc::regprocedure::text END AS func,
                   c.castcontext, c.castmethod
            FROM pg_cast c
            WHERE (EXISTS (SELECT 1 FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace
                          WHERE t.oid IN (c.castsource, c.casttarget)
                            AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%')
               OR EXISTS (SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                          WHERE p.oid = c.castfunc
                            AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'))
              -- exclude casts PostgreSQL auto-creates (e.g. range↔multirange) or that belong to an
              -- extension; those reappear on their own when the owning object is created.
              AND NOT EXISTS (SELECT 1 FROM pg_depend d
                              WHERE d.classid = 'pg_cast'::regclass AND d.objid = c.oid
                                AND d.deptype IN ('i','a','e'))
            ORDER BY 1, 2;";

    public string Conversions { get; init; } = @"
            SELECT n.nspname, c.conname,
                   pg_encoding_to_char(c.conforencoding) AS src,
                   pg_encoding_to_char(c.contoencoding) AS dst,
                   c.conproc::regproc::text AS func,
                   c.condefault
            FROM pg_conversion c
            JOIN pg_namespace n ON n.oid = c.connamespace
            WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, c.conname;";
}
