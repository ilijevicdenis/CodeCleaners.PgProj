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
/// </summary>
public sealed record CatalogQueries
{
    // Wave-1, table-independent.
    public string Schemas { get; init; } = "SELECT nspname FROM pg_namespace ORDER BY nspname;";

    public string TablesAndColumns { get; init; } = @"
            SELECT n.nspname, c.relname, a.attname,
                   format_type(a.atttypid, a.atttypmod) AS datatype,
                   a.attnotnull, pg_get_expr(d.adbin, d.adrelid) AS default_expr,
                   a.attidentity, a.attgenerated
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
            WHERE c.relkind IN ('r','p') AND a.attnum > 0 AND NOT a.attisdropped
              -- Typed tables (CREATE TABLE … OF <type>) are reconstructed in their `OF type` form by
              -- ReadTypedTablesAsync; flattening them to a column list here would both lose that nature
              -- and double-list the table, so skip them in the finely-modelled read.
              AND c.reloftype = 0
              AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, c.relname, a.attnum;";

    public string Indexes { get; init; } = @"
            SELECT n.nspname, c.relname AS tbl, ic.relname AS idx, ix.indisunique,
                   am.amname, pg_get_indexdef(ix.indexrelid) AS def
            FROM pg_index ix
            JOIN pg_class ic ON ic.oid = ix.indexrelid
            JOIN pg_class c ON c.oid = ix.indrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_am am ON am.oid = ic.relam
            WHERE NOT ix.indisprimary
              AND NOT EXISTS (SELECT 1 FROM pg_constraint con WHERE con.conindid = ix.indexrelid)
              AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, ic.relname;";

    public string Views { get; init; } = @"
            SELECT n.nspname, c.relname, pg_get_viewdef(c.oid, true) AS def, c.relkind
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('v','m') AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, c.relname;";

    public string Sequences { get; init; } = @"
            SELECT schemaname, sequencename, data_type::text,
                   increment_by, min_value, max_value, start_value, cache_size, cycle
            FROM pg_sequences
            WHERE schemaname NOT IN ('pg_catalog','information_schema') AND schemaname NOT LIKE 'pg_%'
            ORDER BY schemaname, sequencename;";

    public string Functions { get; init; } = @"
            SELECT n.nspname, p.proname,
                   pg_get_function_identity_arguments(p.oid) AS args,
                   pg_get_functiondef(p.oid) AS def
            FROM pg_proc p
            JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
              AND p.prokind IN ('f','p')
            ORDER BY n.nspname, p.proname;";

    // Wave-2, table-dependent.
    public string Constraints { get; init; } = @"
            SELECT n.nspname, c.relname, con.conname, con.contype, a.attname, k.ord
            FROM pg_constraint con
            JOIN pg_class c ON c.oid = con.conrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN LATERAL unnest(con.conkey) WITH ORDINALITY AS k(attnum, ord) ON true
            JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = k.attnum
            WHERE con.contype IN ('p','u')
              AND n.nspname NOT IN ('pg_catalog','information_schema')
            ORDER BY n.nspname, c.relname, con.conname, k.ord;";

    public string Checks { get; init; } = @"
            SELECT n.nspname, c.relname, con.conname, pg_get_constraintdef(con.oid) AS def
            FROM pg_constraint con
            JOIN pg_class c ON c.oid = con.conrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE con.contype = 'c'
              AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, c.relname, con.conname;";

    // EXCLUDE constraints (contype 'x') reconstructed verbatim via pg_get_constraintdef — e.g.
    // ""EXCLUDE USING gist (room_id WITH =, during WITH &&)"" — landing in TableDefinition.OtherConstraints,
    // the same verbatim slot the parser fills, so a project's EXCLUDE round-trips against the live read (#98).
    public string ExcludeConstraints { get; init; } = @"
            SELECT n.nspname, c.relname, con.conname, pg_get_constraintdef(con.oid) AS def
            FROM pg_constraint con
            JOIN pg_class c ON c.oid = con.conrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE con.contype = 'x'
              AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, c.relname, con.conname;";

