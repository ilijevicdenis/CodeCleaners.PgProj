using System.Collections.Generic;
using System.Linq;
using PgProj.Core.Model;

namespace PgProj.Core.Ast;

/// <summary>
/// Base of the PgProj abstract syntax tree. Every node exposes its <see cref="Children"/> so the
/// <see cref="SqlVisitor"/> can walk the whole tree generically — this is what makes static
/// analysis (rules that inspect structure) possible, as opposed to parsing straight into a model.
/// </summary>
public abstract class SqlNode
{
    public int Position { get; init; }
    public virtual IEnumerable<SqlNode> Children => System.Array.Empty<SqlNode>();
}

// ---- script + statements ----------------------------------------------------------------

public sealed class SqlScript : SqlNode
{
    public IReadOnlyList<SqlStatement> Statements { get; init; } = new List<SqlStatement>();
    public override IEnumerable<SqlNode> Children => Statements;
}

public abstract class SqlStatement : SqlNode
{
    /// <summary>The full source text of this statement (used to rebuild function/raw object bodies).</summary>
    public string RawText { get; set; } = "";
}

public sealed class CreateSchemaStatement : SqlStatement
{
    public required string Name { get; init; }
    public bool IfNotExists { get; init; }
}

public sealed class CreateSequenceStatement : SqlStatement
{
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public string? DataType { get; init; }
    public long? Increment { get; init; }
    public long? MinValue { get; init; }
    public long? MaxValue { get; init; }
    public long? Start { get; init; }
    public long? Cache { get; init; }
    public bool Cycle { get; init; }
}

public sealed class CreateTableStatement : SqlStatement
{
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public bool IfNotExists { get; init; }
    public IReadOnlyList<ColumnNode> Columns { get; init; } = new List<ColumnNode>();
    public IReadOnlyList<TableConstraintNode> Constraints { get; init; } = new List<TableConstraintNode>();
    public string? TrailingOptions { get; init; }
    public override IEnumerable<SqlNode> Children => Columns.Cast<SqlNode>().Concat(Constraints);
}

public sealed class CreateIndexStatement : SqlStatement
{
    public required string Name { get; init; }
    public required string Schema { get; init; }
    public required string Table { get; init; }
    public bool Unique { get; init; }
    public string? Method { get; init; }
    public IReadOnlyList<string> Columns { get; init; } = new List<string>(); // rendered column/expression text
    public string? Where { get; init; }
}

public sealed class CreateViewStatement : SqlStatement
{
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public bool Materialized { get; init; }
    public required string BodyText { get; init; }   // verbatim AS query (used to build the model)
    public SelectQuery? Query { get; init; }          // structured form, for analysis
    public override IEnumerable<SqlNode> Children => Query is null ? base.Children : new[] { Query };
}

public sealed class CreateFunctionStatement : SqlStatement
{
    public required FunctionHeader Header { get; init; }
    public required FunctionBody Body { get; init; }
    public override IEnumerable<SqlNode> Children => new SqlNode[] { Header, Body };
}

/// <summary>The long tail of object kinds captured verbatim (types, domains, triggers, policies, …).</summary>
public sealed class RawStatement : SqlStatement
{
    public ObjectKind Kind { get; init; }
    public string Schema { get; init; } = "";
    public string Name { get; init; } = "";
    public required string Identity { get; init; }
    public string? OnObject { get; init; }
    public required string BodyText { get; init; }
    public bool BodyComparable { get; init; } = true;
}

// ---- table elements ---------------------------------------------------------------------

public sealed class TypeName : SqlNode
{
    public required string Raw { get; init; }
    public required string Normalized { get; init; }
    public bool IsSerial { get; init; }
}

public sealed class ColumnNode : SqlNode
{
    public required string Name { get; init; }
    public required TypeName Type { get; init; }
    public IReadOnlyList<ColumnConstraintNode> Constraints { get; init; } = new List<ColumnConstraintNode>();
    public override IEnumerable<SqlNode> Children => Constraints.Cast<SqlNode>().Prepend(Type);
}

