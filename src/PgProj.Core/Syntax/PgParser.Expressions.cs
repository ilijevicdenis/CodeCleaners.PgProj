using System;
using System.Collections.Generic;
using System.Linq;
using PgProj.Core.Parsing;

namespace PgProj.Core.Syntax;

// Expression grammar for PgParser. Layered recursive descent, one method per precedence band
// (ParseOr → ParseAnd → … → ParsePrimary). Mutually recursive with the SELECT parser via subqueries.
public sealed partial class PgParser
{
    private static readonly HashSet<string> ComparisonOps = new() { "<", ">", "=", "<=", ">=", "<>", "!=" };

    // operators that have their own precedence band and must NOT be treated as "general" operators
    private static readonly HashSet<string> ReservedOps = new()
    { "+", "-", "*", "/", "%", "^", "::", ".", "<", ">", "=", "<=", ">=", "<>", "!=" };

    private Expr ParseExpression(TokenCursor c) => ParseOr(c);

    private Expr ParseOr(TokenCursor c)
    {
        var left = ParseAnd(c);
        while (c.MatchWord("OR")) left = new BinaryExpr { Op = "OR", Left = left, Right = ParseAnd(c) };
        return left;
    }

    private Expr ParseAnd(TokenCursor c)
    {
        var left = ParseNot(c);
        while (c.MatchWord("AND")) left = new BinaryExpr { Op = "AND", Left = left, Right = ParseNot(c) };
        return left;
    }

    private Expr ParseNot(TokenCursor c)
    {
        if (c.MatchWord("NOT")) return new UnaryExpr { Op = "NOT", Operand = ParseNot(c) };
        return ParsePredicate(c);
    }

    // comparison / IS / BETWEEN / IN / LIKE / quantified — sits above the arithmetic/general bands
    private Expr ParsePredicate(TokenCursor c)
    {
        var left = ParseGeneralOp(c);
        while (true)
        {
            if (c.CurrentOperator is { } op && ComparisonOps.Contains(op))
            {
                c.Advance();
                if (c.AtAnyWord("ANY", "ALL", "SOME"))
                {
                    var q = c.Advance().Value.ToUpperInvariant();
                    left = ParseQuantified(c, left, op, q);
                }
                else left = new BinaryExpr { Op = op, Left = left, Right = ParseGeneralOp(c) };
                continue;
            }

            bool not = false;
            if (c.AtWord("NOT") && (c.Peek() is { } p && (p.IsWord("BETWEEN") || p.IsWord("IN") || p.IsWord("LIKE") || p.IsWord("ILIKE") || p.IsWord("SIMILAR"))))
            { c.Advance(); not = true; }

            if (c.MatchWord("BETWEEN")) { left = ParseBetween(c, left, not); continue; }
            if (c.MatchWord("IN")) { left = ParseIn(c, left, not); continue; }
            if (c.AtAnyWord("LIKE", "ILIKE")) { var k = c.Advance().Value.ToUpperInvariant(); left = ParseLike(c, left, k, not); continue; }
            if (c.MatchWord("SIMILAR")) { c.ExpectWord("TO"); left = ParseLike(c, left, "SIMILAR TO", not); continue; }
            if (not) throw new ParseException("expected BETWEEN/IN/LIKE after NOT", c.Here);

            if (c.MatchWord("IS")) { left = ParseIs(c, left); continue; }
            if (c.MatchWord("ISNULL")) { left = new PostfixExpr { Op = "ISNULL", Operand = left }; continue; }
            if (c.MatchWord("NOTNULL")) { left = new PostfixExpr { Op = "NOTNULL", Operand = left }; continue; }
            break;
        }
        return left;
    }

    private Expr ParseQuantified(TokenCursor c, Expr left, string op, string quant)
    {
        c.ExpectSymbol('(');
        if (c.AtAnyWord("SELECT", "WITH", "VALUES", "TABLE"))
        {
            var sub = ParseSelectStatement(c);
            c.ExpectSymbol(')');
            return new QuantifiedExpr { Left = left, Op = op, Quantifier = quant, Subquery = sub };
        }
        var arr = ParseExpression(c);
        c.ExpectSymbol(')');
        return new QuantifiedExpr { Left = left, Op = op, Quantifier = quant, Array = arr };
    }

