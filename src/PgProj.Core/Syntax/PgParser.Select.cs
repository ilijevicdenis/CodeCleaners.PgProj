using System;
using System.Collections.Generic;
using PgProj.Core.Parsing;

namespace PgProj.Core.Syntax;

// SELECT / VALUES / TABLE / WITH grammar for PgParser.
public sealed partial class PgParser
{
    // words that cannot be an implicit output-column alias (they start a following clause)
    private static readonly HashSet<string> NotAnAlias = new(StringComparer.OrdinalIgnoreCase)
    {
        "FROM", "WHERE", "GROUP", "HAVING", "WINDOW", "ORDER", "LIMIT", "OFFSET", "FETCH", "FOR",
        "UNION", "INTERSECT", "EXCEPT", "INTO", "AS", "RETURNING",
    };

    private SelectQuery ParseSelectStatement(TokenCursor c)
    {
        List<CommonTableExpr>? ctes = null;
        bool recursive = false;
        if (c.AtWord("WITH")) (ctes, recursive) = ParseCteList(c);
        var q = ParseSelectBody(c);
        if (ctes is not null) { q.With.AddRange(ctes); q.WithRecursive = recursive; }
        return q;
    }

    private (List<CommonTableExpr>, bool) ParseCteList(TokenCursor c)
    {
        c.ExpectWord("WITH");
        bool recursive = c.MatchWord("RECURSIVE");
        var ctes = new List<CommonTableExpr> { ParseCte(c) };
        while (c.MatchSymbol(',')) ctes.Add(ParseCte(c));
        return (ctes, recursive);
    }

    private SelectQuery ParseSelectBody(TokenCursor c)
    {
        var q = ParseSetOpChain(c);
        ParseSelectTail(c, q);
        return q;
    }

    private CommonTableExpr ParseCte(TokenCursor c)
    {
        var cte = new CommonTableExpr { Name = c.ExpectIdentifier() };
        if (c.AtSymbol('(')) cte.Columns.AddRange(ParseColumnNameList(c));
        c.ExpectWord("AS");
        if (c.MatchWord("MATERIALIZED")) cte.Materialized = "MATERIALIZED";
        else if (c.MatchWords("NOT", "MATERIALIZED")) cte.Materialized = "NOT MATERIALIZED";
        c.ExpectSymbol('(');
        if (c.AtAnyWord("SELECT", "WITH", "VALUES", "TABLE"))
            cte.Query = ParseSelectStatement(c);
        else
            cte.RawBody = Token.Render(CaptureBalancedInner(c));   // data-modifying CTE (INSERT/UPDATE/DELETE)
        if (cte.Query is not null) c.ExpectSymbol(')');
        // SEARCH / CYCLE clauses (captured, rarely needed for accept)
        while (c.AtAnyWord("SEARCH", "CYCLE"))
        {
            c.Advance();
            while (!c.AtEnd && !c.AtSymbol(',') && !c.AtWord("SEARCH") && !c.AtWord("CYCLE")
                   && !(c.CurrentOperator is null && c.Current!.Kind == TokenKind.Word && (c.AtWord("SELECT")))) c.Advance();
        }
        return cte;
    }

    private SelectQuery ParseSetOpChain(TokenCursor c)
    {
        var left = ParseSelectPrimary(c);
        while (c.AtAnyWord("UNION", "INTERSECT", "EXCEPT"))
        {
            var op = c.Advance().Value.ToUpperInvariant();
            if (c.MatchWord("ALL")) op += " ALL";
            else if (c.MatchWord("DISTINCT")) op += " DISTINCT";
            var right = ParseSelectPrimary(c);
            left = new SelectQuery { SetOp = new SetOperation { Op = op, Left = left, Right = right } };
        }
        return left;
    }

