using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using PgProj.Core.Model;

namespace PgProj.Core.Data;

/// <summary>
/// Exports a live database's user-table rows as a deterministic, FK-ordered <c>INSERT</c> script (#134 —
/// the <c>ExtractAllTableData</c>/BACPAC analogue). Emitted as a post-deploy seed so a normal
/// <c>publish</c> loads schema then data; identity columns are written with <c>OVERRIDING SYSTEM VALUE</c>
/// and their owning sequences are <c>setval</c>-corrected after load, so a round-trip reproduces both the
/// rows and the next-id state. Generated-STORED columns are skipped (they are recomputed by PostgreSQL).
/// </summary>
public static class DataExporter
{
    /// <summary>
    /// Build the data script for <paramref name="onlyTables"/> (all eligible tables when null/empty). Tables
    /// are emitted parents-before-children so foreign keys hold as the inserts run.
    /// </summary>
    public static async Task<string> ExportAsync(
        string connectionString, DatabaseModel model,
        IReadOnlyCollection<string>? onlyTables = null, CancellationToken ct = default)
    {
        var filter = onlyTables is { Count: > 0 }
            ? new HashSet<string>(onlyTables, StringComparer.OrdinalIgnoreCase)
            : null;
        var tables = FkOrder(model.Tables.Where(t => filter is null || filter.Contains(t.QualifiedName)).ToList());

        var sb = new StringBuilder();
        sb.AppendLine("-- ============================================================");
        sb.AppendLine("-- PgProj extracted table data (post-deploy seed) — load order is FK-safe");
        sb.AppendLine("-- ============================================================");
        sb.AppendLine();

        var seqFixups = new List<string>();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        foreach (var t in tables)
        {
            var cols = t.Columns.Where(c => c.GeneratedExpression is null).ToList();
            if (cols.Count == 0) continue;
            var hasAlwaysIdentity = cols.Any(c => c is { IsIdentity: true, IdentityKind: "ALWAYS" });

            var rows = await ReadAllAsync(conn, t, cols, ct);
            if (rows.Count > 0)
            {
                var colList = string.Join(", ", cols.Select(c => Quote(c.Name)));
                var overriding = hasAlwaysIdentity ? " OVERRIDING SYSTEM VALUE" : "";
                sb.AppendLine($"INSERT INTO {Quote(t.Schema)}.{Quote(t.Name)} ({colList}){overriding} VALUES");
                sb.AppendLine(string.Join(",\n", rows.Select(r => "  (" + string.Join(", ", r) + ")")) + ";");
                sb.AppendLine();
            }

            // After loading explicit ids, advance each identity/serial column's sequence past the max.
            foreach (var c in cols.Where(c => c.IsIdentity || c.IsSerial))
                seqFixups.Add(
                    $"SELECT setval(pg_get_serial_sequence('{t.Schema}.{t.Name}', '{c.Name}'), " +
                    $"GREATEST((SELECT COALESCE(MAX({Quote(c.Name)}), 1) FROM {Quote(t.Schema)}.{Quote(t.Name)}), 1));");
        }

        if (seqFixups.Count > 0)
        {
            sb.AppendLine("-- advance identity/serial sequences past the loaded rows");
            foreach (var f in seqFixups) sb.AppendLine(f);
        }

        return sb.ToString();
    }

    private static async Task<List<string[]>> ReadAllAsync(NpgsqlConnection conn, TableDefinition t, IReadOnlyList<ColumnDefinition> cols, CancellationToken ct)
    {
        var keyOrder = t.PrimaryKey is { Columns.Count: > 0 } pk ? pk.Columns : cols.Select(c => c.Name).ToList();
        var sql = $"SELECT {string.Join(", ", cols.Select(c => Quote(c.Name)))} FROM {Quote(t.Schema)}.{Quote(t.Name)} " +
                  $"ORDER BY {string.Join(", ", keyOrder.Select(Quote))}";
        var rows = new List<string[]>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var lits = new string[cols.Count];
            for (var i = 0; i < cols.Count; i++) lits[i] = DataCompare.Literal(reader.IsDBNull(i) ? null : reader.GetValue(i));
            rows.Add(lits);
        }
        return rows;
    }

    /// <summary>Topologically order tables so a referenced (parent) table precedes the tables that reference it.
    /// Self-references and cycles fall back to a stable name order for the remaining nodes.</summary>
    private static IReadOnlyList<TableDefinition> FkOrder(IReadOnlyList<TableDefinition> tables)
    {
        var byName = tables.ToDictionary(t => t.QualifiedName, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<TableDefinition>();
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(TableDefinition t)
        {
            if (done.Contains(t.QualifiedName) || !visiting.Add(t.QualifiedName)) return;
            foreach (var fk in t.ForeignKeys)
            {
                var parent = $"{fk.ReferencedSchema}.{fk.ReferencedTable}";
                if (!string.Equals(parent, t.QualifiedName, StringComparison.OrdinalIgnoreCase)
                    && byName.TryGetValue(parent, out var pt))
                    Visit(pt);
            }
            visiting.Remove(t.QualifiedName);
            if (done.Add(t.QualifiedName)) ordered.Add(t);
        }

        foreach (var t in tables.OrderBy(t => t.QualifiedName, StringComparer.Ordinal))
            Visit(t);
        return ordered;
    }

    private static string Quote(string ident) => "\"" + ident.Replace("\"", "\"\"") + "\"";
}