    private Expr ParseBetween(TokenCursor c, Expr left, bool not)
    {
        bool sym = c.MatchWord("SYMMETRIC"); if (!sym) c.MatchWord("ASYMMETRIC");
        var lo = ParseGeneralOp(c);
        c.ExpectWord("AND");
        var hi = ParseGeneralOp(c);
        return new BetweenExpr { Operand = left, Low = lo, High = hi, Not = not, Symmetric = sym };
    }

    private Expr ParseIn(TokenCursor c, Expr left, bool not)
    {
        c.ExpectSymbol('(');
        if (c.AtAnyWord("SELECT", "WITH", "VALUES", "TABLE"))
        {
            var sub = ParseSelectStatement(c);
            c.ExpectSymbol(')');
            return new InExpr { Operand = left, Not = not, Subquery = sub };
        }
        var list = new List<Expr> { ParseExpression(c) };
        while (c.MatchSymbol(',')) list.Add(ParseExpression(c));
        c.ExpectSymbol(')');
        return new InExpr { Operand = left, Not = not, List = list };
    }

    private Expr ParseLike(TokenCursor c, Expr left, string kind, bool not)
    {
        var pat = ParseGeneralOp(c);
        var node = new PatternMatchExpr { Operand = left, Kind = kind, Not = not, Pattern = pat };
        if (c.MatchWord("ESCAPE")) node.Escape = ParseGeneralOp(c);
        return node;
    }

    private Expr ParseIs(TokenCursor c, Expr left)
    {
        bool not = c.MatchWord("NOT");
        if (c.MatchWord("NULL")) return new IsCheckExpr { Operand = left, Not = not, What = "NULL" };
        if (c.MatchWord("TRUE")) return new IsCheckExpr { Operand = left, Not = not, What = "TRUE" };
        if (c.MatchWord("FALSE")) return new IsCheckExpr { Operand = left, Not = not, What = "FALSE" };
        if (c.MatchWord("UNKNOWN")) return new IsCheckExpr { Operand = left, Not = not, What = "UNKNOWN" };
        if (c.MatchWord("DOCUMENT")) return new IsCheckExpr { Operand = left, Not = not, What = "DOCUMENT" };
        if (c.MatchWords("DISTINCT", "FROM")) return new IsCheckExpr { Operand = left, Not = not, What = "DISTINCT", Other = ParseGeneralOp(c) };
        if (c.MatchWord("JSON"))
        {
            // IS [NOT] JSON [VALUE|OBJECT|ARRAY|SCALAR] [WITH|WITHOUT UNIQUE [KEYS]]
            c.MatchWord("VALUE"); c.MatchWord("OBJECT"); c.MatchWord("ARRAY"); c.MatchWord("SCALAR");
            if (c.MatchWord("WITH") || c.MatchWord("WITHOUT")) { c.MatchWord("UNIQUE"); c.MatchWord("KEYS"); }
            return new IsCheckExpr { Operand = left, Not = not, What = "JSON" };
        }
        throw new ParseException("expected NULL/TRUE/FALSE/UNKNOWN/DISTINCT FROM/DOCUMENT/JSON after IS", c.Here);
    }

    // general (named/symbolic) binary operators: ||, @>, ->, ->>, #>, &, |, #, <<, >>, ~, !~, etc.
    private const string OperatorChars = "+-*/<>=~!@#%^&|?:";
    private static bool IsOperatorSymbol(string v)
    {
        if (v.Length == 0) return false;
        foreach (var ch in v) if (OperatorChars.IndexOf(ch) < 0) return false;
        return true;
    }

    private Expr ParseGeneralOp(TokenCursor c)
    {
        var left = ParseAdditive(c);
        while (c.CurrentOperator is { } op && IsOperatorSymbol(op) && !ReservedOps.Contains(op))
        {
            c.Advance();
            if (c.AtAnyWord("ANY", "ALL", "SOME"))
            {
                var q = c.Advance().Value.ToUpperInvariant();
                left = ParseQuantified(c, left, op, q);
            }
            else left = new BinaryExpr { Op = op, Left = left, Right = ParseAdditive(c) };
        }
        return left;
    }

