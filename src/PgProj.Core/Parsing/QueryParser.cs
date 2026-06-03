using System;
using System.Collections.Generic;
using PgProj.Core.Ast;

namespace PgProj.Core.Parsing;

/// <summary>
/// A structured SELECT parser: CTEs (WITH [RECURSIVE]), DISTINCT, projection items (expr + alias +
/// star), FROM with table references and JOINs (type + ON/USING), WHERE / GROUP BY / HAVING /
/// ORDER BY / LIMIT / OFFSET, and set operations (UNION/INTERSECT/EXCEPT). Leaf predicates and
/// scalar expressions go through <see cref="ExpressionParser"/>. The public entry point never
/// throws — on anything outside the grammar it returns a query carrying the raw text.
/// </summary>
public static class QueryParser
{
    private static readonly HashSet<string> SelectStops = new(StringComparer.OrdinalIgnoreCase)
    {
        "FROM", "WHERE", "GROUP", "HAVING", "WINDOW", "ORDER", "LIMIT", "OFFSET",
        "FETCH", "UNION", "INTERSECT", "EXCEPT",
    };
    private static readonly HashSet<string> AfterFrom = new(SelectStops, StringComparer.OrdinalIgnoreCase) { };
    private static readonly HashSet<string> AfterWhere = new(StringComparer.OrdinalIgnoreCase)
    { "GROUP", "HAVING", "WINDOW", "ORDER", "LIMIT", "OFFSET", "FETCH", "UNION", "INTERSECT", "EXCEPT" };
    private static readonly HashSet<string> AfterGroup = new(StringComparer.OrdinalIgnoreCase)
    { "HAVING", "WINDOW", "ORDER", "LIMIT", "OFFSET", "FETCH", "UNION", "INTERSECT", "EXCEPT" };
    private static readonly HashSet<string> AfterHaving = new(StringComparer.OrdinalIgnoreCase)
    { "WINDOW", "ORDER", "LIMIT", "OFFSET", "FETCH", "UNION", "INTERSECT", "EXCEPT" };
    private static readonly HashSet<string> AfterOrder = new(StringComparer.OrdinalIgnoreCase)
    { "LIMIT", "OFFSET", "FETCH", "UNION", "INTERSECT", "EXCEPT" };
    private static readonly HashSet<string> AfterLimit = new(StringComparer.OrdinalIgnoreCase)
    { "OFFSET", "FETCH", "UNION", "INTERSECT", "EXCEPT" };
    private static readonly HashSet<string> JoinStarters = new(StringComparer.OrdinalIgnoreCase)
    { "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "NATURAL" };

    public static SelectQuery Parse(IReadOnlyList<Token> tokens)
    {
        try { return new Cursor(tokens).ParseQuery(); }
        catch { return new SelectQuery { RawText = Token.Render(tokens) }; }
    }

    private sealed class Cursor
    {
        private readonly IReadOnlyList<Token> _t;
        private int _i;
        public Cursor(IReadOnlyList<Token> t) => _t = t;

        private bool End => _i >= _t.Count;
        private Token? Cur => _i < _t.Count ? _t[_i] : null;
        private Token? Peek(int n = 1) => _i + n < _t.Count ? _t[_i + n] : null;
        private Token Next() => _t[_i++];
        private bool MatchWord(string kw) { if (Cur is { } t && t.IsWord(kw)) { _i++; return true; } return false; }
        private bool MatchSymbol(char c) { if (Cur is { } t && t.IsSymbol(c)) { _i++; return true; } return false; }
        private bool IsSymbol(char c) => Cur is { } t && t.IsSymbol(c);
        private bool IsWord(string kw) => Cur is { } t && t.IsWord(kw);

        public SelectQuery ParseQuery()
        {
            var recursive = false;
            var ctes = new List<CommonTableExpression>();
            if (MatchWord("WITH")) { recursive = MatchWord("RECURSIVE"); ctes = ParseCtes(); }

            var query = ParseTerm(ctes, recursive);

            var current = query;
            while (TryMatchSetOp(out var op))
            {
                var right = ParseTerm(new List<CommonTableExpression>(), false);
                current.SetOp = new SetOperation { Op = op, Right = right };
                current = right;
            }
            return query;
        }

        private SelectQuery ParseTerm(List<CommonTableExpression> ctes, bool recursive)
        {
            MatchWord("SELECT");
            var distinct = MatchWord("DISTINCT");
            MatchWord("ALL");
            if (distinct && MatchWord("ON")) SkipBalanced();

            var items = ParseSelectItems();
            FromClause? from = MatchWord("FROM") ? ParseFrom() : null;
            var where = MatchWord("WHERE") ? ExpressionParser.Parse(CaptureUntil(AfterWhere)) : null;

            var groupBy = new List<Expression>();
            if (MatchWord("GROUP")) { MatchWord("BY"); groupBy = ParseExprList(AfterGroup); }
            var having = MatchWord("HAVING") ? ExpressionParser.Parse(CaptureUntil(AfterHaving)) : null;

            var orderBy = new List<OrderByItem>();
            if (MatchWord("ORDER")) { MatchWord("BY"); orderBy = ParseOrderBy(); }

            var limit = MatchWord("LIMIT") ? ExpressionParser.Parse(CaptureUntil(AfterLimit)) : null;
            var offset = MatchWord("OFFSET") ? ExpressionParser.Parse(CaptureUntil(AfterLimit)) : null;

            return new SelectQuery
            {
                With = ctes, Recursive = recursive, Distinct = distinct, Items = items,
                From = from, Where = where, GroupBy = groupBy, Having = having,
                OrderBy = orderBy, Limit = limit, Offset = offset,
            };
        }

        private List<CommonTableExpression> ParseCtes()
        {
            var list = new List<CommonTableExpression>();
            while (!End)
            {
                var name = ReadIdent();
                if (IsSymbol('(')) SkipBalanced(); // optional column list
                MatchWord("AS");
                MatchWord("NOT"); MatchWord("MATERIALIZED");
                if (!IsSymbol('(')) break;
                list.Add(new CommonTableExpression { Name = name, Query = new Cursor(CaptureBalancedInner()).ParseQuery() });
                if (!MatchSymbol(',')) break;
            }
            return list;
        }

        private List<SelectItem> ParseSelectItems()
        {
            var items = new List<SelectItem>();
            foreach (var seg in SplitTopLevelCommas(CaptureUntil(SelectStops)))
            {
                if (seg.Count == 0) continue;
                if (seg.Count == 1 && seg[0].IsSymbol('*')) { items.Add(new SelectItem { IsStar = true }); continue; }
                var (exprToks, alias) = SplitAlias(seg);
                items.Add(new SelectItem { Expr = ExpressionParser.Parse(exprToks), Alias = alias, IsStar = false });
            }
            return items;
        }

        private FromClause ParseFrom()
        {
            var relations = new List<TableReference>();
            foreach (var seg in SplitTopLevelCommas(CaptureUntil(AfterFrom)))
                if (seg.Count > 0) relations.Add(ParseTableReference(seg));
            return new FromClause { Relations = relations };
        }

        private static TableReference ParseTableReference(List<Token> seg)
        {
            var c = new Cursor(seg);
            var (baseRef, _) = c.ReadBaseRelation();
            var joins = new List<JoinClause>();
            while (!c.End && c.Cur is { Kind: TokenKind.Word } w && JoinStarters.Contains(w.Value))
                joins.Add(c.ReadJoin());
            return new TableReference { TableName = baseRef.TableName, Subquery = baseRef.Subquery, Alias = baseRef.Alias, Joins = joins };
        }

        private (TableReference Ref, bool Ok) ReadBaseRelation()
        {
            if (IsSymbol('('))
            {
                var inner = CaptureBalancedInner();
                var alias = ReadOptionalAlias();
                return (new TableReference { Subquery = new Cursor(inner).ParseQuery(), Alias = alias }, true);
            }
            var name = ReadQualified();
            var al = ReadOptionalAlias();
            return (new TableReference { TableName = name, Alias = al }, true);
        }

        private JoinClause ReadJoin()
        {
            var type = "INNER";
            if (MatchWord("NATURAL")) type = "NATURAL";
            if (MatchWord("INNER")) type = "INNER";
            else if (MatchWord("LEFT")) { type = "LEFT"; MatchWord("OUTER"); }
            else if (MatchWord("RIGHT")) { type = "RIGHT"; MatchWord("OUTER"); }
            else if (MatchWord("FULL")) { type = "FULL"; MatchWord("OUTER"); }
            else if (MatchWord("CROSS")) type = "CROSS";
            MatchWord("JOIN");

            var (right, _) = ReadBaseRelation();
            Expression? on = null;
            var usingCols = new List<string>();
            if (MatchWord("ON"))
                on = ExpressionParser.Parse(CaptureJoinCondition());
            else if (MatchWord("USING") && IsSymbol('('))
                foreach (var t in CaptureBalancedInner()) if (t.IsIdentifierLike) usingCols.Add(t.Value);

            return new JoinClause { JoinType = type, Right = right, On = on, Using = usingCols };
        }

        // ON condition runs until the next JOIN starter or end of this relation segment.
        private List<Token> CaptureJoinCondition()
        {
            var toks = new List<Token>(); var depth = 0;
            while (!End)
            {
                var t = Cur!;
                if (t.IsSymbol('(')) depth++;
                else if (t.IsSymbol(')')) depth = Math.Max(0, depth - 1);
                if (depth == 0 && t.Kind == TokenKind.Word && JoinStarters.Contains(t.Value)) break;
                toks.Add(Next());
            }
            return toks;
        }

        private List<OrderByItem> ParseOrderBy()
        {
            var items = new List<OrderByItem>();
            foreach (var seg in SplitTopLevelCommas(CaptureUntil(AfterOrder)))
            {
                if (seg.Count == 0) continue;
                string? dir = null;
                var toks = seg;
                if (toks[^1].IsWord("ASC")) { dir = "ASC"; toks = toks.GetRange(0, toks.Count - 1); }
                else if (toks[^1].IsWord("DESC")) { dir = "DESC"; toks = toks.GetRange(0, toks.Count - 1); }
                items.Add(new OrderByItem { Expr = ExpressionParser.Parse(toks), Direction = dir });
            }
            return items;
        }

        private List<Expression> ParseExprList(HashSet<string> stops)
        {
            var list = new List<Expression>();
            foreach (var seg in SplitTopLevelCommas(CaptureUntil(stops)))
                if (seg.Count > 0) list.Add(ExpressionParser.Parse(seg));
            return list;
        }

        // ---- low-level helpers ----

        private bool TryMatchSetOp(out string op)
        {
            op = "";
            if (IsWord("UNION")) { Next(); op = MatchWord("ALL") ? "UNION ALL" : "UNION"; return true; }
            if (IsWord("INTERSECT")) { Next(); MatchWord("ALL"); op = "INTERSECT"; return true; }
            if (IsWord("EXCEPT")) { Next(); MatchWord("ALL"); op = "EXCEPT"; return true; }
            return false;
        }

        private string ReadIdent() => Cur is { } t && (t.Kind == TokenKind.Word || t.Kind == TokenKind.QuotedIdent) ? Next().Value : "";

        private string ReadQualified()
        {
            var first = ReadIdent();
            if (MatchSymbol('.')) return $"{first}.{ReadIdent()}";
            return first;
        }

        private string? ReadOptionalAlias()
        {
            if (MatchWord("AS")) return ReadIdent();
            if (Cur is { Kind: TokenKind.Word } w && !IsReserved(w.Value)) return Next().Value;
            if (Cur is { Kind: TokenKind.QuotedIdent }) return Next().Value;
            return null;
        }

        private static bool IsReserved(string w) =>
            JoinStarters.Contains(w) || w.ToUpperInvariant() is "ON" or "USING" or "WHERE" or "GROUP"
                or "ORDER" or "HAVING" or "LIMIT" or "OFFSET" or "UNION" or "INTERSECT" or "EXCEPT";

        private List<Token> CaptureUntil(HashSet<string> stops)
        {
            var toks = new List<Token>(); var depth = 0;
            while (!End)
            {
                var t = Cur!;
                if (t.IsSymbol('(')) depth++;
                else if (t.IsSymbol(')')) depth = Math.Max(0, depth - 1);
                if (depth == 0 && t.Kind == TokenKind.Word && stops.Contains(t.Value)) break;
                toks.Add(Next());
            }
            return toks;
        }

        private void SkipBalanced()
        {
            if (!IsSymbol('(')) return;
            _i++; var depth = 1;
            while (!End && depth > 0) { var t = Next(); if (t.IsSymbol('(')) depth++; else if (t.IsSymbol(')')) depth--; }
        }

        private List<Token> CaptureBalancedInner()
        {
            var toks = new List<Token>(); _i++; var depth = 1;
            while (!End && depth > 0)
            {
                var t = Cur!;
                if (t.IsSymbol('(')) depth++;
                else if (t.IsSymbol(')')) { depth--; if (depth == 0) { _i++; break; } }
                toks.Add(t); _i++;
            }
            return toks;
        }

        private static IEnumerable<List<Token>> SplitTopLevelCommas(List<Token> tokens)
        {
            var cur = new List<Token>(); var depth = 0;
            foreach (var t in tokens)
            {
                if (t.IsSymbol('(') || t.IsSymbol('[')) depth++;
                else if (t.IsSymbol(')') || t.IsSymbol(']')) depth--;
                if (depth == 0 && t.IsSymbol(',')) { yield return cur; cur = new List<Token>(); continue; }
                cur.Add(t);
            }
            if (cur.Count > 0) yield return cur;
        }

        // Split "expr [AS] alias" — alias is a trailing bare identifier/quoted-ident not part of the expr.
        private static (List<Token> Expr, string? Alias) SplitAlias(List<Token> seg)
        {
            for (var i = 0; i < seg.Count; i++)
                if (seg[i].IsWord("AS"))
                    return (seg.GetRange(0, i), i + 1 < seg.Count ? seg[i + 1].Value : null);

            if (seg.Count >= 2)
            {
                var last = seg[^1]; var prev = seg[^2];
                var lastIsAlias = (last.Kind == TokenKind.Word && !IsReserved(last.Value)) || last.Kind == TokenKind.QuotedIdent;
                var prevEndsExpr = prev.IsIdentifierLike || prev.Kind == TokenKind.Number || prev.IsSymbol(')');
                if (lastIsAlias && prevEndsExpr)
                    return (seg.GetRange(0, seg.Count - 1), last.Value);
            }
            return (seg, null);
        }
    }
}
