using System;
using System.Collections.Generic;
using PgProj.Core.Parsing;

namespace PgProj.Core.Syntax;

// Structured CREATE TYPE: composite / ENUM / RANGE / base / shell. Validates the syntax-level shape
// (required name, AS/parens, well-formed option and attribute lists) so the malformed forms Postgres
// rejects are caught here. Catalog-dependent errors (a SUBTYPE that names a missing type, a
// SUBTYPE_DIFF whose function has the wrong return type) are left for a future semantic pass.
public sealed partial class PgParser
{
    private static readonly HashSet<string> RangeOptionKeys = new(StringComparer.OrdinalIgnoreCase)
    { "SUBTYPE", "SUBTYPE_OPCLASS", "COLLATION", "CANONICAL", "SUBTYPE_DIFF", "MULTIRANGE_TYPE_NAME" };

    private SqlStatement ParseCreateType(TokenCursor c)
    {
        var (s, n) = ParseQualifiedName(c);                       // a name is required
        var node = new RawCreateStatement { ObjectKind = "TYPE", Schema = s, Name = n };

        if (c.AtEnd) return node;                                 // CREATE TYPE name;  — shell (forward) type

        if (c.MatchWord("AS"))
        {
            if (c.MatchWord("ENUM")) { ParseEnumBody(c); return node; }
            if (c.MatchWord("RANGE")) { ParseRangeBody(c); return node; }
            ParseCompositeBody(c);                                // AS ( attr type [, …] )
            return node;
        }
        if (c.AtSymbol('(')) { ParseKeyValueOptions(c, null, "base type parameter"); return node; }   // base type: ( INPUT = …, OUTPUT = …, … )

        throw new ParseException("expected AS or '(' after the type name in CREATE TYPE", c.Here);
    }

    // AS ENUM ( 'label' [, 'label'] … )  — labels are string literals, unique, no trailing comma. Empty is legal.
    private void ParseEnumBody(TokenCursor c)
    {
        c.ExpectSymbol('(');
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (!c.AtSymbol(')'))
            do
            {
                if (c.AtSymbol(')')) throw new ParseException("trailing comma in ENUM label list", c.Here);
                var lbl = ConsumeStringLabel(c) ?? throw new ParseException("ENUM labels must be string literals", c.Here);
                if (!seen.Add(lbl)) throw new ParseException($"duplicate ENUM label '{lbl}'", c.Here);
            } while (c.MatchSymbol(','));
        c.ExpectSymbol(')');
    }

    // Consume a string-literal label in any spelling — 'x', E'x', B'x', X'x', $$x$$, U&'x' [UESCAPE 'c'] —
    // and return a representative value for duplicate detection, or null if the next token isn't a string.
    private string? ConsumeStringLabel(TokenCursor c)
    {
        if (c.Current is { Kind: TokenKind.String or TokenKind.DollarString } s) { c.Advance(); return s.Value; }
        if (c.Current is { Kind: TokenKind.Word } p && p.Value.Length == 1 && "EBXebx".IndexOf(p.Value[0]) >= 0
            && c.Peek() is { Kind: TokenKind.String } ps && ps.Position == p.Position + 1) { c.Advance(); c.Advance(); return p.Value + "'" + ps.Value + "'"; }
        if (c.Current is { Kind: TokenKind.Word } u && (u.Value is "U" or "u")
            && c.Peek() is { } amp && amp.IsSymbol('&') && amp.Position == u.Position + 1
            && c.Peek(2) is { Kind: TokenKind.String } us && us.Position == amp.Position + 1)
        {
            c.Advance(); c.Advance(); c.Advance();
            if (c.MatchWord("UESCAPE") && c.Current is { Kind: TokenKind.String }) c.Advance();
            return "U&'" + us.Value + "'";
        }
        return null;
    }

    // AS RANGE ( SUBTYPE = … [, key = value] … )  — SUBTYPE required, known keys only, no duplicates.
    private void ParseRangeBody(TokenCursor c)
    {
        var keys = ParseKeyValueOptions(c, RangeOptionKeys, "RANGE option");
        if (!keys.Contains("SUBTYPE")) throw new ParseException("RANGE type requires a SUBTYPE option", c.Here);
    }

    // AS ( attr_name data_type [COLLATE collation] [, …] )  — each element is name + type; no trailing comma.
    private void ParseCompositeBody(TokenCursor c)
    {
        c.ExpectSymbol('(');
        if (!c.AtSymbol(')'))
            do
            {
                if (c.AtSymbol(')')) throw new ParseException("trailing comma in composite attribute list", c.Here);
                c.ExpectIdentifier();                            // attribute name
                ParseCastType(c);                                // data type (throws if absent)
                if (c.MatchWord("COLLATE")) ParseQualifiedName(c);
            } while (c.MatchSymbol(','));
        c.ExpectSymbol(')');
    }

    // ( key = value [, …] ) — shared by RANGE and base-type options. Rejects an empty list, a missing '='
    // or value, a duplicate key, and (when knownKeys is given) an unknown key. Returns the set of keys seen.
    private HashSet<string> ParseKeyValueOptions(TokenCursor c, HashSet<string>? knownKeys, string what, bool rejectDuplicates = true)
    {
        c.ExpectSymbol('(');
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (c.AtSymbol(')')) throw new ParseException($"{what} list cannot be empty", c.Here);
        do
        {
            if (c.AtSymbol(')')) throw new ParseException($"trailing comma in {what} list", c.Here);
            var key = c.ExpectIdentifier();
            if (knownKeys is not null && !knownKeys.Contains(key)) throw new ParseException($"unknown {what} \"{key}\"", c.Here);
            if (!seen.Add(key) && rejectDuplicates) throw new ParseException($"duplicate {what} \"{key}\"", c.Here);
            if (!c.MatchOperator("=")) throw new ParseException($"expected '=' after {what} \"{key}\"", c.Here);
            if (c.AtEnd || c.AtSymbol(',') || c.AtSymbol(')')) throw new ParseException($"missing value for {what} \"{key}\"", c.Here);
            ParseOptionValue(c);
        } while (c.MatchSymbol(','));
        c.ExpectSymbol(')');
        return seen;
    }

    // A single option value: a qualified name (possibly an operator/function ref) or a literal — captured
    // up to the next top-level comma or ')'.
    private void ParseOptionValue(TokenCursor c)
    {
        int depth = 0;
        while (!c.AtEnd)
        {
            if (depth == 0 && (c.AtSymbol(',') || c.AtSymbol(')'))) break;
            if (c.AtSymbol('(')) depth++;
            else if (c.AtSymbol(')')) depth--;
            c.Advance();
        }
    }
}