    private Expr ParseAdditive(TokenCursor c)
    {
        var left = ParseMultiplicative(c);
        while (c.AtOperator("+") || c.AtOperator("-"))
        {
            var op = c.Advance().Value;
            left = new BinaryExpr { Op = op, Left = left, Right = ParseMultiplicative(c) };
        }
        return left;
    }

    private Expr ParseMultiplicative(TokenCursor c)
    {
        var left = ParseExponent(c);
        while (c.AtOperator("*") || c.AtOperator("/") || c.AtOperator("%"))
        {
            var op = c.Advance().Value;
            left = new BinaryExpr { Op = op, Left = left, Right = ParseExponent(c) };
        }
        return left;
    }

    private Expr ParseExponent(TokenCursor c)
    {
        var left = ParseUnarySign(c);
        if (c.MatchOperator("^")) return new BinaryExpr { Op = "^", Left = left, Right = ParseExponent(c) };
        return left;
    }

    private Expr ParseUnarySign(TokenCursor c)
    {
        if (c.AtOperator("+") || c.AtOperator("-") || c.AtOperator("~"))
        {
            var op = c.Advance().Value;
            return new UnaryExpr { Op = op, Operand = ParseUnarySign(c) };
        }
        return ParsePostfix(c);
    }

    private Expr ParsePostfix(TokenCursor c)
    {
        var e = ParsePrimary(c);
        while (true)
        {
            if (c.MatchOperator("::")) { e = new CastExpr { Operand = e, TypeText = ParseCastType(c) }; continue; }
            if (c.AtSymbol('[')) { e = new SubscriptExpr { Operand = e, IndexText = Token.Render(CaptureBracket(c)) }; continue; }
            if (c.MatchWord("COLLATE")) { var (s, n) = ParseQualifiedName(c); e = new CollateExpr { Operand = e, Collation = s is null ? n : $"{s}.{n}" }; continue; }
            if (c.LookaheadWords("AT", "TIME", "ZONE")) { c.MatchWords("AT", "TIME", "ZONE"); e = new BinaryExpr { Op = "AT TIME ZONE", Left = e, Right = ParseAdditive(c) }; continue; }
            break;
        }
        return e;
    }

    private Expr ParsePrimary(TokenCursor c)
    {
        var t = c.Current ?? throw new ParseException("expected an expression", c.Here);

        // literals
        if (t.Kind == TokenKind.Number) { c.Advance(); return new LiteralExpr { Kind = "number", Text = t.Value }; }
        if (t.Kind == TokenKind.String)
        {
            c.Advance();
            // adjacent string literals concatenate
            while (c.Current is { Kind: TokenKind.String }) c.Advance();
            return new LiteralExpr { Kind = "string", Text = t.Value };
        }
        if (t.Kind == TokenKind.DollarString) { c.Advance(); return new LiteralExpr { Kind = "string", Text = t.Value }; }

        if (t.Kind == TokenKind.Symbol)
        {
            if (t.IsSymbol('(')) return ParseParenOrSubquery(c);
            if (t.IsSymbol('*')) { c.Advance(); return new StarExpr(); }   // leading * is the star, not multiply
            // prefix general operator (e.g. @, |/, ~)
            if (IsOperatorSymbol(t.Value))
            {
                c.Advance();
                return new UnaryExpr { Op = t.Value, Operand = ParseUnarySign(c) };
            }
            throw new ParseException($"unexpected '{t.Value}' in expression", c.Here);
        }

        // keyword literals & specials
        if (c.MatchWord("NULL")) return new LiteralExpr { Kind = "null", Text = "NULL" };
        if (c.MatchWord("TRUE")) return new LiteralExpr { Kind = "bool", Text = "TRUE" };
        if (c.MatchWord("FALSE")) return new LiteralExpr { Kind = "bool", Text = "FALSE" };
        if (c.AtWord("CASE")) return ParseCase(c);
        if (c.AtWord("CAST")) return ParseCastFunction(c);
        if (c.AtWord("EXISTS") && c.Peek()?.IsSymbol('(') == true) { c.Advance(); c.ExpectSymbol('('); var q = ParseSelectStatement(c); c.ExpectSymbol(')'); return new ExistsExpr { Query = q }; }
        if (c.AtWord("ARRAY")) return ParseArray(c);
        if (c.AtWord("ROW") && c.Peek()?.IsSymbol('(') == true) { c.Advance(); return ParseRow(c, explicitRow: true); }
        if (c.AtAnyWord("INTERVAL")) return ParseTypedLiteralWord(c);
        if (c.AtAnyWord("EXTRACT", "POSITION", "OVERLAY", "SUBSTRING", "TRIM")) return ParseSpecialFunction(c);

        // a bare type keyword immediately before a string is a typed literal: timestamp '…'
        if (t.Kind == TokenKind.Word && c.Peek() is { Kind: TokenKind.String } && IsTypeKeyword(t.Value))
        { c.Advance(); var s = c.Advance(); return new LiteralExpr { Kind = "typed", Text = $"{t.Value} '{s.Value}'" }; }

        // identifier chain → column ref / function call / star
        return ParseNameOrCall(c);
    }

