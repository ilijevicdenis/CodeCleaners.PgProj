using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using PgProj.Core.Model;

namespace PgProj.Core.Introspection;

/// <summary>
/// Reads the live schema of a Postgres server into a <see cref="DatabaseModel"/> by querying the
/// system catalogs. Types come straight from <c>format_type()</c> (the same canonical spelling we
/// normalise project types to), so the model it produces can be diffed directly against a project
/// model with minimal phantom differences. This is the reverse direction of the project build —
/// the input to both <c>compare</c> and <c>extract</c>.
/// </summary>
public sealed class LiveDatabaseReader
{
    private static readonly string[] SystemSchemas = { "pg_catalog", "information_schema" };

    public async Task<DatabaseModel> ReadAsync(string connectionString, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var model = new DatabaseModel();
        await ReadSchemasAsync(conn, model, ct);
        var tablesByKey = await ReadTablesAndColumnsAsync(conn, model, ct);
        await ReadConstraintsAsync(conn, tablesByKey, ct);
        await ReadForeignKeysAsync(conn, tablesByKey, ct);
        await ReadIndexesAsync(conn, model, ct);
        await ReadViewsAsync(conn, model, ct);
        await ReadSequencesAsync(conn, model, ct);
        await ReadFunctionsAsync(conn, model, ct);
        return model;
    }

    private static bool IsUserSchema(string schema) =>
        !SystemSchemas.Contains(schema) && !schema.StartsWith("pg_", StringComparison.Ordinal);

    private async Task ReadSchemasAsync(NpgsqlConnection conn, DatabaseModel model, CancellationToken ct)
    {
        const string sql = "SELECT nspname FROM pg_namespace ORDER BY nspname;";
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var name = r.GetString(0);
            if (IsUserSchema(name)) model.Schemas.Add(new SchemaDefinition(name));
        }
    }

    private async Task<Dictionary<string, TableDefinition>> ReadTablesAndColumnsAsync(
        NpgsqlConnection conn, DatabaseModel model, CancellationToken ct)
    {
        const string sql = @"
            SELECT n.nspname, c.relname, a.attname,
                   format_type(a.atttypid, a.atttypmod) AS datatype,
                   a.attnotnull, pg_get_expr(d.adbin, d.adrelid) AS default_expr,
                   a.attidentity
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
            WHERE c.relkind IN ('r','p') AND a.attnum > 0 AND NOT a.attisdropped
              AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, c.relname, a.attnum;";

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
                model.Tables.Add(def);
            }

            var dataType = TypeNormalizer.Normalize(r.GetString(3));
            var notNull = r.GetBoolean(4);
            var defExpr = r.IsDBNull(5) ? null : r.GetString(5);
            var identity = !r.IsDBNull(6) && r.GetString(6) is { Length: > 0 } s && s != "\0";

            def.Columns.Add(new ColumnDefinition(r.GetString(2), dataType, !notNull, defExpr, identity));
        }
        return byKey;
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

        // (schema,table,conname,type) -> ordered columns
        var pk = new Dictionary<(string, string, string), List<string>>();
        var uq = new Dictionary<(string, string, string), List<string>>();

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(0);
            var table = r.GetString(1);
            var name = r.GetString(2);
            var type = r.GetString(3);
            var col = r.GetString(4);
            var bucket = type == "p" ? pk : uq;
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

    // The catalog's confdeltype/confupdtype are the single-byte internal "char" type; read
    // defensively so a provider that surfaces it as a string doesn't blow up.
    private static char ReadChar(NpgsqlDataReader r, int ordinal)
    {
        if (r.IsDBNull(ordinal)) return 'a';
        var v = r.GetValue(ordinal);
        return v switch
        {
            char c => c,
            string s => s.Length > 0 ? s[0] : 'a',
            _ => 'a',
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

    private async Task ReadIndexesAsync(NpgsqlConnection conn, DatabaseModel model, CancellationToken ct)
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

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(0);
            var table = r.GetString(1);
            var name = r.GetString(2);
            var unique = r.GetBoolean(3);
            var method = r.GetString(4);
            var def = r.GetString(5);
            var (cols, where) = ParseIndexDef(def);
            model.Indexes.Add(new IndexDefinition(name, schema, table, cols, unique, method, where));
        }
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

    private async Task ReadViewsAsync(NpgsqlConnection conn, DatabaseModel model, CancellationToken ct)
    {
        const string sql = @"
            SELECT n.nspname, c.relname, pg_get_viewdef(c.oid, true) AS def
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind = 'v' AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, c.relname;";

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            model.Views.Add(new ViewDefinition(r.GetString(0), r.GetString(1), r.GetString(2)));
    }

    private async Task ReadSequencesAsync(NpgsqlConnection conn, DatabaseModel model, CancellationToken ct)
    {
        const string sql = @"
            SELECT n.nspname, c.relname
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind = 'S' AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_%'
            ORDER BY n.nspname, c.relname;";

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            model.Sequences.Add(new SequenceDefinition(r.GetString(0), r.GetString(1)));
    }

    private async Task ReadFunctionsAsync(NpgsqlConnection conn, DatabaseModel model, CancellationToken ct)
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

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(0);
            var name = r.GetString(1);
            var args = r.IsDBNull(2) ? string.Empty : r.GetString(2);
            var def = r.GetString(3);
            model.Functions.Add(new FunctionDefinition(schema, name, $"{schema}.{name}({args})", def));
        }
    }
}
