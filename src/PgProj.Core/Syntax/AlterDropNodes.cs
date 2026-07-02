using System.Collections.Generic;

namespace PgProj.Core.Syntax;

/// <summary>ALTER &lt;object&gt; … — kind recorded; ALTER TABLE actions are validated structurally.
/// The structured capture lists below feed <c>ModelBuilder</c>, so a standalone ALTER TABLE changes the
/// table model the same way the equivalent inline CREATE TABLE clause would.</summary>
public sealed class AlterStatement : SqlStatement
{
    public string ObjectKind { get; init; } = "";        // TABLE / VIEW / SEQUENCE / TYPE / …
    public string? Schema { get; set; }
    public string Name { get; set; } = "";
    public List<string> Actions { get; } = new();        // action verbs, for inspection
    public List<TableConstraint> AddedConstraints { get; } = new();  // structured ADD CONSTRAINT details (#153) — folded into the table model
    public List<ColumnDef> AddedColumns { get; } = new();            // structured ADD COLUMN details — folded into the table model
    public List<string> DroppedColumns { get; } = new();             // DROP COLUMN names — folded (column + its constraints removed)
    public List<AlterColumnAction> ColumnActions { get; } = new();   // structured ALTER COLUMN actions — folded

    /// <summary>True when this ALTER carries structured details that ModelBuilder folds into the table
    /// model. Consumers that re-emit the table FROM the model (TableDesigner companions) must skip such
    /// statements — re-adding their text double-folds (the #153 lesson).</summary>
    public bool FoldsIntoTableModel =>
        AddedConstraints.Count > 0 || AddedColumns.Count > 0 || DroppedColumns.Count > 0 || ColumnActions.Count > 0;

    // Actions that change NAMES or shape the catalog cannot track. Column adds/drops/retypes are folded
    // into the semantic catalog (CatalogBuilder), and constraint/storage/RLS actions don't affect
    // name/column resolution — so only these force the analyzers' skip-the-file conservatism.
    private static readonly System.Collections.Generic.HashSet<string> BindingInvalidatingActions =
        new(System.StringComparer.Ordinal)
        { "RENAME", "SET SCHEMA", "OF", "NOT OF", "INHERIT", "NO INHERIT", "PARTITION", "RENAME VALUE", "RENAME ATTRIBUTE", "ATTRIBUTE" };

    /// <summary>True when this ALTER makes name/column binding unreliable for its parse unit (rename /
    /// schema move / typed-of / inheritance / partition attach). Everything else is either folded into
    /// the catalog or irrelevant to binding, so validation can stay ON for the file (P1 audit fix:
    /// the extractor's routine <c>ADD CONSTRAINT</c> used to disable validation for the whole file).</summary>
    public bool InvalidatesBinding
    {
        get
        {
            foreach (var a in Actions)
                if (BindingInvalidatingActions.Contains(a)) return true;
            return false;
        }
    }
}

/// <summary>One structured <c>ALTER TABLE … ALTER COLUMN</c> action ModelBuilder can fold.
/// <see cref="Kind"/> is "TYPE" (Value = new type), "SET DEFAULT" (Value = expression),
/// "DROP DEFAULT", "SET NOT NULL", or "DROP NOT NULL" (Value = null for the last three).</summary>
public sealed record AlterColumnAction(string Column, string Kind, string? Value);

/// <summary>DROP &lt;object&gt; [IF EXISTS] name[, …] [CASCADE|RESTRICT].</summary>
public sealed class DropStatement : SqlStatement
{
    public string ObjectKind { get; init; } = "";
    public bool IfExists { get; set; }
    public bool Concurrently { get; set; }
    public List<string> Names { get; } = new();
    public string? DropOption { get; set; }              // CASCADE / RESTRICT
}
