using System.Collections.Generic;
using PgProj.Core.Parsing;

namespace PgProj.Core.Syntax;

// Finely-modelled CREATE VIEW / SEQUENCE / INDEX / FUNCTION — enough structure for the model the
// comparer/deploy consume (view body, sequence options, index columns, function arg types). Lenient
// on option tails so it never rejects valid PostgreSQL.
public sealed partial class PgParser
{
    private SqlStatement ParseCreateView(TokenCursor c, bool materialized)
    {
        c.MatchWords("IF", "NOT", "EXISTS");
        var (s, n) = ParseQualifiedName(c);
        var node = new CreateViewStatement { Schema = s, Name = n, Materialized = materialized };

        // skip pre-AS clauses: column list, WITH (options), USING method, TABLESPACE
        while (!c.AtEnd && !c.AtWord("AS")) { if (c.AtSymbol('(')) CaptureBalancedParens(c); else c.Advance(); }
        c.ExpectWord("AS");

        var body = new List<Token>();
        while (!c.AtEnd)
        {
            if (c.AtWord("WITH") && c.Peek() is { } p && (p.IsWord("CHECK") || p.IsWord("CASCADED") || p.IsWord("LOCAL") || p.IsWord("DATA") || p.IsWord("NO")))
                break;
            body.Add(c.Advance());
        }
        if (body.Count == 0) throw new ParseException("expected a query after AS", c.Here);
        node.BodyText = Token.Render(body);
        ConsumeRest(c);                          // WITH CHECK OPTION / WITH [NO] DATA
        return node;
    }

    private SqlStatement ParseCreateSequence(TokenCursor c)
    {
        c.MatchWords("IF", "NOT", "EXISTS");
        var (s, n) = ParseQualifiedName(c);
        var node = new CreateSequenceStatement { Schema = s, Name = n };
        while (!c.AtEnd)
        {
            if (c.MatchWord("AS")) { node.DataType = ParseCastType(c); }
            else if (c.MatchWord("INCREMENT")) { c.MatchWord("BY"); node.Increment = ParseSignedLong(c); }
            else if (c.MatchWords("NO", "MINVALUE")) { }
            else if (c.MatchWord("MINVALUE")) node.MinValue = ParseSignedLong(c);
            else if (c.MatchWords("NO", "MAXVALUE")) { }
            else if (c.MatchWord("MAXVALUE")) node.MaxValue = ParseSignedLong(c);
            else if (c.MatchWord("START")) { c.MatchWord("WITH"); node.Start = ParseSignedLong(c); }
            else if (c.MatchWord("CACHE")) node.Cache = ParseSignedLong(c);
            else if (c.MatchWords("NO", "CYCLE")) node.Cycle = false;
            else if (c.MatchWord("CYCLE")) node.Cycle = true;
            else if (c.MatchWords("OWNED", "BY")) { if (!c.MatchWord("NONE")) { ParseQualifiedName(c); } }
            else break;
        }
        return node;
    }

    private SqlStatement ParseCreateIndex(TokenCursor c)
    {
        bool unique = c.MatchWord("UNIQUE");
        c.ExpectWord("INDEX");
        c.MatchWord("CONCURRENTLY");
        c.MatchWords("IF", "NOT", "EXISTS");
        var node = new CreateIndexStatement { Unique = unique };
        if (!c.AtWord("ON")) node.Name = c.ExpectIdentifier();
        c.ExpectWord("ON");
        c.MatchWord("ONLY");
        var (ts, table) = ParseQualifiedName(c);
        node.Schema = ts; node.Table = table;
        if (c.MatchWord("USING")) node.Method = c.ExpectIdentifier();

        c.ExpectSymbol('(');
        if (!c.AtSymbol(')'))
            do { node.Columns.Add(CaptureIndexItem(c)); } while (c.MatchSymbol(','));
        c.ExpectSymbol(')');

        if (c.MatchWord("INCLUDE") && c.AtSymbol('(')) CaptureBalancedParens(c);
        if (c.MatchWord("WITH") && c.AtSymbol('(')) CaptureBalancedParens(c);
        if (c.MatchWord("TABLESPACE")) c.ExpectIdentifier();
        if (c.MatchWord("WHERE")) node.Where = Token.Render(CaptureToEndTokens(c));
        return node;
    }

