using System;
using System.Collections.Generic;
using PgProj.Core.Parsing;

namespace PgProj.Core.Syntax;

// ALTER and DROP. ALTER TABLE has a real per-action dispatcher (malformed actions are rejected);
// other ALTER kinds and DROP validate the skeleton and consume option tails leniently.
public sealed partial class PgParser
{
    private SqlStatement ParseAlter(TokenCursor c)
    {
        c.ExpectWord("ALTER");

        if (c.MatchWord("TABLE")) return ParseAlterTable(c);

        // ALTER <kind> … — kind may be multi-word (MATERIALIZED VIEW, FOREIGN TABLE, TEXT SEARCH …,
        // EVENT TRIGGER, OPERATOR CLASS/FAMILY, DEFAULT PRIVILEGES, FOREIGN DATA WRAPPER, USER MAPPING)
        var kind = ParseObjectKind(c);
        var alter = new AlterStatement { ObjectKind = kind };

        if (kind is "DEFAULT PRIVILEGES" or "LARGE OBJECT") { ConsumeRest(c); return alter; }

        c.MatchWords("IF", "EXISTS");
        if (kind is "FUNCTION" or "PROCEDURE" or "AGGREGATE" or "ROUTINE" or "OPERATOR")
            ConsumeSignatureName(c);
        else
        {
            var (s, n) = ParseQualifiedName(c);
            alter.Schema = s; alter.Name = n;
            if (kind == "TRIGGER" || kind == "POLICY" || kind == "RULE") { c.ExpectWord("ON"); ParseQualifiedName(c); }
        }

        if (kind == "TYPE") { ParseAlterTypeAction(c, alter); return alter; }   // ENUM/attribute mutations have their own grammar

        // common tails
        if (c.MatchWord("RENAME"))
        {
            c.MatchWord("COLUMN"); c.MatchWord("CONSTRAINT"); c.MatchWord("ATTRIBUTE"); c.MatchWord("VALUE");
            if (!c.AtWord("TO")) ConsumeNameOrString(c);   // enum RENAME VALUE 'old' uses a string
            c.ExpectWord("TO"); ConsumeNameOrString(c);
            MatchCascadeRestrict(c);                        // RENAME ATTRIBUTE … TO … CASCADE|RESTRICT
            alter.Actions.Add("RENAME");
        }
        else if (c.MatchWords("OWNER", "TO")) { ParseRoleSpec(c); alter.Actions.Add("OWNER"); }
        else if (c.MatchWords("SET", "SCHEMA")) { c.ExpectIdentifier(); alter.Actions.Add("SET SCHEMA"); }
        else if (c.AtEnd) { if (kind != "POLICY") throw new ParseException("expected an ALTER action", c.Here); }   // ALTER POLICY p ON t — bare no-op is valid
        else ConsumeRest(c);

        return alter;
    }

    private AlterStatement ParseAlterTable(TokenCursor c)
    {
        bool ifExists = c.MatchWords("IF", "EXISTS");
        // ALTER TABLE ALL IN TABLESPACE x [OWNED BY ..] SET TABLESPACE y
        if (c.MatchWords("ALL", "IN", "TABLESPACE")) { ConsumeRest(c); return new AlterStatement { ObjectKind = "TABLE" }; }
        c.MatchWord("ONLY");
        var (s, n) = ParseQualifiedName(c);
        c.MatchSymbol('*');
        var alter = new AlterStatement { ObjectKind = "TABLE", Schema = s, Name = n };

        // standalone forms
        if (c.MatchWord("RENAME"))
        {
            if (c.MatchWord("CONSTRAINT")) { c.ExpectIdentifier(); c.ExpectWord("TO"); c.ExpectIdentifier(); }
            else if (c.MatchWord("TO")) { c.ExpectIdentifier(); }
            else { c.MatchWord("COLUMN"); c.ExpectIdentifier(); c.ExpectWord("TO"); c.ExpectIdentifier(); }
            alter.Actions.Add("RENAME"); return alter;
        }
        if (c.AtWord("ATTACH") || c.AtWord("DETACH")) { ConsumeRest(c); alter.Actions.Add("PARTITION"); return alter; }

        // action list
        do { alter.Actions.Add(ParseAlterTableAction(c, alter)); } while (c.MatchSymbol(','));
        return alter;
    }

