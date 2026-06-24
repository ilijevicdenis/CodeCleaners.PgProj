using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using PgProj.Core.Comparison;
using PgProj.Core.Model;

namespace PgProj.Core.Refactoring;

/// <summary>
/// One append-only refactor record (#136 — the <c>.refactorlog</c> analogue). It captures an intentional,
/// data-preserving structural change so the deploy planner emits an <c>ALTER … RENAME</c>/<c>SET SCHEMA</c>
/// instead of the destructive DROP+CREATE the name-keyed diff would otherwise infer.
/// </summary>
/// <param name="Operation">"rename" or "move-schema".</param>
/// <param name="ObjectType">"table" or "column".</param>
/// <param name="OldName">The prior qualified name — <c>schema.table</c> for a table, <c>schema.table.column</c> for a column.</param>
/// <param name="NewName">The new qualified name, same shape as <paramref name="OldName"/>.</param>
public sealed record RefactorEntry(string Operation, string ObjectType, string OldName, string NewName);

/// <summary>
/// The project's committed, source-controlled refactor log: an ordered list of <see cref="RefactorEntry"/>.
/// Read by the deploy planner BY DEFAULT (its mere presence is the opt-in — there is no flag) so logged
/// renames/moves deploy as data-preserving ALTERs. Deleting the file deterministically restores the
/// drop+create behaviour (the documented footgun, mirroring SSDT). Deterministic Core code: no clock/no
/// timestamp leaks, so a log written from identical input is byte-identical.
/// </summary>
public sealed class RefactorLog
{
    /// <summary>The conventional file extension for the refactor log.</summary>
    public const string Extension = ".pgrefactorlog";

    public const string OpRename = "rename";
    public const string OpMoveSchema = "move-schema";
    public const string OpExpandWildcards = "expand-wildcards";
    public const string TypeTable = "table";
    public const string TypeColumn = "column";
    public const string TypeView = "view";

    public IReadOnlyList<RefactorEntry> Entries { get; init; } = Array.Empty<RefactorEntry>();

    public bool IsEmpty => Entries.Count == 0;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The conventional log path for a project: <c>&lt;project-file-name&gt;.pgrefactorlog</c> in the project dir.</summary>
    public static string PathFor(string projectFilePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(projectFilePath)) ?? ".";
        return Path.Combine(dir, Path.GetFileNameWithoutExtension(projectFilePath) + Extension);
    }

    /// <summary>Loads the log at <paramref name="path"/>, or an empty log when the file does not exist.</summary>
    public static RefactorLog Load(string path)
    {
        if (!File.Exists(path)) return new RefactorLog();
        return Parse(File.ReadAllText(path));
    }

    /// <summary>Loads the log conventionally located next to <paramref name="projectFilePath"/> (empty when absent).</summary>
    public static RefactorLog LoadForProject(string projectFilePath) => Load(PathFor(projectFilePath));

    public static RefactorLog Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new RefactorLog();
        var entries = JsonSerializer.Deserialize<List<RefactorEntry>>(json, Json) ?? new List<RefactorEntry>();
        return new RefactorLog { Entries = entries };
    }

    public string ToJson() => JsonSerializer.Serialize(Entries, Json);

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, ToJson());
    }

    /// <summary>Returns a new log with <paramref name="entry"/> appended (append-only; the log never mutates in place).</summary>
    public RefactorLog Append(RefactorEntry entry) =>
        new() { Entries = Entries.Append(entry).ToList() };

    // ---- deploy-planner consumption ------------------------------------------------------------

    /// <summary>
    /// Builds the table-level <see cref="IdentityDiffEngine.RenamePlan"/> implied by the log, guarded against
    /// the real models: a rename is only emitted when the target actually still has the OLD table and the
    /// source has the NEW one (a stale entry is ignored). Same-schema name changes → <see cref="RenameTableChange"/>;
    /// schema moves → <see cref="SetTableSchemaChange"/>; a combined rename+move emits both.
    /// </summary>
    public IdentityDiffEngine.RenamePlan BuildTableRenamePlan(DatabaseModel source, DatabaseModel target)
    {
        var plan = new IdentityDiffEngine.RenamePlan();
        foreach (var e in Entries)
        {
            if (!string.Equals(e.ObjectType, TypeTable, StringComparison.OrdinalIgnoreCase)) continue;
            var (oldSchema, oldName) = SplitQualified(e.OldName);
            var (newSchema, newName) = SplitQualified(e.NewName);
            if (oldSchema is null || newSchema is null) continue;

            // Stale-entry guard: the OLD table must exist in the target and the NEW in the source.
            if (!HasTable(target, oldSchema, oldName) || !HasTable(source, newSchema, newName)) continue;

            var oldQ = $"{oldSchema}.{oldName}";
            var newQ = $"{newSchema}.{newName}";

            // Schema move first (so a later RENAME lands in the new schema), then in-schema rename.
            if (!DatabaseModel.NameEquals(oldSchema, newSchema))
                plan.Record(IdentityDiffEngine.KindTable, oldQ, newQ,
                    new SetTableSchemaChange(oldSchema, oldName, newSchema));
            if (!DatabaseModel.NameEquals(oldName, newName))
                plan.Record(IdentityDiffEngine.KindTable, oldQ, newQ,
                    new RenameTableChange(newSchema, oldName, newName));
        }
        return plan;
    }

    /// <summary>
    /// The logged column renames for one table (source/new schema+name), each guarded so it only applies when
    /// the target table still has the OLD column and the source has the NEW one. Returns (oldColumn, newColumn).
    /// </summary>
    public IReadOnlyList<(string Old, string New)> ColumnRenamesFor(
        DatabaseModel source, DatabaseModel target, TableDefinition sourceTable, TableDefinition targetTable)
    {
        var result = new List<(string, string)>();
        foreach (var e in Entries)
        {
            if (!string.Equals(e.ObjectType, TypeColumn, StringComparison.OrdinalIgnoreCase)) continue;
            var (oldSchema, oldTable, oldCol) = SplitColumn(e.OldName);
            var (_, _, newCol) = SplitColumn(e.NewName);
            if (oldSchema is null || oldCol is null || newCol is null) continue;

            // The entry's table must be the one being compared (matched by the source/new identity).
            if (!DatabaseModel.NameEquals(oldSchema, sourceTable.Schema) || !DatabaseModel.NameEquals(oldTable!, sourceTable.Name))
                continue;
            // Guard: target has the OLD column, source has the NEW column.
            if (!targetTable.Columns.Any(c => DatabaseModel.NameEquals(c.Name, oldCol))) continue;
            if (!sourceTable.Columns.Any(c => DatabaseModel.NameEquals(c.Name, newCol))) continue;

            result.Add((oldCol, newCol));
        }
        return result;
    }

    private static bool HasTable(DatabaseModel m, string schema, string name) =>
        m.Tables.Any(t => DatabaseModel.NameEquals(t.Schema, schema) && DatabaseModel.NameEquals(t.Name, name));

    private static (string? Schema, string Name) SplitQualified(string qualified)
    {
        var dot = qualified.IndexOf('.');
        return dot < 0 ? (null, qualified) : (qualified[..dot], qualified[(dot + 1)..]);
    }

    private static (string? Schema, string? Table, string? Column) SplitColumn(string qualified)
    {
        var parts = qualified.Split('.');
        return parts.Length == 3 ? (parts[0], parts[1], parts[2]) : (null, null, null);
    }
}
