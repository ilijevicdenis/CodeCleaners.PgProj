using System;
using System.Collections.Generic;
using PgProj.Core.Ast;

namespace PgProj.Core.Parsing;

/// <summary>
/// Parses a SELECT query to the depth static analysis needs: the <c>WITH</c> list (CTEs, each a
/// nested <see cref="SelectQuery"/>) and the <c>WHERE</c> predicate (a real <see cref="Expression"/>
/// tree) are structured; projection / FROM / trailing clauses are captured as text. Used for view
/// bodies and subqueries. Top-level scanning respects paren depth, so nested subqueries are left to
/// their own parse.
/// </summary>
public static class QueryParser
{
    private static readonly HashSet<string> ClauseStops = new(StringComparer.OrdinalIgnoreCase)
    {
        "FROM", "WHERE", "GROUP", "HAVING", "WINDOW", "ORDER", "LIMIT", "OFFSET",
        "FETCH", "UNION", "INTERSECT", "EXCEPT", "RETURNING", "FOR",
    };

    private static readonly HashSet<string> AfterWhere = new(StringComparer.OrdinalIgnoreCase)
    {
        "GROUP", "HAVING", "WINDOW", "ORDER", "LIMIT", "OFFSET", "FETCH",
        "UNION", "INTERSECT", "EXCEPT", "RETURNING", "FOR",
    };

    public static SelectQuery Parse(IReadOnlyList<Token> tokens)
    {
        var c = new Cursor(tokens);
        var recursive = false;
        var ctes = new List<CommonTableExpression>();

        if (c.MatchWord("WITH"))
        {
            recursive = c.MatchWord("RECURSIVE");
            ctes = ParseCtes(c);
        }

        c.MatchWord("SELECT");
        c.MatchWord("DISTINCT");
        c.MatchWord("ALL");

        var projection = c.CaptureUntil(ClauseStops);
        string? fromText = null;
        if (c.MatchWord("FROM"))
        {
            var stops = new HashSet<string>(AfterWhere, StringComparer.OrdinalIgnoreCase) { "WHERE" };
            fromText = c.CaptureUntil(stops);
        }

        Expression? where = null;
        if (c.MatchWord("WHERE"))
            where = ExpressionParser.Parse(c.CaptureUntilTokens(AfterWhere));

        var tail = c.Rest();

        return new SelectQuery
        {
            Recursive = recursive,
            With = ctes,
            ProjectionText = projection,
            FromText = string.IsNullOrWhiteSpace(fromText) ? null : fromText,
            Where = where,
            Tail = string.IsNullOrWhiteSpace(tail) ? null : tail,
        };
    }

    private static List<CommonTableExpression> ParseCtes(Cursor c)
    {
        var list = new List<CommonTableExpression>();
        while (!c.AtEnd)
        {
            var name = c.ReadIdentifier();
            // optional (col, ...) — skip
            if (c.IsSymbol('(')) c.SkipBalanced();
            c.MatchWord("AS");
            c.MatchWord("MATERIALIZED");
            c.MatchWord("NOT");
            c.MatchWord("MATERIALIZED");
            if (!c.IsSymbol('(')) break;
            var inner = c.CaptureBalancedInner();
            list.Add(new CommonTableExpression { Name = name, Query = Parse(inner) });
            if (!c.MatchSymbol(',')) break;
        }
        return list;
    }

    private sealed class Cursor
    {
        private readonly IReadOnlyList<Token> _t;
        private int _i;
        public Cursor(IReadOnlyList<Token> t) => _t = t;
        public bool AtEnd => _i >= _t.Count;
        private Token? Cur => _i < _t.Count ? _t[_i] : null;

        public bool MatchWord(string kw) { if (Cur is { } t && t.IsWord(kw)) { _i++; return true; } return false; }
        public bool MatchSymbol(char ch) { if (Cur is { } t && t.IsSymbol(ch)) { _i++; return true; } return false; }
        public bool IsSymbol(char ch) => Cur is { } t && t.IsSymbol(ch);

        public string ReadIdentifier()
        {
            if (Cur is { } t && (t.Kind == TokenKind.Word || t.Kind == TokenKind.QuotedIdent)) { _i++; return t.Value; }
            return "";
        }

        public string CaptureUntil(HashSet<string> stops) => Token.Render(CaptureUntilTokens(stops));

        public List<Token> CaptureUntilTokens(HashSet<string> stops)
        {
            var toks = new List<Token>(); var depth = 0;
            while (!AtEnd)
            {
                var t = _t[_i];
                if (t.IsSymbol('(')) depth++;
                else if (t.IsSymbol(')')) depth = Math.Max(0, depth - 1);
                if (depth == 0 && t.Kind == TokenKind.Word && stops.Contains(t.Value)) break;
                toks.Add(t); _i++;
            }
            return toks;
        }

        public void SkipBalanced()
        {
            if (!IsSymbol('(')) return;
            _i++; var depth = 1;
            while (!AtEnd && depth > 0) { var t = _t[_i++]; if (t.IsSymbol('(')) depth++; else if (t.IsSymbol(')')) depth--; }
        }

        public List<Token> CaptureBalancedInner()
        {
            var toks = new List<Token>();
            _i++; // opening '('
            var depth = 1;
            while (!AtEnd && depth > 0)
            {
                var t = _t[_i];
                if (t.IsSymbol('(')) depth++;
                else if (t.IsSymbol(')')) { depth--; if (depth == 0) { _i++; break; } }
                toks.Add(t); _i++;
            }
            return toks;
        }

        public string Rest()
        {
            var toks = new List<Token>();
            while (!AtEnd) toks.Add(_t[_i++]);
            return Token.Render(toks);
        }
    }
}