    private Expr ParseParenOrSubquery(TokenCursor c)
    {
        c.ExpectSymbol('(');
        if (c.AtAnyWord("SELECT", "WITH", "VALUES", "TABLE"))
        {
            var q = ParseSelectStatement(c);
            c.ExpectSymbol(')');
            return new SubqueryExpr { Query = q };
        }
        var first = ParseExpression(c);
        if (c.MatchSymbol(','))
        {
            var row = new RowExpr();
            row.Items.Add(first);
            do { row.Items.Add(ParseExpression(c)); } while (c.MatchSymbol(','));
            c.ExpectSymbol(')');
            return row;
        }
        c.ExpectSymbol(')');
        return first;
    }

    private Expr ParseNameOrCall(TokenCursor c)
    {
        if (c.AtSymbol('*')) { c.Advance(); return new StarExpr(); }
        var parts = new List<string> { c.ExpectIdentifier() };
        while (c.MatchSymbol('.'))
        {
            if (c.MatchSymbol('*')) { var star = new StarExpr(); star.Qualifier.AddRange(parts); return star; }
            parts.Add(c.ExpectIdentifier());
        }
        if (c.AtSymbol('(')) return ParseCallTail(c, parts);
        return new ColumnRef { Parts = { } }.With(parts);
    }

    private FuncCallExpr ParseCallTail(TokenCursor c, List<string> name)
    {
        var call = new FuncCallExpr();
        call.Name.AddRange(name);
        c.ExpectSymbol('(');
        if (c.MatchSymbol('*')) { call.Star = true; c.ExpectSymbol(')'); }
        else if (c.AtSymbol(')')) { c.Advance(); }
        else
        {
            if (c.MatchWord("DISTINCT")) call.Distinct = true; else c.MatchWord("ALL");
            call.Variadic = c.MatchWord("VARIADIC");
            call.Args.Add(ParseExpression(c));
            while (c.MatchSymbol(','))
            {
                c.MatchWord("VARIADIC");
                call.Args.Add(ParseExpression(c));
            }
            if (c.MatchWords("ORDER", "BY")) call.OrderBy.AddRange(ParseOrderByList(c));
            c.ExpectSymbol(')');
        }
        // post-call: WITHIN GROUP (ORDER BY …), FILTER (WHERE …), OVER (…)
        if (c.MatchWords("WITHIN", "GROUP")) { c.ExpectSymbol('('); c.MatchWords("ORDER", "BY"); call.WithinGroup.AddRange(ParseOrderByList(c)); c.ExpectSymbol(')'); }
        if (c.MatchWord("FILTER")) { c.ExpectSymbol('('); c.ExpectWord("WHERE"); call.Filter = ParseExpression(c); c.ExpectSymbol(')'); }
        if (c.MatchWord("OVER")) call.Over = ParseWindowSpecOrName(c);
        return call;
    }

