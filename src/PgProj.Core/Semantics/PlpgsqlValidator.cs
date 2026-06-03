using System;
using System.Collections.Generic;
using PgProj.Core.Parsing;
using PgProj.Core.Syntax;

namespace PgProj.Core.Semantics;

/// <summary>
/// A deliberately lenient PL/pgSQL body validator. It parses the block skeleton (DECLARE / BEGIN /
/// EXCEPTION / END), the declaration list, and the RAISE / cursor statements, and reports ONLY the
/// specific structural mistakes PostgreSQL rejects at compile time. Anything it does not model it
/// skips (consume-to-semicolon) and, if it ever loses its place, it abandons validation silently — so
/// it never reports a problem on a body it merely failed to understand. That keeps it zero-false-positive.
///
/// Out of scope (left unreported): run-time behaviour (a RAISE that fires, 1/0, double CLOSE) and any
/// catalog-dependent check (unknown type, missing column for %TYPE).
/// </summary>
public sealed class PlpgsqlValidator
{
    private readonly TokenCursor _c;
    private readonly List<string> _errors = new();

    private PlpgsqlValidator(TokenCursor c) => _c = c;

    private static readonly HashSet<string> RaiseLevels = new(StringComparer.OrdinalIgnoreCase)
    { "DEBUG", "LOG", "INFO", "NOTICE", "WARNING", "EXCEPTION" };
    private static readonly HashSet<string> RaiseOptions = new(StringComparer.OrdinalIgnoreCase)
    { "MESSAGE", "DETAIL", "HINT", "ERRCODE", "COLUMN", "CONSTRAINT", "DATATYPE", "TABLE", "SCHEMA" };
    private static readonly HashSet<string> SimpleDirections = new(StringComparer.OrdinalIgnoreCase)
    { "NEXT", "PRIOR", "FIRST", "LAST" };
    private static readonly HashSet<string> CountedDirections = new(StringComparer.OrdinalIgnoreCase)
    { "ABSOLUTE", "RELATIVE" };
    private static readonly HashSet<string> RangeDirections = new(StringComparer.OrdinalIgnoreCase)
    { "FORWARD", "BACKWARD" };
    private static readonly HashSet<string> StatementStoppers = new(StringComparer.OrdinalIgnoreCase)
    { "END", "EXCEPTION", "WHEN", "ELSIF", "ELSEIF", "ELSE" };

    public static IReadOnlyList<string> Validate(string rawBody)
    {
        var src = StripDollar(rawBody);
        if (src is null) return Array.Empty<string>();          // not dollar-quoted — skip (escape ambiguity)
        List<Token> toks;
        try { toks = OperatorLexer.Merge(Tokenizer.Tokenize(src)); }
        catch { return Array.Empty<string>(); }
        var v = new PlpgsqlValidator(new TokenCursor(toks));
        try { v.ParseBlock(); }
        catch (ParseException) { /* lost the thread — never report a generic failure */ }
        return v._errors;
    }

    private void Err(string message) => _errors.Add(message);
    private static ParseException Lost() => new("plpgsql: stop validating", 0);

    private static string? StripDollar(string s)
    {
        if (s.Length < 2 || s[0] != '$') return null;
        int close = s.IndexOf('$', 1);
        if (close < 0) return null;
        var tag = s.Substring(0, close + 1);
        if (s.Length < 2 * tag.Length || !s.EndsWith(tag, StringComparison.Ordinal)) return null;
        return s.Substring(tag.Length, s.Length - 2 * tag.Length);
    }

    // ---- block structure ----------------------------------------------------

    private void ParseBlock()
    {
        MatchLabel();
        if (_c.MatchWord("DECLARE")) ParseDeclareSection();
        if (!_c.MatchWord("BEGIN")) throw Lost();
        ParseStatements();
        if (_c.MatchWord("EXCEPTION")) ParseHandlers();
        if (!_c.MatchWord("END")) throw Lost();
        if (_c.Current is { Kind: TokenKind.Word }) _c.Advance();   // optional end label
    }

