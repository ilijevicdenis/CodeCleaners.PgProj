using System.Collections.Generic;

namespace PgProj.Core.Syntax;

// Clean, typed AST for the hand-written recursive-descent parser (PgParser). One node per real
// grammar construct; no token-capture-and-render except where PostgreSQL grammar is genuinely
// free-form (expression bodies, type names), and there it is a single well-named helper.

/// <summary>A parse problem with source coordinates so it can be reported as line:column.</summary>
public sealed record ParseDiagnostic(string Message, int Line, int Column, int Offset)
{
    public override string ToString() => $"{Line}:{Column}: {Message}";
}

/// <summary>The outcome of parsing a (possibly multi-statement) SQL string.</summary>
public sealed class ParseResult
{
    public List<SqlStatement> Statements { get; } = new();
    public List<ParseDiagnostic> Diagnostics { get; } = new();

    /// <summary>
    /// False when at least one statement is of a kind PgParser does not yet own (so callers may
    /// defer to the legacy parser during the incremental migration). True means PgParser took full
    /// ownership and its verdict (Statements + Diagnostics) is authoritative.
    /// </summary>
    public bool FullyRecognized { get; set; } = true;
}

public abstract class SqlStatement { public int Position { get; init; } }

/// <summary>A statement kind PgParser does not implement yet (caller falls back to legacy).</summary>
public sealed class UnsupportedStatement : SqlStatement { public string LeadingKeyword { get; init; } = ""; }

/// <summary>A CREATE of a kind not finely modelled (VIEW/SEQUENCE/FUNCTION/TYPE/INDEX/TRIGGER/…);
/// the object's kind + schema-qualified name are captured so the catalog can record it.</summary>
public sealed class RawCreateStatement : SqlStatement
{
    public string ObjectKind { get; init; } = "";
    public string? Schema { get; set; }
    public string? Name { get; set; }
}

// ---- CREATE TABLE -----------------------------------------------------------

public sealed class CreateTableStatement : SqlStatement
{
    public string? Schema { get; init; }
    public string Name { get; init; } = "";
    public bool IfNotExists { get; init; }
    public string? Persistence { get; init; }          // TEMP / UNLOGGED / null
    public List<ColumnDef> Columns { get; } = new();
    public List<TableConstraint> Constraints { get; } = new();
    public string? TrailingText { get; set; }           // PARTITION BY / INHERITS / WITH / TABLESPACE …
    public bool IsPartitionOrTyped { get; init; }       // PARTITION OF / OF type form (no column list)
    public bool HasLikeElement { get; set; }            // a LIKE source element was present (adds unknown columns)
}

public sealed class CreateTableAsStatement : SqlStatement
{
    public string? Schema { get; init; }
    public string Name { get; init; } = "";
    public bool IfNotExists { get; init; }
    public List<string> ColumnAliases { get; } = new();
    public string QueryText { get; set; } = "";
    public bool? WithData { get; set; }                  // WITH DATA / WITH NO DATA
}

public sealed class CreateSchemaStatement : SqlStatement
{
    public string? Name { get; init; }                  // null when only AUTHORIZATION is given
    public bool IfNotExists { get; init; }
    public string? Authorization { get; init; }
}

// ---- columns & constraints --------------------------------------------------

public sealed class TypeName { public string Text { get; init; } = ""; }

public sealed class ColumnDef
{
    public string Name { get; init; } = "";
    public TypeName Type { get; init; } = new();
    public List<ColumnConstraint> Constraints { get; } = new();
}

public sealed class Deferrability { public bool? Deferrable { get; set; } public bool? InitiallyDeferred { get; set; } }

public sealed class RefAction { public string Action { get; init; } = ""; public List<string> Columns { get; } = new(); }

public abstract class ColumnConstraint { public string? Name { get; set; } }
public sealed class NotNullConstraint : ColumnConstraint { }
public sealed class NullConstraint : ColumnConstraint { }
public sealed class DefaultConstraint : ColumnConstraint { public string Expression { get; init; } = ""; }
public sealed class CollateConstraint : ColumnConstraint { public string Collation { get; init; } = ""; }
public sealed class StorageOption : ColumnConstraint { public string Kind { get; init; } = ""; public string Value { get; init; } = ""; }  // STORAGE x / COMPRESSION x
public sealed class InlinePrimaryKey : ColumnConstraint { public List<string> Include { get; } = new(); public Deferrability Deferrability { get; } = new(); }
public sealed class InlineUnique : ColumnConstraint { public bool NullsNotDistinct { get; set; } public List<string> Include { get; } = new(); public Deferrability Deferrability { get; } = new(); }
public sealed class InlineCheck : ColumnConstraint { public string Expression { get; init; } = ""; public bool NoInherit { get; set; } public bool NotValid { get; set; } public Deferrability Deferrability { get; } = new(); }
public sealed class GeneratedIdentity : ColumnConstraint { public string Kind { get; init; } = ""; }      // ALWAYS / BY DEFAULT
public sealed class GeneratedStored : ColumnConstraint { public string Expression { get; init; } = ""; }
public sealed class InlineReferences : ColumnConstraint
{
    public string? RefSchema { get; init; }
    public string RefTable { get; init; } = "";
    public List<string> RefColumns { get; } = new();
    public string? Match { get; set; }
    public RefAction? OnDelete { get; set; }
    public RefAction? OnUpdate { get; set; }
    public Deferrability Deferrability { get; } = new();
    public bool NotValid { get; set; }
}

public abstract class TableConstraint { public string? Name { get; set; } public Deferrability Deferrability { get; } = new(); }
public sealed class PrimaryKeyConstraint : TableConstraint { public List<string> Columns { get; } = new(); public List<string> Include { get; } = new(); }
public sealed class UniqueConstraint : TableConstraint { public bool NullsNotDistinct { get; set; } public List<string> Columns { get; } = new(); public List<string> Include { get; } = new(); }
public sealed class CheckConstraint : TableConstraint { public string Expression { get; init; } = ""; public bool NoInherit { get; set; } public bool NotValid { get; set; } }
public sealed class ExcludeConstraint : TableConstraint { public string RawText { get; init; } = ""; }
public sealed class NotNullTableConstraint : TableConstraint { public string Column { get; init; } = ""; public bool NoInherit { get; set; } }
public sealed class ForeignKeyConstraint : TableConstraint
{
    public List<string> Columns { get; } = new();
    public string? RefSchema { get; init; }
    public string RefTable { get; init; } = "";
    public List<string> RefColumns { get; } = new();
    public string? Match { get; set; }
    public RefAction? OnDelete { get; set; }
    public RefAction? OnUpdate { get; set; }
    public bool NotValid { get; set; }
}
