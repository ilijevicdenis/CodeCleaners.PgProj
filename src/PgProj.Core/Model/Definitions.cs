using System.Collections.Generic;

namespace PgProj.Core.Model;

/// <summary>A Postgres schema (namespace).</summary>
public sealed record SchemaDefinition(string Name);

/// <summary>
/// A single column. <see cref="DataType"/> is stored in canonical form
/// (see <see cref="TypeNormalizer"/>) so that a column parsed from a .sql file and the same
/// column read back from a live server compare equal instead of producing a phantom diff.
/// </summary>
public sealed record ColumnDefinition(
    string Name,
    string DataType,
    bool IsNullable,
    string? Default = null,
    bool IsIdentity = false,
    string? IdentityKind = null,        // "ALWAYS" | "BY DEFAULT" when IsIdentity
    string? GeneratedExpression = null,  // the (expr) for GENERATED ALWAYS AS (expr) STORED|VIRTUAL
    bool IsSerial = false,               // serial/bigserial/smallserial (auto-sequence pseudo-type)
    bool GeneratedIsStored = true);      // STORED vs VIRTUAL when GeneratedExpression is set (PG18 virtual)

public sealed record PrimaryKeyDefinition(string? Name, IReadOnlyList<string> Columns);

public sealed record UniqueConstraintDefinition(string? Name, IReadOnlyList<string> Columns);

public sealed record CheckConstraintDefinition(string? Name, string Expression);

public sealed record ForeignKeyDefinition(
    string? Name,
    IReadOnlyList<string> Columns,
    string ReferencedSchema,
    string ReferencedTable,
    IReadOnlyList<string> ReferencedColumns,
    string? OnDelete = null,
    string? OnUpdate = null);

public sealed record TableDefinition
{
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public List<ColumnDefinition> Columns { get; init; } = new();
    public PrimaryKeyDefinition? PrimaryKey { get; set; }

    // Most tables carry NO unique/FK/check/other-constraint rows, yet eagerly allocating an (empty) List
    // for each was ~28% of the Table-bucket model-build allocation (issue #8). These four are lazily
    // materialised on first touch: a read or an Add allocates the backing List once and caches it; every
    // later access returns the same instance. Fully transparent — no caller invariant, no shared/poolable
    // buffer, no release contract — so a constraint-free table allocates none of them. (Columns stays eager:
    // every table has columns.) TableDefinition is never used as a dictionary key / in record equality, so
    // the lazy getters' allocate-on-access never fires from a synthesized Equals/GetHashCode.
    private List<UniqueConstraintDefinition>? _unique;
    public List<UniqueConstraintDefinition> Unique { get => _unique ??= new(); init => _unique = value; }
    private List<ForeignKeyDefinition>? _foreignKeys;
    public List<ForeignKeyDefinition> ForeignKeys { get => _foreignKeys ??= new(); init => _foreignKeys = value; }
    private List<CheckConstraintDefinition>? _checks;
    public List<CheckConstraintDefinition> Checks { get => _checks ??= new(); init => _checks = value; }

    /// <summary>Constraint clauses captured verbatim (EXCLUDE and anything not finely modelled).</summary>
    private List<string>? _otherConstraints;
    public List<string> OtherConstraints { get => _otherConstraints ??= new(); init => _otherConstraints = value; }

    /// <summary>Clauses after the column list, captured verbatim: INHERITS / PARTITION BY / WITH / ON COMMIT.</summary>
    public string? TrailingOptions { get; set; }

    public string QualifiedName => $"{Schema}.{Name}";

    public ColumnDefinition? FindColumn(string name) =>
        Columns.Find(c => DatabaseModel.NameEquals(c.Name, name));
}

public sealed record IndexDefinition(
    string Name,
    string Schema,
    string Table,
    IReadOnlyList<string> Columns,
    bool IsUnique,
    string? Method = null,
    string? WhereClause = null);

public sealed record ViewDefinition(string Schema, string Name, string Body, bool IsMaterialized = false);

public sealed record SequenceDefinition(
    string Schema,
    string Name,
    string? DataType = null,
    long? Increment = null,
    long? MinValue = null,
    long? MaxValue = null,
    long? Start = null,
    long? Cache = null,
    bool Cycle = false);

/// <summary>
/// A function/procedure. <see cref="Signature"/> is the schema-qualified name plus argument
/// types (the identity Postgres uses for overload resolution); <see cref="Body"/> is the full
/// CREATE statement, replayed verbatim with CREATE OR REPLACE on deploy.
/// </summary>
public sealed record FunctionDefinition(string Schema, string Name, string Signature, string Body, string ArgTypes = "");
