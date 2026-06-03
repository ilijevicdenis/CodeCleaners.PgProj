using System;
using System.Collections.Generic;
using PgProj.Core.Ast;

namespace PgProj.Core.Parsing;

/// <summary>
/// Parses a function body into a control-flow tree: BEGIN/EXCEPTION/END blocks, IF/ELSIF/ELSE,
/// LOOP/WHILE/FOR, assignments, RETURN variants, and embedded SQL (classified via
/// <see cref="FunctionBodyClassifier"/>). Composite statements expose their inner statements as
/// children, so a rule walking the tree sees DML nested inside an IF or a loop. SQL-language bodies
/// (no BEGIN) parse as a flat statement list. Never throws — falls back to flat classification.
/// </summary>
public static class PlpgsqlParser
{
    public static IReadOnlyList<BodyStatement> Parse(string body)
    {
        var tokens = Tokenizer.Tokenize(body ?? string.Empty);
        try
        {
            var c = new Cursor(tokens);
            var list = c.ParseStatements();
            return list.Count > 0 || tokens.Count == 0 ? list : FunctionBodyClassifier.FlatClassify(tokens);
        }
        catch
        {
            return FunctionBodyClassifier.FlatClassify(tokens);
        }
    }

    private sealed class Cursor
    {
        private readonly IReadOnlyList<Token> _t;
        private int _i;
        public Cursor(IReadOnlyList<Token> t) => _t = t;

        private bool End => _i >= _t.Count;
        private Token? Cur => _i < _t.Count ? _t[_i] : null;
        private Token? Peek => _i + 1 < _t.Count ? _t[_i + 1] : null;
        private Token Next() => _t[_i++];
        private bool MatchWord(string kw) { if (Cur is { } t && t.IsWord(kw)) { _i++; return true; } return false; }
        private bool MatchSymbol(char c) { if (Cur is { } t && t.IsSymbol(c)) { _i++; return true; } return false; }
        private bool IsSymbol(char c) => Cur is { } t && t.IsSymbol(c);

        private static bool IsStopWord(Token? t) =>
            t is { Kind: TokenKind.Word } && t.Value.ToUpperInvariant() is "END" or "ELSIF" or "ELSE" or "EXCEPTION" or "WHEN";

        public List<BodyStatement> ParseStatements()
        {
            var list = new List<BodyStatement>();
            var guard = 0;
            while (!End && !IsStopWord(Cur))
            {
                var before = _i;
                var s = ParseStatement();
                if (s is not null) list.Add(s);
                if (_i == before) { _i++; } // never stall
                if (++guard > 100000) break;
            }
            return list;
        }

        private BodyStatement? ParseStatement()
        {
            SkipLabel();
            MatchSymbol(';'); // empty statement
            if (End || IsStopWord(Cur)) return null;

            if (Cur is { Kind: TokenKind.Word } w)
            {
                switch (w.Value.ToUpperInvariant())
                {
                    case "DECLARE": return ParseDeclareBlock();
                    case "BEGIN": return ParseBlock(null);
                    case "IF": return ParseIf();
                    case "WHILE": return ParseWhile();
                    case "LOOP": return ParseLoop();
                    case "FOR":
                    case "FOREACH": return ParseFor(w.Value.ToUpperInvariant());
                    case "CASE": return ParseCase();
                    case "RETURN": return ParseReturn();
                }
            }
            return ParseSimple();
        }

        private BodyStatement ParseDeclareBlock()
        {
            MatchWord("DECLARE");
            var decls = Token.Render(CaptureUntilWord("BEGIN"));
            return ParseBlock(string.IsNullOrWhiteSpace(decls) ? null : decls);
        }

        private BodyStatement ParseBlock(string? declarations)
        {
            MatchWord("BEGIN");
            var body = ParseStatements();
            var handlers = new List<ExceptionHandler>();
            if (MatchWord("EXCEPTION"))
                while (Cur is { } w && w.IsWord("WHEN"))
                    handlers.Add(ParseHandler());
            MatchWord("END");
            if (Cur is { Kind: TokenKind.Word }) Next(); // optional block label
            MatchSymbol(';');
            return new BlockStatement { DeclarationsText = declarations, Body = body, Handlers = handlers };
        }

        private ExceptionHandler ParseHandler()
        {
            MatchWord("WHEN");
            var cond = Token.Render(CaptureUntilWord("THEN"));
            MatchWord("THEN");
            return new ExceptionHandler { ConditionText = cond, Body = ParseStatements() };
        }

        private BodyStatement ParseIf()
        {
            MatchWord("IF");
            var cond = ExpressionParser.Parse(CaptureUntilWord("THEN"));
            MatchWord("THEN");
            var then = ParseStatements();
            var elsifs = new List<ElsifBranch>();
            while (MatchWord("ELSIF") || MatchWord("ELSEIF"))
            {
                var c = ExpressionParser.Parse(CaptureUntilWord("THEN"));
                MatchWord("THEN");
                elsifs.Add(new ElsifBranch { Condition = c, Body = ParseStatements() });
            }
            var elseBody = new List<BodyStatement>();
            if (MatchWord("ELSE")) elseBody = ParseStatements();
            MatchWord("END"); MatchWord("IF"); MatchSymbol(';');
            return new IfStatement { Condition = cond, Then = then, Elsifs = elsifs, Else = elseBody };
        }

