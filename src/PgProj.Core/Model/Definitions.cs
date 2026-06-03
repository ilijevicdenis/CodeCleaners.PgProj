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
    bool IsIdentity = false);

public sealed record PrimaryKeyDefinition(string? Name, IReadOnlyList<string> Columns);

public sealed record UniqueConstraintDefinition(string? Name, IReadOnlyList<string> Columns);

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
    public List<UniqueConstraintDefinition> Unique { get; init; } = new();
    public List<ForeignKeyDefinition> ForeignKeys { get; init; } = new();

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

public sealed record SequenceDefinition(string Schema, string Name);

/// <summary>
/// A function/procedure. <see cref="Signature"/> is the schema-qualified name plus argument
/// types (the identity Postgres uses for overload resolution); <see cref="Body"/> is the full
/// CREATE statement, replayed verbatim with CREATE OR REPLACE on deploy.
/// </summary>
public sealed record FunctionDefinition(string Schema, string Name, string Signature, string Body);
