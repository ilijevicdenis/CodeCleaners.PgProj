namespace PgProj.Core.Versioning;

/// <summary>Routine-shaped catalog queries: functions, aggregates, triggers, rules, event triggers,
/// procedural languages, operators, and operator classes/families.</summary>
public sealed partial record CatalogQueries
{
    public string Functions { get; init; } = @"
            SELECT n.nspname, p.proname,
                   pg_get_function_identity_arguments(p.oid) AS args,
                   pg_get_functiondef(p.oid) AS def
            FROM pg_proc p
            JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
              AND p.prokind IN ('f','p')
            ORDER BY n.nspname, p.proname;";

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

    public string EventTriggers { get; init; } = @"
            SELECT e.evtname, e.evtevent, np.nspname, p.proname, e.evttags
            FROM pg_event_trigger e
            JOIN pg_proc p ON p.oid = e.evtfoid
            JOIN pg_namespace np ON np.oid = p.pronamespace
            ORDER BY e.evtname;";

    // Procedural languages (#108): user CREATE LANGUAGE (lanispl) that isn't extension-owned (plpgsql is
    // owned by its extension and recreated by CREATE EXTENSION, so it's excluded).
    public string Languages { get; init; } = @"
            SELECT l.lanname, l.lanpltrusted,
                   l.lanplcallfoid::regproc::text AS handler,
                   NULLIF(l.laninline, 0)::regproc::text AS inline,
                   NULLIF(l.lanvalidator, 0)::regproc::text AS validator
            FROM pg_language l
            WHERE l.lanispl
              AND NOT EXISTS (SELECT 1 FROM pg_depend d
                              WHERE d.classid = 'pg_language'::regclass AND d.objid = l.oid AND d.deptype = 'e')
            ORDER BY l.lanname;";

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
}
