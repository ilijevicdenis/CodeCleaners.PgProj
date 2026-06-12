using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using PgProj.Core.Model;

namespace PgProj.Core.Introspection;

/// <summary>Relation readers: schemas, tables/columns plus the wave-2 constraint readers, indexes,
/// views, sequences, typed/partition tables, and foreign tables.</summary>
public sealed partial class LiveDatabaseReader
{
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

    // Partitioning + inheritance trailing clauses (#99): set TrailingOptions on the finely-modelled parent
    // (PARTITION BY …) and on a non-partition INHERITS child (INHERITS (…)). Wave-2 reader writing ONLY the
    // disjoint TrailingOptions member, so it races nothing the constraint/check/fk/exclude readers touch.
    private async Task ReadTablePartitioningAsync(NpgsqlConnection conn, Dictionary<string, TableDefinition> tables, CancellationToken ct)
    {
        await using (var cmd = new NpgsqlCommand(_q.PartitionKeys, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                if (tables.TryGetValue($"{r.GetString(0)}.{r.GetString(1)}", out var def))
                    def.TrailingOptions = $"PARTITION BY {r.GetString(2)}";

        await using (var cmd = new NpgsqlCommand(_q.TableInheritance, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                if (tables.TryGetValue($"{r.GetString(0)}.{r.GetString(1)}", out var def))
                    def.TrailingOptions = $"INHERITS ({r.GetString(2)})";
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

    // Partition children: CREATE TABLE … PARTITION OF parent <bound> (#99). Emitted as a raw `table:` object
    // (the same modelling the parser uses) so extract/round-trip preserves the partition relationship instead
    // of flattening the child to a standalone table. Excluded from the finely-modelled read by relispartition.
    private async Task<List<RawObjectDefinition>> ReadPartitionChildrenAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var sql = _q.PartitionChildren;

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(0);
            var name = r.GetString(1);
            var parent = r.GetString(2);
            var bound = r.IsDBNull(3) ? "DEFAULT" : r.GetString(3);   // relpartbound: "FOR VALUES …" or DEFAULT
            var body = $"CREATE TABLE {schema}.{name} PARTITION OF {parent} {bound};";
            list.Add(MakeRaw(ObjectKind.Table, schema, name, $"table:{schema}.{name}", body));
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
}
