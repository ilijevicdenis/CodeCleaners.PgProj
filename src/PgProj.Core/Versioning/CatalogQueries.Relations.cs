namespace PgProj.Core.Versioning;

/// <summary>Relation-shaped catalog queries: schemas, tables/columns, partitioning, indexes, views,
/// sequences, constraints, and foreign tables.</summary>
public sealed partial record CatalogQueries
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
              -- Partition children (CREATE TABLE … PARTITION OF …) are reconstructed in their PARTITION OF
              -- form by ReadPartitionChildrenAsync (a raw table object, matching the project parse); reading
              -- them here as a plain column list would lose the partition relationship and double-list them.
              AND NOT c.relispartition
              AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, c.relname, a.attnum;";

    // Partition children (#99): CREATE TABLE … PARTITION OF parent <bound>. relpartbound renders as
    // ""FOR VALUES …"" or ""DEFAULT"". Modelled as a raw table object (matching the project parse), so the
    // partition relationship round-trips instead of flattening to a standalone table.
    public string PartitionChildren { get; init; } = @"
            SELECT n.nspname, c.relname, pn.nspname || '.' || p.relname AS parent,
                   pg_get_expr(c.relpartbound, c.oid) AS bound
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_inherits i ON i.inhrelid = c.oid
            JOIN pg_class p ON p.oid = i.inhparent
            JOIN pg_namespace pn ON pn.oid = p.relnamespace
            WHERE c.relispartition AND c.relkind IN ('r','p')   -- table partitions only, not partitioned indexes
              AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, c.relname;";

    // Partition keys (#99): the PARTITION BY clause for a partitioned parent (relkind 'p'). Applied to the
    // finely-modelled parent's TrailingOptions so extract/redeploy recreates it as partitioned.
    public string PartitionKeys { get; init; } = @"
            SELECT n.nspname, c.relname, pg_get_partkeydef(c.oid) AS keydef
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind = 'p'
              AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, c.relname;";

    // Table inheritance (#99): the INHERITS (…) parents of a non-partition child. Applied to the child's
    // TrailingOptions so the inheritance relationship round-trips.
    public string TableInheritance { get; init; } = @"
            SELECT n.nspname, c.relname,
                   string_agg(pn.nspname || '.' || p.relname, ', ' ORDER BY i.inhseqno) AS parents
            FROM pg_inherits i
            JOIN pg_class c ON c.oid = i.inhrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_class p ON p.oid = i.inhparent
            JOIN pg_namespace pn ON pn.oid = p.relnamespace
            WHERE NOT c.relispartition AND c.relkind IN ('r','p')   -- real INHERITS children, not index/partition rows
              AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            GROUP BY n.nspname, c.relname;";

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
}
