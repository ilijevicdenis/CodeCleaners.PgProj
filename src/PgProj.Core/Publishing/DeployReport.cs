using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using PgProj.Core.Comparison;
using PgProj.Core.Comparison.Risk;

namespace PgProj.Core.Publishing;

/// <summary>
/// EP-DEPLOYREPORT (#141) — the machine-readable planned-change report for a publish: exactly what a
/// <c>pgproj publish</c> against a live target WOULD change, without applying anything. The SqlPackage
/// <b>DeployReport</b> analogue: the artifact a CI approval gate reviews before authorizing the deploy.
/// Built from the same <see cref="PublishPlan"/> a real publish runs, so the reported change set is the
/// applied change set by construction. Serialized with the EP-RPC conventions (camelCase, string enums,
/// deterministic deploy order); <see cref="DeployReportBuilder.SerializeXml"/> emits the equivalent XML
/// for SqlPackage-style consumers.
/// </summary>
public sealed record DeployReportDto
{
    /// <summary>Contract schema version (matches the EP-RPC JSON contract major).</summary>
    public string SchemaVersion { get; init; } = "1.0";

    public string Verb { get; init; } = "deployReport";

    /// <summary>The source (desired-state) endpoint.</summary>
    public required SchemaCompareEndpointDto Source { get; init; }

    /// <summary>The live target the plan was computed against.</summary>
    public required SchemaCompareEndpointDto Target { get; init; }

    /// <summary>True when the publish would change nothing (and no deploy scripts would run).</summary>
    public required bool InSync { get; init; }

    public required int ChangeCount { get; init; }

    public required int DestructiveCount { get; init; }

    /// <summary>True when ANY operation classifies as <see cref="RiskLevel.DataLoss"/> — gate fail-closed on this.</summary>
    public required bool BlocksOnDataLoss { get; init; }

    /// <summary>Distinct object-types present, sorted — the report reader's filter vocabulary.</summary>
    public IReadOnlyList<string> ObjectTypes { get; init; } = new List<string>();

    /// <summary>Whether the project splices a pre-deploy script before the diff.</summary>
    public required bool HasPreDeployScript { get; init; }

    /// <summary>Whether the project splices a post-deploy script after the diff.</summary>
    public required bool HasPostDeployScript { get; init; }

    /// <summary>
    /// The apply path the SAME inputs would take: <c>phased</c> (parallel, phase-level atomicity) or
    /// <c>wholeScript</c> (one transaction). Pre/post-deploy scripts force <c>wholeScript</c>.
    /// </summary>
    public required string ApplyStrategy { get; init; }

    /// <summary>Every planned operation, in deploy order.</summary>
    public IReadOnlyList<DeployReportOperationDto> Operations { get; init; } = new List<DeployReportOperationDto>();
}

/// <summary>One planned operation: what, in which order, and how risky.</summary>
public sealed record DeployReportOperationDto
{
    /// <summary>1-based deploy-ordered position.</summary>
    public required int Position { get; init; }

    /// <summary>Stable change id (same scheme as the schema-compare report).</summary>
    public required string Id { get; init; }

    /// <summary>The change-record type name (e.g. <c>CreateTableChange</c>).</summary>
    public required string Kind { get; init; }

    /// <summary>The coarse object-type (<c>table</c>, <c>index</c>, …).</summary>
    public required string ObjectType { get; init; }

    /// <summary>One-line human description.</summary>
    public required string Description { get; init; }

    /// <summary><c>Safe</c> / <c>Warning</c> / <c>Dangerous</c> / <c>DataLoss</c> (RiskAnalyzer verdict).</summary>
    public required string RiskLevel { get; init; }

    /// <summary>Why the risk level was assigned.</summary>
    public required string RiskRationale { get; init; }

    public required bool Destructive { get; init; }

    public bool RequiresTableRewrite { get; init; }

    public bool RequiresExclusiveLock { get; init; }
}