    public string ForeignKeys { get; init; } = @"
            SELECT n.nspname, c.relname, con.conname,
                   a.attname AS col, k.ord,
                   rn.nspname AS ref_schema, rc.relname AS ref_table, ra.attname AS ref_col,
                   con.confdeltype, con.confupdtype
            FROM pg_constraint con
            JOIN pg_class c ON c.oid = con.conrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_class rc ON rc.oid = con.confrelid
            JOIN pg_namespace rn ON rn.oid = rc.relnamespace
            JOIN LATERAL unnest(con.conkey) WITH ORDINALITY AS k(attnum, ord) ON true
            JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = k.attnum
            JOIN LATERAL unnest(con.confkey) WITH ORDINALITY AS rk(attnum, ord) ON rk.ord = k.ord
            JOIN pg_attribute ra ON ra.attrelid = con.confrelid AND ra.attnum = rk.attnum
            WHERE con.contype = 'f' AND n.nspname NOT IN ('pg_catalog','information_schema')
            ORDER BY n.nspname, c.relname, con.conname, k.ord;";

    // Raw objects.
    public string Extensions { get; init; } = "SELECT extname FROM pg_extension ORDER BY extname;";

    public string TypedTables { get; init; } = @"
            SELECT n.nspname, c.relname,
                   tn.nspname AS type_schema, t.typname AS type_name,
                   (SELECT string_agg(a.attname, ', ' ORDER BY k.ord)
                      FROM pg_constraint con
                      JOIN LATERAL unnest(con.conkey) WITH ORDINALITY AS k(attnum, ord) ON true
                      JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = k.attnum
                      WHERE con.conrelid = c.oid AND con.contype = 'p') AS pk_cols
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_type t ON t.oid = c.reloftype
            JOIN pg_namespace tn ON tn.oid = t.typnamespace
            WHERE c.relkind IN ('r','p') AND c.reloftype <> 0
              AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, c.relname;";

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

    public string Aggregates { get; init; } = @"
            SELECT n.nspname, p.proname,
                   pg_get_function_identity_arguments(p.oid) AS args,
                   a.aggtransfn::regproc::text AS sfunc,
                   format_type(a.aggtranstype, NULL) AS stype,
                   NULLIF(a.aggfinalfn, 0)::regproc::text AS finalfunc,
                   NULLIF(a.aggcombinefn, 0)::regproc::text AS combinefunc,
                   a.agginitval AS initcond
            FROM pg_aggregate a
            JOIN pg_proc p ON p.oid = a.aggfnoid
            JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, p.proname;";

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

    public string Triggers { get; init; } = @"
            SELECT n.nspname, c.relname, t.tgname, pg_get_triggerdef(t.oid, true) AS def
            FROM pg_trigger t
            JOIN pg_class c ON c.oid = t.tgrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE NOT t.tgisinternal
              AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, c.relname, t.tgname;";

    public string Rules { get; init; } = @"
            SELECT n.nspname, c.relname, r.rulename, pg_get_ruledef(r.oid, true) AS def
            FROM pg_rewrite r
            JOIN pg_class c ON c.oid = r.ev_class
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE r.rulename <> '_RETURN'
              AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, c.relname, r.rulename;";

    public string Policies { get; init; } = @"
            SELECT n.nspname, c.relname, pol.polname, pol.polcmd, pol.polpermissive,
                   pg_get_expr(pol.polqual, pol.polrelid) AS using_expr,
                   pg_get_expr(pol.polwithcheck, pol.polrelid) AS check_expr,
                   CASE WHEN pol.polroles = '{0}'::oid[] THEN ARRAY['public']
                        ELSE ARRAY(SELECT r.rolname FROM pg_roles r WHERE r.oid = ANY(pol.polroles) ORDER BY r.rolname)
                   END AS roles
            FROM pg_policy pol
            JOIN pg_class c ON c.oid = pol.polrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, c.relname, pol.polname;";

    public string EventTriggers { get; init; } = @"
            SELECT e.evtname, e.evtevent, np.nspname, p.proname, e.evttags
            FROM pg_event_trigger e
            JOIN pg_proc p ON p.oid = e.evtfoid
            JOIN pg_namespace np ON np.oid = p.pronamespace
            ORDER BY e.evtname;";