    private SelectQuery ParseSelectPrimary(TokenCursor c)
    {
        if (c.AtSymbol('('))
        {
            c.Advance();
            var inner = ParseSelectStatement(c);
            c.ExpectSymbol(')');
            return inner;
        }
        if (c.AtWord("VALUES")) return ParseValues(c);
        if (c.AtWord("TABLE")) { c.Advance(); var (s, n) = ParseQualifiedName(c); return new SelectQuery { IsTableCommand = true, TableName = s is null ? n : $"{s}.{n}" }; }
        return ParseSelectCore(c);
    }

    private SelectQuery ParseValues(TokenCursor c)
    {
        c.ExpectWord("VALUES");
        var q = new SelectQuery { IsValues = true };
        do
        {
            c.ExpectSymbol('(');
            var row = new List<Expr> { ParseExpression(c) };
            while (c.MatchSymbol(',')) row.Add(ParseExpression(c));
            c.ExpectSymbol(')');
            q.ValuesRows.Add(row);
        } while (c.MatchSymbol(','));
        return q;
    }

    private SelectQuery ParseSelectCore(TokenCursor c)
    {
        c.ExpectWord("SELECT");
        var q = new SelectQuery();
        if (c.MatchWord("ALL")) { }
        else if (c.MatchWord("DISTINCT"))
        {
            q.Distinct = true;
            if (c.MatchWord("ON")) { c.ExpectSymbol('('); q.DistinctOn.Add(ParseExpression(c)); while (c.MatchSymbol(',')) q.DistinctOn.Add(ParseExpression(c)); c.ExpectSymbol(')'); }
        }

        // target list (may be empty only for SELECT INTO-ish? require at least one normally)
        if (!c.AtEnd && !c.AtAnyWord("FROM", "WHERE", "GROUP", "HAVING", "WINDOW", "ORDER", "LIMIT", "OFFSET", "FETCH", "FOR", "UNION", "INTERSECT", "EXCEPT") && !c.AtSymbol(')'))
        {
            q.Items.Add(ParseSelectItem(c));
            while (c.MatchSymbol(',')) q.Items.Add(ParseSelectItem(c));
        }

        if (c.MatchWord("INTO")) { c.MatchWord("TEMPORARY"); c.MatchWord("TEMP"); c.MatchWord("UNLOGGED"); c.MatchWord("TABLE"); ParseQualifiedName(c); }

        if (c.MatchWord("FROM")) q.From = ParseFromClause(c);
        if (c.MatchWord("WHERE")) q.Where = ParseExpression(c);
        if (c.MatchWords("GROUP", "BY")) ParseGroupBy(c, q);
        if (c.MatchWord("HAVING")) q.Having = ParseExpression(c);
        if (c.MatchWord("WINDOW"))
        {
            do { var name = c.ExpectIdentifier(); c.ExpectWord("AS"); q.Windows.Add(new NamedWindow { Name = name, Spec = ParseWindowDetails(c) }); }
            while (c.MatchSymbol(','));
        }
        return q;
    }

    private SelectItem ParseSelectItem(TokenCursor c)
    {
        var item = new SelectItem { Expr = ParseExpression(c) };
        if (c.MatchWord("AS")) item.Alias = c.ExpectIdentifier();
        else if (c.Current is { Kind: TokenKind.Word } w && !NotAnAlias.Contains(w.Value)) item.Alias = c.Advance().Value;
        else if (c.Current is { Kind: TokenKind.QuotedIdent }) item.Alias = c.Advance().Value;
        return item;
    }

    private void ParseGroupBy(TokenCursor c, SelectQuery q)
    {
        c.MatchWord("ALL"); c.MatchWord("DISTINCT");
        if (c.AtAnyWord("ROLLUP", "CUBE"))
        {
            q.GroupByKind = c.Advance().Value.ToUpperInvariant();
            c.ExpectSymbol('('); q.GroupBy.Add(ParseExpression(c)); while (c.MatchSymbol(',')) q.GroupBy.Add(ParseExpression(c)); c.ExpectSymbol(')');
            return;
        }
        if (c.MatchWords("GROUPING", "SETS"))
        {
            q.GroupByKind = "GROUPING SETS";
            c.ExpectSymbol('(');
            int depth = 1; while (!c.AtEnd && depth > 0) { var t = c.Advance(); if (t.IsSymbol('(')) depth++; else if (t.IsSymbol(')')) depth--; }
            return;
        }
        q.GroupBy.Add(ParseExpression(c));
        while (c.MatchSymbol(',')) q.GroupBy.Add(ParseExpression(c));
    }

