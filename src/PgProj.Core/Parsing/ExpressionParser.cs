using System.Collections.Generic;
using PgProj.Core.Ast;

namespace PgProj.Core.Parsing;

/// <summary>
/// A Pratt (precedence-climbing) parser that turns a token slice into an <see cref="Expression"/>
/// tree — used for CHECK / DEFAULT / index expressions. It models the common SQL expression subset
/// (literals, dotted identifiers, function calls, unary/binary operators, IS [NOT] NULL, :: casts,
/// parentheses). Anything it can't parse cleanly degrades to a <see cref="RawExpr"/> so callers
/// always get a node and never throw — the raw text is preserved for faithful re-emission.
/// </summary>
public static class ExpressionParser
{
    public static Expression Parse(IReadOnlyList<Token> tokens)
    {
        if (tokens.Count == 0) return new RawExpr { Text = "" };
        try
        {
            var p = new Cursor(tokens);
            var expr = p.ParseExpr(0);
            // If we didn't consume everything, the grammar exceeded our subset — fall back.
            return p.AtEnd ? expr : new RawExpr { Text = Token.Render(tokens) };
        }
        catch
        {
            return new RawExpr { Text = Token.Render(tokens) };
        }
    }

    private sealed class Cursor
    {
        private readonly IReadOnlyList<Token> _t;
        private int _i;
        public Cursor(IReadOnlyList<Token> t) => _t = t;

        public bool AtEnd => _i >= _t.Count;
        private Token? Cur => _i < _t.Count ? _t[_i] : null;
        private Token Next() => _t[_i++];

        // Left binding power of infix operators (higher = tighter).
        private static int Lbp(Token t)
        {
            if (t.Kind == TokenKind.Word)
            {
                return t.Value.ToUpperInvariant() switch
                {
                    "OR" => 1, "AND" => 2,
                    "NOT" or "LIKE" or "ILIKE" or "IN" or "IS" or "SIMILAR" or "BETWEEN" => 7,
                    _ => 0,
                };
            }
            if (t.Kind != TokenKind.Symbol) return 0;
            return t.Value switch
            {
                "=" or "<" or ">" => 7,
                "+" or "-" => 10,
                "*" or "/" or "%" => 20,
                ":" => 30, // leading char of '::' cast
                _ => 0,
            };
        }

        public Expression ParseExpr(int rbp)
        {
            var left = Nud();
            while (!AtEnd && Lbp(Cur!) > rbp)
                left = Led(left);
            return left;
        }

        // Null denotation — prefix / primary.
        private Expression Nud()
        {
            var t = Next();
            switch (t.Kind)
            {
                case TokenKind.Number:
                    return new LiteralExpr { Value = t.Value, Kind = LiteralKind.Number };
                case TokenKind.String:
                    return new LiteralExpr { Value = t.Value, Kind = LiteralKind.String };
                case TokenKind.Word:
                {
                    var up = t.Value.ToUpperInvariant();
                    if (up == "NULL") return new LiteralExpr { Value = "null", Kind = LiteralKind.Null };
                    if (up is "TRUE" or "FALSE") return new LiteralExpr { Value = up.ToLowerInvariant(), Kind = LiteralKind.Boolean };
                    if (up == "NOT") return new UnaryExpr { Op = "NOT", Operand = ParseExpr(6) };
                    if (up == "CASE") return ParseCase();
                    return ParseIdentifierOrCall(t.Value);
                }
                case TokenKind.QuotedIdent:
                    return ParseIdentifierOrCall(t.Value);
                case TokenKind.Symbol when t.Value is "-" or "+":
                    return new UnaryExpr { Op = t.Value, Operand = ParseExpr(25) };
                case TokenKind.Symbol when t.Value == "(":
                {
                    // A parenthesised subquery (SELECT … / WITH …) vs a grouped scalar expression.
                    if (Cur is { Kind: TokenKind.Word } w2 && (w2.IsWord("SELECT") || w2.IsWord("WITH")))
                    {
                        var sub = CaptureBalancedFromOpen();
                        return new SubqueryExpr { Query = QueryParser.Parse(sub) };
                    }
                    var inner = ParseExpr(0);
                    Expect(")");
                    return new ParenExpr { Inner = inner };
                }
            }
            throw new ParseException($"Unexpected token '{t.Value}' in expression.");
        }

