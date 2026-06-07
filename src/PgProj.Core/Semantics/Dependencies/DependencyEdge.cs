namespace PgProj.Core.Semantics.Dependencies;

/// <summary>
/// How binding a dependency edge to <em>deploy ordering</em> is allowed. Postgres only enforces some
/// orderings; others are merely preferred, and a third class is observed but unsafe to order on.
/// </summary>
public enum DependencyKind
{
    /// <summary>
    /// Postgres-enforced ordering: the dependent cannot be created until the referent exists. A view's
    /// SELECT must resolve every table/function it reads, a column typed as a user type needs that type,
    /// a trigger names its function. Topo-sort MUST honor these; a cycle over them is an error.
    /// </summary>
    Hard,

    /// <summary>
    /// Preferred-but-not-required ordering (cosmetic / grouping, e.g. "create the parent table before its
    /// comment"). Honored when acyclic; a cycle over <em>only</em> soft edges is a warning, never an error,
    /// because Postgres will still accept the script in any order.
    /// </summary>
    Soft,

    /// <summary>
    /// A dependency discovered from a runtime construct — dynamic SQL inside a function body
    /// (<c>EXECUTE 'SELECT … FROM other'</c>), a string-built object name. It is surfaced for
    /// visualization/impact analysis but is NEVER used for ordering: the reference only fires at call
    /// time, after every object already exists, so ordering on it would be wrong (and could manufacture
    /// false cycles). Runtime edges are excluded from cycle detection.
    /// </summary>
    Runtime,
}

/// <summary>
/// One directed dependency edge: <see cref="FromKey"/> (the dependent — a view, function, trigger, …)
/// depends on <see cref="ToKey"/> (the referent — a table, type, function, …). Keys are
/// <see cref="SymbolEntry.Key"/> values, so an edge survives a rebuild of the same model. The edge
/// carries a human-readable <see cref="Reason"/> for diagnostics/visualization.
/// </summary>
public sealed record DependencyEdge(string FromKey, string ToKey, DependencyKind Kind, string Reason)
{
    public override string ToString() => $"{FromKey} --{Kind}--> {ToKey}  ({Reason})";
}