/// <summary>Builds and serializes <see cref="DeployReportDto"/> from a computed <see cref="PublishPlan"/>.</summary>
public static class DeployReportBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static DeployReportDto Build(
        PublishPlan plan,
        SchemaCompareEndpointDto source,
        SchemaCompareEndpointDto target,
        bool parallelRequested = false)
    {
        // Reuse the schema-compare wrapper for ids/kind/object-type/description so the two report
        // families describe the same change identically; risk comes from the same analyzer the
        // editors badge with. Dedup the stable ids exactly like SchemaChangeSet.Build does.
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var operations = new List<DeployReportOperationDto>(plan.Changes.Count);
        foreach (var (change, index) in plan.Changes.Select((c, i) => (c, i)))
        {
            var hash = SelectableChange.HashOf(SelectableChange.Signature(change));
            var n = seen.TryGetValue(hash, out var c0) ? c0 + 1 : 0;
            seen[hash] = n;
            var selectable = new SelectableChange(n == 0 ? hash : $"{hash}#{n}", change, included: true);
            var risk = RiskAnalyzer.Default.Classify(change);

            operations.Add(new DeployReportOperationDto
            {
                Position = index + 1,
                Id = selectable.Id,
                Kind = selectable.Kind,
                ObjectType = selectable.ObjectType,
                Description = selectable.Description,
                RiskLevel = risk.Level.ToString(),
                RiskRationale = risk.Rationale,
                Destructive = selectable.IsDestructive,
                RequiresTableRewrite = risk.RequiresTableRewrite,
                RequiresExclusiveLock = risk.RequiresExclusiveLock,
            });
        }

        return new DeployReportDto
        {
            Source = source,
            Target = target,
            InSync = plan.NothingToDo,
            ChangeCount = plan.ChangeCount,
            DestructiveCount = plan.DestructiveCount,
            BlocksOnDataLoss = operations.Any(o => o.RiskLevel == nameof(Comparison.Risk.RiskLevel.DataLoss)),
            ObjectTypes = operations.Select(o => o.ObjectType).Distinct(StringComparer.Ordinal)
                .OrderBy(t => t, StringComparer.Ordinal).ToList(),
            HasPreDeployScript = plan.HasPreDeployScript,
            HasPostDeployScript = plan.HasPostDeployScript,
            ApplyStrategy = parallelRequested && !plan.HasDeployScripts ? "phased" : "wholeScript",
            Operations = operations,
        };
    }

    /// <summary>Stable JSON form (camelCase, string enums, indented — same conventions as compare).</summary>
    public static string Serialize(DeployReportDto report) => JsonSerializer.Serialize(report, JsonOptions);

    /// <summary>
    /// Equivalent XML for SqlPackage-DeployReport-style consumers: same element names as the JSON
    /// properties, operations in the same deterministic order.
    /// </summary>
    public static string SerializeXml(DeployReportDto report)
    {
        var root = new XElement("deployReport",
            new XAttribute("schemaVersion", report.SchemaVersion),
            new XElement("source", new XAttribute("kind", report.Source.Kind), new XAttribute("displayName", report.Source.DisplayName)),
            new XElement("target", new XAttribute("kind", report.Target.Kind), new XAttribute("displayName", report.Target.DisplayName)),
            new XElement("inSync", report.InSync),
            new XElement("changeCount", report.ChangeCount),
            new XElement("destructiveCount", report.DestructiveCount),
            new XElement("blocksOnDataLoss", report.BlocksOnDataLoss),
            new XElement("hasPreDeployScript", report.HasPreDeployScript),
            new XElement("hasPostDeployScript", report.HasPostDeployScript),
            new XElement("applyStrategy", report.ApplyStrategy),
            new XElement("objectTypes", report.ObjectTypes.Select(t => new XElement("objectType", t))),
            new XElement("operations", report.Operations.Select(o => new XElement("operation",
                new XAttribute("position", o.Position),
                new XAttribute("id", o.Id),
                new XAttribute("kind", o.Kind),
                new XAttribute("objectType", o.ObjectType),
                new XAttribute("riskLevel", o.RiskLevel),
                new XAttribute("destructive", o.Destructive),
                new XAttribute("requiresTableRewrite", o.RequiresTableRewrite),
                new XAttribute("requiresExclusiveLock", o.RequiresExclusiveLock),
                new XElement("description", o.Description),
                new XElement("riskRationale", o.RiskRationale)))));
        return new XDocument(new XDeclaration("1.0", "utf-8", null), root).ToString();
    }
}
