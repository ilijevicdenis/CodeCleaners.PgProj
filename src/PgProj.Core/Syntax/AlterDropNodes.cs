using System.Collections.Generic;

namespace PgProj.Core.Syntax;

/// <summary>ALTER &lt;object&gt; … — kind recorded; ALTER TABLE actions are validated structurally.</summary>
public sealed class AlterStatement : SqlStatement
{
    public string ObjectKind { get; init; } = "";        // TABLE / VIEW / SEQUENCE / TYPE / …
    public string? Schema { get; set; }
    public string Name { get; set; } = "";
    public List<string> Actions { get; } = new();        // action verbs, for inspection
    public List<TableConstraint> AddedConstraints { get; } = new();  // structured ADD CONSTRAINT details (#153) — folded into the table model
}

/// <summary>DROP &lt;object&gt; [IF EXISTS] name[, …] [CASCADE|RESTRICT].</summary>
public sealed class DropStatement : SqlStatement
{
    public string ObjectKind { get; init; } = "";
    public bool IfExists { get; set; }
    public bool Concurrently { get; set; }
    public List<string> Names { get; } = new();
    public string? DropOption { get; set; }              // CASCADE / RESTRICT
}