        private Expression ParseCase()
        {
            // 'CASE' already consumed. Optional simple-form operand, then WHEN…THEN…, optional ELSE, END.
            Expression? operand = null;
            if (!(Cur is { } w && w.IsWord("WHEN")))
                operand = ParseExpr(0);

            var branches = new List<CaseBranch>();
            while (Cur is { } wt && wt.IsWord("WHEN"))
            {
                _i++; // WHEN
                var cond = ParseExpr(0);
                if (!(Cur is { } th && th.IsWord("THEN"))) throw new ParseException("Expected THEN in CASE.");
                _i++; // THEN
                var result = ParseExpr(0);
                branches.Add(new CaseBranch { When = cond, Then = result });
            }

            Expression? elseExpr = null;
            if (Cur is { } e && e.IsWord("ELSE")) { _i++; elseExpr = ParseExpr(0); }
            if (!(Cur is { } end && end.IsWord("END"))) throw new ParseException("Expected END in CASE.");
            _i++; // END
            return new CaseExpr { Operand = operand, Branches = branches, Else = elseExpr };
        }

        // The cursor sits just after a consumed '('; collect through the matching ')'.
        private List<Token> CaptureBalancedFromOpen()
        {
            var toks = new List<Token>(); var depth = 1;
            while (!AtEnd)
            {
                var t = _t[_i++];
                if (t.IsSymbol('(')) depth++;
                else if (t.IsSymbol(')')) { depth--; if (depth == 0) break; }
                toks.Add(t);
            }
            return toks;
        }

        private Expression ParseIdentifierOrCall(string first)
        {
            var parts = new List<string> { first };
            while (Cur is { } c && c.IsSymbol('.'))
            {
                _i++; // '.'
                parts.Add(Next().Value);
            }
            if (Cur is { } open && open.IsSymbol('('))
            {
                _i++; // '('
                var args = new List<Expression>();
                if (!(Cur is { } x && x.IsSymbol(')')))
                {
                    args.Add(ParseExpr(0));
                    while (Cur is { } comma && comma.IsSymbol(',')) { _i++; args.Add(ParseExpr(0)); }
                }
                Expect(")");
                return new FunctionCallExpr { Name = string.Join(".", parts), Arguments = args };
            }
            return new IdentifierExpr { Parts = parts };
        }

        // Left denotation — infix / postfix.
        private Expression Led(Expression left)
        {
            var t = Cur!;
            // '::' cast
            if (t.IsSymbol(':') && _i + 1 < _t.Count && _t[_i + 1].IsSymbol(':'))
            {
                _i += 2;
                var typeName = Next().Value; // simple type name
                return new CastExpr { Operand = left, TypeName = typeName };
            }
            if (t.Kind == TokenKind.Word && t.Value.ToUpperInvariant() == "IS")
            {
                _i++;
                var not = Cur is { } n && n.IsWord("NOT");
                if (not) _i++;
                if (Cur is { } nu && nu.IsWord("NULL")) { _i++; return new UnaryExpr { Op = not ? "IS NOT NULL" : "IS NULL", Operand = left }; }
                return new UnaryExpr { Op = "IS", Operand = left };
            }
            if (t.Kind == TokenKind.Word && t.Value.ToUpperInvariant() == "IN")
            {
                _i++;
                return ParseIn(left, negated: false);
            }
            if (t.Kind == TokenKind.Word && t.Value.ToUpperInvariant() == "NOT")
            {
                _i++;
                if (Cur is { } n2 && n2.IsWord("IN")) { _i++; return ParseIn(left, negated: true); }
                if (Cur is { } n3 && (n3.IsWord("LIKE") || n3.IsWord("ILIKE")))
                    return new BinaryExpr { Op = "NOT " + Next().Value.ToUpperInvariant(), Left = left, Right = ParseExpr(7) };
                return new BinaryExpr { Op = "NOT", Left = left, Right = ParseExpr(7) };
            }
            var op = Next().Value;
            var bp = Lbp(t);
            var right = ParseExpr(bp);
            return new BinaryExpr { Op = op, Left = left, Right = right };
        }

        private Expression ParseIn(Expression left, bool negated)
        {
            Expect("(");
            if (Cur is { } w && (w.IsWord("SELECT") || w.IsWord("WITH")))
            {
                var sub = CaptureBalancedFromOpen();
                return new InExpr { Operand = left, Negated = negated, Subquery = new SubqueryExpr { Query = QueryParser.Parse(sub) } };
            }
            var items = new List<Expression>();
            if (!(Cur is { } close && close.IsSymbol(')')))
            {
                items.Add(ParseExpr(0));
                while (Cur is { } comma && comma.IsSymbol(',')) { _i++; items.Add(ParseExpr(0)); }
            }
            Expect(")");
            return new InExpr { Operand = left, Negated = negated, Items = items };
        }

        private void Expect(string symbol)
        {
            if (Cur is { } c && c.Kind == TokenKind.Symbol && c.Value == symbol) { _i++; return; }
            throw new ParseException($"Expected '{symbol}'.");
        }
    }
}
