using System.Collections.Generic;
using PgProj.Core.Parsing;

namespace PgProj.Core.Syntax;

// Structured CREATE TRIGGER. The full grammar is validated so the malformed forms Postgres rejects
// are caught without a catalog, including the cross-field rules (INSTEAD OF ⇒ row-level, no TRUNCATE,
// no transition tables; TRUNCATE ⇒ statement-level; transition tables ⇒ AFTER and no column list).
// Catalog-dependent errors (table vs view for INSTEAD OF, missing function) are left for later.
public sealed partial class PgParser
{
    private SqlStatement ParseCreateTrigger(TokenCursor c, bool constraint)
    {
        var node = new RawCreateStatement { ObjectKind = "TRIGGER" };

        if (c.AtAnyWord("BEFORE", "AFTER", "INSTEAD")) throw new ParseException("missing trigger name", c.Here);
        node.Name = c.ExpectIdentifier();

        string timing;
        if (c.MatchWord("BEFORE")) timing = "BEFORE";
        else if (c.MatchWord("AFTER")) timing = "AFTER";
        else if (c.MatchWords("INSTEAD", "OF")) timing = "INSTEAD OF";
        else throw new ParseException("expected BEFORE, AFTER or INSTEAD OF", c.Here);

        var events = new List<string>();
        bool updateOfColumns = false;
        do
        {
            if (c.MatchWord("INSERT")) events.Add("INSERT");
            else if (c.MatchWord("DELETE")) events.Add("DELETE");
            else if (c.MatchWord("TRUNCATE")) events.Add("TRUNCATE");
            else if (c.MatchWord("UPDATE")) { events.Add("UPDATE"); if (c.MatchWord("OF")) { updateOfColumns = true; do { c.ExpectIdentifier(); } while (c.MatchSymbol(',')); } }
            else throw new ParseException("expected INSERT, UPDATE, DELETE or TRUNCATE", c.Here);
        } while (c.MatchWord("OR"));

        c.ExpectWord("ON");
        ParseQualifiedName(c);

        if (constraint)                                  // CONSTRAINT trigger: optional FROM table, deferrability
        {
            if (c.MatchWord("FROM")) ParseQualifiedName(c);
            if (c.MatchWord("NOT")) c.ExpectWord("DEFERRABLE"); else c.MatchWord("DEFERRABLE");
            if (c.MatchWord("INITIALLY")) { if (!c.MatchWord("DEFERRED")) c.ExpectWord("IMMEDIATE"); }
        }

        bool hasOld = false, hasNew = false, hasReferencing = false;
        if (c.MatchWord("REFERENCING"))
        {
            hasReferencing = true;
            do
            {
                bool isNew = c.MatchWord("NEW");
                if (!isNew) c.ExpectWord("OLD");
                c.ExpectWord("TABLE"); c.MatchWord("AS"); c.ExpectIdentifier();
                if (isNew) { if (hasNew) throw new ParseException("NEW TABLE specified more than once", c.Here); hasNew = true; }
                else { if (hasOld) throw new ParseException("OLD TABLE specified more than once", c.Here); hasOld = true; }
            } while (c.AtAnyWord("OLD", "NEW"));
        }

        string frequency = "STATEMENT";
        if (c.MatchWord("FOR"))
        {
            c.MatchWord("EACH");
            if (c.MatchWord("ROW")) frequency = "ROW";
            else if (c.MatchWord("STATEMENT")) frequency = "STATEMENT";
            else throw new ParseException("expected ROW or STATEMENT", c.Here);
        }

        if (c.MatchWord("WHEN")) { if (!c.AtSymbol('(')) throw new ParseException("expected '(' after WHEN", c.Here); CaptureBalancedParens(c); }

        c.ExpectWord("EXECUTE");
        if (!c.MatchWord("FUNCTION")) c.ExpectWord("PROCEDURE");
        ParseQualifiedName(c);
        if (!c.AtSymbol('(')) throw new ParseException("expected '(' for the trigger function arguments", c.Here);
        CaptureBalancedParens(c);

        // cross-field rules (no catalog needed)
        if (timing == "INSTEAD OF")
        {
            if (events.Contains("TRUNCATE")) throw new ParseException("INSTEAD OF triggers do not support TRUNCATE", c.Here);
            if (updateOfColumns) throw new ParseException("INSTEAD OF triggers cannot specify a column list", c.Here);
            if (frequency != "ROW") throw new ParseException("INSTEAD OF triggers must be FOR EACH ROW", c.Here);
            if (hasReferencing) throw new ParseException("INSTEAD OF triggers cannot have transition tables", c.Here);
        }
        if (events.Contains("TRUNCATE") && frequency == "ROW") throw new ParseException("TRUNCATE triggers cannot be FOR EACH ROW", c.Here);
        if (hasReferencing && timing != "AFTER") throw new ParseException("transition tables may only be specified for AFTER triggers", c.Here);
        if (hasReferencing && updateOfColumns) throw new ParseException("transition tables cannot be used with a column list", c.Here);

        return node;
    }
}
