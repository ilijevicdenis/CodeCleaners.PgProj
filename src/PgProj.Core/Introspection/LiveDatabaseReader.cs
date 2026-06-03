using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using PgProj.Core.Comparison;
using PgProj.Core.Model;

namespace PgProj.Core.Introspection;

/// <summary>
/// Reads the live schema of a Postgres server into a <see cref="DatabaseModel"/> by querying the
/// system catalogs with raw ADO.NET (Npgsql — no ORM, to avoid materialisation/tracking overhead).
/// Types come straight from <c>format_type()</c>, the same canonical spelling project types normalise
/// to, so the model diffs against a project model with minimal phantom differences.
///
/// Reads run CONCURRENTLY: a single Npgsql connection cannot multiplex commands, so each catalog
/// query gets its own pooled connection and the independent reads fan out (bounded by a semaphore).
/// Each read returns its own data — nothing mutates the shared <see cref="DatabaseModel"/> off-thread —
/// and the results are merged single-threaded at the end. Table-dependent reads (PK/unique/check/FK)
/// run as a second wave once the table map exists, touching disjoint members of each table object.
/// </summary>
public sealed class LiveDatabaseReader
{
    private const int MaxConcurrentReads = 8;

    public async Task<DatabaseModel> ReadAsync(string connectionString, CancellationToken ct = default)
    {
        using var gate = new SemaphoreSlim(MaxConcurrentReads);

        // Run a read on its own pooled connection (commands on one connection can't run concurrently).
        async Task<T> Read<T>(Func<NpgsqlConnection, Task<T>> body)
        {
            await gate.WaitAsync(ct);
            try
            {
                await using var conn = new NpgsqlConnection(connectionString);
                await conn.OpenAsync(ct);
                return await body(conn);
            }
            finally { gate.Release(); }
        }
        // Non-result variant for the table-dependent reads (they mutate the shared table objects).
        Task ReadVoid(Func<NpgsqlConnection, Task> body) => Read<object?>(async c => { await body(c); return null; });

        // ---- wave 1: everything that doesn't depend on the table map, in parallel ----
        var schemasTask   = Read(c => ReadSchemasAsync(c, ct));
        var tablesTask    = Read(c => ReadTablesAndColumnsAsync(c, ct));
        var indexesTask   = Read(c => ReadIndexesAsync(c, ct));
        var viewsTask     = Read(c => ReadViewsAsync(c, ct));
        var sequencesTask = Read(c => ReadSequencesAsync(c, ct));
        var functionsTask = Read(c => ReadFunctionsAsync(c, ct));

        var rawTasks = new[]
        {
            Read(c => ReadExtensionsAsync(c, ct)),
            Read(c => ReadEnumTypesAsync(c, ct)),
            Read(c => ReadCompositeTypesAsync(c, ct)),
            Read(c => ReadRangeTypesAsync(c, ct)),
            Read(c => ReadShellTypesAsync(c, ct)),
            Read(c => ReadCollationsAsync(c, ct)),
            Read(c => ReadAggregatesAsync(c, ct)),
            Read(c => ReadDomainsAsync(c, ct)),
            Read(c => ReadTriggersAsync(c, ct)),
            Read(c => ReadRulesAsync(c, ct)),
            Read(c => ReadPoliciesAsync(c, ct)),
            Read(c => ReadEventTriggersAsync(c, ct)),
            Read(c => ReadCommentsAsync(c, ct)),
            Read(c => ReadConversionsAsync(c, ct)),
            Read(c => ReadForeignDataWrappersAsync(c, ct)),
            Read(c => ReadServersAsync(c, ct)),
            Read(c => ReadStatisticsAsync(c, ct)),
            Read(c => ReadCastsAsync(c, ct)),
            Read(c => ReadForeignTablesAsync(c, ct)),
            Read(c => ReadOperatorsAsync(c, ct)),
            Read(c => ReadOperatorFamiliesAsync(c, ct)),
            Read(c => ReadOperatorClassesAsync(c, ct)),
            Read(c => ReadTextSearchDictionariesAsync(c, ct)),
            Read(c => ReadTextSearchConfigurationsAsync(c, ct)),
            Read(c => ReadPublicationsAsync(c, ct)),
            Read(c => ReadExistenceObjectsAsync(c, ct)),
        };

        // ---- wave 2: table-dependent reads, started as soon as the table map is ready ----
        var (tables, byKey) = await tablesTask;
        var constraintsTask = ReadVoid(c => ReadConstraintsAsync(c, byKey, ct));
        var checksTask      = ReadVoid(c => ReadChecksAsync(c, byKey, ct));
        var fksTask         = ReadVoid(c => ReadForeignKeysAsync(c, byKey, ct));

        await Task.WhenAll(rawTasks);
        await Task.WhenAll(schemasTask, indexesTask, viewsTask, sequencesTask, functionsTask,
                           constraintsTask, checksTask, fksTask);

        // ---- merge (single-threaded) ----
        var model = new DatabaseModel();
        model.Schemas.AddRange(schemasTask.Result);
        model.Tables.AddRange(tables);
        model.Indexes.AddRange(indexesTask.Result);
        model.Views.AddRange(viewsTask.Result);
        model.Sequences.AddRange(sequencesTask.Result);
        model.Functions.AddRange(functionsTask.Result);
        foreach (var t in rawTasks) model.Objects.AddRange(t.Result);
        return model;
    }

