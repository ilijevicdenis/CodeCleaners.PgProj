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
        while (!c.AtEnd && !c.AtWord("AS")) { if (c.AtSymbol('(')) c.SkipBalancedParens(); else c.Advance(); }
        c.ExpectWord("AS");

        int bm = c.Mark();
        while (!c.AtEnd)
        {
            if (c.AtWord("WITH") && c.Peek() is { } p && (p.IsWord("CHECK") || p.IsWord("CASCADED") || p.IsWord("LOCAL") || p.IsWord("DATA") || p.IsWord("NO")))
                break;
            c.Advance();
        }
        if (c.Mark() == bm) throw new ParseException("expected a query after AS", c.Here);
        node.BodyText = c.RenderRange(bm, c.Mark());
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
        if (c.MatchWord("WHERE")) { int m = c.Mark(); ParseExpression(c); node.Where = c.RenderRange(m, c.Mark()); }   // a real predicate is required

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
        { c.Advance(); while (c.MatchSymbol('.')) c.ExpectIdentifier(); if (c.AtSymbol('(')) c.SkipBalancedParens(); }   // opclass [schema-qualified] [(params)]
        bool asc = c.MatchWord("ASC"), desc = !asc && c.MatchWord("DESC");
        if (((asc || desc) && c.AtAnyWord("ASC", "DESC"))) throw new ParseException("ASC and DESC are mutually exclusive", c.Here);
        if (c.MatchWord("NULLS"))
        {
            if (!c.MatchWord("FIRST")) c.ExpectWord("LAST");
            if (c.AtWord("NULLS")) throw new ParseException("duplicate NULLS ordering", c.Here);
        }
        return c.RenderRange(m, c.Mark());
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

        if (!c.AtSymbol('(')) throw new ParseException("expected '(' for the function parameter list", c.Here);
        var argInner = CaptureBalancedParens(c);
        node.ArgTypes = ExtractArgTypes(argInner);                // unchanged — model identity depends on this
        bool hasOut = ValidateFunctionArgs(argInner);            // overlay validation, no effect on the model
        node.HasOutParams = hasOut;

        // RETURNS … and the option/body tail — captured as before, validated via a sub-cursor.
        int m = c.Mark();
        ConsumeRest(c);
        ValidateFunctionTail(c.Segment(m, c.Mark()), hasOut, node);
        return node;
    }

    // Validate the captured argument tokens: one mode per arg, VARIADIC must be an array, defaults must be
    // trailing, no empty/trailing-comma entries. Returns whether any OUT/INOUT parameter is present.
    private bool ValidateFunctionArgs(IReadOnlyList<Token> argInner)
    {
        if (argInner.Count == 0) return false;
        var a = new TokenCursor(argInner);
        bool hasOut = false, seenDefault = false;
        do
        {
            if (a.AtEnd) throw new ParseException("trailing comma in function argument list", a.Here);
            string? mode = null;
            if (a.AtAnyWord("IN", "OUT", "INOUT", "VARIADIC"))
            {
                mode = a.Advance().Value.ToUpperInvariant();
                if (mode == "IN" && a.MatchWord("OUT")) mode = "INOUT";       // "IN OUT" is the two-word spelling of INOUT
                else if (a.AtAnyWord("IN", "OUT", "INOUT", "VARIADIC")) throw new ParseException("only one parameter mode is allowed per argument", a.Here);
            }
            if (mode is "OUT" or "INOUT") hasOut = true;

            int mk = a.Mark();                                   // name is optional: "[name] type"
            a.ExpectIdentifier();
            bool nameThenType = !(a.AtEnd || a.AtSymbol(',') || a.AtWord("DEFAULT") || a.AtOperator("="));
            a.Reset(mk);
            if (nameThenType) a.ExpectIdentifier();
            var type = CaptureArgTypeText(a);                    // lenient — handles dotted names, %TYPE, array/precision suffixes
            if (mode == "VARIADIC" && !IsArrayOrAnyType(type)) throw new ParseException("a VARIADIC parameter must be an array type", a.Here);

            bool thisDefault = false;
            if (a.MatchWord("DEFAULT") || a.MatchOperator("=")) { thisDefault = true; while (!a.AtEnd && !a.AtSymbol(',')) { if (a.AtSymbol('(')) a.SkipBalancedParens(); else a.Advance(); } }
            if (mode is null or "IN" or "INOUT" or "VARIADIC")
            {
                if (seenDefault && !thisDefault) throw new ParseException("input parameters after one with a default value must also have defaults", a.Here);
                if (thisDefault) seenDefault = true;
            }
        } while (a.MatchSymbol(','));
        return hasOut;
    }

    // Capture an argument's type text up to a top-level comma / DEFAULT / '=' — lenient enough for
    // dotted names, anchored types (col%TYPE), and precision/array suffixes.
    private static string CaptureArgTypeText(TokenCursor a)
    {
        int m = a.Mark(), depth = 0;
        while (!a.AtEnd)
        {
            var t = a.Current!.Value;
            if (depth == 0 && (t.IsSymbol(',') || t.IsWord("DEFAULT") || t.IsSymbol('='))) break;
            if (t.IsSymbol('(') || t.IsSymbol('[')) depth++;
            else if (t.IsSymbol(')') || t.IsSymbol(']')) depth--;
            a.Advance();
        }
        return a.RenderRange(m, a.Mark());
    }

    private static bool IsArrayOrAnyType(string type)
        => type.Contains('[') || type.EndsWith("ARRAY", System.StringComparison.OrdinalIgnoreCase)
           || type.Trim().Equals("any", System.StringComparison.OrdinalIgnoreCase) || type.Contains("\"any\"");

    private static readonly System.Collections.Generic.HashSet<string> ParallelValues =
        new(System.StringComparer.OrdinalIgnoreCase) { "SAFE", "UNSAFE", "RESTRICTED" };

    // Validate RETURNS + the option/body tail: at most one of each option category, valid PARALLEL value,
    // COST > 0, ROWS only for set-returning functions, OUT params incompatible with RETURNS TABLE, and a
    // body must be present. Unknown trailing tokens fall back to lenient acceptance (no false positives).
    private void ValidateFunctionTail(IReadOnlyList<Token> tail, bool hasOut, CreateFunctionStatement node)
    {
        var o = new TokenCursor(tail);
        bool returnsSet = false, returnsTable = false;
        if (o.MatchWord("RETURNS"))
        {
            if (o.MatchWord("TABLE")) { returnsTable = true; returnsSet = true; if (!o.AtSymbol('(')) throw new ParseException("expected '(' after RETURNS TABLE", o.Here); o.SkipBalancedParens(); }
            else { if (o.MatchWord("SETOF")) returnsSet = true; var rt = ParseCastType(o); if (rt.Trim().Equals("void", System.StringComparison.OrdinalIgnoreCase)) node.ReturnsVoid = true; }
        }
        node.ReturnsSetof = returnsSet;
        if (hasOut && returnsTable) throw new ParseException("cannot use OUT parameters together with RETURNS TABLE", o.Here);

        var seen = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        void Once(string cat) { if (!seen.Add(cat)) throw new ParseException($"conflicting or redundant {cat} option", o.Here); }
        bool hasBody = false, hasRows = false, clean = true;
        while (!o.AtEnd)
        {
            if (o.MatchWord("LANGUAGE")) { node.Language = (o.Current is { Kind: TokenKind.String } ls ? o.Advance().Value : o.ExpectIdentifier()).ToLowerInvariant(); continue; }
            if (o.AtAnyWord("IMMUTABLE", "STABLE", "VOLATILE")) { o.Advance(); Once("volatility"); continue; }
            if (o.MatchWords("NOT", "LEAKPROOF") || o.MatchWord("LEAKPROOF")) { Once("leakproof"); continue; }
            if (o.MatchWord("STRICT") || o.MatchWords("CALLED", "ON", "NULL", "INPUT") || o.MatchWords("RETURNS", "NULL", "ON", "NULL", "INPUT")) { Once("null-handling"); continue; }
            if (o.MatchWords("EXTERNAL", "SECURITY") || o.MatchWord("SECURITY")) { if (!o.MatchWord("DEFINER")) o.ExpectWord("INVOKER"); Once("security"); continue; }
            if (o.MatchWord("PARALLEL")) { var v = o.ExpectIdentifier(); if (!ParallelValues.Contains(v)) throw new ParseException($"invalid PARALLEL value \"{v}\"", o.Here); Once("parallel"); continue; }
            if (o.MatchWord("COST")) { var v = ParseOptNumber(o); if (v is <= 0) throw new ParseException("COST must be positive", o.Here); Once("cost"); continue; }
            if (o.MatchWord("ROWS")) { ParseOptNumber(o); hasRows = true; Once("rows"); continue; }
            if (o.MatchWord("WINDOW")) { Once("window"); continue; }
            if (o.MatchWord("SUPPORT")) { ParseQualifiedName(o); continue; }
            if (o.MatchWord("TRANSFORM")) { do { o.ExpectWord("FOR"); o.ExpectWord("TYPE"); ParseCastType(o); } while (o.MatchSymbol(',')); continue; }
            if (o.MatchWord("SET")) { o.ExpectIdentifier(); if (o.MatchWords("FROM", "CURRENT")) { } else { if (!(o.MatchWord("TO") || o.MatchOperator("="))) throw new ParseException("expected TO or = in SET", o.Here); do { o.Advance(); } while (o.MatchSymbol(',') && !o.AtEnd); } continue; }
            if (o.MatchWord("AS")) { hasBody = true; if (o.Current is { Kind: TokenKind.DollarString or TokenKind.String } b) node.Body = b.Value; ConsumeRest(o); continue; }
            if (o.MatchWords("BEGIN", "ATOMIC")) { hasBody = true; ConsumeRest(o); continue; }
            if (o.MatchWord("RETURN")) { hasBody = true; ConsumeRest(o); continue; }
            if (o.Current is { Kind: TokenKind.DollarString or TokenKind.String }) throw new ParseException("expected AS before the function body", o.Here);
            clean = false; break;                                // unknown token — stop validating, stay lenient
        }
        if (clean)
        {
            if (hasRows && !returnsSet) throw new ParseException("ROWS is only valid for set-returning functions", o.Here);
            if (!hasBody) throw new ParseException("no function body specified", o.Here);
        }
    }

    private static double? ParseOptNumber(TokenCursor c)
    {
        bool neg = c.MatchOperator("-"); if (!neg) c.MatchOperator("+");
        if (c.Current is not { Kind: TokenKind.Number } t) return null;
        c.Advance();
        return double.TryParse(t.Value, System.Globalization.CultureInfo.InvariantCulture, out var d) ? (neg ? -d : d) : null;
    }

    // ---- helpers ------------------------------------------------------------

    private static long? ParseSignedLong(TokenCursor c)
    {
        bool neg = c.MatchOperator("-");
        if (c.Current is { Kind: TokenKind.Number } t && long.TryParse(t.Value, out var v)) { c.Advance(); return neg ? -v : v; }
        return null;
    }


    /// <summary>Best-effort argument-type list for a function signature (modes/names/defaults stripped).</summary>
    private static string ExtractArgTypes(IReadOnlyList<Token> argInner)
    {
        var args = new List<string>();
        foreach (var part in SplitTopLevel(argInner))
        {
            var toks = part;   // freshly yielded per-arg list — safe to splice in place, no defensive copy
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

    private static IEnumerable<List<Token>> SplitTopLevel(IReadOnlyList<Token> tokens)
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
