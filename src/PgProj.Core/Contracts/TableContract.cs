using System.Collections.Generic;

namespace PgProj.Core.Contracts;

// The wire DTOs for the graphical table designer (epic EP-DESIGNER, issue #26). A single table's
// structured model, shaped so a webview form can bind to it directly. Like the rest of the contract
// (see JsonContract), these are plain records with stable, camelCase-serialised names — they never
// leak an internal model type, so TableDefinition / IndexDefinition can evolve without breaking the
// designer. The mapping model<->DTO lives in TableContractMapper and the round-trip (parse -> DTO ->
// .sql via SqlEmitter) in TableDesigner, so .sql generation stays in the engine's single emitter.

/// <summary>One column, as the designer form edits it. Mirrors <c>ColumnDefinition</c> field-for-field.</summary>
public sealed record DesignerColumnDto
{
    public required string Name { get; init; }
    public required string DataType { get; init; }
    public bool Nullable { get; init; } = true;

    /// <summary>DEFAULT expression (verbatim), or null when none.</summary>
    public string? Default { get; init; }

    /// <summary>True for <c>GENERATED … AS IDENTITY</c>.</summary>
    public bool Identity { get; init; }

    /// <summary>"ALWAYS" | "BY DEFAULT" when <see cref="Identity"/>.</summary>
    public string? IdentityKind { get; init; }

    /// <summary>The (expr) for <c>GENERATED ALWAYS AS (expr) STORED</c>, or null.</summary>
    public string? Generated { get; init; }

    /// <summary>serial / bigserial / smallserial pseudo-type (auto-sequence).</summary>
    public bool Serial { get; init; }
}

/// <summary>A primary-key / unique key spec (optional name + ordered columns).</summary>
public sealed record DesignerKeyDto
{
    public string? Name { get; init; }
    public IReadOnlyList<string> Columns { get; init; } = new List<string>();
}

/// <summary>A foreign-key spec, mirroring <c>ForeignKeyDefinition</c>.</summary>
public sealed record DesignerForeignKeyDto
{
    public string? Name { get; init; }
    public IReadOnlyList<string> Columns { get; init; } = new List<string>();
    public required string ReferencedSchema { get; init; }
    public required string ReferencedTable { get; init; }
    public IReadOnlyList<string> ReferencedColumns { get; init; } = new List<string>();
    public string? OnDelete { get; init; }
    public string? OnUpdate { get; init; }
}

/// <summary>A CHECK constraint (optional name + expression).</summary>
public sealed record DesignerCheckDto
{
    public string? Name { get; init; }
    public required string Expression { get; init; }
}

/// <summary>An index on the table, mirroring <c>IndexDefinition</c>. Lives outside the table in the
/// model, so the designer carries it alongside the table for a one-stop edit surface.</summary>
public sealed record DesignerIndexDto
{
    public required string Name { get; init; }
    public bool Unique { get; init; }
    public IReadOnlyList<string> Columns { get; init; } = new List<string>();
    public string? Method { get; init; }
    public string? Where { get; init; }
}

/// <summary>
/// The full designer payload for a single table: the editable surfaces plus the verbatim
/// pass-through fields (<see cref="OtherConstraints"/> = EXCLUDE etc., <see cref="TrailingOptions"/> =
/// PARTITION BY / INHERITS / WITH …) and any companion statements that follow the CREATE TABLE in the
/// same .sql file (RLS <c>ALTER … ENABLE ROW LEVEL SECURITY</c>, policies, comments). The pass-through
/// fields are view-only in the form but survive the round-trip byte-for-byte.
/// </summary>
public sealed record TableModelDto
{
    public string SchemaVersion { get; init; } = JsonContract.SchemaVersion;
    public string Verb { get; init; } = "describe-table";

    public required string Schema { get; init; }
    public required string Name { get; init; }
    public IReadOnlyList<DesignerColumnDto> Columns { get; init; } = new List<DesignerColumnDto>();
    public DesignerKeyDto? PrimaryKey { get; init; }
    public IReadOnlyList<DesignerKeyDto> Unique { get; init; } = new List<DesignerKeyDto>();
    public IReadOnlyList<DesignerForeignKeyDto> ForeignKeys { get; init; } = new List<DesignerForeignKeyDto>();
    public IReadOnlyList<DesignerCheckDto> Checks { get; init; } = new List<DesignerCheckDto>();
    public IReadOnlyList<DesignerIndexDto> Indexes { get; init; } = new List<DesignerIndexDto>();

    /// <summary>Constraint clauses captured verbatim (EXCLUDE and anything not finely modelled). View-only.</summary>
    public IReadOnlyList<string> OtherConstraints { get; init; } = new List<string>();

    /// <summary>Clauses after the column list, verbatim: PARTITION BY / INHERITS / WITH / ON COMMIT. View-only.</summary>
    public string? TrailingOptions { get; init; }

    /// <summary>
    /// Companion statements that followed the <c>CREATE TABLE</c> in the source file and are not part of
    /// the table/index/FK model — RLS enable + policies, comments, etc. Preserved verbatim and re-emitted
    /// after the table so an editor round-trip never silently drops them. View-only in the form.
    /// </summary>
    public IReadOnlyList<string> Companions { get; init; } = new List<string>();
}