    private string ParseAlterTableAction(TokenCursor c, AlterStatement alter)
    {
        if (c.MatchWord("ADD")) return ParseAlterAdd(c, alter);
        if (c.MatchWord("DROP")) return ParseAlterDropAction(c);
        if (c.MatchWord("ALTER")) return ParseAlterColumnOrConstraint(c);
        if (c.MatchWord("VALIDATE")) { c.ExpectWord("CONSTRAINT"); c.ExpectIdentifier(); return "VALIDATE"; }
        if (c.MatchWord("OWNER")) { c.ExpectWord("TO"); ParseRoleSpec(c); return "OWNER"; }
        if (c.MatchWord("CLUSTER")) { c.ExpectWord("ON"); c.ExpectIdentifier(); return "CLUSTER"; }
        if (c.MatchWords("SET", "WITHOUT")) { if (!c.MatchWord("CLUSTER")) c.ExpectWord("OIDS"); return "SET WITHOUT"; }
        if (c.MatchWords("SET", "SCHEMA")) { c.ExpectIdentifier(); return "SET SCHEMA"; }
        if (c.MatchWords("SET", "TABLESPACE")) { c.ExpectIdentifier(); return "SET TABLESPACE"; }
        if (c.MatchWords("SET", "LOGGED") || c.MatchWords("SET", "UNLOGGED")) return "SET LOGGED";
        if (c.MatchWord("SET")) { if (c.AtSymbol('(')) CaptureNonEmptyParens(c); else ConsumeActionRest(c); return "SET"; }
        if (c.MatchWord("RESET")) { if (c.AtSymbol('(')) c.SkipBalancedParens(); return "RESET"; }
        if (c.AtAnyWord("ENABLE", "DISABLE", "FORCE")) { ConsumeActionRest(c); return "ENABLE/DISABLE"; }
        if (c.MatchWords("NO", "FORCE")) { ConsumeActionRest(c); return "NO FORCE"; }
        if (c.MatchWord("INHERIT")) { ParseQualifiedName(c); return "INHERIT"; }
        if (c.MatchWords("NO", "INHERIT")) { ParseQualifiedName(c); return "NO INHERIT"; }
        if (c.MatchWord("OF")) { ParseQualifiedName(c); return "OF"; }
        if (c.MatchWords("NOT", "OF")) return "NOT OF";
        if (c.MatchWords("REPLICA", "IDENTITY")) { ConsumeActionRest(c); return "REPLICA IDENTITY"; }
        throw new ParseException($"unknown ALTER TABLE action at {Render(c.Current)}", c.Here);
    }

    private string ParseAlterAdd(TokenCursor c, AlterStatement alter)
    {
        if (c.MatchWord("COLUMN")) { c.MatchWords("IF", "NOT", "EXISTS"); var b = MakeTableHolder(); ParseColumnInto(c, b); return "ADD COLUMN"; }
        if (c.AtWord("CONSTRAINT") || c.AtAnyWord("PRIMARY", "UNIQUE", "FOREIGN", "CHECK", "EXCLUDE", "NOT"))
        {
            string? name = null;
            if (c.MatchWord("CONSTRAINT")) name = c.ExpectIdentifier();
            if (c.AtWord("NOT"))   // PG18 table-level NOT NULL column [NO INHERIT]
            { c.ExpectWord("NOT"); c.ExpectWord("NULL"); c.ExpectIdentifier(); c.MatchWords("NO", "INHERIT"); c.MatchWords("NOT", "VALID"); return "ADD CONSTRAINT"; }
            // Capture the parsed constraint (#153) so ModelBuilder can fold a standalone ADD CONSTRAINT into the
            // table model — same node the CREATE TABLE path produces, so no behavioural change to accept/reject.
            alter.AddedConstraints.Add(ParseTableConstraint(c, name));
            c.MatchWords("NOT", "VALID");
            return "ADD CONSTRAINT";
        }
        // ADD column without the COLUMN keyword
        c.MatchWords("IF", "NOT", "EXISTS");
        var holder = MakeTableHolder();
        ParseColumnInto(c, holder);
        return "ADD COLUMN";
    }

