using System.Collections.Generic;
using PgProj.Core.Parsing;

namespace PgProj.Core.Syntax;

// INSERT / UPDATE / DELETE / MERGE / TRUNCATE for PgParser, reusing the expression, FROM and SELECT
// grammar. One method per statement and per shared clause (SET list, RETURNING, ON CONFLICT).
public sealed partial class PgParser
{
    private static void AttachWith(DmlStatement s, List<CommonTableExpr>? ctes, bool recursive)
    {
        if (ctes is null) return;
        s.With.AddRange(ctes);
        s.WithRecursive = recursive;
    }

    private InsertStatement ParseInsert(TokenCursor c, List<CommonTableExpr>? ctes, bool recursive)
    {
        c.ExpectWord("INSERT");
        c.ExpectWord("INTO");
        var ins = new InsertStatement();
        AttachWith(ins, ctes, recursive);
        (ins.Schema, ins.Table) = ParseQualifiedName(c);
        if (c.MatchWord("AS")) ins.Alias = c.ExpectIdentifier();

        // optional column list — but not the start of a VALUES/SELECT
        if (c.AtSymbol('(') && !(c.Peek()?.IsWord("SELECT") == true || c.Peek()?.IsWord("VALUES") == true))
            ins.Columns.AddRange(ParseColumnNameList(c));

        if (c.MatchWord("OVERRIDING")) { ins.Overriding = c.ExpectIdentifier(); c.ExpectWord("VALUE"); }

        if (c.MatchWords("DEFAULT", "VALUES")) ins.DefaultValues = true;
        else { var prev = _returningIsAliasBoundary; _returningIsAliasBoundary = true; try { ins.Source = ParseSelectStatement(c); } finally { _returningIsAliasBoundary = prev; } }

        if (c.MatchWords("ON", "CONFLICT")) ins.OnConflict = ParseOnConflict(c);
        ParseReturning(c, ins);
        return ins;
    }

    private OnConflictClause ParseOnConflict(TokenCursor c)
    {
        var oc = new OnConflictClause();
        if (c.MatchWords("ON", "CONSTRAINT")) oc.OnConstraint = c.ExpectIdentifier();
        else if (c.AtSymbol('('))
        {
            oc.IndexColumns.AddRange(ParseColumnNameList(c));
            if (c.MatchWord("WHERE")) oc.IndexPredicate = ParseExpression(c);
        }
        c.ExpectWord("DO");
        if (c.MatchWord("NOTHING")) oc.DoNothing = true;
        else
        {
            c.ExpectWord("UPDATE");
            c.ExpectWord("SET");
            oc.Set.AddRange(ParseSetClauses(c));
            if (c.MatchWord("WHERE")) oc.Where = ParseExpression(c);
        }
        return oc;
    }

    private UpdateStatement ParseUpdate(TokenCursor c, List<CommonTableExpr>? ctes, bool recursive)
    {
        c.ExpectWord("UPDATE");
        var up = new UpdateStatement { Only = c.MatchWord("ONLY") };
        AttachWith(up, ctes, recursive);
        (up.Schema, up.Table) = ParseQualifiedName(c);
        c.MatchSymbol('*');
        if (c.MatchWord("AS")) up.Alias = c.ExpectIdentifier();
        else if (c.Current is { Kind: TokenKind.Word } w && !w.IsWord("SET")) up.Alias = c.Advance().Value;

        c.ExpectWord("SET");
        up.Set.AddRange(ParseSetClauses(c));

        if (c.MatchWord("FROM")) { var prev = _returningIsAliasBoundary; _returningIsAliasBoundary = true; try { up.From = ParseFromClause(c); } finally { _returningIsAliasBoundary = prev; } }
        ParseWhereOrCurrentOf(c, v => up.Where = v, cur => up.WhereCurrentOf = cur);
        ParseReturning(c, up);
        return up;
    }

    private DeleteStatement ParseDelete(TokenCursor c, List<CommonTableExpr>? ctes, bool recursive)
    {
        c.ExpectWord("DELETE");
        c.ExpectWord("FROM");
        var del = new DeleteStatement { Only = c.MatchWord("ONLY") };
        AttachWith(del, ctes, recursive);
        (del.Schema, del.Table) = ParseQualifiedName(c);
        c.MatchSymbol('*');
        if (c.MatchWord("AS")) del.Alias = c.ExpectIdentifier();
        else if (c.Current is { Kind: TokenKind.Word } w && !w.IsWord("USING") && !w.IsWord("WHERE") && !w.IsWord("RETURNING")) del.Alias = c.Advance().Value;

        if (c.MatchWord("USING")) { var prev = _returningIsAliasBoundary; _returningIsAliasBoundary = true; try { del.Using = ParseFromClause(c); } finally { _returningIsAliasBoundary = prev; } }
        ParseWhereOrCurrentOf(c, v => del.Where = v, cur => del.WhereCurrentOf = cur);
        ParseReturning(c, del);
        return del;
    }

    private MergeStatement ParseMerge(TokenCursor c, List<CommonTableExpr>? ctes, bool recursive)
    {
        c.ExpectWord("MERGE");
        c.ExpectWord("INTO");
        var m = new MergeStatement { Only = c.MatchWord("ONLY") };
        AttachWith(m, ctes, recursive);
        (m.Schema, m.Table) = ParseQualifiedName(c);
        c.MatchSymbol('*');                                  // MERGE INTO t * — include descendant tables
        if (c.MatchWord("AS")) m.Alias = c.ExpectIdentifier();
        else if (c.Current is { Kind: TokenKind.Word } w && !w.IsWord("USING")) m.Alias = c.Advance().Value;

        c.ExpectWord("USING");
        m.Source = ParseTableRefAtom(c);
        c.ExpectWord("ON");
        m.On = ParseExpression(c);

        while (c.AtWord("WHEN")) m.Whens.Add(ParseMergeWhen(c));
        if (m.Whens.Count == 0) throw new ParseException("MERGE requires at least one WHEN clause", c.Here);
        ParseReturning(c, m);
        return m;
    }