    private FromClause ParseFromClause(TokenCursor c)
    {
        var from = new FromClause();
        from.Relations.Add(ParseTableRefWithJoins(c));
        while (c.MatchSymbol(',')) from.Relations.Add(ParseTableRefWithJoins(c));
        return from;
    }

    private TableRef ParseTableRefWithJoins(TokenCursor c)
    {
        var rel = ParseTableRefAtom(c);
        while (true)
        {
            bool natural = c.MatchWord("NATURAL");
            string? jt = null;
            if (c.MatchWord("CROSS")) { c.ExpectWord("JOIN"); jt = "CROSS"; }
            else if (c.AtAnyWord("INNER", "LEFT", "RIGHT", "FULL", "JOIN"))
            {
                if (c.MatchWord("INNER")) jt = "INNER";
                else if (c.MatchWord("LEFT")) { c.MatchWord("OUTER"); jt = "LEFT"; }
                else if (c.MatchWord("RIGHT")) { c.MatchWord("OUTER"); jt = "RIGHT"; }
                else if (c.MatchWord("FULL")) { c.MatchWord("OUTER"); jt = "FULL"; }
                else jt = "INNER";
                c.ExpectWord("JOIN");
            }
            else { if (natural) throw new ParseException("expected JOIN after NATURAL", c.Here); break; }

            var join = new JoinClause { JoinType = natural ? "NATURAL " + jt : jt!, Right = ParseTableRefAtom(c) };
            if (jt != "CROSS" && !natural)
            {
                if (c.MatchWord("ON")) join.On = ParseExpression(c);
                else if (c.MatchWord("USING")) join.Using.AddRange(ParseColumnNameList(c));
                else throw new ParseException("JOIN requires ON or USING (unless CROSS/NATURAL)", c.Here);
            }
            rel.Joins.Add(join);
        }
        return rel;
    }

    private TableRef ParseTableRefAtom(TokenCursor c)
    {
        var rel = new TableRef { Lateral = c.MatchWord("LATERAL") };

        if (c.AtSymbol('('))
        {
            c.Advance();
            if (c.AtAnyWord("SELECT", "WITH", "VALUES", "TABLE")) { rel.Subquery = ParseSelectStatement(c); c.ExpectSymbol(')'); }
            else { var nested = ParseTableRefWithJoins(c); c.ExpectSymbol(')'); rel.Subquery = null; rel.RawText = "(joined)"; rel.Schema = nested.Schema; rel.TableName = nested.TableName; rel.Joins.AddRange(nested.Joins); }
        }
        else if (c.AtWord("ROWS") && c.Peek()?.IsWord("FROM") == true)
        {
            c.Advance(); c.Advance(); rel.RawText = "ROWS FROM " + Token.Render(WithParensList(CaptureBalancedParens(c)));
        }
        else
        {
            rel.Only = c.MatchWord("ONLY");
            var (s, n) = ParseQualifiedName(c);
            if (c.AtSymbol('(')) { rel.Function = ParseCallTail(c, s is null ? new List<string> { n } : new List<string> { s, n }); }
            else { rel.Schema = s; rel.TableName = n; c.MatchSymbol('*'); }
        }

        if (c.MatchWords("WITH", "ORDINALITY")) rel.WithOrdinality = true;

        if (c.MatchWord("TABLESAMPLE"))
        {
            c.ExpectIdentifier();
            if (c.AtSymbol('(')) CaptureBalancedParens(c);
            if (c.MatchWord("REPEATABLE")) { c.ExpectSymbol('('); ParseExpression(c); c.ExpectSymbol(')'); }
        }

        // alias
        if (c.MatchWord("AS")) { rel.Alias = c.ExpectIdentifier(); if (c.AtSymbol('(')) rel.ColumnAliases.AddRange(ParseColumnNameList(c)); }
        else if (c.Current is { Kind: TokenKind.Word } w && !IsFromBoundary(w.Value))
        { rel.Alias = c.Advance().Value; if (c.AtSymbol('(')) rel.ColumnAliases.AddRange(ParseColumnNameList(c)); }
        else if (c.Current is { Kind: TokenKind.QuotedIdent }) { rel.Alias = c.Advance().Value; if (c.AtSymbol('(')) rel.ColumnAliases.AddRange(ParseColumnNameList(c)); }

        return rel;
    }