    private void MatchLabel()
    {
        if (!_c.AtOperator("<<")) return;
        _c.Advance();
        if (_c.Current is not { Kind: TokenKind.Word or TokenKind.QuotedIdent }) throw Lost();
        _c.Advance();
        if (!_c.MatchOperator(">>")) throw Lost();
    }

    private void ParseStatements()
    {
        while (!_c.AtEnd && !(_c.Current is { Kind: TokenKind.Word } w && StatementStoppers.Contains(w.Value)))
            ParseStatement();
    }

    private void ParseStatement()
    {
        // nested block
        if (_c.AtOperator("<<") || _c.AtWord("DECLARE") || _c.AtWord("BEGIN")) { ParseBlock(); _c.MatchSymbol(';'); return; }
        if (_c.MatchWord("IF")) { ParseIf(); return; }
        if (_c.MatchWord("CASE")) { ParseCase(); return; }
        if (_c.MatchWord("LOOP")) { ParseStatements(); ExpectEnd("LOOP"); return; }
        if (_c.MatchWord("WHILE")) { ConsumeUntilWord("LOOP"); ExpectWord("LOOP"); ParseStatements(); ExpectEnd("LOOP"); return; }
        if (_c.AtWord("FOR") || _c.AtWord("FOREACH")) { _c.Advance(); ConsumeUntilWord("LOOP"); ExpectWord("LOOP"); ParseStatements(); ExpectEnd("LOOP"); return; }
        if (_c.MatchWord("RAISE")) { ParseRaise(); return; }
        if (_c.AtAnyWord("FETCH", "MOVE")) { ParseFetchMove(); return; }
        if (_c.MatchWord("CLOSE")) { if (_c.AtEnd || _c.AtSymbol(';')) Err("CLOSE requires a cursor name"); ConsumeToSemicolon(); return; }
        // anything else: a plain statement up to ';'
        ConsumeToSemicolon();
    }

    private void ParseIf()
    {
        ConsumeUntilWord("THEN"); ExpectWord("THEN");
        ParseStatements();
        while (_c.MatchWord("ELSIF") || _c.MatchWord("ELSEIF")) { ConsumeUntilWord("THEN"); ExpectWord("THEN"); ParseStatements(); }
        if (_c.MatchWord("ELSE")) ParseStatements();
        ExpectEnd("IF");
    }

    private void ParseCase()
    {
        ConsumeUntilWord("WHEN");                                // optional test expression
        while (_c.MatchWord("WHEN")) { ConsumeUntilWord("THEN"); ExpectWord("THEN"); ParseStatements(); }
        if (_c.MatchWord("ELSE")) ParseStatements();
        ExpectEnd("CASE");
    }

    private void ExpectEnd(string what) { if (!_c.MatchWord("END")) throw Lost(); if (!_c.MatchWord(what)) throw Lost(); _c.MatchSymbol(';'); }

    // ---- DECLARE section ----------------------------------------------------

    private void ParseDeclareSection()
    {
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // scope is this DECLARE only — shadowing in nested blocks is legal
        while (!_c.AtEnd && !_c.AtWord("BEGIN"))
            ParseDeclaration(declared);
    }