    private string ParseAlterDropAction(TokenCursor c)
    {
        if (c.MatchWord("CONSTRAINT")) { c.MatchWords("IF", "EXISTS"); c.ExpectIdentifier(); MatchCascadeRestrict(c); return "DROP CONSTRAINT"; }
        c.MatchWord("COLUMN");
        c.MatchWords("IF", "EXISTS");
        c.ExpectIdentifier();
        MatchCascadeRestrict(c);
        return "DROP COLUMN";
    }

    private string ParseAlterColumnOrConstraint(TokenCursor c)
    {
        if (c.MatchWord("CONSTRAINT")) { c.ExpectIdentifier(); ConsumeActionRest(c); return "ALTER CONSTRAINT"; }
        c.MatchWord("COLUMN");
        c.ExpectIdentifier();    // column name

        if (c.MatchWords("SET", "DATA", "TYPE") || c.MatchWord("TYPE"))
        {
            ParseCastType(c);
            if (c.MatchWord("COLLATE")) ParseQualifiedName(c);
            if (c.MatchWord("USING")) ParseExpression(c);
            return "ALTER COLUMN TYPE";
        }
        if (c.MatchWords("SET", "DEFAULT")) { ParseExpression(c); return "SET DEFAULT"; }
        if (c.MatchWords("DROP", "DEFAULT")) return "DROP DEFAULT";
        if (c.MatchWords("SET", "NOT", "NULL")) return "SET NOT NULL";
        if (c.MatchWords("DROP", "NOT", "NULL")) return "DROP NOT NULL";
        if (c.MatchWords("DROP", "EXPRESSION")) { c.MatchWords("IF", "EXISTS"); return "DROP EXPRESSION"; }
        if (c.MatchWords("ADD", "GENERATED")) { ConsumeActionRest(c); return "ADD GENERATED"; }
        if (c.MatchWords("SET", "GENERATED")) { ConsumeActionRest(c); return "SET GENERATED"; }
        if (c.MatchWords("DROP", "IDENTITY")) { c.MatchWords("IF", "EXISTS"); return "DROP IDENTITY"; }
        if (c.MatchWords("SET", "STATISTICS")) { ConsumeActionRest(c); return "SET STATISTICS"; }
        if (c.MatchWords("SET", "STORAGE")) { var v = c.ExpectIdentifier(); if (!StorageModes.Contains(v)) throw new ParseException($"invalid storage mode \"{v}\"", c.Here); return "SET STORAGE"; }
        if (c.MatchWords("SET", "COMPRESSION")) { c.ExpectIdentifier(); return "SET COMPRESSION"; }
        if (c.MatchWord("SET")) { if (c.AtSymbol('(')) CaptureNonEmptyParens(c); else ConsumeActionRest(c); return "SET opts"; }
        if (c.MatchWord("RESET")) { if (c.AtSymbol('(')) c.SkipBalancedParens(); return "RESET"; }
        if (c.MatchWord("RESTART")) { ConsumeActionRest(c); return "RESTART"; }
        throw new ParseException($"unknown ALTER COLUMN action at {Render(c.Current)}", c.Here);
    }