    private SqlStatement ParseCreateFunction(TokenCursor c)
    {
        bool isProc = c.MatchWord("PROCEDURE");
        if (!isProc) c.ExpectWord("FUNCTION");
        var (s, n) = ParseQualifiedName(c);
        var node = new CreateFunctionStatement { Schema = s, Name = n, IsProcedure = isProc };
        if (c.AtSymbol('(')) node.ArgTypes = ExtractArgTypes(CaptureBalancedParens(c));
        ConsumeRest(c);                          // RETURNS / LANGUAGE / options / AS body — kept as SourceText
        return node;
    }

    // ---- helpers ------------------------------------------------------------

    private static long? ParseSignedLong(TokenCursor c)
    {
        bool neg = c.MatchOperator("-");
        if (c.Current is { Kind: TokenKind.Number } t && long.TryParse(t.Value, out var v)) { c.Advance(); return neg ? -v : v; }
        return null;
    }

    /// <summary>One index element up to a top-level comma or ')': its leading column/expression text.</summary>
    private static string CaptureIndexItem(TokenCursor c)
    {
        var toks = new List<Token>(); int depth = 0;
        while (!c.AtEnd)
        {
            var t = c.Current!;
            if (depth == 0 && (t.IsSymbol(',') || t.IsSymbol(')'))) break;
            if (t.IsSymbol('(') || t.IsSymbol('[')) depth++;
            else if (t.IsSymbol(')') || t.IsSymbol(']')) depth--;
            toks.Add(c.Advance());
        }
        return Token.Render(toks);
    }

    private static List<Token> CaptureToEndTokens(TokenCursor c)
    {
        var toks = new List<Token>();
        while (!c.AtEnd) toks.Add(c.Advance());
        return toks;
    }

    /// <summary>Best-effort argument-type list for a function signature (modes/names/defaults stripped).</summary>
    private static string ExtractArgTypes(List<Token> argInner)
    {
        var args = new List<string>();
        foreach (var part in SplitTopLevel(argInner))
        {
            var toks = new List<Token>(part);
            // strip a leading mode keyword
            if (toks.Count > 0 && toks[0].Kind == TokenKind.Word &&
                (toks[0].IsWord("IN") || toks[0].IsWord("OUT") || toks[0].IsWord("INOUT") || toks[0].IsWord("VARIADIC")))
                toks.RemoveAt(0);
            // drop everything from DEFAULT / '=' onwards
            int cut = toks.FindIndex(t => t.IsWord("DEFAULT") || t.IsSymbol('='));
            if (cut >= 0) toks.RemoveRange(cut, toks.Count - cut);
            // if it starts "name type…" (two leading words, second not a type-modifier), drop the name
            if (toks.Count >= 2 && toks[0].Kind == TokenKind.Word && toks[1].Kind == TokenKind.Word
                && !IsTypeContinuation(toks[1].Value) && !IsBuiltinTypeWord(toks[0].Value))
                toks.RemoveAt(0);
            var s = Token.Render(toks).Trim();
            if (s.Length > 0) args.Add(s);
        }
        return string.Join(", ", args);
    }

    private static IEnumerable<List<Token>> SplitTopLevel(List<Token> tokens)
    {
        var cur = new List<Token>(); int depth = 0;
        foreach (var t in tokens)
        {
            if (t.IsSymbol('(') || t.IsSymbol('[')) depth++;
            else if (t.IsSymbol(')') || t.IsSymbol(']')) depth--;
            if (depth == 0 && t.IsSymbol(',')) { if (cur.Count > 0) yield return cur; cur = new List<Token>(); continue; }
            cur.Add(t);
        }
        if (cur.Count > 0) yield return cur;
    }

    private static bool IsBuiltinTypeWord(string w) => w.ToLowerInvariant() is
        "int" or "integer" or "bigint" or "smallint" or "numeric" or "decimal" or "real" or "text" or
        "varchar" or "char" or "character" or "boolean" or "bool" or "date" or "timestamp" or "time" or
        "interval" or "json" or "jsonb" or "uuid" or "bytea" or "double" or "money" or "serial" or "bigserial";
}
