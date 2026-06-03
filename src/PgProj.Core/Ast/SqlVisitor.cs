using System.Collections.Generic;
using System.Linq;

namespace PgProj.Core.Ast;

/// <summary>
/// Pre-order tree walker over the PgProj AST. Subclass and override the typed Visit* hooks for the
/// nodes you care about; everything else falls through to <see cref="DefaultVisit"/>, which recurses
/// into <see cref="SqlNode.Children"/>. This is the substrate every static-analysis rule runs on.
/// </summary>
public abstract class SqlVisitor
{
    public virtual void Visit(SqlNode node)
    {
        switch (node)
        {
            case SqlScript n: VisitScript(n); break;
            case CreateTableStatement n: VisitCreateTable(n); break;
            case CreateIndexStatement n: VisitCreateIndex(n); break;
            case CreateViewStatement n: VisitCreateView(n); break;
            case CreateFunctionStatement n: VisitCreateFunction(n); break;
            case CreateSequenceStatement n: VisitCreateSequence(n); break;
            case CreateSchemaStatement n: VisitCreateSchema(n); break;
            case RawStatement n: VisitRaw(n); break;
            case FunctionHeader n: VisitFunctionHeader(n); break;
            case FunctionBody n: VisitFunctionBody(n); break;
            case DmlStatementNode n: VisitDml(n); break;
            case DynamicSqlStatementNode n: VisitDynamicSql(n); break;
            case SchemaMutationStatementNode n: VisitSchemaMutation(n); break;
            case CheckConstraintNode n: VisitCheckConstraint(n); break;
            case FunctionCallExpr n: VisitFunctionCall(n); break;
            default: DefaultVisit(node); break;
        }
    }

    protected virtual void VisitScript(SqlScript n) => DefaultVisit(n);
    protected virtual void VisitCreateTable(CreateTableStatement n) => DefaultVisit(n);
    protected virtual void VisitCreateIndex(CreateIndexStatement n) => DefaultVisit(n);
    protected virtual void VisitCreateView(CreateViewStatement n) => DefaultVisit(n);
    protected virtual void VisitCreateFunction(CreateFunctionStatement n) => DefaultVisit(n);
    protected virtual void VisitCreateSequence(CreateSequenceStatement n) => DefaultVisit(n);
    protected virtual void VisitCreateSchema(CreateSchemaStatement n) => DefaultVisit(n);
    protected virtual void VisitRaw(RawStatement n) => DefaultVisit(n);
    protected virtual void VisitFunctionHeader(FunctionHeader n) => DefaultVisit(n);
    protected virtual void VisitFunctionBody(FunctionBody n) => DefaultVisit(n);
    protected virtual void VisitDml(DmlStatementNode n) => DefaultVisit(n);
    protected virtual void VisitDynamicSql(DynamicSqlStatementNode n) => DefaultVisit(n);
    protected virtual void VisitSchemaMutation(SchemaMutationStatementNode n) => DefaultVisit(n);
    protected virtual void VisitCheckConstraint(CheckConstraintNode n) => DefaultVisit(n);
    protected virtual void VisitFunctionCall(FunctionCallExpr n) => DefaultVisit(n);

    protected virtual void DefaultVisit(SqlNode node)
    {
        foreach (var child in node.Children)
            Visit(child);
    }
}

/// <summary>Query helpers over the tree, for rules that prefer LINQ to a visitor subclass.</summary>
public static class SqlTree
{
    public static IEnumerable<SqlNode> DescendantsAndSelf(SqlNode root)
    {
        yield return root;
        foreach (var child in root.Children)
            foreach (var d in DescendantsAndSelf(child))
                yield return d;
    }

    public static IEnumerable<T> Descendants<T>(SqlNode root) where T : SqlNode =>
        DescendantsAndSelf(root).OfType<T>();
}