    private SqlStatement ParseDrop(TokenCursor c)
    {
        c.ExpectWord("DROP");
        var kind = ParseObjectKind(c);
        var drop = new DropStatement { ObjectKind = kind };
        drop.Concurrently = c.MatchWord("CONCURRENTLY");
        drop.IfExists = c.MatchWords("IF", "EXISTS");

        if (kind is "OWNED")            // DROP OWNED BY role
        { c.ExpectWord("BY"); do { ParseRoleSpec(c); } while (c.MatchSymbol(',')); drop.DropOption = MatchCascadeRestrict(c); return drop; }

        do { drop.Names.Add(ParseDropTarget(c, kind)); } while (c.MatchSymbol(','));
        if (drop.Names.Count == 0) throw new ParseException("DROP needs at least one object", c.Here);
        drop.DropOption = MatchCascadeRestrict(c);
        return drop;
    }

    private string ParseDropTarget(TokenCursor c, string kind)
    {
        // function/aggregate/operator/cast have signature-ish targets (name may be a symbol or "(types)")
        if (kind is "FUNCTION" or "PROCEDURE" or "AGGREGATE" or "ROUTINE" or "OPERATOR" or "CAST")
        { ConsumeSignatureName(c); return "(signature)"; }

        if (kind is "OPERATOR CLASS" or "OPERATOR FAMILY")
        { var (s, n) = ParseQualifiedName(c); if (c.MatchWord("USING")) c.ExpectIdentifier(); return $"{s}.{n}"; }

        if (kind == "USER MAPPING")  // DROP USER MAPPING FOR { name | USER | CURRENT_USER | CURRENT_ROLE | SESSION_USER | PUBLIC } SERVER name
        {
            c.ExpectWord("FOR");
            if (!(c.MatchWord("CURRENT_USER") || c.MatchWord("CURRENT_ROLE") || c.MatchWord("SESSION_USER") || c.MatchWord("USER") || c.MatchWord("PUBLIC")))
                c.ExpectIdentifier();
            c.ExpectWord("SERVER");
            return $"mapping@{c.ExpectIdentifier()}";
        }

        if (kind == "TRANSFORM")    // DROP TRANSFORM FOR <type> LANGUAGE <lang>
        {
            c.MatchWord("FOR");
            while (!c.AtEnd && !c.AtWord("LANGUAGE") && !c.AtWord("CASCADE") && !c.AtWord("RESTRICT") && !c.AtSymbol(',')) c.Advance();
            if (c.MatchWord("LANGUAGE")) c.ExpectIdentifier();
            return "transform";
        }

        var (sc, nm) = ParseQualifiedName(c);
        if (kind is "TRIGGER" or "RULE" or "POLICY") { c.ExpectWord("ON"); ParseQualifiedName(c); }
        return sc is null ? nm : $"{sc}.{nm}";
    }

    // ---- shared helpers -----------------------------------------------------

    /// <summary>CREATE of a kind not finely modelled — validate the skeleton, capture name for the catalog.</summary>
    private SqlStatement ParseCreateGeneric(TokenCursor c)
    {
        c.MatchWord("UNIQUE");        // CREATE UNIQUE INDEX
        c.MatchWord("CONSTRAINT");    // CREATE CONSTRAINT TRIGGER
        var kind = ParseObjectKind(c);
        var node = new RawCreateStatement { ObjectKind = kind };

        switch (kind)
        {
            case "VIEW" or "MATERIALIZED VIEW" or "SEQUENCE" or "FOREIGN TABLE" or "TYPE" or "DOMAIN":
                c.MatchWords("IF", "NOT", "EXISTS");
                (node.Schema, node.Name) = ParseQualifiedName(c);
                ConsumeRest(c);
                return node;
            case "FUNCTION" or "PROCEDURE" or "AGGREGATE":
                c.MatchWords("IF", "NOT", "EXISTS");
                (node.Schema, node.Name) = ParseQualifiedName(c);
                if (c.AtSymbol('(')) c.SkipBalancedParens();
                ConsumeRest(c);
                return node;
            case "INDEX":
                c.MatchWord("CONCURRENTLY");
                c.MatchWords("IF", "NOT", "EXISTS");
                if (!c.AtWord("ON")) c.ExpectIdentifier();          // optional index name
                c.ExpectWord("ON");
                if (c.AtEnd) throw new ParseException("incomplete CREATE INDEX", c.Here);
                ConsumeRest(c);
                return node;
            default:
                if (c.AtEnd) throw new ParseException($"incomplete CREATE {kind}", c.Here);
                ConsumeRest(c);
                return node;
        }
    }

