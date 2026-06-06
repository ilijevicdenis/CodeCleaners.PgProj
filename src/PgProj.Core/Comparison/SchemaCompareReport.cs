using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PgProj.Core.Comparison;

/// <summary>
/// The <c>compare --output diff.json</c> wire shape (EP-SCHEMACOMPARE): a structured, selectable diff a UI
/// renders as a side-by-side, checkable change list. Each <see cref="SchemaCompareChangeDto"/> carries the
/// stable id, object-type, included/excluded state, and destructiveness the UI needs to drive selection;
/// the report carries the resolved source/target endpoints so the UI can label the two sides. Serialized
/// with the same conventions as the EP-RPC contract (camelCase, string enums, deterministic order).
/// </summary>
public sealed record SchemaCompareReportDto
{
    /// <summary>Contract schema version (matches the EP-RPC JSON contract major).</summary>
    public string SchemaVersion { get; init; } = "1.0";

    public string Verb { get; init; } = "compare";

    /// <summary>The source (left) endpoint — the desired state.</summary>
    public required SchemaCompareEndpointDto Source { get; init; }

    /// <summary>The target (right) endpoint — the actual state the changes migrate toward the source.</summary>
    public required SchemaCompareEndpointDto Target { get; init; }

    /// <summary>True when source and target are identical (no changes).</summary>
    public required bool InSync { get; init; }

    /// <summary>Total number of changes (included and excluded).</summary>
    public int ChangeCount { get; init; }

    /// <summary>Number of currently-included changes (the subset a script/apply would act on).</summary>
    public int IncludedCount { get; init; }

    /// <summary>Number of destructive changes across the whole set.</summary>
    public int DestructiveCount { get; init; }

    /// <summary>Distinct object-types present, sorted — the UI's filter vocabulary for this diff.</summary>
    public IReadOnlyList<string> ObjectTypes { get; init; } = new List<string>();

    /// <summary>Every change, in deploy order.</summary>
    public IReadOnlyList<SchemaCompareChangeDto> Changes { get; init; } = new List<SchemaCompareChangeDto>();
}

/// <summary>A resolved compare endpoint, for the UI to label each side of the diff.</summary>
public sealed record SchemaCompareEndpointDto
{
    /// <summary>What the spec resolved to: <c>project</c>, <c>package</c>, or <c>liveDatabase</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Human label (project/package name, or "(database)").</summary>
    public required string DisplayName { get; init; }

    /// <summary>Build problems for a project source (empty for packages/live DBs).</summary>
    public IReadOnlyList<string> BuildDiagnostics { get; init; } = new List<string>();
}

/// <summary>One checkable change in the diff, shaped for a side-by-side review UI.</summary>
public sealed record SchemaCompareChangeDto
{
    /// <summary>Stable, deterministic id — refer to this change across re-compares and from a saved selection.</summary>
    public required string Id { get; init; }

    /// <summary>The change-record type name (e.g. <c>CreateTableChange</c>) — a stable kind discriminator.</summary>
    public required string Kind { get; init; }

    /// <summary>The coarse object-type (e.g. <c>table</c>, <c>index</c>, <c>extension</c>) the filters operate on.</summary>
    public required string ObjectType { get; init; }

    /// <summary>One-line human description of the change.</summary>
    public required string Description { get; init; }

    /// <summary>Whether this change is currently part of the subset to script/apply.</summary>
    public required bool Included { get; init; }

    /// <summary>Whether applying the change can lose data/objects.</summary>
    public required bool Destructive { get; init; }

    /// <summary>Deploy-ordering phase (lower runs first).</summary>
    public int Phase { get; init; }

    /// <summary>The exact SQL this change emits — shown in the UI's detail pane / used to script a subset.</summary>
    public required string Sql { get; init; }
}

/// <summary>Builds and serializes the <see cref="SchemaCompareReportDto"/> from a compare result.</summary>
public static class SchemaCompareReport
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Builds the report DTO from a resolving compare result (source/target endpoints + change set).</summary>
    public static SchemaCompareReportDto Build(SchemaCompareResult result) =>
        new()
        {
            Source = Endpoint(result.Source),
            Target = Endpoint(result.Target),
            InSync = result.ChangeSet.InSync,
            ChangeCount = result.ChangeSet.Count,
            IncludedCount = result.ChangeSet.IncludedCount,
            DestructiveCount = result.ChangeSet.DestructiveCount,
            ObjectTypes = result.ChangeSet.ObjectTypes,
            Changes = result.ChangeSet.Changes.Select(Change).ToList(),
        };

    /// <summary>Builds the report DTO from a bare change set, labelling both sides with given names/kinds.</summary>
    public static SchemaCompareReportDto Build(
        SchemaChangeSet changeSet,
        SchemaCompareEndpointDto source,
        SchemaCompareEndpointDto target) =>
        new()
        {
            Source = source,
            Target = target,
            InSync = changeSet.InSync,
            ChangeCount = changeSet.Count,
            IncludedCount = changeSet.IncludedCount,
            DestructiveCount = changeSet.DestructiveCount,
            ObjectTypes = changeSet.ObjectTypes,
            Changes = changeSet.Changes.Select(Change).ToList(),
        };

    /// <summary>Serializes the report to its stable JSON form (camelCase, string enums, indented).</summary>
    public static string Serialize(SchemaCompareReportDto report) => JsonSerializer.Serialize(report, JsonOptions);

    private static SchemaCompareEndpointDto Endpoint(Cli.ResolvedEndpoint e) => new()
    {
        Kind = KindToken(e.Kind),
        DisplayName = e.DisplayName,
        BuildDiagnostics = e.BuildDiagnostics,
    };

    private static SchemaCompareChangeDto Change(SelectableChange c) => new()
    {
        Id = c.Id,
        Kind = c.Kind,
        ObjectType = c.ObjectType,
        Description = c.Description,
        Included = c.Included,
        Destructive = c.IsDestructive,
        Phase = c.Phase,
        Sql = c.Change.ToSql(),
    };

    private static string KindToken(Cli.EndpointKind kind) => kind switch
    {
        Cli.EndpointKind.Project => "project",
        Cli.EndpointKind.Package => "package",
        Cli.EndpointKind.LiveDatabase => "liveDatabase",
        _ => "unknown",
    };
}