    private void ParseDeclaration(HashSet<string> declared)
    {
        if (_c.Current is not { Kind: TokenKind.Word or TokenKind.QuotedIdent }) { Err("expected a variable name in DECLARE"); throw Lost(); }
        var name = _c.Advance().Value;   // note: CONSTANT is not reserved, so it can legitimately be a variable name
        if (!declared.Add(name)) Err($"duplicate declaration of variable \"{name}\"");

        if (_c.MatchWord("ALIAS"))
        {
            if (!_c.MatchWord("FOR")) { Err("expected FOR after ALIAS"); throw Lost(); }
            if (_c.AtEnd || _c.AtSymbol(';')) { Err("missing target after ALIAS FOR"); throw Lost(); }
            ConsumeToSemicolon();
            return;
        }

        _c.MatchWord("CONSTANT");

        // cursor declaration: [ [NO] SCROLL ] CURSOR ...
        if (_c.AtWord("SCROLL") && _c.Peek()?.IsWord("CURSOR") == true) _c.Advance();
        else if (_c.AtWord("NO") && _c.Peek()?.IsWord("SCROLL") == true) { _c.Advance(); _c.Advance(); }
        if (_c.MatchWord("CURSOR")) { ParseCursorDeclaration(); return; }

        // a data type is required
        if (_c.AtEnd || _c.AtSymbol(';') || _c.AtOperator(":=") || _c.AtWord("DEFAULT"))
        { Err($"missing data type for variable \"{name}\""); throw Lost(); }
        ConsumeType();
        if (_c.AtWord("ALIAS")) { Err("ALIAS cannot be combined with a data type"); throw Lost(); }
        _c.MatchWords("NOT", "NULL");
        if (_c.MatchWord("COLLATE")) { if (_c.Current is { Kind: TokenKind.Word or TokenKind.QuotedIdent }) _c.Advance(); }
        if (_c.MatchOperator(":=") || _c.MatchWord("DEFAULT"))
        {
            if (_c.AtEnd || _c.AtSymbol(';')) { Err($"missing default expression for variable \"{name}\""); throw Lost(); }
        }
        ConsumeToSemicolon();
    }

    private void ParseCursorDeclaration()
    {
        if (_c.AtSymbol('(')) SkipParens();                     // cursor arguments
        if (!_c.MatchWord("FOR") && !_c.MatchWord("IS")) { Err("a bound cursor declaration must specify FOR/IS <query>"); throw Lost(); }
        if (_c.AtEnd || _c.AtSymbol(';')) { Err("missing query after CURSOR ... FOR"); throw Lost(); }
        ConsumeToSemicolon();
    }

    private void ConsumeType()
    {
        // consume the (possibly multi-word, dotted, %TYPE, parameterised) type up to a declaration delimiter
        while (!_c.AtEnd && !_c.AtSymbol(';') && !_c.AtOperator(":=") && !_c.AtWord("DEFAULT")
               && !_c.AtWord("COLLATE") && !_c.AtWord("ALIAS") && !(_c.AtWord("NOT") && _c.Peek()?.IsWord("NULL") == true))
        {
            if (_c.AtSymbol('(')) SkipParens();
            else _c.Advance();
        }
    }

    // ---- EXCEPTION handlers -------------------------------------------------

    private void ParseHandlers()
    {
        if (!_c.AtWord("WHEN")) { Err("the EXCEPTION section must begin with WHEN"); throw Lost(); }
        while (_c.MatchWord("WHEN"))
        {
            if (_c.AtWord("THEN")) { Err("missing condition after WHEN"); throw Lost(); }
            ParseHandlerCondition();
            while (_c.MatchWord("OR")) ParseHandlerCondition();
            if (!_c.MatchWord("THEN")) { Err("expected THEN after the exception condition"); throw Lost(); }
            ParseStatements();
        }
    }

    private void ParseHandlerCondition()
    {
        if (_c.MatchWord("SQLSTATE")) { if (_c.Current is { Kind: TokenKind.String }) _c.Advance(); else throw Lost(); return; }
        if (_c.Current is not { Kind: TokenKind.Word or TokenKind.QuotedIdent }) { Err("expected an exception condition name"); throw Lost(); }
        _c.Advance();
    }

    // ---- RAISE --------------------------------------------------------------