    /// <summary>Reads the (possibly multi-word) object kind after ALTER/DROP.</summary>
    private static string ParseObjectKind(TokenCursor c)
    {
        if (c.MatchWords("MATERIALIZED", "VIEW")) return "MATERIALIZED VIEW";
        if (c.MatchWords("FOREIGN", "TABLE")) return "FOREIGN TABLE";
        if (c.MatchWords("FOREIGN", "DATA", "WRAPPER")) return "FOREIGN DATA WRAPPER";
        if (c.MatchWords("USER", "MAPPING")) return "USER MAPPING";
        if (c.MatchWords("EVENT", "TRIGGER")) return "EVENT TRIGGER";
        if (c.MatchWords("DEFAULT", "PRIVILEGES")) return "DEFAULT PRIVILEGES";
        if (c.MatchWords("LARGE", "OBJECT")) return "LARGE OBJECT";
        if (c.MatchWords("ACCESS", "METHOD")) return "ACCESS METHOD";
        if (c.MatchWords("TEXT", "SEARCH", "CONFIGURATION")) return "TEXT SEARCH CONFIGURATION";
        if (c.MatchWords("TEXT", "SEARCH", "DICTIONARY")) return "TEXT SEARCH DICTIONARY";
        if (c.MatchWords("TEXT", "SEARCH", "PARSER")) return "TEXT SEARCH PARSER";
        if (c.MatchWords("TEXT", "SEARCH", "TEMPLATE")) return "TEXT SEARCH TEMPLATE";
        if (c.MatchWords("OPERATOR", "CLASS")) return "OPERATOR CLASS";
        if (c.MatchWords("OPERATOR", "FAMILY")) return "OPERATOR FAMILY";
        if (c.MatchWords("PROCEDURAL", "LANGUAGE")) return "LANGUAGE";   // CREATE/DROP PROCEDURAL LANGUAGE
        return c.ExpectIdentifier().ToUpperInvariant();    // TABLE/VIEW/INDEX/SEQUENCE/TYPE/DOMAIN/FUNCTION/…
    }

    /// <summary>Consume a name optionally followed by an argument-type list / operator args.</summary>
    private void ConsumeSignatureName(TokenCursor c)
    {
        // the name may be a qualified identifier OR an operator symbol; the signature is the "(types)"
        while (!c.AtEnd && !c.AtSymbol('(') && !c.AtSymbol(',')
               && !c.AtAnyWord("CASCADE", "RESTRICT", "RENAME", "OWNER", "SET", "DEPENDS")) c.Advance();
        if (c.AtSymbol('(')) c.SkipBalancedParens();
    }

    private static string? MatchCascadeRestrict(TokenCursor c)
        => c.MatchWord("CASCADE") ? "CASCADE" : (c.MatchWord("RESTRICT") ? "RESTRICT" : null);

    private void CaptureNonEmptyParens(TokenCursor c)
    {
        if (c.AtSymbol('(') && c.Peek()?.IsSymbol(')') == true) throw new ParseException("option list cannot be empty", c.Here);
        c.SkipBalancedParens();
    }

    private static void ExpectStringLit(TokenCursor c, string what)
    {
        if (c.Current is not { Kind: TokenKind.String }) throw new ParseException($"{what} must be a string literal", c.Here);
        c.Advance();
    }