    private Expr ParseCase(TokenCursor c)
    {
        c.ExpectWord("CASE");
        var node = new CaseExpr { Operand = c.AtWord("WHEN") ? null : ParseExpression(c) };
        while (c.MatchWord("WHEN"))
        {
            var when = ParseExpression(c);
            c.ExpectWord("THEN");
            node.Branches.Add((when, ParseExpression(c)));
        }
        if (c.MatchWord("ELSE")) node.Else = ParseExpression(c);
        c.ExpectWord("END");
        if (node.Branches.Count == 0) throw new ParseException("CASE requires at least one WHEN", c.Here);
        return node;
    }

    private Expr ParseCastFunction(TokenCursor c)
    {
        c.ExpectWord("CAST");
        c.ExpectSymbol('(');
        var e = ParseExpression(c);
        c.ExpectWord("AS");
        var ty = ParseCastType(c);
        c.ExpectSymbol(')');
        return new CastExpr { Operand = e, TypeText = ty };
    }

    private Expr ParseArray(TokenCursor c)
    {
        c.ExpectWord("ARRAY");
        if (c.AtSymbol('('))
        {
            c.ExpectSymbol('(');
            var q = ParseSelectStatement(c);
            c.ExpectSymbol(')');
            return new ArrayExpr { Subquery = q };
        }
        if (!c.AtSymbol('[')) throw new ParseException("expected '[' or '(' after ARRAY", c.Here);
        return ParseArrayBracket(c);
    }

    private ArrayExpr ParseArrayBracket(TokenCursor c)
    {
        c.ExpectSymbol('[');
        var arr = new ArrayExpr();
        if (!c.AtSymbol(']'))
        {
            do
            {
                if (c.AtSymbol('[')) arr.Elements.Add(ParseArrayBracket(c));
                else arr.Elements.Add(ParseExpression(c));
            } while (c.MatchSymbol(','));
        }
        c.ExpectSymbol(']');
        return arr;
    }

    private Expr ParseRow(TokenCursor c, bool explicitRow)
    {
        c.ExpectSymbol('(');
        var row = new RowExpr { ExplicitRow = explicitRow };
        if (!c.AtSymbol(')'))
            do { row.Items.Add(ParseExpression(c)); } while (c.MatchSymbol(','));
        c.ExpectSymbol(')');
        return row;
    }

    private Expr ParseTypedLiteralWord(TokenCursor c)
    {
        var kw = c.Advance().Value;                          // INTERVAL
        if (c.Current is not { Kind: TokenKind.String }) throw new ParseException($"expected a string literal after {kw}", c.Here);
        var s = c.Advance();
        // optional interval fields / precision
        var tail = new List<Token>();
        while (c.Current is { } n && (n.Kind == TokenKind.Word || n.IsSymbol('(') )
               && (n.Kind != TokenKind.Word || IsIntervalField(n.Value)))
        {
            if (c.AtSymbol('(')) tail.AddRange(CaptureBalancedParens(c).Prepend(new Token(TokenKind.Symbol, "(", 0)).Append(new Token(TokenKind.Symbol, ")", 0)));
            else tail.Add(c.Advance());
        }
        return new LiteralExpr { Kind = "typed", Text = $"{kw} '{s.Value}'" };
    }