        private BodyStatement ParseWhile()
        {
            MatchWord("WHILE");
            var cond = ExpressionParser.Parse(CaptureUntilWord("LOOP"));
            MatchWord("LOOP");
            var body = ParseStatements();
            MatchWord("END"); MatchWord("LOOP"); SkipLabelTail();
            return new LoopStatement { Kind = "WHILE", Condition = cond, Body = body };
        }

        private BodyStatement ParseLoop()
        {
            MatchWord("LOOP");
            var body = ParseStatements();
            MatchWord("END"); MatchWord("LOOP"); SkipLabelTail();
            return new LoopStatement { Kind = "LOOP", Body = body };
        }

        private BodyStatement ParseFor(string kind)
        {
            MatchWord(kind);
            var header = Token.Render(CaptureUntilWord("LOOP"));
            MatchWord("LOOP");
            var body = ParseStatements();
            MatchWord("END"); MatchWord("LOOP"); SkipLabelTail();
            return new LoopStatement { Kind = kind, HeaderText = header, Body = body };
        }

        // CASE statement: union the branch bodies into a block so nested DML is discoverable.
        private BodyStatement ParseCase()
        {
            MatchWord("CASE");
            CaptureUntilWord("WHEN"); // optional operand, ignored structurally
            var body = new List<BodyStatement>();
            while (MatchWord("WHEN"))
            {
                CaptureUntilWord("THEN");
                MatchWord("THEN");
                body.AddRange(ParseStatements());
            }
            if (MatchWord("ELSE")) body.AddRange(ParseStatements());
            MatchWord("END"); MatchWord("CASE"); MatchSymbol(';');
            return new BlockStatement { Body = body };
        }

        private BodyStatement ParseReturn()
        {
            MatchWord("RETURN");
            if (MatchWord("QUERY"))
            {
                var toks = CaptureToSemicolon();
                return new ReturnStatement { Kind = "RETURN QUERY", Query = QueryParser.Parse(toks) };
            }
            if (MatchWord("NEXT"))
                return new ReturnStatement { Kind = "RETURN NEXT", Value = ExpressionParser.Parse(CaptureToSemicolon()) };
            var rest = CaptureToSemicolon();
            return new ReturnStatement { Kind = "RETURN", Value = rest.Count > 0 ? ExpressionParser.Parse(rest) : null };
        }

        private BodyStatement? ParseSimple()
        {
            var toks = CaptureToSemicolon();
            if (toks.Count == 0) return null;

            var assignAt = TopLevelAssignIndex(toks);
            if (assignAt > 0)
            {
                var target = Token.Render(toks.GetRange(0, assignAt));
                var valueToks = toks.GetRange(assignAt + 2, toks.Count - assignAt - 2);
                return new AssignmentStatement { RawText = Token.Render(toks), Target = target, Value = valueToks.Count > 0 ? ExpressionParser.Parse(valueToks) : null };
            }
            return FunctionBodyClassifier.ClassifySimple(toks);
        }

        // ---- helpers ----

        private void SkipLabel()
        {
            if (IsSymbol('<') && Peek is { } p && p.IsSymbol('<'))
            {
                Next(); Next();
                while (!End) { var a = Next(); if (a.IsSymbol('>') && IsSymbol('>')) { Next(); break; } }
            }
        }

        private void SkipLabelTail()
        {
            if (Cur is { Kind: TokenKind.Word }) Next(); // optional loop label after END LOOP
            MatchSymbol(';');
        }

        private List<Token> CaptureUntilWord(string stop)
        {
            var toks = new List<Token>(); var depth = 0;
            while (!End)
            {
                var t = Cur!;
                if (t.IsSymbol('(')) depth++;
                else if (t.IsSymbol(')')) depth = Math.Max(0, depth - 1);
                if (depth == 0 && t.IsWord(stop)) break;
                toks.Add(Next());
            }
            return toks;
        }

        private List<Token> CaptureToSemicolon()
        {
            var toks = new List<Token>(); var depth = 0;
            while (!End)
            {
                var t = Cur!;
                if (t.IsSymbol('(')) depth++;
                else if (t.IsSymbol(')')) depth = Math.Max(0, depth - 1);
                if (depth == 0 && t.IsSymbol(';')) { _i++; break; }
                if (depth == 0 && IsStopWord(t)) break;
                toks.Add(Next());
            }
            return toks;
        }

        // Index of a top-level ':=' (returns the position of ':'), or -1.
        private static int TopLevelAssignIndex(List<Token> toks)
        {
            var depth = 0;
            for (var i = 0; i < toks.Count - 1; i++)
            {
                if (toks[i].IsSymbol('(')) depth++;
                else if (toks[i].IsSymbol(')')) depth = Math.Max(0, depth - 1);
                else if (depth == 0 && toks[i].IsSymbol(':') && toks[i + 1].IsSymbol('=')) return i;
            }
            return -1;
        }
    }
}
