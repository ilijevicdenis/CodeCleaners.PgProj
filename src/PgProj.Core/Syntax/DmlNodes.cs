using System.Collections.Generic;

namespace PgProj.Core.Syntax;

/// <summary>Common base for data-modifying statements (carries an optional leading WITH).</summary>
public abstract class DmlStatement : SqlStatement
{
    public List<CommonTableExpr> With { get; } = new();
    public bool WithRecursive { get; set; }
    public string? Schema { get; set; }
    public string Table { get; set; } = "";
    public string? Alias { get; set; }
    public bool ReturningStar { get; set; }
    public List<SelectItem> Returning { get; } = new();
}

public sealed class InsertStatement : DmlStatement
{
    public List<string> Columns { get; } = new();
    public bool DefaultValues { get; set; }
    public string? Overriding { get; set; }                  // SYSTEM / USER
    public SelectQuery? Source { get; set; }                 // VALUES / SELECT / TABLE
    public OnConflictClause? OnConflict { get; set; }
}

public sealed class OnConflictClause
{
    public List<string> IndexColumns { get; } = new();
    public string? OnConstraint { get; set; }
    public Expr? IndexPredicate { get; set; }
    public bool DoNothing { get; set; }
    public List<SetClause> Set { get; } = new();
    public Expr? Where { get; set; }
}

public sealed class UpdateStatement : DmlStatement
{
    public bool Only { get; set; }
    public List<SetClause> Set { get; } = new();
    public FromClause? From { get; set; }
    public Expr? Where { get; set; }
    public string? WhereCurrentOf { get; set; }
}

public sealed class DeleteStatement : DmlStatement
{
    public bool Only { get; set; }
    public FromClause? Using { get; set; }
    public Expr? Where { get; set; }
    public string? WhereCurrentOf { get; set; }
}

public sealed class MergeStatement : DmlStatement
{
    public bool Only { get; set; }
    public TableRef Source { get; set; } = null!;
    public Expr On { get; set; } = null!;
    public List<MergeWhen> Whens { get; } = new();
}

public sealed class MergeWhen
{
    public bool Matched { get; set; }
    public string? By { get; set; }                          // SOURCE / TARGET / null
    public Expr? And { get; set; }
    public string Action { get; set; } = "";                 // UPDATE / DELETE / DO NOTHING / INSERT
    public List<SetClause> Set { get; } = new();
    public List<string> InsertColumns { get; } = new();
    public bool InsertDefaultValues { get; set; }
    public List<Expr> InsertValues { get; } = new();
    public string? Overriding { get; set; }
}

/// <summary>A SET assignment: single (col = value|DEFAULT) or multi ((cols) = (values|sub-select)).</summary>
public sealed class SetClause
{
    public List<string> Columns { get; } = new();
    public Expr? Value { get; set; }
    public bool Default { get; set; }
    public SelectQuery? SubSelect { get; set; }
    public List<Expr> Values { get; } = new();              // for (c1,c2) = (v1,v2)
    public bool Multi { get; set; }
}

public sealed class TruncateStatement : SqlStatement
{
    public List<string> Tables { get; } = new();
    public string? IdentityOption { get; set; }             // RESTART IDENTITY / CONTINUE IDENTITY
    public string? DropOption { get; set; }                 // CASCADE / RESTRICT
}
