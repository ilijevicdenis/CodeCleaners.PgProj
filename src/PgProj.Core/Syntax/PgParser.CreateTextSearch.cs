using PgProj.Core.Parsing;

namespace PgProj.Core.Syntax;

// Structured CREATE TEXT SEARCH {CONFIGURATION|DICTIONARY|TEMPLATE|PARSER}. Validates the required
// name, the option-list shape (key = value, non-empty) and each subtype's required options, with zero
// false positives. Unknown option keys are allowed (dictionaries take template-specific options), and
// catalog-dependent errors (a parser/template/config that doesn't exist) are left for later.
public sealed partial class PgParser
{
    private SqlStatement ParseCreateTextSearch(TokenCursor c)
    {
        string kind;
        if (c.MatchWord("CONFIGURATION")) kind = "CONFIGURATION";
        else if (c.MatchWord("DICTIONARY")) kind = "DICTIONARY";
        else if (c.MatchWord("TEMPLATE")) kind = "TEMPLATE";
        else if (c.MatchWord("PARSER")) kind = "PARSER";
        else throw new ParseException("expected CONFIGURATION, DICTIONARY, TEMPLATE or PARSER", c.Here);

        var (s, n) = ParseQualifiedName(c);                       // name is required
        var node = new RawCreateStatement { ObjectKind = "TEXT SEARCH " + kind, Schema = s, Name = n };

        if (!c.AtSymbol('(')) throw new ParseException($"expected '(' for the text search {kind.ToLowerInvariant()} options", c.Here);
        var keys = ParseKeyValueOptions(c, null, "text search option", rejectDuplicates: false);   // PG allows duplicate options (last wins)

        switch (kind)
        {
            case "CONFIGURATION":
                bool hasParser = keys.Contains("PARSER"), hasCopy = keys.Contains("COPY");
                if (hasParser && hasCopy) throw new ParseException("text search configuration cannot specify both PARSER and COPY", c.Here);
                if (!hasParser && !hasCopy) throw new ParseException("text search configuration requires PARSER or COPY", c.Here);
                break;
            case "DICTIONARY":
                if (!keys.Contains("TEMPLATE")) throw new ParseException("text search dictionary requires a TEMPLATE", c.Here);
                break;
            case "TEMPLATE":
                if (!keys.Contains("LEXIZE")) throw new ParseException("text search template requires LEXIZE", c.Here);
                break;
            case "PARSER":
                foreach (var req in new[] { "START", "GETTOKEN", "END", "LEXTYPES" })
                    if (!keys.Contains(req)) throw new ParseException($"text search parser requires {req}", c.Here);
                break;
        }
        ConsumeRest(c);
        return node;
    }
}