public abstract class ColumnConstraintNode : SqlNode { public string? Name { get; init; } }
public sealed class NotNullConstraintNode : ColumnConstraintNode { }
public sealed class NullConstraintNode : ColumnConstraintNode { }
public sealed class DefaultConstraintNode : ColumnConstraintNode
{
    public required Expression Expression { get; init; }
    public string RawText { get; init; } = "";
    public override IEnumerable<SqlNode> Children => new[] { Expression };
}
public sealed class InlinePrimaryKeyNode : ColumnConstraintNode { }
public sealed class InlineUniqueNode : ColumnConstraintNode { }
public sealed class InlineReferencesNode : ColumnConstraintNode
{
    public required string RefSchema { get; init; }
    public required string RefTable { get; init; }
    public IReadOnlyList<string> RefColumns { get; init; } = new List<string>();
    public string? OnDelete { get; init; }
    public string? OnUpdate { get; init; }
}
public sealed class IdentityConstraintNode : ColumnConstraintNode { public required string Kind { get; init; } } // ALWAYS | BY DEFAULT
public sealed class GeneratedConstraintNode : ColumnConstraintNode
{
    public required Expression Expression { get; init; }
    public string RawText { get; init; } = ""; // the "(expr)" form, for faithful re-emission
    public override IEnumerable<SqlNode> Children => new[] { Expression };
}
public sealed class CheckColumnConstraintNode : ColumnConstraintNode
{
    public required Expression Expression { get; init; }
    public string RawText { get; init; } = "";
    public override IEnumerable<SqlNode> Children => new[] { Expression };
}
public sealed class CollateConstraintNode : ColumnConstraintNode { public required string Collation { get; init; } }

public abstract class TableConstraintNode : SqlNode { public string? Name { get; init; } }
public sealed class PrimaryKeyConstraintNode : TableConstraintNode { public IReadOnlyList<string> Columns { get; init; } = new List<string>(); }
public sealed class UniqueConstraintNode : TableConstraintNode { public IReadOnlyList<string> Columns { get; init; } = new List<string>(); }
public sealed class ForeignKeyConstraintNode : TableConstraintNode
{
    public IReadOnlyList<string> Columns { get; init; } = new List<string>();
    public required string RefSchema { get; init; }
    public required string RefTable { get; init; }
    public IReadOnlyList<string> RefColumns { get; init; } = new List<string>();
    public string? OnDelete { get; init; }
    public string? OnUpdate { get; init; }
}
public sealed class CheckConstraintNode : TableConstraintNode
{
    public required Expression Expression { get; init; }
    public string RawText { get; init; } = ""; // the "(expr)" form, for faithful re-emission
    public override IEnumerable<SqlNode> Children => new[] { Expression };
}
public sealed class RawConstraintNode : TableConstraintNode { public required string Text { get; init; } }

// ---- functions --------------------------------------------------------------------------

public sealed class FunctionHeader : SqlNode
{
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public IReadOnlyList<FunctionParameter> Parameters { get; init; } = new List<FunctionParameter>();
    public string? Returns { get; init; }
    public string Language { get; init; } = "sql";
    public string? Volatility { get; init; }            // IMMUTABLE | STABLE | VOLATILE
    public string Security { get; init; } = "INVOKER";  // INVOKER | DEFINER
    public bool Strict { get; init; }
    public IReadOnlyList<string> SetClauses { get; init; } = new List<string>(); // e.g. "search_path = pg_catalog"
    public bool IsProcedure { get; init; }
    public string ArgTypes { get; init; } = "";
    public override IEnumerable<SqlNode> Children => Parameters;
}

public sealed class FunctionParameter : SqlNode
{
    public string? Mode { get; init; }   // IN | OUT | INOUT | VARIADIC
    public string? Name { get; init; }
    public required TypeName Type { get; init; }
    public Expression? Default { get; init; }
    public override IEnumerable<SqlNode> Children => Default is null ? new[] { (SqlNode)Type } : new SqlNode[] { Type, Default };
}

public sealed class FunctionBody : SqlNode
{
    public string Language { get; init; } = "sql";
    public string RawText { get; init; } = "";
    public IReadOnlyList<BodyStatement> Statements { get; init; } = new List<BodyStatement>();
    public override IEnumerable<SqlNode> Children => Statements;
}

public abstract class BodyStatement : SqlNode { public string RawText { get; init; } = ""; }

/// <summary>A data statement found in a function body — the unit most safety rules care about.</summary>
public sealed class DmlStatementNode : BodyStatement
{
    public required string Verb { get; init; }     // SELECT | INSERT | UPDATE | DELETE | TRUNCATE | PERFORM
    public string? TargetTable { get; init; }
    public bool HasWhere { get; init; }
    public Expression? WhereExpression { get; init; }  // parsed predicate when present
    public override IEnumerable<SqlNode> Children => WhereExpression is null ? base.Children : new[] { WhereExpression };
}

/// <summary>EXECUTE / dynamic SQL — an injection surface; rules flag it for review.</summary>
public sealed class DynamicSqlStatementNode : BodyStatement { }

/// <summary>Schema mutation from inside a function body (DROP/ALTER/CREATE/GRANT/TRUNCATE).</summary>
public sealed class SchemaMutationStatementNode : BodyStatement { public required string Verb { get; init; } }

