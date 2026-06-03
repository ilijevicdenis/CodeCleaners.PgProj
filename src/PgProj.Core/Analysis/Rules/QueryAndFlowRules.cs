using System.Collections.Generic;
using System.Linq;
using PgProj.Core.Ast;

namespace PgProj.Core.Analysis.Rules;

/// <summary>
/// PG006 — a JOIN with neither ON nor USING (and not CROSS/NATURAL) is an accidental Cartesian
/// product. Only detectable now that FROM/JOINs are structured.
/// </summary>
public sealed class JoinWithoutConditionRule : IAnalysisRule
{
    public string Id => "PG006";
    public string Title => "JOIN without ON/USING (accidental cross join)";

    public IEnumerable<Diagnostic> Analyze(SqlScript script)
    {
        foreach (var q in SqlTree.Descendants<SelectQuery>(script))
            foreach (var rel in q.From?.Relations ?? Enumerable.Empty<TableReference>())
                foreach (var j in rel.Joins)
                    if (j.JoinType is not "CROSS" and not "NATURAL" && j.On is null && j.Using.Count == 0)
                        yield return new Diagnostic(Id, DiagnosticSeverity.Warning,
                            $"{j.JoinType} JOIN has no ON or USING — this is a Cartesian product. Add a join condition or use CROSS JOIN explicitly.",
                            j.Right.TableName ?? "(subquery)");
    }
}

/// <summary>PG007 — <c>SELECT *</c> in a view silently changes shape when the underlying columns change.</summary>
public sealed class SelectStarInViewRule : IAnalysisRule
{
    public string Id => "PG007";
    public string Title => "SELECT * in a view";

    public IEnumerable<Diagnostic> Analyze(SqlScript script)
    {
        foreach (var v in SqlTree.Descendants<CreateViewStatement>(script))
            if (v.Query is { } q && q.Items.Any(i => i.IsStar))
                yield return new Diagnostic(Id, DiagnosticSeverity.Warning,
                    "View uses SELECT * — its columns change implicitly when the base table changes. List columns explicitly.",
                    $"{v.Schema}.{v.Name}");
    }
}

/// <summary>
/// PG008 — <c>NOT IN (subquery)</c> returns zero rows if the subquery yields any NULL (three-valued
/// logic). A perennial correctness bug; prefer <c>NOT EXISTS</c>.
/// </summary>
public sealed class NotInSubqueryRule : IAnalysisRule
{
    public string Id => "PG008";
    public string Title => "NOT IN (subquery) NULL pitfall";

    public IEnumerable<Diagnostic> Analyze(SqlScript script)
    {
        foreach (var inx in SqlTree.Descendants<InExpr>(script))
            if (inx.Negated && inx.Subquery is not null)
                yield return new Diagnostic(Id, DiagnosticSeverity.Warning,
                    "NOT IN (subquery) returns no rows if the subquery produces any NULL. Use NOT EXISTS instead.",
                    (inx.Operand as IdentifierExpr)?.Name ?? "predicate");
    }
}

/// <summary>PG009 — <c>LIMIT</c> without <c>ORDER BY</c> returns an arbitrary, nondeterministic subset.</summary>
public sealed class LimitWithoutOrderByRule : IAnalysisRule
{
    public string Id => "PG009";
    public string Title => "LIMIT without ORDER BY";

    public IEnumerable<Diagnostic> Analyze(SqlScript script)
    {
        foreach (var q in SqlTree.Descendants<SelectQuery>(script))
            if (q.Limit is not null && q.OrderBy.Count == 0 && q.SetOp is null)
                yield return new Diagnostic(Id, DiagnosticSeverity.Info,
                    "LIMIT without ORDER BY returns an arbitrary subset; add ORDER BY for deterministic results.",
                    "query");
    }
}

/// <summary>PG010 — DML inside a loop is per-row work; a set-based statement is usually far faster.</summary>
public sealed class DmlInLoopRule : IAnalysisRule
{
    public string Id => "PG010";
    public string Title => "DML inside a loop (N+1)";

    public IEnumerable<Diagnostic> Analyze(SqlScript script)
    {
        foreach (var fn in SqlTree.Descendants<CreateFunctionStatement>(script))
            foreach (var loop in SqlTree.Descendants<LoopStatement>(fn))
            {
                var hasDml = SqlTree.Descendants<DmlStatementNode>(loop).Any()
                          || SqlTree.Descendants<DynamicSqlStatementNode>(loop).Any();
                if (hasDml)
                {
                    yield return new Diagnostic(Id, DiagnosticSeverity.Info,
                        $"DML runs inside a {loop.Kind} loop (row-by-row). Consider a single set-based statement.",
                        $"{fn.Header.Schema}.{fn.Header.Name}");
                    break; // one finding per function is enough
                }
            }
    }
}

/// <summary>PG011 — a SECURITY DEFINER function that writes runs those writes with the owner's rights.</summary>
public sealed class SecurityDefinerWritesRule : IAnalysisRule
{
    public string Id => "PG011";
    public string Title => "SECURITY DEFINER function performs writes";

    private static readonly HashSet<string> Writes = new() { "INSERT", "UPDATE", "DELETE", "TRUNCATE" };

    public IEnumerable<Diagnostic> Analyze(SqlScript script)
    {
        foreach (var fn in SqlTree.Descendants<CreateFunctionStatement>(script))
        {
            if (fn.Header.Security != "DEFINER") continue;
            var writes = SqlTree.Descendants<DmlStatementNode>(fn.Body)
                .Where(d => Writes.Contains(d.Verb)).Select(d => d.Verb).Distinct().ToList();
            if (writes.Count > 0)
                yield return new Diagnostic(Id, DiagnosticSeverity.Warning,
                    $"SECURITY DEFINER function performs writes ({string.Join("/", writes)}) with the owner's privileges; confirm callers cannot abuse it.",
                    $"{fn.Header.Schema}.{fn.Header.Name}");
        }
    }
}