    private static bool IsFromBoundary(string w) =>
        w.Equals("ON", StringComparison.OrdinalIgnoreCase) || w.Equals("USING", StringComparison.OrdinalIgnoreCase)
        || w.Equals("WHERE", StringComparison.OrdinalIgnoreCase) || w.Equals("GROUP", StringComparison.OrdinalIgnoreCase)
        || w.Equals("HAVING", StringComparison.OrdinalIgnoreCase) || w.Equals("WINDOW", StringComparison.OrdinalIgnoreCase)
        || w.Equals("ORDER", StringComparison.OrdinalIgnoreCase) || w.Equals("LIMIT", StringComparison.OrdinalIgnoreCase)
        || w.Equals("OFFSET", StringComparison.OrdinalIgnoreCase) || w.Equals("FETCH", StringComparison.OrdinalIgnoreCase)
        || w.Equals("FOR", StringComparison.OrdinalIgnoreCase) || w.Equals("UNION", StringComparison.OrdinalIgnoreCase)
        || w.Equals("INTERSECT", StringComparison.OrdinalIgnoreCase) || w.Equals("EXCEPT", StringComparison.OrdinalIgnoreCase)
        || w.Equals("JOIN", StringComparison.OrdinalIgnoreCase) || w.Equals("INNER", StringComparison.OrdinalIgnoreCase)
        || w.Equals("LEFT", StringComparison.OrdinalIgnoreCase) || w.Equals("RIGHT", StringComparison.OrdinalIgnoreCase)
        || w.Equals("FULL", StringComparison.OrdinalIgnoreCase) || w.Equals("CROSS", StringComparison.OrdinalIgnoreCase)
        || w.Equals("NATURAL", StringComparison.OrdinalIgnoreCase) || w.Equals("TABLESAMPLE", StringComparison.OrdinalIgnoreCase)
        || w.Equals("WITH", StringComparison.OrdinalIgnoreCase);

    private void ParseSelectTail(TokenCursor c, SelectQuery q)
    {
        if (c.MatchWords("ORDER", "BY")) q.OrderBy.AddRange(ParseOrderByList(c));

        while (c.AtAnyWord("LIMIT", "OFFSET", "FETCH"))
        {
            if (c.MatchWord("LIMIT")) { if (c.MatchWord("ALL")) q.Limit = "ALL"; else q.Limit = ExprText(c); }
            else if (c.MatchWord("OFFSET")) { q.Offset = ExprText(c); c.MatchWord("ROW"); c.MatchWord("ROWS"); }
            else { c.MatchWord("FIRST"); c.MatchWord("NEXT"); if (!c.AtWord("ROW") && !c.AtWord("ROWS")) q.Limit = ExprText(c); c.MatchWord("ROW"); c.MatchWord("ROWS"); if (!c.MatchWord("ONLY")) c.MatchWords("WITH", "TIES"); }
        }

        while (c.MatchWord("FOR"))
        {
            var lk = new LockingClause { Strength = ParseLockStrength(c) };
            if (c.MatchWord("OF")) { lk.Of.Add(c.ExpectIdentifier()); while (c.MatchSymbol(',')) lk.Of.Add(c.ExpectIdentifier()); }
            if (c.MatchWord("NOWAIT")) lk.Wait = "NOWAIT";
            else if (c.MatchWords("SKIP", "LOCKED")) lk.Wait = "SKIP LOCKED";
            q.Locking.Add(lk);
        }
    }

