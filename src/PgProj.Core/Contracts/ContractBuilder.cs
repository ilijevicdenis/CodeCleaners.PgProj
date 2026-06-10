using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PgProj.Core.Analysis;
using PgProj.Core.Comparison;
using PgProj.Core.Model;
using PgProj.Core.Project;
using PgProj.Core.Syntax;

namespace PgProj.Core.Contracts;

/// <summary>
/// Builds the EP-RPC JSON report DTOs from the same Core operations the human CLI verbs use. This is the
/// one place that turns a build/analysis/compare/publish into the editor contract, so the CLI verb and a
/// future <c>pgproj serve</c> host (and the unit tests) share identical payloads.
/// </summary>
public static class ContractBuilder
{
    /// <summary>Builds the <c>build --format json</c> report (optionally including the model tree).</summary>
    public static async Task<BuildReportDto> BuildAsync(DatabaseProject project, bool includeTree = true)
    {
        var result = await project.BuildAsync();
        // Use UnifiedDiagnostics (carries related locations, #63) → ToDto preserves the Related field on
        // the wire; source anchors come from the build itself (#45), so no dedicated re-parse pass.
        var diagnostics = result.UnifiedDiagnostics.Select(ContractMappers.ToDto).ToList();
        var positions = includeTree ? result.Positions : null;

        return new BuildReportDto
        {
            Project = project.Name,
            Success = result.UnifiedDiagnostics.Count == 0,
            FileCount = result.Files.Count,
            Model = ContractMappers.SummaryOf(result.Model),
            Summary = ContractMappers.SummaryOf(diagnostics),
            Diagnostics = diagnostics,
            ModelTree = includeTree ? ModelTreeBuilder.Build(result.Model, project.Name, positions) : null,
        };
    }

    /// <summary>Builds the <c>model-tree --format json</c> payload (build + flatten, with positions).</summary>
    public static async Task<ModelTreeDto> ModelTreeAsync(DatabaseProject project)
    {
        var result = await project.BuildAsync();
        // Positions are a byproduct of the build — the dedicated re-parse pass is gone (#45).
        return ModelTreeBuilder.Build(result.Model, project.Name, result.Positions);
    }

    /// <summary>
    /// Builds the <c>analyze --format json</c> report. <paramref name="strict"/> mirrors the gate;
    /// <paramref name="config"/> applies per-rule enable/severity overrides (null → all rules at defaults).
    /// Per-file rules (built-in + <paramref name="externalRules"/>) run over each parsed file; model rules
    /// (built-in <see cref="ModelAnalyzer"/> + <paramref name="externalModelRules"/>) run once over the
    /// merged model lowered from the same parses, so cross-file relationships are visible to them.
    /// </summary>
    public static AnalyzeReportDto Analyze(DatabaseProject project, bool strict, AnalysisConfig? config = null,
        IReadOnlyList<IPgRule>? externalRules = null, IReadOnlyList<IModelRule>? externalModelRules = null)
    {
        var positions = SourcePositionIndex.Build(project);
        var analyzer = new PgAnalyzer(config);
        var cfg = config ?? AnalysisConfig.Empty;
        var rules = externalRules ?? (IReadOnlyList<IPgRule>)System.Array.Empty<IPgRule>();
        var modelRules = externalModelRules ?? (IReadOnlyList<IModelRule>)System.Array.Empty<IModelRule>();
        var findings = new List<Diagnostic>();
        var model = new DatabaseModel();
        var modelBuilder = new ModelBuilder();
        foreach (var file in project.ResolveSqlFiles())
        {
            var parsed = new PgParser().Parse(SourceReader.ReadAllText(file));
            findings.AddRange(analyzer.Analyze(parsed));
            findings.AddRange(ExternalRules.Run(rules, parsed, cfg));   // EP-ANALYSIS+ #79 external rule packs
            modelBuilder.Build(parsed, model);   // accumulate the merged model for the model-level pass
        }

        // Model-level pass: cross-file rules over the merged model (PG014 + external IModelRules).
        findings.AddRange(new ModelAnalyzer(config).Analyze(model));
        findings.AddRange(ExternalModelRules.Run(modelRules, model, cfg));

        var diags = findings.Select(f => ContractMappers.ToDto(f, positions)).ToList();
        var summary = ContractMappers.SummaryOf(diags);
        var blocked = summary.Errors > 0 || (strict && summary.Warnings > 0);

        return new AnalyzeReportDto
        {
            Project = project.Name,
            RuleCount = PgAnalyzer.RuleCount + ModelAnalyzer.RuleCount + rules.Count + modelRules.Count,
            Blocked = blocked,
            Summary = summary,
            Diagnostics = diags,
        };
    }

    /// <summary>Builds the <c>compare --format json</c> report from an already-read target model.</summary>
    public static CompareReportDto Compare(DatabaseModel source, DatabaseModel target, string projectName, bool allowDrops)
    {
        var changes = new SchemaComparer().Compare(source, target, new ComparerOptions { DropObjectsNotInSource = allowDrops });
        var dtos = changes.Select(ContractMappers.ToDto).ToList();
        return new CompareReportDto
        {
            Project = projectName,
            InSync = changes.Count == 0,
            ChangeCount = changes.Count,
            DestructiveCount = changes.Count(c => c.IsDestructive),
            Changes = dtos,
        };
    }

    /// <summary>Builds the <c>publish --dry-run --format json</c> report (plan + generated script).</summary>
    public static PublishPlanDto PublishPlan(DatabaseModel source, DatabaseModel target, string projectName,
        bool allowDrops, bool wrapInTransaction)
    {
        var changes = new SchemaComparer().Compare(source, target, new ComparerOptions { DropObjectsNotInSource = allowDrops });
        var script = new DeployScriptGenerator().Generate(changes, new DeployOptions { WrapInTransaction = wrapInTransaction });
        return new PublishPlanDto
        {
            Project = projectName,
            DryRun = true,
            InSync = changes.Count == 0,
            ChangeCount = changes.Count,
            DestructiveCount = changes.Count(c => c.IsDestructive),
            Changes = changes.Select(ContractMappers.ToDto).ToList(),
            Script = script,
        };
    }
}