    private MergeWhen ParseMergeWhen(TokenCursor c)
    {
        c.ExpectWord("WHEN");
        var when = new MergeWhen();
        if (c.MatchWord("MATCHED")) when.Matched = true;
        else
        {
            c.ExpectWord("NOT");
            c.ExpectWord("MATCHED");
            when.Matched = false;
            if (c.MatchWord("BY")) when.By = c.ExpectIdentifier();   // SOURCE / TARGET
        }
        if (c.MatchWord("AND")) when.And = ParseExpression(c);
        c.ExpectWord("THEN");

        if (c.MatchWords("DO", "NOTHING")) { when.Action = "DO NOTHING"; return when; }
        if (c.MatchWord("DELETE")) { when.Action = "DELETE"; return when; }
        if (c.MatchWord("UPDATE")) { when.Action = "UPDATE"; c.ExpectWord("SET"); when.Set.AddRange(ParseSetClauses(c)); return when; }
        if (c.MatchWord("INSERT"))
        {
            when.Action = "INSERT";
            if (c.AtSymbol('(')) when.InsertColumns.AddRange(ParseColumnNameList(c));
            if (c.MatchWord("OVERRIDING")) { when.Overriding = c.ExpectIdentifier(); c.ExpectWord("VALUE"); }
            if (c.MatchWords("DEFAULT", "VALUES")) when.InsertDefaultValues = true;
            else { c.ExpectWord("VALUES"); c.ExpectSymbol('('); when.InsertValues.Add(ParseExpression(c)); while (c.MatchSymbol(',')) when.InsertValues.Add(ParseExpression(c)); c.ExpectSymbol(')'); }
            return when;
        }
        throw new ParseException("expected UPDATE / DELETE / INSERT / DO NOTHING after THEN", c.Here);
    }

    private TruncateStatement ParseTruncate(TokenCursor c)
    {
        c.ExpectWord("TRUNCATE");
        c.MatchWord("TABLE");
        var tr = new TruncateStatement();
        do
        {
            c.MatchWord("ONLY");
            var (s, n) = ParseQualifiedName(c);
            c.MatchSymbol('*');
            tr.Tables.Add(s is null ? n : $"{s}.{n}");
        } while (c.MatchSymbol(','));
        if (c.MatchWords("RESTART", "IDENTITY")) tr.IdentityOption = "RESTART IDENTITY";
        else if (c.MatchWords("CONTINUE", "IDENTITY")) tr.IdentityOption = "CONTINUE IDENTITY";
        if (c.MatchWord("CASCADE")) tr.DropOption = "CASCADE";
        else if (c.MatchWord("RESTRICT")) tr.DropOption = "RESTRICT";
        return tr;
    }

    // ---- shared clauses -----------------------------------------------------

    private List<SetClause> ParseSetClauses(TokenCursor c)
    {
        var list = new List<SetClause>();
        do { list.Add(ParseSetClause(c)); } while (c.MatchSymbol(','));
        return list;
    }

    private SetClause ParseSetClause(TokenCursor c)
    {
        var sc = new SetClause();
        if (c.AtSymbol('('))                                 // (c1, c2) = (v1, v2) | ROW(..) | (sub-select)
        {
            sc.Multi = true;
            sc.Columns.AddRange(ParseColumnNameList(c));
            if (!c.MatchOperator("=")) throw new ParseException("expected '=' in multi-column SET", c.Here);
            if (c.AtSymbol('('))
            {
                c.Advance();
                if (c.AtAnyWord("SELECT", "WITH", "VALUES", "TABLE")) sc.SubSelect = ParseSelectStatement(c);
                else { sc.Values.Add(ParseExpression(c)); while (c.MatchSymbol(',')) sc.Values.Add(ParseExpression(c)); }
                c.ExpectSymbol(')');
            }
            else sc.Value = ParseExpression(c);              // ROW(...) etc
            return sc;
        }
        sc.Columns.Add(c.ExpectIdentifier());
        while (c.AtSymbol('[')) CaptureBracket(c);            // tags[1] = …
        while (c.MatchSymbol('.')) { c.ExpectIdentifier(); while (c.AtSymbol('[')) CaptureBracket(c); }   // home.city = …
        if (!c.MatchOperator("=")) throw new ParseException("expected '=' in SET assignment", c.Here);
        if (c.MatchWord("DEFAULT")) sc.Default = true;
        else sc.Value = ParseExpression(c);
        return sc;
    }

    private void ParseWhereOrCurrentOf(TokenCursor c, System.Action<Expr> setWhere, System.Action<string> setCursor)
    {
        if (!c.MatchWord("WHERE")) return;
        if (c.MatchWords("CURRENT", "OF")) setCursor(c.ExpectIdentifier());
        else setWhere(ParseExpression(c));
    }

    private void ParseReturning(TokenCursor c, DmlStatement s)
    {
        if (!c.MatchWord("RETURNING")) return;
        if (c.MatchWord("WITH") && c.AtSymbol('(')) c.SkipBalancedParens();   // PG18: RETURNING WITH (OLD/NEW AS …)
        if (c.MatchSymbol('*')) { s.ReturningStar = true; if (!c.MatchSymbol(',')) return; }   // *, o.val, n.val — more items may follow
        s.Returning.Add(ParseSelectItem(c));
        while (c.MatchSymbol(',')) s.Returning.Add(ParseSelectItem(c));
    }
}
