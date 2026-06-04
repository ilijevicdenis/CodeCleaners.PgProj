using System.Collections.Generic;

namespace PgProj.Core.Contracts;

// The wire DTOs for the EP-RPC JSON contract. These are intentionally plain records with explicit,
// stable property names — they never expose an internal model/AST type directly, so the model layer
// stays free to evolve without breaking editors. Serialization is governed by JsonContract.Options
// (camelCase, omit-null, string enums).

/// <summary>Severity of a diagnostic, mirrored into the contract as a stable string set.</summary>
public enum ContractSeverity { Info, Warning, Error }

/// <summary>
/// One diagnostic, shaped so an editor can map it straight onto a Problems-panel entry. This is the
/// single diagnostic shape reused across every verb (build/analyze/compare/publish). <see cref="File"/>
/// is project-relative when known; <see cref="Line"/>/<see cref="Col"/> are 1-based, 0 when unknown.
/// </summary>
public sealed record DiagnosticDto
{
    public required string RuleId { get; init; }
    public required ContractSeverity Severity { get; init; }
    public required string Message { get; init; }

    /// <summary>The object/target the diagnostic is about (e.g. <c>afd.customers</c>), for grouping.</summary>
    public string? Target { get; init; }

    /// <summary>Project-relative source file, or null when the finding has no file anchor.</summary>
    public string? File { get; init; }

    /// <summary>1-based line, 0 when unknown.</summary>
    public int Line { get; init; }

    /// <summary>1-based column, 0 when unknown.</summary>
    public int Col { get; init; }
}

/// <summary>A counts roll-up shared by the verb reports (so a UI can render badges without re-counting).</summary>
public sealed record DiagnosticSummaryDto
{
    public int Errors { get; init; }
    public int Warnings { get; init; }
    public int Infos { get; init; }
    public int Total => Errors + Warnings + Infos;
}

// ---- build -----------------------------------------------------------------------------------

/// <summary>The <c>build --format json</c> payload.</summary>
public sealed record BuildReportDto
{
    public string SchemaVersion { get; init; } = JsonContract.SchemaVersion;
    public string Verb { get; init; } = "build";
    public required string Project { get; init; }
    public required bool Success { get; init; }
    public int FileCount { get; init; }
    public required ModelSummaryDto Model { get; init; }
    public required DiagnosticSummaryDto Summary { get; init; }
    public IReadOnlyList<DiagnosticDto> Diagnostics { get; init; } = new List<DiagnosticDto>();

    /// <summary>The model tree, included so an editor gets the build result and the outline in one call.</summary>
    public ModelTreeDto? ModelTree { get; init; }
}

/// <summary>Object counts by finely-modelled kind plus the generic raw-object bucket.</summary>
public sealed record ModelSummaryDto
{
    public int Schemas { get; init; }
    public int Tables { get; init; }
    public int Indexes { get; init; }
    public int Views { get; init; }
    public int Sequences { get; init; }
    public int Functions { get; init; }
    public int Objects { get; init; }
}

// ---- analyze ---------------------------------------------------------------------------------

/// <summary>The <c>analyze --format json</c> payload.</summary>
public sealed record AnalyzeReportDto
{
    public string SchemaVersion { get; init; } = JsonContract.SchemaVersion;
    public string Verb { get; init; } = "analyze";
    public required string Project { get; init; }
    public int RuleCount { get; init; }

    /// <summary>True when the analysis gate would block (errors, or warnings under <c>--strict</c>).</summary>
    public required bool Blocked { get; init; }
    public required DiagnosticSummaryDto Summary { get; init; }
    public IReadOnlyList<DiagnosticDto> Diagnostics { get; init; } = new List<DiagnosticDto>();
}

// ---- compare ---------------------------------------------------------------------------------

/// <summary>One planned change, as an editor/CI sees it.</summary>
public sealed record ChangeDto
{
    /// <summary>The change record type name (e.g. <c>CreateTableChange</c>) — stable kind discriminator.</summary>
    public required string Kind { get; init; }
    public required string Description { get; init; }
    public required bool Destructive { get; init; }

    /// <summary>Deploy-ordering phase (lower runs first); lets a UI group the plan the way it deploys.</summary>
    public int Phase { get; init; }
}

/// <summary>The <c>compare --format json</c> payload.</summary>
public sealed record CompareReportDto
{
    public string SchemaVersion { get; init; } = JsonContract.SchemaVersion;
    public string Verb { get; init; } = "compare";
    public required string Project { get; init; }

    /// <summary>True when there are no differences (the target already matches the project).</summary>
    public required bool InSync { get; init; }
    public int ChangeCount { get; init; }
    public int DestructiveCount { get; init; }
    public IReadOnlyList<ChangeDto> Changes { get; init; } = new List<ChangeDto>();
}

// ---- publish --dry-run -----------------------------------------------------------------------

/// <summary>The <c>publish --dry-run --format json</c> payload: the plan plus the deploy script text.</summary>
public sealed record PublishPlanDto
{
    public string SchemaVersion { get; init; } = JsonContract.SchemaVersion;
    public string Verb { get; init; } = "publish";
    public required string Project { get; init; }
    public required bool DryRun { get; init; }
    public required bool InSync { get; init; }
    public int ChangeCount { get; init; }
    public int DestructiveCount { get; init; }
    public IReadOnlyList<ChangeDto> Changes { get; init; } = new List<ChangeDto>();

    /// <summary>The generated deploy script (the same text the human dry-run prints).</summary>
    public required string Script { get; init; }
}

// ---- model-tree ------------------------------------------------------------------------------

/// <summary>A node in the model tree: one schema object, with its source anchor for go-to-definition.</summary>
public sealed record ModelTreeNodeDto
{
    /// <summary>The object kind, e.g. <c>schema</c>, <c>table</c>, <c>view</c>, <c>function</c>, <c>trigger</c>.</summary>
    public required string Kind { get; init; }
    public required string Schema { get; init; }
    public required string Name { get; init; }

    /// <summary>Schema-qualified display name (plus arg types for functions), for tree labels.</summary>
    public required string QualifiedName { get; init; }

    /// <summary>Project-relative source file, when the object was located in the project sources.</summary>
    public string? File { get; init; }

    /// <summary>1-based line of the object's CREATE statement, 0 when unknown.</summary>
    public int Line { get; init; }

    /// <summary>1-based column, 0 when unknown.</summary>
    public int Col { get; init; }

    /// <summary>Child nodes (e.g. a table's columns), for tree views. Empty when the kind has no children.</summary>
    public IReadOnlyList<ModelTreeNodeDto> Children { get; init; } = new List<ModelTreeNodeDto>();
}

/// <summary>The <c>model-tree</c> / <c>build --format json</c> tree payload: every object the model holds.</summary>
public sealed record ModelTreeDto
{
    public string SchemaVersion { get; init; } = JsonContract.SchemaVersion;
    public string Verb { get; init; } = "model-tree";
    public required string Project { get; init; }
    public required ModelSummaryDto Summary { get; init; }
    public IReadOnlyList<ModelTreeNodeDto> Nodes { get; init; } = new List<ModelTreeNodeDto>();
}