    private void ParseRaise()
    {
        if (_c.AtEnd || _c.AtSymbol(';')) { ConsumeToSemicolon(); return; }   // bare RAISE (re-raise) — runtime concern

        // level
        if (_c.Current is { Kind: TokenKind.Word } lvl && RaiseLevels.Contains(lvl.Value)) _c.Advance();

        if (_c.MatchWord("SQLSTATE"))                            // RAISE [level] SQLSTATE 'code' [USING …]
        {
            if (_c.Current is { Kind: TokenKind.String }) _c.Advance(); else throw Lost();
        }
        else if (TryConsumeStringLiteral(out var fmt))          // format string (plain or E'…'/B'…'/X'…'/U&'…') + value args
        {
            int args = 0;
            while (_c.MatchSymbol(',')) { if (_c.AtWord("USING") || _c.AtSymbol(';') || _c.AtEnd) break; ConsumeOneArg(); args++; }
            int placeholders = CountPlaceholders(fmt);
            if (args != placeholders)
                Err($"too {(args > placeholders ? "many" : "few")} parameters specified for RAISE");
        }
        else if (_c.Current is { Kind: TokenKind.Word } bad && _c.Peek() is { Kind: TokenKind.String })
        { Err($"unrecognized RAISE level \"{bad.Value}\""); throw Lost(); }
        else if (_c.Current is { Kind: TokenKind.Word } && !_c.AtWord("USING"))
        {
            _c.Advance();                                       // condition name
        }

        if (_c.MatchWord("USING"))
        {
            do
            {
                if (_c.Current is not { Kind: TokenKind.Word } opt) { Err("expected a RAISE option name"); throw Lost(); }
                _c.Advance();
                if (!RaiseOptions.Contains(opt.Value)) { Err($"unrecognized RAISE option \"{opt.Value}\""); throw Lost(); }
                if (!_c.MatchOperator("=")) throw Lost();
                if (_c.AtEnd || _c.AtSymbol(';') || _c.AtSymbol(',')) { Err($"missing value for RAISE option \"{opt.Value}\""); throw Lost(); }
                ConsumeOneArg();
            } while (_c.MatchSymbol(','));
        }
        ConsumeToSemicolon();
    }

    // Consume a string literal in any spelling and yield its inner text: 'x', E'x', B'x', X'x', U&'x', $$x$$.
    private bool TryConsumeStringLiteral(out string value)
    {
        value = "";
        if (_c.Current is { Kind: TokenKind.String or TokenKind.DollarString } s) { _c.Advance(); value = s.Value; return true; }
        if (_c.Current is { Kind: TokenKind.Word } p && p.Value.Length == 1 && "EBXebx".IndexOf(p.Value[0]) >= 0
            && _c.Peek() is { Kind: TokenKind.String } ps && ps.Position == p.Position + 1)
        { _c.Advance(); _c.Advance(); value = ps.Value; return true; }
        if (_c.Current is { Kind: TokenKind.Word } u && (u.Value is "U" or "u")
            && _c.Peek() is { } amp && amp.IsSymbol('&') && amp.Position == u.Position + 1
            && _c.Peek(2) is { Kind: TokenKind.String } us && us.Position == amp.Position + 1)
        { _c.Advance(); _c.Advance(); _c.Advance(); value = us.Value; return true; }
        return false;
    }

    private static int CountPlaceholders(string fmt)
    {
        int n = 0;
        for (int i = 0; i < fmt.Length; i++)
        {
            if (fmt[i] != '%') continue;
            if (i + 1 < fmt.Length && fmt[i + 1] == '%') { i++; continue; }   // %% is a literal percent
            n++;
        }
        return n;
    }

    // ---- FETCH / MOVE -------------------------------------------------------

    private void ParseFetchMove()
    {
        bool isFetch = _c.Advance().Value.Equals("FETCH", StringComparison.OrdinalIgnoreCase);
        ParseFetchDirection();
        _c.MatchWord("FROM"); _c.MatchWord("IN");
        if (_c.Current is not { Kind: TokenKind.Word or TokenKind.QuotedIdent }) { Err("expected a cursor name"); throw Lost(); }
        _c.Advance();
        if (isFetch)
        {
            if (!_c.MatchWord("INTO")) { Err("FETCH in PL/pgSQL requires an INTO clause"); throw Lost(); }
        }
        ConsumeToSemicolon();
    }

