using System;
using System.Collections.Generic;
using PgProj.Core.Parsing;

namespace PgProj.Core.Syntax;

// Session / procedural / utility commands. Each has its own parse method; the dispatcher routes by
// leading keyword. Cores are validated (so malformed forms are rejected); free-form option tails are
// consumed leniently.
public sealed partial class PgParser
{
    private static readonly HashSet<string> CommandKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "DO", "CALL", "SET", "SHOW", "RESET", "EXPLAIN", "LOCK", "PREPARE", "EXECUTE", "DEALLOCATE",
        "LISTEN", "NOTIFY", "UNLISTEN", "BEGIN", "START", "COMMIT", "END", "ROLLBACK", "ABORT",
        "SAVEPOINT", "RELEASE", "DECLARE", "FETCH", "MOVE", "CLOSE", "COPY", "GRANT", "REVOKE",
        "REFRESH", "CHECKPOINT", "DISCARD", "VACUUM", "ANALYZE", "REINDEX", "CLUSTER",
    };

    private SqlStatement ParseCommand(TokenCursor c)
    {
        var kw = c.Current!.Value.ToUpperInvariant();
        return kw switch
        {
            "DO" => ParseDo(c),
            "CALL" => ParseCall(c),
            "SET" => ParseSet(c),
            "SHOW" => ParseShow(c),
            "RESET" => ParseReset(c),
            "EXPLAIN" => ParseExplain(c),
            "LOCK" => ParseLock(c),
            "PREPARE" => ParsePrepare(c),
            "EXECUTE" => ParseExecute(c),
            "DEALLOCATE" => ParseDeallocate(c),
            "LISTEN" or "UNLISTEN" => ParseListen(c),
            "NOTIFY" => ParseNotify(c),
            "BEGIN" or "START" or "COMMIT" or "END" or "ROLLBACK" or "ABORT" or "SAVEPOINT" or "RELEASE" => ParseTransaction(c),
            "DECLARE" => ParseDeclareCursor(c),
            "FETCH" or "MOVE" => ParseFetchMove(c),
            "CLOSE" => ParseClose(c),
            "COPY" => ParseCopy(c),
            "GRANT" or "REVOKE" => ParseGrantRevoke(c),
            _ => ParseSimpleUtility(c),     // REFRESH / CHECKPOINT / DISCARD / VACUUM / ANALYZE / REINDEX / CLUSTER
        };
    }

    private CommandStatement ParseDo(TokenCursor c)
    {
        c.ExpectWord("DO");
        string? lang = null;
        if (c.MatchWord("LANGUAGE")) lang = LangNameOrString(c);
        if (c.Current is not { Kind: TokenKind.DollarString or TokenKind.String })
            throw new ParseException("expected a code block after DO", c.Here);
        var body = c.Advance().Value;
        if (c.MatchWord("LANGUAGE")) lang = LangNameOrString(c);   // LANGUAGE may follow the body
        return new CommandStatement { Kind = "DO", Detail = lang, Body = body };
    }

    private static string LangNameOrString(TokenCursor c)
        => c.Current is { Kind: TokenKind.String } s ? c.Advance().Value : c.ExpectIdentifier();

    private CommandStatement ParseCall(TokenCursor c)
    {
        c.ExpectWord("CALL");
        var (s, n) = ParseQualifiedName(c);
        ParseCallTail(c, s is null ? new List<string> { n } : new List<string> { s, n });
        return new CommandStatement { Kind = "CALL", Detail = n };
    }

    private CommandStatement ParseSet(TokenCursor c)
    {
        c.ExpectWord("SET");
        bool local = c.MatchWord("LOCAL");
        // SESSION is either a scope keyword (mutually exclusive with LOCAL) or the first word of the
        // targets SESSION AUTHORIZATION / SESSION CHARACTERISTICS. A scope SESSION after LOCAL is illegal.
        if (c.AtWord("SESSION") && (c.Peek()?.IsWord("AUTHORIZATION") == true || c.Peek()?.IsWord("CHARACTERISTICS") == true)) c.Advance();
        else if (!local) c.MatchWord("SESSION");

        if (c.MatchWord("CHARACTERISTICS")) { ConsumeRest(c); return new CommandStatement { Kind = "SET" }; }  // SET SESSION CHARACTERISTICS AS …
        if (c.MatchWord("AUTHORIZATION")) { ConsumeRest(c); return new CommandStatement { Kind = "SET" }; }
        if (c.MatchWords("TIME", "ZONE")) { ConsumeRest(c); return new CommandStatement { Kind = "SET" }; }
        if (c.MatchWord("CONSTRAINTS")) { ConsumeRest(c); return new CommandStatement { Kind = "SET CONSTRAINTS" }; }
        if (c.MatchWord("TRANSACTION")) { ConsumeRest(c); return new CommandStatement { Kind = "SET TRANSACTION" }; }
        if (c.MatchWord("ROLE")) { c.ExpectIdentifier(); return new CommandStatement { Kind = "SET ROLE" }; }
        if (c.MatchWord("NAMES")) { ConsumeRest(c); return new CommandStatement { Kind = "SET" }; }            // SET NAMES ['encoding']
        if (c.MatchWord("SCHEMA")) { if (c.AtEnd) throw new ParseException("expected a schema value", c.Here); ConsumeRest(c); return new CommandStatement { Kind = "SET SCHEMA" }; }

        var name = ParseDottedName(c);
        if (!(c.MatchWord("TO") || c.MatchOperator("=")))
            throw new ParseException("expected TO or = in SET", c.Here);
        if (c.AtEnd) throw new ParseException("expected a value in SET", c.Here);
        ConsumeRest(c);
        return new CommandStatement { Kind = "SET", Detail = name };
    }

    private CommandStatement ParseShow(TokenCursor c)
    {
        c.ExpectWord("SHOW");
        if (c.MatchWord("ALL")) return new CommandStatement { Kind = "SHOW", Detail = "ALL" };
        if (c.MatchWords("TIME", "ZONE")) { RejectShowValue(c); return new CommandStatement { Kind = "SHOW", Detail = "TIME ZONE" }; }
        if (c.MatchWords("TRANSACTION", "ISOLATION", "LEVEL")) { RejectShowValue(c); return new CommandStatement { Kind = "SHOW", Detail = "TRANSACTION ISOLATION LEVEL" }; }
        if (c.MatchWords("SESSION", "AUTHORIZATION")) { RejectShowValue(c); return new CommandStatement { Kind = "SHOW", Detail = "SESSION AUTHORIZATION" }; }
        var name = ParseDottedName(c);
        RejectShowValue(c);
        return new CommandStatement { Kind = "SHOW", Detail = name };
    }

    // SHOW takes only a name — never a value. Reject any `=`/`TO`/leftover token.
    private static void RejectShowValue(TokenCursor c)
    {
        if (!c.AtEnd) throw new ParseException($"unexpected '{c.Current!.Value}' — SHOW does not take a value", c.Here);
    }

    private CommandStatement ParseReset(TokenCursor c)
    {
        c.ExpectWord("RESET");
        if (c.MatchWord("ALL")) return new CommandStatement { Kind = "RESET", Detail = "ALL" };
        if (c.MatchWords("TIME", "ZONE")) return new CommandStatement { Kind = "RESET", Detail = "TIME ZONE" };
        if (c.MatchWords("SESSION", "AUTHORIZATION")) return new CommandStatement { Kind = "RESET", Detail = "SESSION AUTHORIZATION" };
        var name = ParseDottedName(c);
        return new CommandStatement { Kind = "RESET", Detail = name };
    }

    private CommandStatement ParseExplain(TokenCursor c)
    {
        c.ExpectWord("EXPLAIN");
        if (c.AtSymbol('(')) CaptureBalancedParens(c);           // ( ANALYZE, VERBOSE, FORMAT … )
        else while (c.AtAnyWord("ANALYZE", "VERBOSE")) c.Advance();
        var inner = ParseStatement(c);
        return new CommandStatement { Kind = "EXPLAIN", Inner = inner };
    }

    private CommandStatement ParseLock(TokenCursor c)
    {
        c.ExpectWord("LOCK");
        c.MatchWord("TABLE");
        do { c.MatchWord("ONLY"); ParseQualifiedName(c); c.MatchSymbol('*'); } while (c.MatchSymbol(','));
        if (c.MatchWord("IN"))
        {
            // mode: ACCESS SHARE / ROW EXCLUSIVE / SHARE ROW EXCLUSIVE / ACCESS EXCLUSIVE / …
            var modeWords = 0;
            while (c.Current is { Kind: TokenKind.Word } && !c.AtWord("MODE")) { c.Advance(); modeWords++; }
            c.ExpectWord("MODE");
            if (modeWords == 0) throw new ParseException("expected a lock mode", c.Here);
        }
        c.MatchWord("NOWAIT");
        return new CommandStatement { Kind = "LOCK" };
    }

    private CommandStatement ParsePrepare(TokenCursor c)
    {
        c.ExpectWord("PREPARE");
        var name = c.ExpectIdentifier();
        if (c.AtSymbol('(')) CaptureBalancedParens(c);           // (type, …)
        c.ExpectWord("AS");
        var inner = ParseStatement(c);
        return new CommandStatement { Kind = "PREPARE", Detail = name, Inner = inner };
    }

    private CommandStatement ParseExecute(TokenCursor c)
    {
        c.ExpectWord("EXECUTE");
        var name = c.ExpectIdentifier();
        if (c.AtSymbol('(')) CaptureBalancedParens(c);
        return new CommandStatement { Kind = "EXECUTE", Detail = name };
    }

    private CommandStatement ParseDeallocate(TokenCursor c)
    {
        c.ExpectWord("DEALLOCATE");
        c.MatchWord("PREPARE");
        if (!c.MatchWord("ALL")) c.ExpectIdentifier();
        return new CommandStatement { Kind = "DEALLOCATE" };
    }

    private CommandStatement ParseListen(TokenCursor c)
    {
        var kw = c.Advance().Value.ToUpperInvariant();           // LISTEN / UNLISTEN
        if (kw == "UNLISTEN" && c.MatchSymbol('*')) return new CommandStatement { Kind = kw };
        c.ExpectIdentifier();
        return new CommandStatement { Kind = kw };
    }

    private CommandStatement ParseNotify(TokenCursor c)
    {
        c.ExpectWord("NOTIFY");
        c.ExpectIdentifier();
        if (c.MatchSymbol(','))
        {
            var t = c.Current;
            if (t is { Kind: TokenKind.String } or { Kind: TokenKind.DollarString }) c.Advance();   // 'x' / $$x$$ / $tag$x$tag$
            else if (t is { Kind: TokenKind.Word } w && w.Value.Length == 1 && "EBXebx".IndexOf(w.Value[0]) >= 0
                     && c.Peek() is { Kind: TokenKind.String } ps && ps.Position == w.Position + 1) { c.Advance(); c.Advance(); }  // E'…' etc
            else if (t is { Kind: TokenKind.Word } u && (u.Value is "U" or "u") && c.Peek()?.IsSymbol('&') == true
                     && c.Peek(2) is { Kind: TokenKind.String }) { c.Advance(); c.Advance(); c.Advance(); if (c.MatchWord("UESCAPE") && c.Current is { Kind: TokenKind.String }) c.Advance(); }  // U&'…'
            else throw new ParseException("NOTIFY payload must be a string literal", c.Here);
            while (c.Current is { Kind: TokenKind.String }) c.Advance();   // adjacent string concatenation
        }
        return new CommandStatement { Kind = "NOTIFY" };
    }

    private CommandStatement ParseTransaction(TokenCursor c)
    {
        var kw = c.Advance().Value.ToUpperInvariant();
        switch (kw)
        {
            case "SAVEPOINT": c.ExpectIdentifier(); break;
            case "RELEASE": c.MatchWord("SAVEPOINT"); c.ExpectIdentifier(); break;
            case "ROLLBACK":
                c.MatchWord("WORK"); c.MatchWord("TRANSACTION");
                if (c.MatchWords("TO")) { c.MatchWord("SAVEPOINT"); c.ExpectIdentifier(); }
                else { c.MatchWord("AND"); c.MatchWord("NO"); c.MatchWord("CHAIN"); }
                break;
            case "COMMIT":
            case "END":
                c.MatchWord("WORK"); c.MatchWord("TRANSACTION");
                c.MatchWord("AND"); c.MatchWord("NO"); c.MatchWord("CHAIN");
                break;
            default:    // BEGIN / START [TRANSACTION|WORK] [isolation/read/deferrable …] ; ABORT
                ConsumeRest(c);
                break;
        }
        return new CommandStatement { Kind = kw };
    }

    private CommandStatement ParseDeclareCursor(TokenCursor c)
    {
        c.ExpectWord("DECLARE");
        var name = c.ExpectIdentifier();
        c.MatchWord("BINARY"); c.MatchWord("INSENSITIVE"); c.MatchWord("ASENSITIVE");
        if (!c.MatchWord("NO")) c.MatchWord("SCROLL"); else c.ExpectWord("SCROLL");
        c.ExpectWord("CURSOR");
        if (c.MatchWord("WITH")) c.ExpectWord("HOLD");
        else if (c.MatchWord("WITHOUT")) c.ExpectWord("HOLD");
        c.ExpectWord("FOR");
        var q = ParseSelectStatement(c);
        return new CommandStatement { Kind = "DECLARE", Detail = name, Query = q };
    }

    private CommandStatement ParseFetchMove(TokenCursor c)
    {
        var kw = c.Advance().Value.ToUpperInvariant();           // FETCH / MOVE
        // optional direction
        if (c.AtAnyWord("NEXT", "PRIOR", "FIRST", "LAST", "ALL")) c.Advance();
        else if (c.AtAnyWord("ABSOLUTE", "RELATIVE")) { c.Advance(); c.MatchOperator("-"); c.MatchOperator("+"); if (c.Current is { Kind: TokenKind.Number }) c.Advance(); }
        else if (c.AtAnyWord("FORWARD", "BACKWARD")) { c.Advance(); if (c.AtWord("ALL")) c.Advance(); else if (c.Current is { Kind: TokenKind.Number }) c.Advance(); }
        else if (c.Current is { Kind: TokenKind.Number }) c.Advance();
        else if (c.AtOperator("-") && c.Peek() is { Kind: TokenKind.Number }) { c.Advance(); c.Advance(); }
        c.MatchWord("FROM"); c.MatchWord("IN");
        c.ExpectIdentifier();                                    // cursor name
        return new CommandStatement { Kind = kw };
    }

    private CommandStatement ParseClose(TokenCursor c)
    {
        c.ExpectWord("CLOSE");
        if (!c.MatchWord("ALL")) c.ExpectIdentifier();
        return new CommandStatement { Kind = "CLOSE" };
    }

    private CommandStatement ParseCopy(TokenCursor c)
    {
        c.ExpectWord("COPY");
        if (c.AtSymbol('(')) CaptureBalancedParens(c);           // COPY ( query ) TO …
        else { ParseQualifiedName(c); if (c.AtSymbol('(')) CaptureBalancedParens(c); }   // table [(cols)]
        if (!(c.MatchWord("FROM") || c.MatchWord("TO")))
            throw new ParseException("expected FROM or TO in COPY", c.Here);
        if (c.MatchWord("PROGRAM")) { if (c.Current is not { Kind: TokenKind.String }) throw new ParseException("expected a program string", c.Here); c.Advance(); }
        else if (!(c.MatchWord("STDIN") || c.MatchWord("STDOUT")))
        {
            if (c.Current is not { Kind: TokenKind.String }) throw new ParseException("expected STDIN/STDOUT/PROGRAM or a file path", c.Here);
            c.Advance();
        }
        ConsumeRest(c);                                          // [WITH] (options) / old-style
        return new CommandStatement { Kind = "COPY" };
    }

    private CommandStatement ParseGrantRevoke(TokenCursor c)
    {
        var kw = c.Advance().Value.ToUpperInvariant();           // GRANT / REVOKE
        bool revoke = kw == "REVOKE";
        if (revoke) c.MatchWords("GRANT", "OPTION", "FOR");

        // privilege list (ALL [PRIVILEGES] | priv[(cols)] [, …])  OR  a role name (GRANT role TO role)
        // Consume up to the ON / TO / FROM boundary, validating it is non-empty.
        int consumed = 0;
        while (!c.AtEnd && !c.AtWord("ON") && !(revoke ? c.AtWord("FROM") : c.AtWord("TO")))
        { c.Advance(); consumed++; }
        if (consumed == 0) throw new ParseException("expected privileges or a role", c.Here);

        if (c.MatchWord("ON")) { while (!c.AtEnd && !(revoke ? c.AtWord("FROM") : c.AtWord("TO"))) c.Advance(); }

        if (!(revoke ? c.MatchWord("FROM") : c.MatchWord("TO")))
            throw new ParseException($"expected {(revoke ? "FROM" : "TO")} in {kw}", c.Here);
        if (c.AtEnd) throw new ParseException("expected a grantee", c.Here);
        ConsumeRest(c);                                          // grantees [WITH …] [CASCADE|RESTRICT]
        return new CommandStatement { Kind = kw };
    }

    private CommandStatement ParseSimpleUtility(TokenCursor c)
    {
        var kw = c.Advance().Value.ToUpperInvariant();           // REFRESH/CHECKPOINT/DISCARD/VACUUM/ANALYZE/REINDEX/CLUSTER
        ConsumeRest(c);
        return new CommandStatement { Kind = kw };
    }

    // ---- helpers ------------------------------------------------------------

    private static string ParseDottedName(TokenCursor c)
    {
        var name = c.ExpectIdentifier();
        while (c.MatchSymbol('.')) name += "." + c.ExpectIdentifier();
        return name;
    }

    private static void ConsumeRest(TokenCursor c) { while (!c.AtEnd) c.Advance(); }
}
