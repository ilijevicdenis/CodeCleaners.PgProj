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
    // These clause lists are EMPTY on most queries (a plain SELECT has no WITH / VALUES / DISTINCT ON /
    // GROUP BY / WINDOW / FOR-locking). Eagerly `= new()`-ing them allocated ~6 empty List objects per
    // SelectQuery (and SelectQuery is one of the top allocated types). So they are lazy: the reader sees a
    // shared zero-alloc Array.Empty<T> until the parser writes via Add*, which allocates the real list on
    // first use. Readers only foreach/index/Single() them, which IReadOnlyList supports. (AllocProbe.)
    private List<CommonTableExpr>? _with;
    public IReadOnlyList<CommonTableExpr> With => _with ?? (IReadOnlyList<CommonTableExpr>)System.Array.Empty<CommonTableExpr>();
    public void AddWith(IEnumerable<CommonTableExpr> ctes) => (_with ??= new()).AddRange(ctes);
    public bool WithRecursive { get; set; }

    // primary kind
    public bool IsValues { get; set; }
    private List<List<Expr>>? _valuesRows;
    public IReadOnlyList<List<Expr>> ValuesRows => _valuesRows ?? (IReadOnlyList<List<Expr>>)System.Array.Empty<List<Expr>>();
    public void AddValuesRow(List<Expr> row) => (_valuesRows ??= new()).Add(row);
    public bool IsTableCommand { get; set; }                 // TABLE name
    public string? TableName { get; set; }

    public bool Distinct { get; set; }
    private List<Expr>? _distinctOn;
    public IReadOnlyList<Expr> DistinctOn => _distinctOn ?? (IReadOnlyList<Expr>)System.Array.Empty<Expr>();
    public void AddDistinctOn(Expr e) => (_distinctOn ??= new()).Add(e);
    public List<SelectItem> Items { get; } = new();         // ~always non-empty → kept eager

    public FromClause? From { get; set; }
    public Expr? Where { get; set; }
    private List<Expr>? _groupBy;
    public IReadOnlyList<Expr> GroupBy => _groupBy ?? (IReadOnlyList<Expr>)System.Array.Empty<Expr>();
    public void AddGroupBy(Expr e) => (_groupBy ??= new()).Add(e);
    public string? GroupByKind { get; set; }                 // ROLLUP / CUBE / GROUPING SETS / null
    public Expr? Having { get; set; }
    private List<NamedWindow>? _windows;
    public IReadOnlyList<NamedWindow> Windows => _windows ?? (IReadOnlyList<NamedWindow>)System.Array.Empty<NamedWindow>();
    public void AddWindow(NamedWindow w) => (_windows ??= new()).Add(w);

    public SetOperation? SetOp { get; set; }                 // UNION/INTERSECT/EXCEPT chain

    public List<OrderByItem> OrderBy { get; } = new();
    public string? Limit { get; set; }
    public string? Offset { get; set; }
    public Expr? LimitExpr { get; set; }    // parsed LIMIT / FETCH count, for constant-folding validation
    public Expr? OffsetExpr { get; set; }   // parsed OFFSET count
    private List<LockingClause>? _locking;
    public IReadOnlyList<LockingClause> Locking => _locking ?? (IReadOnlyList<LockingClause>)System.Array.Empty<LockingClause>();
    public void AddLocking(LockingClause lk) => (_locking ??= new()).Add(lk);
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
