using System.Collections.Generic;
using System.Linq;
using PgProj.Core.Analysis.Rules;
using PgProj.Core.Ast;

namespace PgProj.Core.Analysis;

/// <summary>A static-analysis rule: inspects the AST and yields findings.</summary>
public interface IAnalysisRule
{
    string Id { get; }
    string Title { get; }
    IEnumerable<Diagnostic> Analyze(SqlScript script);
}

/// <summary>
/// Runs a set of <see cref="IAnalysisRule"/>s over a parsed <see cref="SqlScript"/>. This is the
/// entry point for the safety checks: because rules walk a real AST (not a flat model) they can
/// reason about structure — e.g. what statements a function body runs.
/// </summary>
public sealed class SqlAnalyzer
{
    private readonly IReadOnlyList<IAnalysisRule> _rules;

    public SqlAnalyzer(IEnumerable<IAnalysisRule> rules) => _rules = rules.ToList();

    /// <summary>The built-in rule set, focused on function safety.</summary>
    public static SqlAnalyzer Default() => new(new IAnalysisRule[]
    {
        // Function safety (header + body)
        new SecurityDefinerSearchPathRule(),
        new DynamicSqlRule(),
        new UnguardedMutationRule(),
        new SchemaMutationInFunctionRule(),
        new MissingVolatilityRule(),
        // Query- and control-flow-structure rules (exploit the deep AST)
        new JoinWithoutConditionRule(),
        new SelectStarInViewRule(),
        new NotInSubqueryRule(),
        new LimitWithoutOrderByRule(),
        new DmlInLoopRule(),
        new SecurityDefinerWritesRule(),
    });

    public IReadOnlyList<IAnalysisRule> Rules => _rules;

    public IReadOnlyList<Diagnostic> Analyze(SqlScript script) =>
        _rules.SelectMany(r => r.Analyze(script)).ToList();
}
