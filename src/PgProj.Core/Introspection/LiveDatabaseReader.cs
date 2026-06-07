using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using PgProj.Core.Comparison;
using PgProj.Core.Model;
using PgProj.Core.Versioning;

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

    // The catalog SQL this reader issues is sourced from the active version profile's CatalogQueries —
    // no SQL literals live in this file anymore. Defaults to the latest profile; pass an older profile
    // (or one selected from a project's TargetPostgresVersion) to introspect with that version's queries.
    private readonly CatalogQueries _q;

    public LiveDatabaseReader() : this(PostgresVersionProfile.Latest) { }

    public LiveDatabaseReader(PostgresVersionProfile profile) => _q = profile.CatalogQueries;

    /// <summary>One raw-object introspection read: returns its objects for the connection it is given.</summary>
    private delegate Task<List<RawObjectDefinition>> RawObjectReader(NpgsqlConnection conn, CancellationToken ct);

    /// <summary>
    /// The raw-object reader registry (issue #44): the single, ordered list of every raw-object read the
    /// introspector runs. Adding a raw kind = add its reader here (its catalog SQL lives on the
    /// <see cref="PostgresVersionProfile"/>, its metadata in <see cref="Extensibility.ObjectKindRegistry"/>) —
    /// no edit to <see cref="ReadAsync"/>'s fan-out. (Readers don't map 1:1 to kinds — e.g. enum/composite/
    /// range/shell all yield <c>Type</c>, and existence-objects yield several — so this is a reader list,
    /// not a per-kind dispatch.)
    /// </summary>
    private RawObjectReader[] RawObjectReaders =>
    [
        ReadExtensionsAsync, ReadTypedTablesAsync, ReadEnumTypesAsync, ReadCompositeTypesAsync,
        ReadRangeTypesAsync, ReadShellTypesAsync, ReadCollationsAsync, ReadAggregatesAsync,
        ReadDomainsAsync, ReadTriggersAsync, ReadRulesAsync, ReadPoliciesAsync, ReadEventTriggersAsync,
        ReadCommentsAsync, ReadConversionsAsync, ReadForeignDataWrappersAsync, ReadServersAsync,
        ReadStatisticsAsync, ReadCastsAsync, ReadForeignTablesAsync, ReadOperatorsAsync,
        ReadOperatorFamiliesAsync, ReadOperatorClassesAsync, ReadTextSearchDictionariesAsync,
        ReadTextSearchConfigurationsAsync, ReadPublicationsAsync, ReadExistenceObjectsAsync,
    ];

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

        // Raw-object reads come from the reader registry (issue #44) — one entry per reader, the single
        // place a new raw kind's introspection is registered. Each runs on its own pooled connection
        // under the concurrency gate; merge order doesn't matter (model.Objects is sorted canonically
        // after merge, #59).
        var readers = RawObjectReaders;
        var rawTasks = new Task<List<RawObjectDefinition>>[readers.Length];
        for (var i = 0; i < readers.Length; i++)
        {
            var reader = readers[i];
            rawTasks[i] = Read(c => reader(c, ct));
        }

        // ---- wave 2: table-dependent reads, started as soon as the table map is ready ----
        // INVARIANT: these run concurrently and mutate the SAME shared TableDefinition objects, so
        // each must write a DISJOINT member — constraints→PrimaryKey/Unique, checks→Checks,
        // fks→ForeignKeys. They never touch the same list, so no lock is needed. A new wave-2 reader
        // that writes a member another already writes would be a data race: gate it or give it its
        // own wave.
        var (tables, byKey) = await tablesTask;
        var constraintsTask = ReadVoid(c => ReadConstraintsAsync(c, byKey, ct));
        var checksTask      = ReadVoid(c => ReadChecksAsync(c, byKey, ct));
        var fksTask         = ReadVoid(c => ReadForeignKeysAsync(c, byKey, ct));
        var excludesTask    = ReadVoid(c => ReadExcludeConstraintsAsync(c, byKey, ct));

        await Task.WhenAll(rawTasks);
        await Task.WhenAll(schemasTask, indexesTask, viewsTask, sequencesTask, functionsTask,
                           constraintsTask, checksTask, fksTask, excludesTask);

        // ---- merge (single-threaded) ----
        var model = new DatabaseModel();
        model.Schemas.AddRange(schemasTask.Result);
        model.Tables.AddRange(tables);
        model.Indexes.AddRange(indexesTask.Result);
        model.Views.AddRange(viewsTask.Result);
        model.Sequences.AddRange(sequencesTask.Result);
        model.Functions.AddRange(functionsTask.Result);
        foreach (var t in rawTasks) model.Objects.AddRange(t.Result);

        // DETERMINISM (issue #59): the raw reads above fan out across ~25 parallel tasks and a few
        // build their lists from Dictionary enumeration (enum/composite types), so the merged
        // `Objects` order is not stable run-to-run or machine-to-machine. Impose a total, culture-
        // independent order so the introspected model — and everything derived from it (canonical
        // form, hash, diff, deploy script) — is byte-reproducible. Phase ordering still dominates the
        // generated script (the comparer sorts by phase, stably), so this only canonicalises the
        // within-phase tie-break; it cannot reorder a dependency across phases.
        model.Objects.Sort(CompareCanonical);
        return model;
    }

    /// <summary>
    /// Total, culture-independent ordering for raw objects: (kind, schema, name, identity). Used to
    /// canonicalise the parallel-merged <see cref="DatabaseModel.Objects"/> so introspection is
    /// byte-reproducible. <see cref="RawObjectDefinition.Identity"/> is unique, so this is a strict
    /// total order — the final tie-break never falls through. Ordinal string compares keep it
    /// identical regardless of the current culture.
    /// </summary>
    public static int CompareCanonical(RawObjectDefinition a, RawObjectDefinition b)
    {
        var c = ((int)a.Kind).CompareTo((int)b.Kind);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.Schema, b.Schema);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.Name, b.Name);
        if (c != 0) return c;
        return string.CompareOrdinal(a.Identity, b.Identity);
    }

    private async Task<List<SchemaDefinition>> ReadSchemasAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var sql = _q.Schemas;
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
        var sql = _q.TablesAndColumns;

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
        var sql = _q.Constraints;

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
        var sql = _q.Checks;

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

    // EXCLUDE constraints (#98): contype 'x', reconstructed verbatim via pg_get_constraintdef into
    // TableDefinition.OtherConstraints — the same slot the parser uses, so an unchanged EXCLUDE produces no
    // diff on a project→live compare. Wave-2 reader: writes ONLY OtherConstraints (disjoint from the
    // PK/Unique/Check/FK members the other wave-2 readers touch), so it needs no lock.
    private async Task ReadExcludeConstraintsAsync(NpgsqlConnection conn, Dictionary<string, TableDefinition> tables, CancellationToken ct)
    {
        var sql = _q.ExcludeConstraints;

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var key = $"{r.GetString(0)}.{r.GetString(1)}";
            if (!tables.TryGetValue(key, out var def)) continue;
            // pg_get_constraintdef returns the bare "EXCLUDE USING …"; prefix the constraint name so a
            // named source clause ("CONSTRAINT room_no_overlap EXCLUDE …") round-trips without a phantom diff.
            def.OtherConstraints.Add($"CONSTRAINT {r.GetString(2)} {r.GetString(3)}");
        }
    }

    private async Task ReadForeignKeysAsync(NpgsqlConnection conn, Dictionary<string, TableDefinition> tables, CancellationToken ct)
    {
        var sql = _q.ForeignKeys;

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
        var sql = _q.Indexes;

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
        var sql = _q.Views;

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
        var sql = _q.Sequences;

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
        var sql = _q.Functions;

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
        // The query (issue #61) returns a uniform (target, description) per comment across ALL object classes
        // — relation/column/schema/function/procedure/type/domain/trigger — where `target` is the exact
        // `<KIND> <name>` a hand-written COMMENT ON would carry. The comparer pairs comments on their
        // canonical body (RawObjectMeta.ComparisonKey), so the identity here is informational; we still build
        // it in the parser's `comment:<normalized target>` shape for readability/extract file naming.
        var sql = _q.Comments;

        var list = new List<RawObjectDefinition>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var target = r.GetString(0);
            if (r.IsDBNull(1)) continue;
            var desc = r.GetString(1).Replace("'", "''");
            var identity = $"comment:{target.ToLowerInvariant()}";
            if (!seen.Add(identity)) continue; // de-dup the two schema-comment branches (shared vs local catalog)
            list.Add(MakeRaw(ObjectKind.Comment, "", "", identity, $"COMMENT ON {target} IS '{desc}';"));
        }
        return list;
    }

    private async Task<List<RawObjectDefinition>> ReadExtensionsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(_q.Extensions, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var name = r.GetString(0);
            list.Add(MakeRaw(ObjectKind.Extension, "", name, $"extension:{name}",
                $"CREATE EXTENSION IF NOT EXISTS {SqlEmitter.Quote(name)};"));
        }
        return list;
    }

    // Typed tables: CREATE TABLE … OF <composite type>. The columns are dictated by the type, so the
    // reconstruction omits the column list (matching the source form) and adds only table-level pieces
    // the type itself doesn't carry — currently the PRIMARY KEY. Emitted as a raw `table:` object (same
    // modelling the parser uses for the source form) so extract/round-trip preserves the `OF type`
    // nature instead of flattening it to a plain column-list CREATE TABLE.
    private async Task<List<RawObjectDefinition>> ReadTypedTablesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var sql = _q.TypedTables;

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(0);
            var name = r.GetString(1);
            var ofType = $"{r.GetString(2)}.{r.GetString(3)}";
            var pkCols = r.IsDBNull(4) ? null : r.GetString(4);
            var body = pkCols is null
                ? $"CREATE TABLE {schema}.{name} OF {ofType};"
                : $"CREATE TABLE {schema}.{name} OF {ofType} (PRIMARY KEY ({pkCols}));";
            list.Add(MakeRaw(ObjectKind.Table, schema, name, $"table:{schema}.{name}", body));
        }
        return list;
    }

    private async Task<List<RawObjectDefinition>> ReadEnumTypesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var sql = _q.EnumTypes;

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
        var sql = _q.CompositeTypes;

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
        var sql = _q.RangeTypes;

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
        var sql = _q.ShellTypes;

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
        var sql = _q.Collations;

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
        var sql = _q.Aggregates;

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
        var sql = _q.Domains;

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
        var sql = _q.Triggers;

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
        var sql = _q.Rules;

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
        var sql = _q.Policies;

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

            // Roles the policy applies to (#103). PUBLIC is polroles {0} → reconstructed as TO PUBLIC.
            var roles = r.IsDBNull(7) ? Array.Empty<string>() : r.GetFieldValue<string[]>(7);

            var forCmd = cmdLetter switch { 'r' => "SELECT", 'a' => "INSERT", 'w' => "UPDATE", 'd' => "DELETE", _ => "ALL" };
            var body = $"CREATE POLICY {name} ON {on} AS {(permissive ? "PERMISSIVE" : "RESTRICTIVE")} FOR {forCmd}";
            if (roles.Length > 0)
                body += " TO " + string.Join(", ", roles.Select(role => role.Equals("public", StringComparison.OrdinalIgnoreCase) ? "PUBLIC" : SqlEmitter.Quote(role)));
            if (!string.IsNullOrWhiteSpace(usingExpr)) body += $" USING ({usingExpr})";
            if (!string.IsNullOrWhiteSpace(checkExpr)) body += $" WITH CHECK ({checkExpr})";
            // TO PUBLIC is the policy default, so a source that writes it and one that omits it both map to
            // polroles {0}; NormalizeRawBody can't reconcile that, so policies stay identity-only (not
            // body-compared) to avoid phantom diffs — the reconstructed roles are for extract fidelity.
            list.Add(MakeRaw(ObjectKind.Policy, schema, name, $"policy:{name} on {on}", body + ";", on, bodyComparable: false));
        }
        return list;
    }

    private async Task<List<RawObjectDefinition>> ReadEventTriggersAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var sql = _q.EventTriggers;

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var name = r.GetString(0);
            var evt = r.GetString(1);
            var fn = $"{r.GetString(2)}.{r.GetString(3)}";
            // evttags (text[]) preserves the WHEN TAG IN order; NULL/empty = no tag filter (#104).
            var tags = r.IsDBNull(4) ? null : r.GetFieldValue<string[]>(4);
            var when = tags is { Length: > 0 }
                ? " WHEN TAG IN (" + string.Join(", ", tags.Select(t => "'" + t.Replace("'", "''") + "'")) + ")"
                : "";
            var body = $"CREATE EVENT TRIGGER {name} ON {evt}{when} EXECUTE FUNCTION {fn}();";
            // With the tags reconstructed the body now matches the parsed source under NormalizeRawBody,
            // so it is body-comparable (was identity-only while tags were dropped).
            list.Add(MakeRaw(ObjectKind.EventTrigger, "", name, $"eventtrigger:{name}", body));
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

        // Conversion, FDW, Server, Cast, and column-based Statistics now have real DDL reconstruction
        // (below); the rest stay existence-only until they get a clean reconstruction too.
        // Expression statistics (stxexprs set) we don't reconstruct yet → keep existence-only.
        await Schema(ObjectKind.Statistics, "statistics", _q.StatisticsExistence);
        return list;
    }

    // ---- operators + text search: full DDL reconstruction (was existence-only / unread) -------

    private async Task<List<RawObjectDefinition>> ReadOperatorsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var sql = _q.Operators;

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
        var sql = _q.OperatorFamilies;

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
        var sql = _q.OperatorClasses;

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

            var amopSql = _q.OperatorClassAmOps;
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

            var amprocSql = _q.OperatorClassAmProcs;
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
        var sql = _q.TextSearchDictionaries;

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
        var cfgSql = _q.TextSearchConfigurations;

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
            var mapSql = _q.TextSearchConfigurationMap;
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
        var sql = _q.Conversions;

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
        var sql = _q.ForeignDataWrappers;

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
        var sql = _q.Servers;

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
        var sql = _q.Statistics;

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
        var sql = _q.Casts;

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
        var sql = _q.ForeignTables;

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
        var sql = _q.Publications;

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