    // Comments across every object class that COMMENT ON supports (issue #61). Each branch returns a uniform
    // (target, description) pair where `target` is the exact `<KIND> <name>` text a hand-written
    // `COMMENT ON <target> IS …` would carry, so the reconstructed statement round-trips against the source.
    // Relations split into TABLE/VIEW/MATERIALIZED VIEW/FOREIGN TABLE/INDEX/SEQUENCE by relkind; columns,
    // schemas, functions/procedures, types vs domains, and table-scoped triggers each have their own branch.
    public string Comments { get; init; } = @"
            -- relation-level (table / view / matview / foreign table / index / sequence)
            SELECT (CASE c.relkind
                        WHEN 'r' THEN 'TABLE ' WHEN 'p' THEN 'TABLE '
                        WHEN 'v' THEN 'VIEW ' WHEN 'm' THEN 'MATERIALIZED VIEW '
                        WHEN 'f' THEN 'FOREIGN TABLE ' WHEN 'i' THEN 'INDEX '
                        WHEN 'S' THEN 'SEQUENCE ' ELSE 'TABLE ' END)
                   || n.nspname || '.' || c.relname AS target,
                   d.description
            FROM pg_description d
            JOIN pg_class c ON c.oid = d.objoid AND d.objsubid = 0
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('r','p','v','m','f','i','S')
              AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
          UNION ALL
            -- column-level
            SELECT 'COLUMN ' || n.nspname || '.' || c.relname || '.' || a.attname AS target, d.description
            FROM pg_description d
            JOIN pg_class c ON c.oid = d.objoid AND d.objsubid > 0
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = d.objsubid
            WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
          UNION ALL
            -- schema
            SELECT 'SCHEMA ' || n.nspname AS target, d.description
            FROM pg_shdescription d JOIN pg_namespace n ON n.oid = d.objoid
            WHERE d.classoid = 'pg_namespace'::regclass
              AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
          UNION ALL
            SELECT 'SCHEMA ' || n.nspname AS target, d.description
            FROM pg_description d JOIN pg_namespace n ON n.oid = d.objoid
            WHERE d.classoid = 'pg_namespace'::regclass
              AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
          UNION ALL
            -- function / procedure: a TYPES-ONLY input signature, matching a hand-written
            -- COMMENT ON FUNCTION name(type, type). pg_get_function_identity_arguments would include
            -- parameter names (e.g. 'a integer, b integer'), which a source comment omits — so derive
            -- the input arg types from proargtypes instead (issue #61 round-trip).
            SELECT (CASE WHEN p.prokind = 'p' THEN 'PROCEDURE ' ELSE 'FUNCTION ' END)
                   || n.nspname || '.' || p.proname || '('
                   || COALESCE((SELECT string_agg(format_type(at.oid, NULL), ', ' ORDER BY at.ord)
                                FROM unnest(p.proargtypes) WITH ORDINALITY AS at(oid, ord)), '')
                   || ')' AS target,
                   d.description
            FROM pg_description d
            JOIN pg_proc p ON p.oid = d.objoid
            JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
          UNION ALL
            -- type vs domain
            SELECT (CASE WHEN t.typtype = 'd' THEN 'DOMAIN ' ELSE 'TYPE ' END)
                   || n.nspname || '.' || t.typname AS target, d.description
            FROM pg_description d
            JOIN pg_type t ON t.oid = d.objoid
            JOIN pg_namespace n ON n.oid = t.typnamespace
            WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
              AND (t.typrelid = 0 OR EXISTS (SELECT 1 FROM pg_class cc WHERE cc.oid=t.typrelid AND cc.relkind='c'))
              AND NOT EXISTS (SELECT 1 FROM pg_type e WHERE e.typarray = t.oid)
          UNION ALL
            -- trigger (table-scoped)
            SELECT 'TRIGGER ' || tg.tgname || ' ON ' || n.nspname || '.' || c.relname AS target, d.description
            FROM pg_description d
            JOIN pg_trigger tg ON tg.oid = d.objoid
            JOIN pg_class c ON c.oid = tg.tgrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY 1;";

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

    public string ForeignDataWrappers { get; init; } = @"
            SELECT w.fdwname,
                   NULLIF(w.fdwhandler,0)::regproc::text AS handler,
                   NULLIF(w.fdwvalidator,0)::regproc::text AS validator,
                   w.fdwoptions
            FROM pg_foreign_data_wrapper w
            ORDER BY w.fdwname;";

    public string Servers { get; init; } = @"
            SELECT s.srvname, w.fdwname, s.srvtype, s.srvversion, s.srvoptions
            FROM pg_foreign_server s
            JOIN pg_foreign_data_wrapper w ON w.oid = s.srvfdw
            ORDER BY s.srvname;";

    public string Statistics { get; init; } = @"
            SELECT n.nspname, s.stxname,
                   (s.stxrelid::regclass)::text AS tbl,
                   (SELECT string_agg(a.attname, ', ' ORDER BY k.ord)
                      FROM unnest(s.stxkeys) WITH ORDINALITY AS k(attnum, ord)
                      JOIN pg_attribute a ON a.attrelid = s.stxrelid AND a.attnum = k.attnum) AS cols,
                   ARRAY(SELECT CASE k WHEN 'd' THEN 'ndistinct' WHEN 'f' THEN 'dependencies'
                                       WHEN 'm' THEN 'mcv' END
                         FROM unnest(s.stxkind) AS k
                         WHERE k IN ('d','f','m')) AS kinds
            FROM pg_statistic_ext s
            JOIN pg_namespace n ON n.oid = s.stxnamespace
            WHERE s.stxexprs IS NULL
              AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, s.stxname;";

    /// <summary>Existence-only expression statistics (stxexprs set); reused with a schema-filter suffix.</summary>
    public string StatisticsExistence { get; init; } =
        "SELECT n.nspname, s.stxname FROM pg_statistic_ext s JOIN pg_namespace n ON n.oid=s.stxnamespace WHERE s.stxexprs IS NOT NULL";

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

    public string ForeignTables { get; init; } = @"
            SELECT n.nspname, c.relname, s.srvname, ft.ftoptions,
                   (SELECT string_agg(
                              a.attname || ' ' || format_type(a.atttypid, a.atttypmod)
                              || CASE WHEN a.attnotnull THEN ' NOT NULL' ELSE '' END,
                              ', ' ORDER BY a.attnum)
                      FROM pg_attribute a
                      WHERE a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped) AS cols
            FROM pg_foreign_table ft
            JOIN pg_class c ON c.oid = ft.ftrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_foreign_server s ON s.oid = ft.ftserver
            WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, c.relname;";

    public string Operators { get; init; } = @"
            SELECT n.nspname, o.oprname,
                   CASE WHEN o.oprleft  <> 0 THEN format_type(o.oprleft,  NULL) END AS leftarg,
                   CASE WHEN o.oprright <> 0 THEN format_type(o.oprright, NULL) END AS rightarg,
                   o.oprcode::regproc::text AS func,
                   (SELECT cn.nspname||'.'||c.oprname FROM pg_operator c JOIN pg_namespace cn ON cn.oid=c.oprnamespace WHERE c.oid=o.oprcom)    AS commutator,
                   (SELECT gn.nspname||'.'||g.oprname FROM pg_operator g JOIN pg_namespace gn ON gn.oid=g.oprnamespace WHERE g.oid=o.oprnegate) AS negator,
                   CASE WHEN o.oprrest <> 0 THEN o.oprrest::regproc::text END AS res,
                   CASE WHEN o.oprjoin <> 0 THEN o.oprjoin::regproc::text END AS joi,
                   o.oprcanmerge, o.oprcanhash
            FROM pg_operator o
            JOIN pg_namespace n ON n.oid = o.oprnamespace
            WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
              AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid='pg_operator'::regclass
                                AND d.objid=o.oid AND d.deptype IN ('i','a','e'))
            ORDER BY n.nspname, o.oprname;";