    private static string ParseLockStrength(TokenCursor c)
    {
        if (c.MatchWord("UPDATE")) return "UPDATE";
        if (c.MatchWords("NO", "KEY", "UPDATE")) return "NO KEY UPDATE";
        if (c.MatchWord("SHARE")) return "SHARE";
        if (c.MatchWords("KEY", "SHARE")) return "KEY SHARE";
        throw new ParseException("expected UPDATE / NO KEY UPDATE / SHARE / KEY SHARE", c.Here);
    }

    private List<OrderByItem> ParseOrderByList(TokenCursor c)
    {
        var items = new List<OrderByItem>();
        do
        {
            var item = new OrderByItem { Expr = ParseExpression(c) };
            if (c.MatchWord("ASC")) item.Direction = "ASC";
            else if (c.MatchWord("DESC")) item.Direction = "DESC";
            else if (c.MatchWord("USING")) item.Direction = "USING " + c.Advance().Value;
            if (c.MatchWord("NULLS")) item.Nulls = c.MatchWord("FIRST") ? "FIRST" : (c.MatchWord("LAST") ? "LAST" : null);
            items.Add(item);
        } while (c.MatchSymbol(','));
        return items;
    }

    private WindowSpec ParseWindowSpecOrName(TokenCursor c)
    {
        if (c.AtSymbol('(')) return ParseWindowDetails(c);
        return new WindowSpec { Name = c.ExpectIdentifier() };
    }

    private WindowSpec ParseWindowDetails(TokenCursor c)
    {
        c.ExpectSymbol('(');
        var w = new WindowSpec();
        if (c.Current is { Kind: TokenKind.Word } && !c.AtWord("PARTITION") && !c.AtWord("ORDER")
            && !c.AtWord("ROWS") && !c.AtWord("RANGE") && !c.AtWord("GROUPS"))
            w.RefName = c.Advance().Value;
        if (c.MatchWords("PARTITION", "BY")) { w.PartitionBy.Add(ParseExpression(c)); while (c.MatchSymbol(',')) w.PartitionBy.Add(ParseExpression(c)); }
        if (c.MatchWords("ORDER", "BY")) w.OrderBy.AddRange(ParseOrderByList(c));
        if (c.AtAnyWord("ROWS", "RANGE", "GROUPS"))
        {
            var frame = new List<Token>();
            while (!c.AtEnd && !c.AtSymbol(')')) frame.Add(c.Advance());
            w.FrameText = Token.Render(frame);
        }
        c.ExpectSymbol(')');
        return w;
    }

    // ---- small helpers ------------------------------------------------------

    private static string ExprText(TokenCursor c)
    {
        // a numeric/parameter/parenthesised limit value
        if (c.Current is { Kind: TokenKind.Number } n) { c.Advance(); return n.Value; }
        if (c.AtSymbol('(')) return Token.Render(WithParensList(CaptureBalancedParens(c)));
        return c.ExpectIdentifier();
    }

    private static List<Token> WithParensList(List<Token> inner)
    {
        var o = new List<Token> { new(TokenKind.Symbol, "(", 0) };
        o.AddRange(inner);
        o.Add(new Token(TokenKind.Symbol, ")", 0));
        return o;
    }

    private static List<Token> CaptureBalancedInner(TokenCursor c)
    {
        c.ExpectSymbol('(');
        var inner = new List<Token>(); int depth = 1;
        while (!c.AtEnd) { var t = c.Advance(); if (t.IsSymbol(')')) { depth--; if (depth == 0) return inner; } else if (t.IsSymbol('(')) depth++; inner.Add(t); }
        throw new ParseException("unbalanced '('", c.Here);
    }
}
