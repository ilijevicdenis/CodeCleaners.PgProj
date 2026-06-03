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
            else if (c.MatchWords("OWNED", "BY")) { if (!c.MatchWord("NONE")) { c.ExpectIdentifier(); while (c.MatchSymbol('.')) c.ExpectIdentifier(); } }   // schema.table.column
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

        // key column list — reject an empty list or a trailing/leading comma
        c.ExpectSymbol('(');
        if (c.AtSymbol(')')) throw new ParseException("index column list cannot be empty", c.Here);
        do
        {
            if (c.AtSymbol(')')) throw new ParseException("trailing comma in index column list", c.Here);
            node.Columns.Add(ParseIndexItem(c));
        } while (c.MatchSymbol(','));
        c.ExpectSymbol(')');

        if (c.MatchWord("INCLUDE"))
        {
            c.ExpectSymbol('(');
            if (c.AtSymbol(')')) throw new ParseException("INCLUDE column list cannot be empty", c.Here);
            do { if (c.AtSymbol(')')) throw new ParseException("trailing comma in INCLUDE list", c.Here); c.ExpectIdentifier(); } while (c.MatchSymbol(','));
            c.ExpectSymbol(')');
        }
        if (c.MatchWord("NULLS")) { c.MatchWord("NOT"); c.ExpectWord("DISTINCT"); }   // INCLUDE precedes NULLS [NOT] DISTINCT
        if (c.MatchWord("WITH")) ParseRelOptions(c);
        if (c.MatchWord("TABLESPACE")) c.ExpectIdentifier();
        if (c.MatchWord("WHERE")) { int m = c.Mark(); ParseExpression(c); node.Where = Token.Render(c.Range(m, c.Mark())); }   // a real predicate is required

        // Only btree supports UNIQUE among the built-in access methods.
        if (unique && node.Method is { } am && NonUniqueBuiltinAm.Contains(am))
            throw new ParseException($"access method \"{am}\" does not support unique indexes", c.Here);
        return node;
    }

    private static readonly System.Collections.Generic.HashSet<string> NonUniqueBuiltinAm =
        new(System.StringComparer.OrdinalIgnoreCase) { "hash", "gin", "gist", "spgist", "brin" };

    // One index element: an expression (or parenthesized expression), optional opclass [(params)],
    // ASC|DESC (not both), and NULLS FIRST|LAST (at most once).
    private string ParseIndexItem(TokenCursor c)
    {
        int m = c.Mark();
        ParseExpression(c);                                  // COLLATE is absorbed here as a postfix
        if (c.Current is { Kind: TokenKind.Word } w && !w.IsWord("ASC") && !w.IsWord("DESC") && !w.IsWord("NULLS"))
        { c.Advance(); while (c.MatchSymbol('.')) c.ExpectIdentifier(); if (c.AtSymbol('(')) CaptureBalancedParens(c); }   // opclass [schema-qualified] [(params)]
        bool asc = c.MatchWord("ASC"), desc = !asc && c.MatchWord("DESC");
        if (((asc || desc) && c.AtAnyWord("ASC", "DESC"))) throw new ParseException("ASC and DESC are mutually exclusive", c.Here);
        if (c.MatchWord("NULLS"))
        {
            if (!c.MatchWord("FIRST")) c.ExpectWord("LAST");
            if (c.AtWord("NULLS")) throw new ParseException("duplicate NULLS ordering", c.Here);
        }
        return Token.Render(c.Range(m, c.Mark()));
    }

    // WITH ( key [= value] [, …] ) reloptions — reject an empty list and out-of-range fillfactor.
    private void ParseRelOptions(TokenCursor c)
    {
        c.ExpectSymbol('(');
        if (c.AtSymbol(')')) throw new ParseException("storage parameter list cannot be empty", c.Here);
        do
        {
            var key = c.ExpectIdentifier();
            string? val = null;
            if (c.MatchOperator("=")) { if (c.AtEnd || c.AtSymbol(',') || c.AtSymbol(')')) throw new ParseException("missing storage parameter value", c.Here); val = c.Advance().Value; }
            if (key.Equals("fillfactor", System.StringComparison.OrdinalIgnoreCase) && int.TryParse(val, out var ff) && (ff < 10 || ff > 100))
                throw new ParseException($"fillfactor must be between 10 and 100, got {ff}", c.Here);
        } while (c.MatchSymbol(','));
        c.ExpectSymbol(')');
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