    public string OperatorFamilies { get; init; } = @"
            SELECT n.nspname, f.opfname, am.amname
            FROM pg_opfamily f
            JOIN pg_namespace n ON n.oid = f.opfnamespace
            JOIN pg_am am ON am.oid = f.opfmethod
            WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
              AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid='pg_opclass'::regclass
                                AND d.refobjid=f.oid AND d.deptype='a')
              AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid='pg_opfamily'::regclass
                                AND d.objid=f.oid AND d.deptype='e')
            ORDER BY n.nspname, f.opfname;";

    public string OperatorClasses { get; init; } = @"
            SELECT n.nspname, c.opcname, c.opcdefault,
                   format_type(c.opcintype, NULL) AS intype,
                   am.amname AS method,
                   c.opcfamily, c.opcintype,
                   fn.nspname AS famschema, f.opfname AS famname,
                   EXISTS(SELECT 1 FROM pg_depend d WHERE d.classid='pg_opclass'::regclass
                            AND d.objid=c.oid AND d.refobjid=c.opcfamily AND d.deptype='a') AS autofam
            FROM pg_opclass c
            JOIN pg_namespace n  ON n.oid  = c.opcnamespace
            JOIN pg_am am        ON am.oid = c.opcmethod
            JOIN pg_opfamily f   ON f.oid  = c.opcfamily
            JOIN pg_namespace fn ON fn.oid = f.opfnamespace
            WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
              AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid='pg_opclass'::regclass
                                AND d.objid=c.oid AND d.deptype='e')   -- skip extension opclasses
            ORDER BY n.nspname, c.opcname;";

    /// <summary>Operator-class AM operator members; parameterised on @fam (opclass family) and @t (intype).</summary>
    public string OperatorClassAmOps { get; init; } = @"
                SELECT amopstrategy, amopopr::regoperator::text, amoppurpose,
                       NULLIF(amopsortfamily,0)::regclass::text
                FROM pg_amop
                WHERE amopfamily=@fam AND amoplefttype=@t AND amoprighttype=@t
                ORDER BY amoppurpose, amopstrategy;";

    /// <summary>Operator-class AM support-function members; parameterised on @fam and @t.</summary>
    public string OperatorClassAmProcs { get; init; } = @"
                SELECT amprocnum, amproc::regprocedure::text
                FROM pg_amproc
                WHERE amprocfamily=@fam AND amproclefttype=@t AND amprocrighttype=@t
                ORDER BY amprocnum;";

    public string TextSearchDictionaries { get; init; } = @"
            SELECT n.nspname, d.dictname, tn.nspname||'.'||t.tmplname AS template, d.dictinitoption
            FROM pg_ts_dict d
            JOIN pg_namespace n  ON n.oid  = d.dictnamespace
            JOIN pg_ts_template t ON t.oid = d.dicttemplate
            JOIN pg_namespace tn ON tn.oid = t.tmplnamespace
            WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, d.dictname;";

    public string TextSearchConfigurations { get; init; } = @"
            SELECT n.nspname, c.cfgname, c.oid, c.cfgparser,
                   pn.nspname||'.'||p.prsname AS parser
            FROM pg_ts_config c
            JOIN pg_namespace n  ON n.oid  = c.cfgnamespace
            JOIN pg_ts_parser p  ON p.oid  = c.cfgparser
            JOIN pg_namespace pn ON pn.oid = p.prsnamespace
            WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, c.cfgname;";

    /// <summary>Per-config token-type → dictionary mapping; parameterised on @parser and @cfg.</summary>
    public string TextSearchConfigurationMap { get; init; } = @"
                SELECT tt.alias, string_agg(dn.nspname||'.'||d.dictname, ', ' ORDER BY m.mapseqno) AS dicts
                FROM pg_ts_config_map m
                JOIN pg_ts_dict d ON d.oid = m.mapdict
                JOIN pg_namespace dn ON dn.oid = d.dictnamespace
                JOIN ts_token_type(@parser) tt ON tt.tokid = m.maptokentype
                WHERE m.mapcfg = @cfg
                GROUP BY tt.alias, m.maptokentype
                ORDER BY m.maptokentype;";

    public string Publications { get; init; } = @"
            SELECT p.pubname, p.puballtables, p.pubinsert, p.pubupdate, p.pubdelete, p.pubtruncate, p.pubviaroot,
                   (SELECT string_agg(quote_ident(n.nspname)||'.'||quote_ident(c.relname), ', '
                                      ORDER BY n.nspname, c.relname)
                      FROM pg_publication_rel pr
                      JOIN pg_class c ON c.oid = pr.prrelid
                      JOIN pg_namespace n ON n.oid = c.relnamespace
                      WHERE pr.prpubid = p.oid) AS tables,
                   (SELECT string_agg(quote_ident(n.nspname), ', ' ORDER BY n.nspname)
                      FROM pg_publication_namespace pn
                      JOIN pg_namespace n ON n.oid = pn.pnnspid
                      WHERE pn.pnpubid = p.oid) AS schemas
            FROM pg_publication p
            ORDER BY p.pubname;";

    /// <summary>The PG18-canonical query set. Older profiles fork from this via <see cref="With"/>.</summary>
    public static CatalogQueries Default { get; } = new();

    /// <summary>
    /// Produce a derived set, applying <paramref name="overrides"/> on top of this one. Because the
    /// properties are <c>init</c>-only, an override is written as a record-style <c>with</c> expression
    /// performed by the caller; this helper exists so a profile can express "same as default except X".
    /// </summary>
    public CatalogQueries With(System.Func<CatalogQueries, CatalogQueries> overrides) => overrides(this);
}
