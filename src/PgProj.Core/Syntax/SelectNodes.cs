using System.Collections.Generic;

namespace PgProj.Core.Syntax;

/// <summary>A top-level query statement (SELECT / VALUES / TABLE / WITH …).</summary>
public sealed class QueryStatement : SqlStatement { public SelectQuery Query { get; init; } = null!; }

/// <summary>
/// A SELECT query (or a VALUES / TABLE primary), possibly chained by a set operation. CTEs from a
/// leading WITH are attached to the first query in the chain.
/// </summary>
public sealed class SelectQuery
{
    public List<CommonTableExpr> With { get; } = new();
    public bool WithRecursive { get; set; }

    // primary kind
    public bool IsValues { get; set; }
    public List<List<Expr>> ValuesRows { get; } = new();
    public bool IsTableCommand { get; set; }                 // TABLE name
    public string? TableName { get; set; }

    public bool Distinct { get; set; }
    public List<Expr> DistinctOn { get; } = new();
    public List<SelectItem> Items { get; } = new();

    public FromClause? From { get; set; }
    public Expr? Where { get; set; }
    public List<Expr> GroupBy { get; } = new();
    public string? GroupByKind { get; set; }                 // ROLLUP / CUBE / GROUPING SETS / null
    public Expr? Having { get; set; }
    public List<NamedWindow> Windows { get; } = new();

    public SetOperation? SetOp { get; set; }                 // UNION/INTERSECT/EXCEPT chain

    public List<OrderByItem> OrderBy { get; } = new();
    public string? Limit { get; set; }
    public string? Offset { get; set; }
    public List<LockingClause> Locking { get; } = new();
}

public sealed class CommonTableExpr
{
    public string Name { get; init; } = "";
    public List<string> Columns { get; } = new();
    public string? Materialized { get; set; }                // MATERIALIZED / NOT MATERIALIZED
    public SelectQuery Query { get; set; } = null!;
    public string? RawBody { get; set; }                     // for data-modifying CTEs captured verbatim
}

public sealed class SelectItem { public Expr Expr { get; init; } = null!; public string? Alias { get; set; } }

public sealed class SetOperation
{
    public string Op { get; init; } = "";                    // UNION / UNION ALL / INTERSECT / EXCEPT …
    public SelectQuery Left { get; init; } = null!;
    public SelectQuery Right { get; init; } = null!;
}

public sealed class FromClause { public List<TableRef> Relations { get; } = new(); }

public sealed class TableRef
{
    public string? Schema { get; set; }
    public string? TableName { get; set; }
    public SelectQuery? Subquery { get; set; }
    public FuncCallExpr? Function { get; set; }              // function-in-FROM
    public bool Lateral { get; set; }
    public string? Alias { get; set; }
    public List<string> ColumnAliases { get; } = new();
    public bool WithOrdinality { get; set; }
    public string? RawText { get; set; }                     // ROWS FROM / TABLESAMPLE / unparsed tail
    public List<JoinClause> Joins { get; } = new();
    public bool Only { get; set; }
}

public sealed class JoinClause
{
    public string JoinType { get; init; } = "";              // INNER / LEFT / RIGHT / FULL / CROSS [+ NATURAL]
    public TableRef Right { get; init; } = null!;
    public Expr? On { get; set; }
    public List<string> Using { get; } = new();
}

public sealed class OrderByItem
{
    public Expr Expr { get; init; } = null!;
    public string? Direction { get; set; }                   // ASC / DESC / USING op
    public string? Nulls { get; set; }                       // FIRST / LAST
}

public sealed class NamedWindow { public string Name { get; init; } = ""; public WindowSpec Spec { get; init; } = null!; }

public sealed class WindowSpec
{
    public string? Name { get; set; }                        // OVER window_name
    public string? RefName { get; set; }                     // existing window referenced inside (…)
    public List<Expr> PartitionBy { get; } = new();
    public List<OrderByItem> OrderBy { get; } = new();
    public string? FrameText { get; set; }                   // ROWS/RANGE/GROUPS … captured
}

public sealed class LockingClause
{
    public string Strength { get; init; } = "";              // UPDATE / NO KEY UPDATE / SHARE / KEY SHARE
    public List<string> Of { get; } = new();
    public string? Wait { get; set; }                        // NOWAIT / SKIP LOCKED
}
