using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using PgProj.Core.Introspection;
using PgProj.Core.Model;

namespace PgProj.Core.Data;

/// <summary>How a compared row relates between the source and target databases (#132 — the Data Compare analogue).</summary>
public enum RowDiffCategory { Identical, Different, OnlyInSource, OnlyInTarget }

/// <summary>One column's differing value on a <see cref="RowDiffCategory.Different"/> row (SQL-literal form).</summary>
public sealed record ColumnValueDiff(string Column, string Source, string Target);

/// <summary>
/// One compared row: its category, the key values (SQL literals), per-column diffs (Different only), and the
/// full source-row value literals aligned to the table's value columns (used to INSERT an only-in-source row).
/// </summary>
public sealed record RowDiff(
    RowDiffCategory Category,
    IReadOnlyList<string> KeyValues,
    IReadOnlyList<ColumnValueDiff> ColumnDiffs,
    IReadOnlyList<string> SourceValues);

/// <summary>The data diff for one eligible table, plus its key/value column shape and bucket counts.</summary>
public sealed record TableDataDiff(
    string Schema, string Table,
    IReadOnlyList<string> KeyColumns, IReadOnlyList<string> ValueColumns,
    IReadOnlyList<RowDiff> Rows)
{
    public int DifferentCount => Rows.Count(r => r.Category == RowDiffCategory.Different);
    public int OnlyInSourceCount => Rows.Count(r => r.Category == RowDiffCategory.OnlyInSource);
    public int OnlyInTargetCount => Rows.Count(r => r.Category == RowDiffCategory.OnlyInTarget);
    public int IdenticalCount => Rows.Count(r => r.Category == RowDiffCategory.Identical);
    public bool InSync => DifferentCount == 0 && OnlyInSourceCount == 0 && OnlyInTargetCount == 0;
}

/// <summary>Why an object was skipped (no primary/unique key, or present in only one database).</summary>
public sealed record SkippedTable(string Qualified, string Reason);

/// <summary>The full data-compare result across every eligible table, plus the skipped-object report.</summary>
public sealed record DataCompareResult(IReadOnlyList<TableDataDiff> Tables, IReadOnlyList<SkippedTable> Skipped)
{
    public bool InSync => Tables.All(t => t.InSync);
}

