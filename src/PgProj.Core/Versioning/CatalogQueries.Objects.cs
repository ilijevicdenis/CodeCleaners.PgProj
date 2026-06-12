namespace PgProj.Core.Versioning;

/// <summary>Remaining object-kind catalog queries: extensions, policies, comments, FDW/servers/user
/// mappings, extended statistics, text search, and publications.</summary>
public sealed partial record CatalogQueries
{
    public string Extensions { get; init; } = "SELECT extname FROM pg_extension ORDER BY extname;";

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

    // User mappings (#108): FOR <user> SERVER <server> [OPTIONS …]. pg_user_mappings.usename is NULL for a
    // PUBLIC mapping; umoptions is visible to the server owner / superuser. Identity matches the parser's
    // `usermapping:for <user> server <server>`.
    public string UserMappings { get; init; } = @"
            SELECT um.usename, um.srvname, um.umoptions
            FROM pg_user_mappings um
            ORDER BY um.srvname, COALESCE(um.usename, '');";

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

    // Expression extended statistics (stxexprs set), reconstructed in full via pg_get_statisticsobjdef
    // (PG13+) rather than existence-only (#110). Column-only stats are handled by Statistics above.
    public string ExpressionStatistics { get; init; } = @"
            SELECT n.nspname, s.stxname, pg_get_statisticsobjdef(s.oid) AS def
            FROM pg_statistic_ext s
            JOIN pg_namespace n ON n.oid = s.stxnamespace
            WHERE s.stxexprs IS NOT NULL
              AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, s.stxname;";

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

    // Text-search PARSER/TEMPLATE (#109): reconstructed from their support-function regprocs. A user can
    // create these in pure SQL by pointing at built-in C support functions (prsd_*, dsimple_*, …).
    public string TextSearchParsers { get; init; } = @"
            SELECT n.nspname, p.prsname,
                   p.prsstart::regproc::text, p.prstoken::regproc::text, p.prsend::regproc::text,
                   p.prslextype::regproc::text, NULLIF(p.prsheadline, 0)::regproc::text
            FROM pg_ts_parser p
            JOIN pg_namespace n ON n.oid = p.prsnamespace
            WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, p.prsname;";

    public string TextSearchTemplates { get; init; } = @"
            SELECT n.nspname, t.tmplname, NULLIF(t.tmplinit, 0)::regproc::text, t.tmpllexize::regproc::text
            FROM pg_ts_template t
            JOIN pg_namespace n ON n.oid = t.tmplnamespace
            WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, t.tmplname;";

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
}