/// <summary>Procedural construct we don't classify further (IF/LOOP/assignment/RETURN/…).</summary>
public sealed class ProceduralStatementNode : BodyStatement { }

// ---- expressions ------------------------------------------------------------------------

public abstract class Expression : SqlNode { }

public enum LiteralKind { String, Number, Boolean, Null }

public sealed class LiteralExpr : Expression
{
    public required string Value { get; init; }
    public LiteralKind Kind { get; init; }
}

public sealed class IdentifierExpr : Expression
{
    public IReadOnlyList<string> Parts { get; init; } = new List<string>();
    public string Name => string.Join(".", Parts);
}

public sealed class FunctionCallExpr : Expression
{
    public required string Name { get; init; }
    public IReadOnlyList<Expression> Arguments { get; init; } = new List<Expression>();
    public override IEnumerable<SqlNode> Children => Arguments;
}

public sealed class UnaryExpr : Expression
{
    public required string Op { get; init; }
    public required Expression Operand { get; init; }
    public override IEnumerable<SqlNode> Children => new[] { Operand };
}

public sealed class BinaryExpr : Expression
{
    public required string Op { get; init; }
    public required Expression Left { get; init; }
    public required Expression Right { get; init; }
    public override IEnumerable<SqlNode> Children => new[] { Left, Right };
}

public sealed class CastExpr : Expression
{
    public required Expression Operand { get; init; }
    public required string TypeName { get; init; }
    public override IEnumerable<SqlNode> Children => new[] { Operand };
}

public sealed class ParenExpr : Expression
{
    public required Expression Inner { get; init; }
    public override IEnumerable<SqlNode> Children => new[] { Inner };
}

public sealed class CaseExpr : Expression
{
    public Expression? Operand { get; init; }                 // CASE <operand> WHEN … (simple form)
    public IReadOnlyList<CaseBranch> Branches { get; init; } = new List<CaseBranch>();
    public Expression? Else { get; init; }
    public override IEnumerable<SqlNode> Children
    {
        get
        {
            var nodes = new List<SqlNode>();
            if (Operand is not null) nodes.Add(Operand);
            foreach (var b in Branches) { nodes.Add(b.When); nodes.Add(b.Then); }
            if (Else is not null) nodes.Add(Else);
            return nodes;
        }
    }
}

public sealed class CaseBranch : SqlNode
{
    public required Expression When { get; init; }
    public required Expression Then { get; init; }
    public override IEnumerable<SqlNode> Children => new[] { When, Then };
}

/// <summary>A parenthesised subquery used as an expression — e.g. <c>x IN (SELECT …)</c>.</summary>
public sealed class SubqueryExpr : Expression
{
    public required SelectQuery Query { get; init; }
    public override IEnumerable<SqlNode> Children => new[] { Query };
}

/// <summary><c>expr IN (a, b, …)</c> or <c>expr IN (SELECT …)</c>.</summary>
public sealed class InExpr : Expression
{
    public required Expression Operand { get; init; }
    public bool Negated { get; init; }
    public IReadOnlyList<Expression> Items { get; init; } = new List<Expression>();
    public SubqueryExpr? Subquery { get; init; }
    public override IEnumerable<SqlNode> Children
    {
        get
        {
            var nodes = new List<SqlNode> { Operand };
            nodes.AddRange(Items);
            if (Subquery is not null) nodes.Add(Subquery);
            return nodes;
        }
    }
}

/// <summary>Fallback for expression fragments the Pratt parser doesn't fully model.</summary>
public sealed class RawExpr : Expression { public required string Text { get; init; } }

// ---- queries ----------------------------------------------------------------------------

/// <summary>
/// A SELECT query, modelled to the depth static analysis needs: its CTEs (<c>WITH</c>) and a parsed
/// <c>WHERE</c> predicate are structured; the projection / FROM / trailing clauses are kept as text
/// (no full join/group-by grammar yet). Used by views and as a subquery payload.
/// </summary>
public sealed class SelectQuery : SqlNode
{
    public bool Recursive { get; init; }
    public IReadOnlyList<CommonTableExpression> With { get; init; } = new List<CommonTableExpression>();
    public string ProjectionText { get; init; } = "";
    public string? FromText { get; init; }
    public Expression? Where { get; init; }
    public string? Tail { get; init; } // GROUP BY / HAVING / ORDER BY / LIMIT, captured raw
    public override IEnumerable<SqlNode> Children
    {
        get
        {
            var nodes = new List<SqlNode>(With);
            if (Where is not null) nodes.Add(Where);
            return nodes;
        }
    }
}

public sealed class CommonTableExpression : SqlNode
{
    public required string Name { get; init; }
    public required SelectQuery Query { get; init; }
    public override IEnumerable<SqlNode> Children => new[] { Query };
}