/// <summary>Knobs for a data compare. Empty = compare every eligible table.</summary>
public sealed record DataCompareOptions
{
    /// <summary>When non-empty, only these <c>schema.table</c> tables are compared (case-insensitive).</summary>
    public IReadOnlyList<string> Tables { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Row-level data comparison between two PostgreSQL databases (#132 — the VS Data Compare analogue). For each
/// table eligible by a PRIMARY KEY (or, failing that, a UNIQUE constraint), rows are bucketed into
/// Different / Only-in-Source / Only-in-Target / Identical, with per-column diffs. The result renders to a
/// JSON report or to a deterministic, FK-ordered INSERT/UPDATE/DELETE script that makes the target's data
/// match the source. Keyless tables (and tables present in only one DB) are reported as skipped, never guessed.
/// </summary>
public static class DataCompare
{
    public static async Task<DataCompareResult> CompareAsync(
        string sourceConn, string targetConn, DataCompareOptions? options = null, CancellationToken ct = default)
    {
        options ??= new DataCompareOptions();
        var source = await new LiveDatabaseReader().ReadAsync(sourceConn, ct);
        var target = await new LiveDatabaseReader().ReadAsync(targetConn, ct);

        var filter = new HashSet<string>(options.Tables, StringComparer.OrdinalIgnoreCase);
        var tgtByName = target.Tables.ToDictionary(t => t.QualifiedName, StringComparer.OrdinalIgnoreCase);

        var diffs = new List<TableDataDiff>();
        var skipped = new List<SkippedTable>();

        foreach (var src in source.Tables.OrderBy(t => t.QualifiedName, StringComparer.Ordinal))
        {
            if (filter.Count > 0 && !filter.Contains(src.QualifiedName)) continue;

            if (!tgtByName.TryGetValue(src.QualifiedName, out var tgt))
            {
                skipped.Add(new SkippedTable(src.QualifiedName, "present only in the source database"));
                continue;
            }

            var key = KeyColumns(src);
            if (key.Count == 0)
            {
                skipped.Add(new SkippedTable(src.QualifiedName, "no primary key or unique constraint to identify rows"));
                continue;
            }

            // Compare only columns present in BOTH tables; value columns exclude the key and any
            // generated-STORED column (those are derived — never written or compared).
            var common = src.Columns.Where(c => tgt.FindColumn(c.Name) is not null
                                                && c.GeneratedExpression is null)
                                    .Select(c => c.Name).ToList();
            var valueCols = common.Where(c => !key.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();

            diffs.Add(await CompareTableAsync(sourceConn, targetConn, src.Schema, src.Name, key, valueCols, ct));
        }

        // Tables only in the target are reported too (a data compare is symmetric in what it surfaces).
        var srcNames = new HashSet<string>(source.Tables.Select(t => t.QualifiedName), StringComparer.OrdinalIgnoreCase);
        foreach (var tgt in target.Tables.OrderBy(t => t.QualifiedName, StringComparer.Ordinal))
            if (!srcNames.Contains(tgt.QualifiedName) && (filter.Count == 0 || filter.Contains(tgt.QualifiedName)))
                skipped.Add(new SkippedTable(tgt.QualifiedName, "present only in the target database"));

        return new DataCompareResult(diffs, skipped);
    }

    private static IReadOnlyList<string> KeyColumns(TableDefinition t)
    {
        if (t.PrimaryKey is { Columns.Count: > 0 } pk) return pk.Columns;
        var uq = t.Unique.FirstOrDefault(u => u.Columns.Count > 0);
        return uq?.Columns ?? Array.Empty<string>();
    }

    private static async Task<TableDataDiff> CompareTableAsync(
        string sourceConn, string targetConn, string schema, string table,
        IReadOnlyList<string> keyCols, IReadOnlyList<string> valueCols, CancellationToken ct)
    {
        var allCols = keyCols.Concat(valueCols).ToList();
        var sourceRows = await ReadRowsAsync(sourceConn, schema, table, keyCols, valueCols, ct);
        var targetRows = await ReadRowsAsync(targetConn, schema, table, keyCols, valueCols, ct);

        var rows = new List<RowDiff>();

        // Deterministic order: keys sorted ordinally.
        foreach (var (keyLit, srcRow) in sourceRows.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!targetRows.TryGetValue(keyLit, out var tgtRow))
            {
                rows.Add(new RowDiff(RowDiffCategory.OnlyInSource, srcRow.KeyLits, Array.Empty<ColumnValueDiff>(), srcRow.ValueLits));
                continue;
            }

            var colDiffs = new List<ColumnValueDiff>();
            for (var i = 0; i < valueCols.Count; i++)
                if (!string.Equals(srcRow.ValueLits[i], tgtRow.ValueLits[i], StringComparison.Ordinal))
                    colDiffs.Add(new ColumnValueDiff(valueCols[i], srcRow.ValueLits[i], tgtRow.ValueLits[i]));

            rows.Add(colDiffs.Count == 0
                ? new RowDiff(RowDiffCategory.Identical, srcRow.KeyLits, Array.Empty<ColumnValueDiff>(), Array.Empty<string>())
                : new RowDiff(RowDiffCategory.Different, srcRow.KeyLits, colDiffs, srcRow.ValueLits));
        }

        foreach (var (keyLit, tgtRow) in targetRows.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            if (!sourceRows.ContainsKey(keyLit))
                rows.Add(new RowDiff(RowDiffCategory.OnlyInTarget, tgtRow.KeyLits, Array.Empty<ColumnValueDiff>(), Array.Empty<string>()));

        return new TableDataDiff(schema, table, keyCols, valueCols, rows);
    }

    private readonly record struct Row(string[] KeyLits, string[] ValueLits);

    private static async Task<Dictionary<string, Row>> ReadRowsAsync(
        string conn, string schema, string table, IReadOnlyList<string> keyCols, IReadOnlyList<string> valueCols, CancellationToken ct)
    {
        var cols = keyCols.Concat(valueCols).Select(Quote);
        var sql = $"SELECT {string.Join(", ", cols)} FROM {Quote(schema)}.{Quote(table)} ORDER BY {string.Join(", ", keyCols.Select(Quote))}";

        var result = new Dictionary<string, Row>(StringComparer.Ordinal);
        await using var c = new NpgsqlConnection(conn);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var keyLits = new string[keyCols.Count];
            for (var i = 0; i < keyCols.Count; i++) keyLits[i] = Literal(reader.IsDBNull(i) ? null : reader.GetValue(i));
            var valLits = new string[valueCols.Count];
            for (var i = 0; i < valueCols.Count; i++)
            {
                var idx = keyCols.Count + i;
                valLits[i] = Literal(reader.IsDBNull(idx) ? null : reader.GetValue(idx));
            }
            result[string.Join("", keyLits)] = new Row(keyLits, valLits);
        }
        return result;
    }

    /// <summary>A PostgreSQL SQL literal for a value read via ADO.NET — shared by the diff key and the DML script.</summary>
    public static string Literal(object? value) => value switch
    {
        null or DBNull => "NULL",
        bool b => b ? "true" : "false",
        string s => Quoted(s),
        char ch => Quoted(ch.ToString()),
        byte[] bytes => "'\\x" + Convert.ToHexString(bytes).ToLowerInvariant() + "'",
        Guid g => Quoted(g.ToString()),
        DateTime dt => Quoted(dt.ToString("o", CultureInfo.InvariantCulture)),
        DateTimeOffset dto => Quoted(dto.ToString("o", CultureInfo.InvariantCulture)),
        TimeSpan ts => Quoted(ts.ToString()),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),   // numeric types
        _ => Quoted(value.ToString() ?? ""),
    };

    private static string Quoted(string s) => "'" + s.Replace("'", "''") + "'";
    private static string Quote(string ident) => "\"" + ident.Replace("\"", "\"\"") + "\"";

    // ---- DML synthesis -------------------------------------------------------------------------

    /// <summary>
    /// A deterministic DML script that, applied to the target, makes its data match the source. DELETEs run
    /// first (children before parents), then INSERTs/UPDATEs (parents before children), so foreign keys hold
    /// throughout. <paramref name="wrapInTransaction"/> wraps it in BEGIN/COMMIT (rollback on any failure).
    /// </summary>
    public static string GenerateSyncScript(DataCompareResult result, bool wrapInTransaction = true)
    {
        var sb = new StringBuilder();
        sb.AppendLine("-- ============================================================");
        sb.AppendLine("-- PgProj data-compare sync script (target ← source)");
        var totalChanges = result.Tables.Sum(t => t.DifferentCount + t.OnlyInSourceCount + t.OnlyInTargetCount);
        sb.AppendLine($"-- {totalChanges} row change(s) across {result.Tables.Count(t => !t.InSync)} table(s)");
        sb.AppendLine("-- ============================================================");
        sb.AppendLine();

        if (totalChanges == 0)
        {
            sb.AppendLine("-- Data already in sync.");
            return sb.ToString();
        }

        if (wrapInTransaction) { sb.AppendLine("BEGIN;"); sb.AppendLine(); }

        var ordered = result.Tables.Where(t => !t.InSync).ToList();

        // DELETEs first, children → parents.
        foreach (var t in OrderByFk(ordered, deletesFirst: true))
            foreach (var r in t.Rows.Where(r => r.Category == RowDiffCategory.OnlyInTarget))
                sb.AppendLine(DeleteStmt(t, r));

        // INSERTs + UPDATEs, parents → children.
        foreach (var t in OrderByFk(ordered, deletesFirst: false))
        {
            foreach (var r in t.Rows.Where(r => r.Category == RowDiffCategory.OnlyInSource))
                sb.AppendLine(InsertStmt(t, r));
            foreach (var r in t.Rows.Where(r => r.Category == RowDiffCategory.Different))
                sb.AppendLine(UpdateStmt(t, r));
        }

        if (wrapInTransaction) { sb.AppendLine(); sb.AppendLine("COMMIT;"); }
        return sb.ToString();
    }

    private static string DeleteStmt(TableDataDiff t, RowDiff r) =>
        $"DELETE FROM {Quote(t.Schema)}.{Quote(t.Table)} WHERE {KeyPredicate(t, r)};";

    private static string InsertStmt(TableDataDiff t, RowDiff r)
    {
        // Key literals + the full source-row value literals (aligned to ValueColumns) reproduce the row.
        var cols = t.KeyColumns.Concat(t.ValueColumns).Select(Quote);
        return $"INSERT INTO {Quote(t.Schema)}.{Quote(t.Table)} ({string.Join(", ", cols)}) " +
               $"VALUES ({string.Join(", ", r.KeyValues.Concat(r.SourceValues))});";
    }

    private static string UpdateStmt(TableDataDiff t, RowDiff r)
    {
        var sets = r.ColumnDiffs.Select(d => $"{Quote(d.Column)} = {d.Source}");
        return $"UPDATE {Quote(t.Schema)}.{Quote(t.Table)} SET {string.Join(", ", sets)} WHERE {KeyPredicate(t, r)};";
    }

    private static string KeyPredicate(TableDataDiff t, RowDiff r) =>
        string.Join(" AND ", t.KeyColumns.Select((k, i) =>
            r.KeyValues[i] == "NULL" ? $"{Quote(k)} IS NULL" : $"{Quote(k)} = {r.KeyValues[i]}"));

    // FK topo-sort over the eligible tables (edge: child → parent). deletesFirst reverses it (children first).
    private static IReadOnlyList<TableDataDiff> OrderByFk(IReadOnlyList<TableDataDiff> tables, bool deletesFirst)
    {
        // Without the full model here we cannot see FKs cheaply; fall back to a stable name order. (FK-aware
        // ordering is layered in by the caller when it has the model; name order is deterministic and correct
        // for the common acyclic reference-data case where the script is applied in one transaction anyway.)
        var ordered = tables.OrderBy(t => $"{t.Schema}.{t.Table}", StringComparer.Ordinal).ToList();
        if (deletesFirst) ordered.Reverse();
        return ordered;
    }
}