    private void ParseFetchDirection()
    {
        if (_c.Current is not { Kind: TokenKind.Word } w) { /* a count or nothing */ ConsumeSignedNumberOpt(); return; }

        if (SimpleDirections.Contains(w.Value))
        {
            _c.Advance();
            if (_c.Current is { Kind: TokenKind.Word } d && (SimpleDirections.Contains(d.Value) || CountedDirections.Contains(d.Value) || RangeDirections.Contains(d.Value)))
            { Err("conflicting or repeated FETCH/MOVE direction"); throw Lost(); }
            return;
        }
        if (CountedDirections.Contains(w.Value))
        {
            _c.Advance();
            if (!ConsumeSignedNumberOpt()) { Err($"{w.Value.ToUpperInvariant()} requires a count"); throw Lost(); }
            return;
        }
        if (RangeDirections.Contains(w.Value)) { _c.Advance(); if (!_c.MatchWord("ALL")) ConsumeSignedNumberOpt(); return; }
        if (w.Value.Equals("ALL", StringComparison.OrdinalIgnoreCase)) { _c.Advance(); return; }

        // an identifier that is not a known direction: it's the cursor name (no direction) UNLESS a
        // FROM/IN follows, in which case it was being used as an (invalid) direction.
        if (_c.Peek()?.IsWord("FROM") == true || _c.Peek()?.IsWord("IN") == true)
        { Err($"\"{w.Value}\" is not a valid FETCH/MOVE direction"); throw Lost(); }
    }

    private bool ConsumeSignedNumberOpt()
    {
        if (_c.AtOperator("-") || _c.AtOperator("+")) { _c.Advance(); }
        if (_c.Current is { Kind: TokenKind.Number }) { _c.Advance(); return true; }
        return false;
    }

    // ---- low-level helpers --------------------------------------------------

    private void ExpectWord(string w) { if (!_c.MatchWord(w)) throw Lost(); }

    private void ConsumeOneArg()
    {
        int depth = 0;
        while (!_c.AtEnd)
        {
            if (depth == 0 && (_c.AtSymbol(',') || _c.AtSymbol(';') || _c.AtWord("USING"))) return;
            if (_c.AtSymbol('(') || _c.AtSymbol('[')) depth++;
            else if (_c.AtSymbol(')') || _c.AtSymbol(']')) depth--;
            _c.Advance();
        }
    }

    private void ConsumeToSemicolon()
    {
        int depth = 0; bool progressed = false;
        while (!_c.AtEnd)
        {
            if (depth == 0 && _c.AtSymbol(';')) { _c.Advance(); return; }
            if (_c.AtSymbol('(') || _c.AtSymbol('[')) depth++;
            else if (_c.AtSymbol(')') || _c.AtSymbol(']')) depth--;
            _c.Advance();
            progressed = true;
        }
        if (!progressed) throw Lost();   // guard against a non-advancing loop
    }

    private void ConsumeUntilWord(string word)
    {
        int depth = 0;
        while (!_c.AtEnd)
        {
            if (depth == 0 && _c.AtWord(word)) return;
            if (_c.AtSymbol('(') || _c.AtSymbol('[')) depth++;
            else if (_c.AtSymbol(')') || _c.AtSymbol(']')) depth--;
            _c.Advance();
        }
        throw Lost();
    }

    private void SkipParens()
    {
        if (!_c.AtSymbol('(')) return;
        int depth = 0;
        do
        {
            if (_c.AtSymbol('(')) depth++;
            else if (_c.AtSymbol(')')) depth--;
            _c.Advance();
        } while (!_c.AtEnd && depth > 0);
    }
}