    // ALTER TYPE actions: ADD VALUE / RENAME VALUE for enums; ADD|DROP|ALTER ATTRIBUTE (comma-separated)
    // for composites; plus RENAME TO / OWNER TO / SET SCHEMA / SET ( … ).
    private void ParseAlterTypeAction(TokenCursor c, AlterStatement alter)
    {
        if (c.AtWord("ADD") && c.Peek()?.IsWord("VALUE") == true)
        {
            c.Advance(); c.Advance();                            // ADD VALUE
            c.MatchWords("IF", "NOT", "EXISTS");
            ExpectStringLit(c, "the new enum label");
            if (c.MatchWord("BEFORE") || c.MatchWord("AFTER"))
            {
                if (c.AtAnyWord("BEFORE", "AFTER")) throw new ParseException("BEFORE and AFTER are mutually exclusive", c.Here);
                ExpectStringLit(c, "the neighbour enum label");
            }
            alter.Actions.Add("ADD VALUE");
            return;
        }
        if (c.MatchWord("RENAME"))
        {
            if (c.MatchWord("VALUE")) { ExpectStringLit(c, "the enum label"); c.ExpectWord("TO"); ExpectStringLit(c, "the new enum label"); alter.Actions.Add("RENAME VALUE"); return; }
            if (c.MatchWord("ATTRIBUTE")) { c.ExpectIdentifier(); c.ExpectWord("TO"); c.ExpectIdentifier(); MatchCascadeRestrict(c); alter.Actions.Add("RENAME ATTRIBUTE"); return; }
            c.ExpectWord("TO"); c.ExpectIdentifier(); alter.Actions.Add("RENAME"); return;   // RENAME TO new_name
        }
        if (c.MatchWords("OWNER", "TO")) { ParseRoleSpec(c); alter.Actions.Add("OWNER"); return; }
        if (c.MatchWords("SET", "SCHEMA")) { c.ExpectIdentifier(); alter.Actions.Add("SET SCHEMA"); return; }
        if (c.MatchWord("SET")) { if (c.AtSymbol('(')) c.SkipBalancedParens(); else ConsumeRest(c); alter.Actions.Add("SET"); return; }

        // attribute mutations — one or a comma-separated list
        if (c.AtAnyWord("ADD", "DROP", "ALTER"))
        {
            do { ParseAlterTypeAttribute(c); } while (c.MatchSymbol(','));
            alter.Actions.Add("ATTRIBUTE");
            return;
        }
        throw new ParseException("expected an ALTER TYPE action", c.Here);
    }

    private void ParseAlterTypeAttribute(TokenCursor c)
    {
        if (c.MatchWord("ADD"))
        {
            c.ExpectWord("ATTRIBUTE"); c.MatchWords("IF", "NOT", "EXISTS");
            c.ExpectIdentifier(); ParseCastType(c);
            if (c.MatchWord("COLLATE")) ParseQualifiedName(c);
            MatchCascadeRestrict(c); return;
        }
        if (c.MatchWord("DROP"))
        {
            c.ExpectWord("ATTRIBUTE"); c.MatchWords("IF", "EXISTS");
            c.ExpectIdentifier(); MatchCascadeRestrict(c); return;
        }
        c.ExpectWord("ALTER");
        c.ExpectWord("ATTRIBUTE"); c.ExpectIdentifier();
        c.MatchWords("SET", "DATA"); c.ExpectWord("TYPE"); ParseCastType(c);
        if (c.MatchWord("COLLATE")) ParseQualifiedName(c);
        MatchCascadeRestrict(c);
    }

    private static void ConsumeNameOrString(TokenCursor c)
    {
        if (c.Current is { Kind: TokenKind.String }) c.Advance();
        else c.ExpectIdentifier();
    }

    private static void ConsumeActionRest(TokenCursor c)
    {
        int depth = 0;
        while (!c.AtEnd)
        {
            if (c.AtSymbol('(') || c.AtSymbol('[')) depth++;
            else if (c.AtSymbol(')') || c.AtSymbol(']')) depth = Math.Max(0, depth - 1);
            else if (depth == 0 && c.AtSymbol(',')) break;
            c.Advance();
        }
    }

    private static CreateTableStatement MakeTableHolder() => new();
    private void ParseColumnInto(TokenCursor c, CreateTableStatement holder) => holder.Columns.Add(ParseColumnDef(c));
}
