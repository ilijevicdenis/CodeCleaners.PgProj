using System.Collections.Generic;
using System.Linq;
using PgProj.Core.Ast;

namespace PgProj.Core.Analysis.Rules;

/// <summary>
/// PG001 — a SECURITY DEFINER function without an explicit <c>SET search_path</c> is a classic
/// privilege-escalation vector: it runs with the owner's rights but resolves unqualified names
/// against the caller's search_path.
/// </summary>
public sealed class SecurityDefinerSearchPathRule : IAnalysisRule
{
    public string Id => "PG001";
    public string Title => "SECURITY DEFINER without SET search_path";

    public IEnumerable<Diagnostic> Analyze(SqlScript script)
    {
        foreach (var fn in SqlTree.Descendants<CreateFunctionStatement>(script))
        {
            var h = fn.Header;
            if (h.Security != "DEFINER") continue;
            var hasSearchPath = h.SetClauses.Any(s => s.Contains("search_path", System.StringComparison.OrdinalIgnoreCase));
            if (!hasSearchPath)
                yield return new Diagnostic(Id, DiagnosticSeverity.Warning,
                    "SECURITY DEFINER function does not pin search_path (SET search_path = …); unqualified names resolve against the caller's path.",
                    $"{h.Schema}.{h.Name}");
        }
    }
}

/// <summary>PG002 — dynamic SQL (EXECUTE) inside a function body is a SQL-injection surface.</summary>
public sealed class DynamicSqlRule : IAnalysisRule
{
    public string Id => "PG002";
    public string Title => "Dynamic SQL in function body";

    public IEnumerable<Diagnostic> Analyze(SqlScript script)
    {
        foreach (var fn in SqlTree.Descendants<CreateFunctionStatement>(script))
        {
            if (fn.Body.Statements.OfType<DynamicSqlStatementNode>().Any())
                yield return new Diagnostic(Id, DiagnosticSeverity.Warning,
                    "Function builds/runs dynamic SQL via EXECUTE; ensure inputs are quoted with format()/quote_ident()/quote_literal().",
                    $"{fn.Header.Schema}.{fn.Header.Name}");
        }
    }
}

/// <summary>PG003 — an UPDATE/DELETE with no WHERE inside a function mutates every row.</summary>
public sealed class UnguardedMutationRule : IAnalysisRule
{
    public string Id => "PG003";
    public string Title => "Unguarded UPDATE/DELETE in function body";

    public IEnumerable<Diagnostic> Analyze(SqlScript script)
    {
        foreach (var fn in SqlTree.Descendants<CreateFunctionStatement>(script))
        {
            foreach (var dml in fn.Body.Statements.OfType<DmlStatementNode>())
            {
                if (dml.Verb is "UPDATE" or "DELETE" && !dml.HasWhere)
                    yield return new Diagnostic(Id, DiagnosticSeverity.Warning,
                        $"{dml.Verb} without a WHERE clause affects every row of {dml.TargetTable ?? "the target table"}.",
                        $"{fn.Header.Schema}.{fn.Header.Name}");
            }
        }
    }
}

/// <summary>PG004 — schema mutation (DROP/ALTER/CREATE/GRANT/TRUNCATE) from inside a function body.</summary>
public sealed class SchemaMutationInFunctionRule : IAnalysisRule
{
    public string Id => "PG004";
    public string Title => "Schema mutation in function body";

    public IEnumerable<Diagnostic> Analyze(SqlScript script)
    {
        foreach (var fn in SqlTree.Descendants<CreateFunctionStatement>(script))
        {
            foreach (var m in fn.Body.Statements.OfType<SchemaMutationStatementNode>())
                yield return new Diagnostic(Id, DiagnosticSeverity.Warning,
                    $"Function performs schema mutation ({m.Verb}); DDL from the data path is hard to audit and can break replication.",
                    $"{fn.Header.Schema}.{fn.Header.Name}");
        }
    }
}

/// <summary>PG005 — a function with no declared volatility defaults to VOLATILE, blocking the planner.</summary>
public sealed class MissingVolatilityRule : IAnalysisRule
{
    public string Id => "PG005";
    public string Title => "Volatility not declared";

    public IEnumerable<Diagnostic> Analyze(SqlScript script)
    {
        foreach (var fn in SqlTree.Descendants<CreateFunctionStatement>(script))
        {
            if (fn.Header.IsProcedure) continue;
            if (fn.Header.Volatility is null)
                yield return new Diagnostic(Id, DiagnosticSeverity.Info,
                    "Function does not declare IMMUTABLE/STABLE/VOLATILE; it defaults to VOLATILE, which the planner cannot optimize or inline.",
                    $"{fn.Header.Schema}.{fn.Header.Name}");
        }
    }
}