    private async Task<List<SchemaDefinition>> ReadSchemasAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = "SELECT nspname FROM pg_namespace ORDER BY nspname;";
        var list = new List<SchemaDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var name = r.GetString(0);
            if (IsUserSchema(name)) list.Add(new SchemaDefinition(name));
        }
        return list;
    }

    private static bool IsUserSchema(string schema) =>
        schema is not ("pg_catalog" or "information_schema") && !schema.StartsWith("pg_", StringComparison.Ordinal);

    private async Task<(List<TableDefinition> Tables, Dictionary<string, TableDefinition> ByKey)> ReadTablesAndColumnsAsync(
        NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
            SELECT n.nspname, c.relname, a.attname,
                   format_type(a.atttypid, a.atttypmod) AS datatype,
                   a.attnotnull, pg_get_expr(d.adbin, d.adrelid) AS default_expr,
                   a.attidentity, a.attgenerated
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
            WHERE c.relkind IN ('r','p') AND a.attnum > 0 AND NOT a.attisdropped
              AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, c.relname, a.attnum;";

        var tables = new List<TableDefinition>();
        var byKey = new Dictionary<string, TableDefinition>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(0);
            var table = r.GetString(1);
            var key = $"{schema}.{table}";
            if (!byKey.TryGetValue(key, out var def))
            {
                def = new TableDefinition { Schema = schema, Name = table };
                byKey[key] = def;
                tables.Add(def);
            }

            var dataType = TypeNormalizer.Normalize(r.GetString(3));
            var notNull = r.GetBoolean(4);
            var defExpr = r.IsDBNull(5) ? null : r.GetString(5);
            var idChar = ReadChar(r, 6, fallback: '\0');
            var genChar = ReadChar(r, 7, fallback: '\0');

            var isIdentity = idChar is 'a' or 'd';
            var identityKind = idChar switch { 'a' => "ALWAYS", 'd' => "BY DEFAULT", _ => (string?)null };
            var isGenerated = genChar == 's';
            var generatedExpr = isGenerated && !string.IsNullOrEmpty(defExpr) ? $"({defExpr})" : null;
            // A nextval(...) default is the signature of a serial column; treat it as such so it
            // matches a project's `serial`/`bigserial` rather than churning a default diff.
            var isSerial = !isIdentity && !isGenerated && defExpr is not null
                           && defExpr.StartsWith("nextval(", StringComparison.OrdinalIgnoreCase);

            def.Columns.Add(new ColumnDefinition(
                r.GetString(2), dataType, !notNull,
                Default: isGenerated || isSerial ? null : defExpr,
                IsIdentity: isIdentity,
                IdentityKind: identityKind,
                GeneratedExpression: generatedExpr,
                IsSerial: isSerial));
        }
        return (tables, byKey);
    }

    private async Task ReadConstraintsAsync(NpgsqlConnection conn, Dictionary<string, TableDefinition> tables, CancellationToken ct)
    {
        const string sql = @"
            SELECT n.nspname, c.relname, con.conname, con.contype, a.attname, k.ord
            FROM pg_constraint con
            JOIN pg_class c ON c.oid = con.conrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN LATERAL unnest(con.conkey) WITH ORDINALITY AS k(attnum, ord) ON true
            JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = k.attnum
            WHERE con.contype IN ('p','u')
              AND n.nspname NOT IN ('pg_catalog','information_schema')
            ORDER BY n.nspname, c.relname, con.conname, k.ord;";

        var pk = new Dictionary<(string, string, string), List<string>>();
        var uq = new Dictionary<(string, string, string), List<string>>();

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(0);
            var table = r.GetString(1);
            var name = r.GetString(2);
            var type = ReadChar(r, 3); // contype is the single-byte internal "char" type
            var col = r.GetString(4);
            var bucket = type == 'p' ? pk : uq;
            var keyTuple = (schema, table, name);
            if (!bucket.TryGetValue(keyTuple, out var list)) bucket[keyTuple] = list = new List<string>();
            list.Add(col);
        }

        foreach (var ((schema, table, name), cols) in pk)
            if (tables.TryGetValue($"{schema}.{table}", out var def))
                def.PrimaryKey = new PrimaryKeyDefinition(name, cols);

        foreach (var ((schema, table, name), cols) in uq)
            if (tables.TryGetValue($"{schema}.{table}", out var def))
                def.Unique.Add(new UniqueConstraintDefinition(name, cols));
    }

    private async Task ReadChecksAsync(NpgsqlConnection conn, Dictionary<string, TableDefinition> tables, CancellationToken ct)
    {
        const string sql = @"
            SELECT n.nspname, c.relname, con.conname, pg_get_constraintdef(con.oid) AS def
            FROM pg_constraint con
            JOIN pg_class c ON c.oid = con.conrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE con.contype = 'c'
              AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, c.relname, con.conname;";

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var key = $"{r.GetString(0)}.{r.GetString(1)}";
            if (!tables.TryGetValue(key, out var def)) continue;
            var name = r.GetString(2);
            var constraintDef = r.GetString(3); // e.g. "CHECK ((value > 0))"
            var expr = constraintDef.StartsWith("CHECK ", StringComparison.OrdinalIgnoreCase)
                ? constraintDef["CHECK ".Length..].Trim()
                : constraintDef;
            def.Checks.Add(new CheckConstraintDefinition(name, expr));
        }
    }

    private async Task ReadForeignKeysAsync(NpgsqlConnection conn, Dictionary<string, TableDefinition> tables, CancellationToken ct)
    {
        const string sql = @"
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

        var fks = new Dictionary<(string, string, string), (List<string> cols, string rs, string rt, List<string> refCols, char del, char upd)>();

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var keyTuple = (r.GetString(0), r.GetString(1), r.GetString(2));
            if (!fks.TryGetValue(keyTuple, out var acc))
            {
                acc = (new List<string>(), r.GetString(5), r.GetString(6), new List<string>(),
                       ReadChar(r, 8), ReadChar(r, 9));
                fks[keyTuple] = acc;
            }
            acc.cols.Add(r.GetString(3));
            acc.refCols.Add(r.GetString(7));
        }

        foreach (var ((schema, table, name), v) in fks)
            if (tables.TryGetValue($"{schema}.{table}", out var def))
                def.ForeignKeys.Add(new ForeignKeyDefinition(name, v.cols, v.rs, v.rt, v.refCols,
                    FkAction(v.del), FkAction(v.upd)));
    }

    // The catalog's char-type columns (contype, confdeltype, …) are the single-byte internal "char";
    // read defensively so a provider that surfaces it as a string doesn't blow up.
    private static char ReadChar(NpgsqlDataReader r, int ordinal, char fallback = 'a')
    {
        if (r.IsDBNull(ordinal)) return fallback;
        return r.GetValue(ordinal) switch
        {
            char c => c,
            string s => s.Length > 0 ? s[0] : fallback,
            _ => fallback,
        };
    }

    private static string? FkAction(char code) => code switch
    {
        'c' => "CASCADE",
        'n' => "SET NULL",
        'd' => "SET DEFAULT",
        'r' => "RESTRICT",
        _ => null, // 'a' = NO ACTION (the default — omit to keep scripts terse)
    };

    private async Task<List<IndexDefinition>> ReadIndexesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
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

        var list = new List<IndexDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var (cols, where) = ParseIndexDef(r.GetString(5));
            list.Add(new IndexDefinition(r.GetString(2), r.GetString(0), r.GetString(1), cols,
                r.GetBoolean(3), r.GetString(4), where));
        }
        return list;
    }

    /// <summary>Pulls the column list and optional WHERE out of a pg_get_indexdef() string.</summary>
    private static (List<string> Columns, string? Where) ParseIndexDef(string def)
    {
        var open = def.IndexOf('(');
        if (open < 0) return (new List<string>(), null);

        var depth = 0; var close = -1;
        for (var i = open; i < def.Length; i++)
        {
            if (def[i] == '(') depth++;
            else if (def[i] == ')') { depth--; if (depth == 0) { close = i; break; } }
        }
        if (close < 0) return (new List<string>(), null);

        var inner = def.Substring(open + 1, close - open - 1);
        var cols = inner.Split(',').Select(c => c.Trim()).Where(c => c.Length > 0).ToList();

        string? where = null;
        var wIdx = def.IndexOf(" WHERE ", close, StringComparison.OrdinalIgnoreCase);
        if (wIdx >= 0) where = def[(wIdx + 7)..].Trim();

        return (cols, where);
    }

    private async Task<List<ViewDefinition>> ReadViewsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
            SELECT n.nspname, c.relname, pg_get_viewdef(c.oid, true) AS def, c.relkind
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('v','m') AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, c.relname;";

        var list = new List<ViewDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new ViewDefinition(r.GetString(0), r.GetString(1), r.GetString(2), IsMaterialized: ReadChar(r, 3, 'v') == 'm'));
        return list;
    }

    private async Task<List<SequenceDefinition>> ReadSequencesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        // pg_sequences (PG 10+) exposes every configured option directly.
        const string sql = @"
            SELECT schemaname, sequencename, data_type::text,
                   increment_by, min_value, max_value, start_value, cache_size, cycle
            FROM pg_sequences
            WHERE schemaname NOT IN ('pg_catalog','information_schema') AND schemaname NOT LIKE 'pg_%'
            ORDER BY schemaname, sequencename;";

        var list = new List<SequenceDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new SequenceDefinition(
                r.GetString(0), r.GetString(1),
                DataType: r.IsDBNull(2) ? null : TypeNormalizer.Normalize(r.GetString(2)),
                Increment: r.IsDBNull(3) ? null : r.GetInt64(3),
                MinValue: r.IsDBNull(4) ? null : r.GetInt64(4),
                MaxValue: r.IsDBNull(5) ? null : r.GetInt64(5),
                Start: r.IsDBNull(6) ? null : r.GetInt64(6),
                Cache: r.IsDBNull(7) ? null : r.GetInt64(7),
                Cycle: !r.IsDBNull(8) && r.GetBoolean(8)));
        }
        return list;
    }

    private async Task<List<FunctionDefinition>> ReadFunctionsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
            SELECT n.nspname, p.proname,
                   pg_get_function_identity_arguments(p.oid) AS args,
                   pg_get_functiondef(p.oid) AS def
            FROM pg_proc p
            JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
              AND p.prokind IN ('f','p')
            ORDER BY n.nspname, p.proname;";

        var list = new List<FunctionDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(0);
            var name = r.GetString(1);
            var args = r.IsDBNull(2) ? string.Empty : r.GetString(2);
            var def = r.GetString(3);
            var argTypes = string.Join(", ", args.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(a => TypeNormalizer.Normalize(a.Trim())));
            list.Add(new FunctionDefinition(schema, name, $"{schema}.{name}({argTypes})", def, argTypes));
        }
        return list;
    }

    // ---- raw objects (each returns its own list; merged after the parallel reads) -----------

    private static RawObjectDefinition MakeRaw(ObjectKind kind, string schema, string name, string identity, string body, string? on = null, bool bodyComparable = true) =>
        new(kind, schema, name, identity.ToLowerInvariant(), body, on, bodyComparable);

    private async Task<List<RawObjectDefinition>> ReadCommentsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
            SELECT n.nspname, c.relname, a.attname, d.description, c.relkind
            FROM pg_description d
            JOIN pg_class c ON c.oid = d.objoid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = d.objsubid
            WHERE c.relkind IN ('r','p','v','m','f')
              AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, c.relname, d.objsubid;";

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(0);
            var rel = r.GetString(1);
            var col = r.IsDBNull(2) ? null : r.GetString(2);
            var desc = r.GetString(3).Replace("'", "''");
            var relkind = ReadChar(r, 4, 'r');
            // A view/matview/foreign-table needs its own COMMENT keyword — COMMENT ON TABLE rejects them.
            var relWord = relkind switch { 'v' => "VIEW", 'm' => "MATERIALIZED VIEW", 'f' => "FOREIGN TABLE", _ => "TABLE" };

            var (target, identity) = col is null
                ? ($"{relWord} {schema}.{rel}", $"comment:{relWord.ToLowerInvariant()} {schema}.{rel}")
                : ($"COLUMN {schema}.{rel}.{col}", $"comment:column {schema}.{rel}.{col}");
            list.Add(MakeRaw(ObjectKind.Comment, "", "", identity, $"COMMENT ON {target} IS '{desc}';"));
        }
        return list;
    }

    private async Task<List<RawObjectDefinition>> ReadExtensionsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand("SELECT extname FROM pg_extension ORDER BY extname;", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var name = r.GetString(0);
            list.Add(MakeRaw(ObjectKind.Extension, "", name, $"extension:{name}",
                $"CREATE EXTENSION IF NOT EXISTS {SqlEmitter.Quote(name)};"));
        }
        return list;
    }

    private async Task<List<RawObjectDefinition>> ReadEnumTypesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
            SELECT n.nspname, t.typname, e.enumlabel
            FROM pg_type t
            JOIN pg_namespace n ON n.oid = t.typnamespace
            JOIN pg_enum e ON e.enumtypid = t.oid
            WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, t.typname, e.enumsortorder;";

        var labels = new Dictionary<(string, string), List<string>>();
        await using (var cmd = new NpgsqlCommand(sql, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var key = (r.GetString(0), r.GetString(1));
                if (!labels.TryGetValue(key, out var l)) labels[key] = l = new List<string>();
                l.Add(r.GetString(2));
            }

        return labels.Select(kv =>
        {
            var ((schema, name), vals) = kv;
            var literals = string.Join(", ", vals.Select(v => "'" + v.Replace("'", "''") + "'"));
            return MakeRaw(ObjectKind.Type, schema, name, $"type:{schema}.{name}",
                $"CREATE TYPE {schema}.{name} AS ENUM ({literals});");
        }).ToList();
    }

    private async Task<List<RawObjectDefinition>> ReadCompositeTypesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
            SELECT n.nspname, t.typname, a.attname, format_type(a.atttypid, a.atttypmod)
            FROM pg_type t
            JOIN pg_namespace n ON n.oid = t.typnamespace
            JOIN pg_class c ON c.oid = t.typrelid
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
            WHERE t.typtype = 'c' AND c.relkind = 'c'
              AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, t.typname, a.attnum;";

        var attrs = new Dictionary<(string, string), List<string>>();
        await using (var cmd = new NpgsqlCommand(sql, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var key = (r.GetString(0), r.GetString(1));
                if (!attrs.TryGetValue(key, out var l)) attrs[key] = l = new List<string>();
                l.Add($"{r.GetString(2)} {r.GetString(3)}");
            }

        return attrs.Select(kv =>
        {
            var ((schema, name), cols) = kv;
            return MakeRaw(ObjectKind.Type, schema, name, $"type:{schema}.{name}",
                $"CREATE TYPE {schema}.{name} AS ({string.Join(", ", cols)});");
        }).ToList();
    }

    private async Task<List<RawObjectDefinition>> ReadRangeTypesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
            SELECT n.nspname, t.typname, format_type(r.rngsubtype, NULL) AS subtype
            FROM pg_range r
            JOIN pg_type t ON t.oid = r.rngtypid
            JOIN pg_namespace n ON n.oid = t.typnamespace
            WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, t.typname;";

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(0);
            var name = r.GetString(1);
            list.Add(MakeRaw(ObjectKind.Type, schema, name, $"type:{schema}.{name}",
                $"CREATE TYPE {schema}.{name} AS RANGE (SUBTYPE = {r.GetString(2)});"));
        }
        return list;
    }

    // Shell types: a bare `CREATE TYPE name;` (typisdefined = false). Defined enum/composite/range/base
    // types are excluded by the typisdefined filter.
    private async Task<List<RawObjectDefinition>> ReadShellTypesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
            SELECT n.nspname, t.typname
            FROM pg_type t
            JOIN pg_namespace n ON n.oid = t.typnamespace
            WHERE NOT t.typisdefined AND t.typtype <> 'b'
              AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, t.typname;";

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(0);
            var name = r.GetString(1);
            list.Add(MakeRaw(ObjectKind.Type, schema, name, $"type:{schema}.{name}", $"CREATE TYPE {schema}.{name};"));
        }
        return list;
    }

    private async Task<List<RawObjectDefinition>> ReadCollationsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
            SELECT n.nspname, c.collname, c.collprovider, c.collisdeterministic,
                   c.collcollate, c.collctype, c.colllocale
            FROM pg_collation c
            JOIN pg_namespace n ON n.oid = c.collnamespace
            WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, c.collname;";

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(0);
            var name = r.GetString(1);
            var provider = ReadChar(r, 2, 'd') switch { 'i' => "icu", 'c' => "libc", 'b' => "builtin", _ => null };
            var deterministic = !r.IsDBNull(3) && r.GetBoolean(3);
            var collate = r.IsDBNull(4) ? null : r.GetString(4);
            var ctype = r.IsDBNull(5) ? null : r.GetString(5);
            var locale = r.IsDBNull(6) ? null : r.GetString(6);

            var opts = new List<string>();
            if (provider is not null) opts.Add($"PROVIDER = {provider}");
            var loc = locale ?? (collate is not null && collate == ctype ? collate : null);
            if (loc is not null) opts.Add($"LOCALE = '{loc.Replace("'", "''")}'");
            else
            {
                if (collate is not null) opts.Add($"LC_COLLATE = '{collate.Replace("'", "''")}'");
                if (ctype is not null) opts.Add($"LC_CTYPE = '{ctype.Replace("'", "''")}'");
            }
            if (!deterministic) opts.Add("DETERMINISTIC = false");

            list.Add(MakeRaw(ObjectKind.Collation, schema, name, $"collation:{schema}.{name}",
                $"CREATE COLLATION {schema}.{name} ({string.Join(", ", opts)});"));
        }
        return list;
    }

    private async Task<List<RawObjectDefinition>> ReadAggregatesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
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

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(0);
            var name = r.GetString(1);
            var args = r.IsDBNull(2) || r.GetString(2).Length == 0 ? "*" : r.GetString(2);
            var opts = new List<string> { $"SFUNC = {r.GetString(3)}", $"STYPE = {r.GetString(4)}" };
            if (!r.IsDBNull(5)) opts.Add($"FINALFUNC = {r.GetString(5)}");
            if (!r.IsDBNull(6)) opts.Add($"COMBINEFUNC = {r.GetString(6)}");
            if (!r.IsDBNull(7)) opts.Add($"INITCOND = '{r.GetString(7).Replace("'", "''")}'");

            list.Add(MakeRaw(ObjectKind.Aggregate, schema, name, $"aggregate:{schema}.{name}({NormalizeArgs(args)})",
                $"CREATE AGGREGATE {schema}.{name} ({args}) ({string.Join(", ", opts)});"));
        }
        return list;
    }

    private static string NormalizeArgs(string args) => args.Trim();

    private async Task<List<RawObjectDefinition>> ReadDomainsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
            SELECT n.nspname, t.typname, format_type(t.typbasetype, t.typtypmod) AS basetype,
                   t.typnotnull, t.typdefault,
                   (SELECT string_agg(pg_get_constraintdef(c.oid), ' ')
                      FROM pg_constraint c WHERE c.contypid = t.oid AND c.contype = 'c') AS checks
            FROM pg_type t
            JOIN pg_namespace n ON n.oid = t.typnamespace
            WHERE t.typtype = 'd'
              AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, t.typname;";

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(0);
            var name = r.GetString(1);
            var baseType = r.GetString(2);
            var notNull = r.GetBoolean(3);
            var def = r.IsDBNull(4) ? null : r.GetString(4);
            var checks = r.IsDBNull(5) ? null : r.GetString(5);

            var body = $"CREATE DOMAIN {schema}.{name} AS {baseType}";
            if (notNull) body += " NOT NULL";
            if (!string.IsNullOrWhiteSpace(def)) body += $" DEFAULT {def}";
            if (!string.IsNullOrWhiteSpace(checks)) body += $" {checks}";
            list.Add(MakeRaw(ObjectKind.Domain, schema, name, $"domain:{schema}.{name}", body + ";"));
        }
        return list;
    }

    private async Task<List<RawObjectDefinition>> ReadTriggersAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
            SELECT n.nspname, c.relname, t.tgname, pg_get_triggerdef(t.oid, true) AS def
            FROM pg_trigger t
            JOIN pg_class c ON c.oid = t.tgrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE NOT t.tgisinternal
              AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, c.relname, t.tgname;";

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(0);
            var on = $"{schema}.{r.GetString(1)}";
            var name = r.GetString(2);
            list.Add(MakeRaw(ObjectKind.Trigger, schema, name, $"trigger:{name} on {on}", r.GetString(3), on));
        }
        return list;
    }

    private async Task<List<RawObjectDefinition>> ReadRulesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
            SELECT n.nspname, c.relname, r.rulename, pg_get_ruledef(r.oid, true) AS def
            FROM pg_rewrite r
            JOIN pg_class c ON c.oid = r.ev_class
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE r.rulename <> '_RETURN'
              AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, c.relname, r.rulename;";

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(0);
            var on = $"{schema}.{r.GetString(1)}";
            var name = r.GetString(2);
            list.Add(MakeRaw(ObjectKind.Rule, schema, name, $"rule:{name} on {on}", r.GetString(3), on));
        }
        return list;
    }

    private async Task<List<RawObjectDefinition>> ReadPoliciesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
            SELECT n.nspname, c.relname, pol.polname, pol.polcmd, pol.polpermissive,
                   pg_get_expr(pol.polqual, pol.polrelid) AS using_expr,
                   pg_get_expr(pol.polwithcheck, pol.polrelid) AS check_expr
            FROM pg_policy pol
            JOIN pg_class c ON c.oid = pol.polrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, c.relname, pol.polname;";

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(0);
            var on = $"{schema}.{r.GetString(1)}";
            var name = r.GetString(2);
            var cmdLetter = ReadChar(r, 3, '*');
            var permissive = !r.IsDBNull(4) && r.GetBoolean(4);
            var usingExpr = r.IsDBNull(5) ? null : r.GetString(5);
            var checkExpr = r.IsDBNull(6) ? null : r.GetString(6);

            var forCmd = cmdLetter switch { 'r' => "SELECT", 'a' => "INSERT", 'w' => "UPDATE", 'd' => "DELETE", _ => "ALL" };
            var body = $"CREATE POLICY {name} ON {on} AS {(permissive ? "PERMISSIVE" : "RESTRICTIVE")} FOR {forCmd}";
            if (!string.IsNullOrWhiteSpace(usingExpr)) body += $" USING ({usingExpr})";
            if (!string.IsNullOrWhiteSpace(checkExpr)) body += $" WITH CHECK ({checkExpr})";
            // Roles (TO ...) are omitted from this reconstruction, so don't body-compare.
            list.Add(MakeRaw(ObjectKind.Policy, schema, name, $"policy:{name} on {on}", body + ";", on, bodyComparable: false));
        }
        return list;
    }

    private async Task<List<RawObjectDefinition>> ReadEventTriggersAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
            SELECT e.evtname, e.evtevent, np.nspname, p.proname
            FROM pg_event_trigger e
            JOIN pg_proc p ON p.oid = e.evtfoid
            JOIN pg_namespace np ON np.oid = p.pronamespace
            ORDER BY e.evtname;";

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var name = r.GetString(0);
            var body = $"CREATE EVENT TRIGGER {name} ON {r.GetString(1)} EXECUTE FUNCTION {r.GetString(2)}.{r.GetString(3)}();";
            list.Add(MakeRaw(ObjectKind.EventTrigger, "", name, $"eventtrigger:{name}", body, bodyComparable: false));
        }
        return list;
    }

    // Existence-only introspection for kinds without a clean reconstruction yet: record the identity
    // (so live compare/extract know they exist) with no body, so they are never spuriously recreated
    // or body-diffed. Runs its small queries on one connection (this whole method is one parallel task).
    private async Task<List<RawObjectDefinition>> ReadExistenceObjectsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var list = new List<RawObjectDefinition>();
        async Task Schema(ObjectKind kind, string tag, string baseSql)
        {
            var sql = baseSql + (baseSql.Contains("WHERE", StringComparison.OrdinalIgnoreCase) ? " AND" : " WHERE")
                      + " n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'";
            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(MakeRaw(kind, r.GetString(0), r.GetString(1), $"{tag}:{r.GetString(0)}.{r.GetString(1)}", string.Empty, bodyComparable: false));
        }
        async Task Global(ObjectKind kind, string tag, string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(MakeRaw(kind, "", r.GetString(0), $"{tag}:{r.GetString(0)}", string.Empty, bodyComparable: false));
        }

        // Conversion, FDW, Server, Cast, and column-based Statistics now have real DDL reconstruction
        // (below); the rest stay existence-only until they get a clean reconstruction too.
        // Expression statistics (stxexprs set) we don't reconstruct yet → keep existence-only.
        await Schema(ObjectKind.Statistics, "statistics",
            "SELECT n.nspname, s.stxname FROM pg_statistic_ext s JOIN pg_namespace n ON n.oid=s.stxnamespace WHERE s.stxexprs IS NOT NULL");
        return list;
    }

    // ---- operators + text search: full DDL reconstruction (was existence-only / unread) -------

    private async Task<List<RawObjectDefinition>> ReadOperatorsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
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

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(0);
            var op = r.GetString(1);
            var left = r.IsDBNull(2) ? null : r.GetString(2);
            var right = r.IsDBNull(3) ? null : r.GetString(3);

            var opts = new List<string> { $"FUNCTION = {r.GetString(4)}" };
            if (left is not null) opts.Add($"LEFTARG = {left}");
            if (right is not null) opts.Add($"RIGHTARG = {right}");
            if (!r.IsDBNull(5)) opts.Add($"COMMUTATOR = OPERATOR({r.GetString(5)})");
            if (!r.IsDBNull(6)) opts.Add($"NEGATOR = OPERATOR({r.GetString(6)})");
            if (!r.IsDBNull(7)) opts.Add($"RESTRICT = {r.GetString(7)}");
            if (!r.IsDBNull(8)) opts.Add($"JOIN = {r.GetString(8)}");
            if (!r.IsDBNull(9) && r.GetBoolean(9)) opts.Add("MERGES");
            if (!r.IsDBNull(10) && r.GetBoolean(10)) opts.Add("HASHES");

            // Name carries the DROP OPERATOR target shape: name (lefttype, righttype) with NONE for unary.
            var dropName = $"{schema}.{op} ({left ?? "NONE"}, {right ?? "NONE"})";
            var body = $"CREATE OPERATOR {schema}.{op} ({string.Join(", ", opts)});";
            list.Add(MakeRaw(ObjectKind.Operator, "", dropName, $"operator:{schema}.{op}({left},{right})", body));
        }
        return list;
    }

    // Standalone operator families only — families PostgreSQL auto-creates for a bare CREATE OPERATOR
    // CLASS (the class carries an 'a' dep on them) are skipped; that class re-creates its family itself.
    private async Task<List<RawObjectDefinition>> ReadOperatorFamiliesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
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

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(0);
            var name = r.GetString(1);
            var method = r.GetString(2);
            list.Add(MakeRaw(ObjectKind.OperatorFamily, "", $"{schema}.{name} USING {method}",
                $"operatorfamily:{schema}.{name} using {method}",
                $"CREATE OPERATOR FAMILY {schema}.{name} USING {method};"));
        }
        return list;
    }

    private async Task<List<RawObjectDefinition>> ReadOperatorClassesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
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

        var headers = new List<(string Schema, string Name, bool Default, string IntType, string Method,
                                uint Family, uint OpcIntType, string FamSchema, string FamName, bool AutoFam)>();
        await using (var cmd = new NpgsqlCommand(sql, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                headers.Add((r.GetString(0), r.GetString(1), r.GetBoolean(2), r.GetString(3), r.GetString(4),
                             r.GetFieldValue<uint>(5), r.GetFieldValue<uint>(6), r.GetString(7), r.GetString(8), r.GetBoolean(9)));

        var list = new List<RawObjectDefinition>();
        foreach (var h in headers)
        {
            var members = new List<string>();

            const string amopSql = @"
                SELECT amopstrategy, amopopr::regoperator::text, amoppurpose,
                       NULLIF(amopsortfamily,0)::regclass::text
                FROM pg_amop
                WHERE amopfamily=@fam AND amoplefttype=@t AND amoprighttype=@t
                ORDER BY amoppurpose, amopstrategy;";
            await using (var oc = new NpgsqlCommand(amopSql, conn))
            {
                oc.Parameters.AddWithValue("fam", NpgsqlTypes.NpgsqlDbType.Oid, h.Family);
                oc.Parameters.AddWithValue("t", NpgsqlTypes.NpgsqlDbType.Oid, h.OpcIntType);
                await using var or = await oc.ExecuteReaderAsync(ct);
                while (await or.ReadAsync(ct))
                {
                    var opr = or.GetString(1);
                    var paren = opr.IndexOf('(');                       // "<(integer,integer)" -> "< (integer,integer)"
                    var named = paren > 0 ? opr[..paren] + " " + opr[paren..] : opr;
                    var orderBy = ReadChar(or, 2, 's') == 'o' && !or.IsDBNull(3)
                        ? $" FOR ORDER BY {or.GetString(3)}" : "";
                    members.Add($"OPERATOR {or.GetInt32(0)} {named}{orderBy}");
                }
            }

            const string amprocSql = @"
                SELECT amprocnum, amproc::regprocedure::text
                FROM pg_amproc
                WHERE amprocfamily=@fam AND amproclefttype=@t AND amprocrighttype=@t
                ORDER BY amprocnum;";
            await using (var pc = new NpgsqlCommand(amprocSql, conn))
            {
                pc.Parameters.AddWithValue("fam", NpgsqlTypes.NpgsqlDbType.Oid, h.Family);
                pc.Parameters.AddWithValue("t", NpgsqlTypes.NpgsqlDbType.Oid, h.OpcIntType);
                await using var pr = await pc.ExecuteReaderAsync(ct);
                while (await pr.ReadAsync(ct))
                    members.Add($"FUNCTION {pr.GetInt32(0)} {pr.GetString(1)}");
            }

            var header = $"CREATE OPERATOR CLASS {h.Schema}.{h.Name}{(h.Default ? " DEFAULT" : "")} " +
                         $"FOR TYPE {h.IntType} USING {h.Method}" +
                         (h.AutoFam ? "" : $" FAMILY {h.FamSchema}.{h.FamName}") + " AS\n    ";
            var body = header + string.Join(",\n    ", members) + ";";
            list.Add(MakeRaw(ObjectKind.OperatorClass, "", $"{h.Schema}.{h.Name} USING {h.Method}",
                $"operatorclass:{h.Schema}.{h.Name} using {h.Method}", body));
        }
        return list;
    }

    private async Task<List<RawObjectDefinition>> ReadTextSearchDictionariesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
            SELECT n.nspname, d.dictname, tn.nspname||'.'||t.tmplname AS template, d.dictinitoption
            FROM pg_ts_dict d
            JOIN pg_namespace n  ON n.oid  = d.dictnamespace
            JOIN pg_ts_template t ON t.oid = d.dicttemplate
            JOIN pg_namespace tn ON tn.oid = t.tmplnamespace
            WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, d.dictname;";

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(0);
            var name = r.GetString(1);
            var opts = $"TEMPLATE = {r.GetString(2)}";
            if (!r.IsDBNull(3) && r.GetString(3).Length > 0) opts += $", {r.GetString(3)}";
            var body = $"CREATE TEXT SEARCH DICTIONARY {schema}.{name} ({opts});";
            list.Add(MakeRaw(ObjectKind.TextSearchDictionary, schema, name, $"textsearchdictionary:{schema}.{name}", body));
        }
        return list;
    }

    private async Task<List<RawObjectDefinition>> ReadTextSearchConfigurationsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        // Pass 1: the configurations (and their parser). Read fully before issuing per-config map queries.
        const string cfgSql = @"
            SELECT n.nspname, c.cfgname, c.oid, c.cfgparser,
                   pn.nspname||'.'||p.prsname AS parser
            FROM pg_ts_config c
            JOIN pg_namespace n  ON n.oid  = c.cfgnamespace
            JOIN pg_ts_parser p  ON p.oid  = c.cfgparser
            JOIN pg_namespace pn ON pn.oid = p.prsnamespace
            WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, c.cfgname;";

        var configs = new List<(string Schema, string Name, uint Oid, uint Parser, string ParserName)>();
        await using (var cmd = new NpgsqlCommand(cfgSql, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                configs.Add((r.GetString(0), r.GetString(1), r.GetFieldValue<uint>(2), r.GetFieldValue<uint>(3), r.GetString(4)));

        var list = new List<RawObjectDefinition>();
        foreach (var c in configs)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"CREATE TEXT SEARCH CONFIGURATION {c.Schema}.{c.Name} (PARSER = {c.ParserName});");

            // Pass 2: token-type → dictionary-list mappings, one ADD MAPPING per token type.
            const string mapSql = @"
                SELECT tt.alias, string_agg(dn.nspname||'.'||d.dictname, ', ' ORDER BY m.mapseqno) AS dicts
                FROM pg_ts_config_map m
                JOIN pg_ts_dict d ON d.oid = m.mapdict
                JOIN pg_namespace dn ON dn.oid = d.dictnamespace
                JOIN ts_token_type(@parser) tt ON tt.tokid = m.maptokentype
                WHERE m.mapcfg = @cfg
                GROUP BY tt.alias, m.maptokentype
                ORDER BY m.maptokentype;";
            await using var mc = new NpgsqlCommand(mapSql, conn);
            mc.Parameters.AddWithValue("parser", NpgsqlTypes.NpgsqlDbType.Oid, c.Parser);
            mc.Parameters.AddWithValue("cfg", NpgsqlTypes.NpgsqlDbType.Oid, c.Oid);
            await using (var mr = await mc.ExecuteReaderAsync(ct))
                while (await mr.ReadAsync(ct))
                    sb.Append($"\nALTER TEXT SEARCH CONFIGURATION {c.Schema}.{c.Name} ADD MAPPING FOR {mr.GetString(0)} WITH {mr.GetString(1)};");

            list.Add(MakeRaw(ObjectKind.TextSearchConfiguration, c.Schema, c.Name,
                $"textsearchconfiguration:{c.Schema}.{c.Name}", sb.ToString()));
        }
        return list;
    }

    // ---- foreign-data + conversion: full DDL reconstruction (was existence-only) -------------

    private async Task<List<RawObjectDefinition>> ReadConversionsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
            SELECT n.nspname, c.conname,
                   pg_encoding_to_char(c.conforencoding) AS src,
                   pg_encoding_to_char(c.contoencoding) AS dst,
                   c.conproc::regproc::text AS func,
                   c.condefault
            FROM pg_conversion c
            JOIN pg_namespace n ON n.oid = c.connamespace
            WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, c.conname;";

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(0);
            var name = r.GetString(1);
            var keyword = !r.IsDBNull(5) && r.GetBoolean(5) ? "CREATE DEFAULT CONVERSION" : "CREATE CONVERSION";
            var body = $"{keyword} {schema}.{name} FOR '{r.GetString(2)}' TO '{r.GetString(3)}' FROM {r.GetString(4)};";
            list.Add(MakeRaw(ObjectKind.Conversion, schema, name, $"conversion:{schema}.{name}", body));
        }
        return list;
    }

    private async Task<List<RawObjectDefinition>> ReadForeignDataWrappersAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
            SELECT w.fdwname,
                   NULLIF(w.fdwhandler,0)::regproc::text AS handler,
                   NULLIF(w.fdwvalidator,0)::regproc::text AS validator,
                   w.fdwoptions
            FROM pg_foreign_data_wrapper w
            ORDER BY w.fdwname;";

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var name = r.GetString(0);
            var body = $"CREATE FOREIGN DATA WRAPPER {name}";
            if (!r.IsDBNull(1)) body += $" HANDLER {r.GetString(1)}";
            if (!r.IsDBNull(2)) body += $" VALIDATOR {r.GetString(2)}";
            body += OptionsClause(r.IsDBNull(3) ? null : r.GetFieldValue<string[]>(3));
            list.Add(MakeRaw(ObjectKind.ForeignDataWrapper, "", name, $"foreigndatawrapper:{name}", body + ";"));
        }
        return list;
    }

    private async Task<List<RawObjectDefinition>> ReadServersAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
            SELECT s.srvname, w.fdwname, s.srvtype, s.srvversion, s.srvoptions
            FROM pg_foreign_server s
            JOIN pg_foreign_data_wrapper w ON w.oid = s.srvfdw
            ORDER BY s.srvname;";

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var name = r.GetString(0);
            var body = $"CREATE SERVER {name}";
            if (!r.IsDBNull(2)) body += $" TYPE '{r.GetString(2).Replace("'", "''")}'";
            if (!r.IsDBNull(3)) body += $" VERSION '{r.GetString(3).Replace("'", "''")}'";
            body += $" FOREIGN DATA WRAPPER {r.GetString(1)}";
            body += OptionsClause(r.IsDBNull(4) ? null : r.GetFieldValue<string[]>(4));
            list.Add(MakeRaw(ObjectKind.Server, "", name, $"server:{name}", body + ";"));
        }
        return list;
    }

    private async Task<List<RawObjectDefinition>> ReadStatisticsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        // Column-based extended statistics only (stxexprs IS NULL); expression stats stay existence-only.
        const string sql = @"
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

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(0);
            var name = r.GetString(1);
            var tbl = r.GetString(2);
            var cols = r.IsDBNull(3) ? "" : r.GetString(3);
            var kinds = r.IsDBNull(4) ? Array.Empty<string>() : r.GetFieldValue<string[]>(4);
            var kindList = kinds.Length > 0 ? $" ({string.Join(", ", kinds)})" : "";
            var body = $"CREATE STATISTICS {schema}.{name}{kindList} ON {cols} FROM {tbl};";
            list.Add(MakeRaw(ObjectKind.Statistics, schema, name, $"statistics:{schema}.{name}", body));
        }
        return list;
    }

    private async Task<List<RawObjectDefinition>> ReadCastsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        // User casts only: those touching a user-schema type or function (built-in casts are excluded).
        const string sql = @"
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

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var src = r.GetString(0);
            var tgt = r.GetString(1);
            var method = ReadChar(r, 4, 'f');
            var with = method switch
            {
                'b' => "WITHOUT FUNCTION",
                'i' => "WITH INOUT",
                _ => $"WITH FUNCTION {r.GetString(2)}",
            };
            var context = ReadChar(r, 3, 'e') switch { 'a' => " AS ASSIGNMENT", 'i' => " AS IMPLICIT", _ => "" };
            var name = $"({src} AS {tgt})";   // also the DROP CAST target shape
            var body = $"CREATE CAST {name} {with}{context};";
            list.Add(MakeRaw(ObjectKind.Cast, "", name, $"cast:{src}->{tgt}", body));
        }
        return list;
    }

    private async Task<List<RawObjectDefinition>> ReadForeignTablesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
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

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(0);
            var name = r.GetString(1);
            var server = r.GetString(2);
            var cols = r.IsDBNull(4) ? "" : r.GetString(4);
            var body = $"CREATE FOREIGN TABLE {schema}.{name} ({cols}) SERVER {server}"
                       + OptionsClause(r.IsDBNull(3) ? null : r.GetFieldValue<string[]>(3)) + ";";
            list.Add(MakeRaw(ObjectKind.ForeignTable, schema, name, $"foreigntable:{schema}.{name}", body));
        }
        return list;
    }

    private async Task<List<RawObjectDefinition>> ReadPublicationsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
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

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var name = r.GetString(0);
            var body = new System.Text.StringBuilder($"CREATE PUBLICATION {name}");

            if (r.GetBoolean(1))
            {
                body.Append(" FOR ALL TABLES");
            }
            else
            {
                var fors = new List<string>();
                if (!r.IsDBNull(7)) fors.Add($"TABLE {r.GetString(7)}");
                if (!r.IsDBNull(8)) fors.Add($"TABLES IN SCHEMA {r.GetString(8)}");
                if (fors.Count > 0) body.Append(" FOR ").Append(string.Join(", ", fors));
            }

            var ops = new List<string>();
            if (r.GetBoolean(2)) ops.Add("insert");
            if (r.GetBoolean(3)) ops.Add("update");
            if (r.GetBoolean(4)) ops.Add("delete");
            if (r.GetBoolean(5)) ops.Add("truncate");
            var with = new List<string> { $"publish = '{string.Join(", ", ops)}'" };
            if (r.GetBoolean(6)) with.Add("publish_via_partition_root = true");
            body.Append($" WITH ({string.Join(", ", with)});");

            list.Add(MakeRaw(ObjectKind.Publication, "", name, $"publication:{name}".ToLowerInvariant(), body.ToString()));
        }
        return list;
    }

    // pg_*options is text[] of "key=value"; render as an OPTIONS (key 'value', …) clause.
    private static string OptionsClause(string[]? opts)
    {
        if (opts is null || opts.Length == 0) return string.Empty;
        var parts = opts.Select(o =>
        {
            var eq = o.IndexOf('=');
            var key = eq >= 0 ? o[..eq] : o;
            var val = eq >= 0 ? o[(eq + 1)..] : string.Empty;
            return $"{key} '{val.Replace("'", "''")}'";
        });
        return $" OPTIONS ({string.Join(", ", parts)})";
    }
}