    private Expr ParseSpecialFunction(TokenCursor c)
    {
        var fn = c.Advance().Value.ToUpperInvariant();
        c.ExpectSymbol('(');
        var call = new FuncCallExpr();
        call.Name.Add(fn.ToLowerInvariant());
        switch (fn)
        {
            case "EXTRACT":
                c.Advance();                                  // field (word or string)
                c.ExpectWord("FROM");
                call.Args.Add(ParseExpression(c));
                break;
            case "POSITION":
                call.Args.Add(ParseGeneralOp(c));             // POSITION(sub IN str) — IN here is the keyword
                c.ExpectWord("IN");
                call.Args.Add(ParseExpression(c));
                break;
            case "SUBSTRING":
                call.Args.Add(ParseExpression(c));
                if (c.MatchWord("FROM")) { call.Args.Add(ParseExpression(c)); if (c.MatchWord("FOR")) call.Args.Add(ParseExpression(c)); }
                else if (c.MatchWord("FOR")) call.Args.Add(ParseExpression(c));
                else while (c.MatchSymbol(',')) call.Args.Add(ParseExpression(c));
                if (c.MatchWord("SIMILAR")) { call.Args.Add(ParseExpression(c)); c.ExpectWord("ESCAPE"); call.Args.Add(ParseExpression(c)); }
                break;
            case "OVERLAY":
                call.Args.Add(ParseExpression(c));
                c.ExpectWord("PLACING"); call.Args.Add(ParseExpression(c));
                c.ExpectWord("FROM"); call.Args.Add(ParseExpression(c));
                if (c.MatchWord("FOR")) call.Args.Add(ParseExpression(c));
                break;
            case "TRIM":
                c.MatchWord("LEADING"); c.MatchWord("TRAILING"); c.MatchWord("BOTH");
                if (!c.AtWord("FROM")) call.Args.Add(ParseExpression(c));
                if (c.MatchWord("FROM")) call.Args.Add(ParseExpression(c));
                while (c.MatchSymbol(',')) call.Args.Add(ParseExpression(c));
                break;
        }
        c.ExpectSymbol(')');
        return call;
    }

    // ---- type & helpers -----------------------------------------------------

    private string ParseCastType(TokenCursor c)
    {
        var toks = new List<Token>();
        if (c.Current is not { Kind: TokenKind.Word or TokenKind.QuotedIdent })
            throw new ParseException("expected a type name", c.Here);
        toks.Add(c.Advance());
        if (c.AtSymbol('.')) { toks.Add(c.Advance()); toks.Add(c.Advance()); }   // schema.type
        // multiword type continuations
        while (c.Current is { Kind: TokenKind.Word } w && IsTypeContinuation(w.Value)) toks.Add(c.Advance());
        if (c.AtSymbol('(')) toks.AddRange(WithParens(CaptureBalancedParens(c)));
        while (c.AtSymbol('[')) toks.AddRange(CaptureBracketWith(c));
        return Token.Render(toks);
    }

    private static IEnumerable<Token> WithParens(List<Token> inner)
    {
        yield return new Token(TokenKind.Symbol, "(", 0);
        foreach (var t in inner) yield return t;
        yield return new Token(TokenKind.Symbol, ")", 0);
    }

    private static List<Token> CaptureBracket(TokenCursor c)
    {
        var toks = new List<Token>();
        c.ExpectSymbol('[');
        int depth = 1;
        while (!c.AtEnd)
        {
            var t = c.Advance();
            if (t.IsSymbol(']')) { depth--; if (depth == 0) break; }
            else if (t.IsSymbol('[')) depth++;
            toks.Add(t);
        }
        return toks;
    }

    private static List<Token> CaptureBracketWith(TokenCursor c)
    {
        var inner = CaptureBracket(c);
        var outp = new List<Token> { new(TokenKind.Symbol, "[", 0) };
        outp.AddRange(inner);
        outp.Add(new Token(TokenKind.Symbol, "]", 0));
        return outp;
    }

    private static readonly HashSet<string> TypeKeywords = new(StringComparer.OrdinalIgnoreCase)
    { "date", "time", "timestamp", "timestamptz", "boolean", "interval", "bit", "numeric", "decimal", "real", "money", "json", "jsonb", "uuid", "inet", "cidr", "macaddr" };
    private static bool IsTypeKeyword(string w) => TypeKeywords.Contains(w);

    private static readonly HashSet<string> TypeContinuations = new(StringComparer.OrdinalIgnoreCase)
    { "varying", "precision", "with", "without", "time", "zone" };
    private static bool IsTypeContinuation(string w) => TypeContinuations.Contains(w);

    private static readonly HashSet<string> IntervalFields = new(StringComparer.OrdinalIgnoreCase)
    { "year", "month", "day", "hour", "minute", "second", "to" };
    private static bool IsIntervalField(string w) => IntervalFields.Contains(w);
}

internal static class ColumnRefExt
{
    public static ColumnRef With(this ColumnRef r, List<string> parts) { r.Parts.AddRange(parts); return r; }
}
