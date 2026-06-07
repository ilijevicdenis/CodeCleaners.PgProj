using System.Collections.Generic;
using PgProj.Core.Parsing;

namespace PgProj.Core.Syntax;

// Clean, typed AST for the hand-written recursive-descent parser (PgParser). One node per real
// grammar construct; no token-capture-and-render except where PostgreSQL grammar is genuinely
// free-form (expression bodies, type names), and there it is a single well-named helper.

/// <summary>A parse problem with source coordinates so it can be reported as line:column.</summary>
public sealed record ParseDiagnostic(string Message, int Line, int Column, int Offset)
{
    public override string ToString() => $"{Line}:{Column}: {Message}";

    /// <summary>Lift this parse problem into the unified compiler-style diagnostic (always an error, code <c>BUILD</c>),
    /// stamping the project-relative <paramref name="file"/> when the caller knows it.</summary>
    public Diagnostics.Diagnostic ToUnified(string? file = null) =>
        Diagnostics.Diagnostic.FromParser(Message, file, Line, Column);
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

    // The pooled token buffer that backs the statements' lazy SourceText segments (main parse path only).
    private PooledTokens? _tokens;
    internal void SetTokens(PooledTokens tokens) => _tokens = tokens;

    /// <summary>
    /// Return the pooled token buffer to <see cref="System.Buffers.ArrayPool{Token}"/>. Drop-then-return:
    /// every statement's source-segment view is nulled FIRST (statements that still hold one never needed
    /// their SourceText — the ones that did were already rendered during model build), THEN the array is
    /// returned, so no live reader can observe a recycled buffer. Call ONLY after the model is built and
    /// SourceText will not be read again (the build pipeline does). Optional: callers that skip it just let
    /// the GC reclaim the array — correct, only unpooled. Idempotent.
    /// </summary>
    public void ReleaseTokens()
    {
        if (_tokens is null) return;
        foreach (var s in Statements) s.DropSegment();
        _tokens.Return();
        _tokens = null;
    }
}

public abstract class SqlStatement
{
    public int Position { get; init; }

    private string? _sourceText;
    private IReadOnlyList<Token>? _sourceSegment;

    /// <summary>
    /// The statement's source text. Rendered lazily from its token segment on first read and cached.
    /// Rendering is deferred because the model builder only reads SourceText for a minority of statement
    /// kinds (functions, raw/unsupported, partition/typed tables); for the common structured statements —
    /// plain tables, views, indexes, sequences, queries — it is never read, so eager <c>Token.Render</c>
    /// was pure waste (≈ the bulk of the parser's "grammar+render" allocation). Setting it explicitly
    /// (tests, synthetic statements) still works and overrides the deferred segment.
    /// </summary>
    public string? SourceText
    {
        get
        {
            if (_sourceText is null && _sourceSegment is not null)
            {
                _sourceText = Token.Render(_sourceSegment);
                _sourceSegment = null;   // release the view (and its hold on the whole token list) once rendered
            }
            return _sourceText;
        }
        set { _sourceText = value; _sourceSegment = null; }
    }

    /// <summary>Defer SourceText materialisation: keep the token segment and render only on first read.</summary>
    public void SetSourceSegment(IReadOnlyList<Token> segment) => _sourceSegment = segment;

    /// <summary>Drop the deferred segment view (used by ParseResult.ReleaseTokens before the pooled token
    /// buffer is returned). A statement still holding a segment here never had its SourceText read, so the
    /// text is simply discarded; statements that needed it rendered + cached + cleared the segment already.</summary>
    internal void DropSegment() => _sourceSegment = null;
}

/// <summary>A statement kind PgParser does not implement yet (caller falls back to legacy).</summary>
public sealed class UnsupportedStatement : SqlStatement { public string LeadingKeyword { get; init; } = ""; }

/// <summary>A CREATE of a kind not finely modelled (TYPE/DOMAIN/TRIGGER/RULE/POLICY/EXTENSION/…);
/// kind + schema-qualified name (+ ON-table for trigger/rule/policy) captured for catalog + model.</summary>
public sealed class RawCreateStatement : SqlStatement
{
    public string ObjectKind { get; init; } = "";
    public string? Schema { get; set; }
    public string? Name { get; set; }
    public string? OnObject { get; set; }   // "schema.table" for trigger/rule/policy

    // ---- CREATE TRIGGER detail (semantic validation, #48) — additive, set only by ParseCreateTrigger.
    // The model/comparer never read these (they re-derive from SourceText); they let the semantic
    // validator resolve the trigger's target relation + the function it EXECUTEs without a re-parse.
    /// <summary>The trigger's target relation schema (the <c>ON schema.table</c>), when written qualified.</summary>
    public string? OnSchema { get; set; }
    /// <summary>The trigger's target relation name (the <c>ON … table</c>), for a CREATE TRIGGER.</summary>
    public string? OnTable { get; set; }
    /// <summary>The schema of the function the trigger EXECUTEs, when written qualified.</summary>
    public string? TriggerFunctionSchema { get; set; }
    /// <summary>The unqualified name of the function the trigger EXECUTEs (FUNCTION/PROCEDURE), for a CREATE TRIGGER.</summary>
    public string? TriggerFunctionName { get; set; }
}

public sealed class CreateViewStatement : SqlStatement
{
    public string? Schema { get; set; }
    public string Name { get; set; } = "";
    public bool Materialized { get; init; }
    public string BodyText { get; set; } = "";
}

public sealed class CreateSequenceStatement : SqlStatement
{
    public string? Schema { get; set; }
    public string Name { get; set; } = "";
    public string? DataType { get; set; }
    public long? Increment { get; set; }
    public long? MinValue { get; set; }
    public long? MaxValue { get; set; }
    public long? Start { get; set; }
    public long? Cache { get; set; }
    public bool Cycle { get; set; }
}

public sealed class CreateIndexStatement : SqlStatement
{
    public string? Name { get; set; }
    public string? Schema { get; set; }
    public string Table { get; set; } = "";
    public bool Unique { get; init; }
    public string? Method { get; set; }
    public List<string> Columns { get; } = new();
    public string? Where { get; set; }
}

public sealed class CreateFunctionStatement : SqlStatement
{
    public string? Schema { get; set; }
    public string Name { get; set; } = "";
    public string ArgTypes { get; set; } = "";
    public bool IsProcedure { get; init; }
    public string? Language { get; set; }            // LANGUAGE name (lowercased), if given
    public string? Body { get; set; }                // the AS body token, verbatim (dollar-quoted or string)
    public bool ReturnsVoid { get; set; }            // RETURNS void
    public bool ReturnsSetof { get; set; }           // RETURNS SETOF … / RETURNS TABLE(…)
    public bool HasOutParams { get; set; }           // any OUT / INOUT parameter
    public string? ReturnType { get; set; }          // the RETURNS scalar type text (e.g. "trigger", "integer"); null for RETURNS TABLE
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
    public SelectQuery? Source { get; set; }             // parsed query (SELECT/VALUES/TABLE/WITH), for analysis
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
