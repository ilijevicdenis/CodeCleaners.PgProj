using System;
using System.Collections.Generic;
using PgProj.Core.Parsing;

namespace PgProj.Core.Syntax;

// Structured CREATE AGGREGATE. Validates the definition's option list (required SFUNC + STYPE, known
// keys, key = value form, no duplicates, valid PARALLEL / *_MODIFY values, flag options) so the
// malformed forms Postgres rejects are caught without a catalog. Function/type existence and
// signature compatibility remain for a future semantic pass.
public sealed partial class PgParser
{
    private static readonly HashSet<string> AggregateKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "SFUNC", "STYPE", "SSPACE", "FINALFUNC", "FINALFUNC_EXTRA", "FINALFUNC_MODIFY", "COMBINEFUNC",
        "SERIALFUNC", "DESERIALFUNC", "INITCOND", "MSFUNC", "MINVFUNC", "MSTYPE", "MSSPACE", "MFINALFUNC",
        "MFINALFUNC_EXTRA", "MFINALFUNC_MODIFY", "MINITCOND", "SORTOP", "PARALLEL", "HYPOTHETICAL", "BASETYPE",
    };
    private static readonly HashSet<string> AggregateFlagKeys = new(StringComparer.OrdinalIgnoreCase)
    { "FINALFUNC_EXTRA", "MFINALFUNC_EXTRA", "HYPOTHETICAL" };
    private static readonly HashSet<string> AggregateModifyValues = new(StringComparer.OrdinalIgnoreCase)
    { "READ_ONLY", "SHAREABLE", "READ_WRITE" };

    private SqlStatement ParseCreateAggregate(TokenCursor c)
    {
        var (s, n) = ParseQualifiedName(c);
        var node = new RawCreateStatement { ObjectKind = "AGGREGATE", Schema = s, Name = n };

        if (!c.AtSymbol('(')) throw new ParseException("expected '(' for the aggregate arguments", c.Here);
        var first = CaptureBalancedParens(c);

        // Old single-paren form puts "key = value" options directly after the name; the modern form has
        // an argument paren followed by a separate definition paren. A top-level '=' marks the old form.
        IReadOnlyList<Token> definition;
        if (HasTopLevelEquals(first)) definition = first;
        else
        {
            if (!c.AtSymbol('(')) throw new ParseException("expected '(' for the aggregate definition", c.Here);
            definition = CaptureBalancedParens(c);
        }
        ValidateAggregateDefinition(definition);
        ConsumeRest(c);
        return node;
    }

    private static bool HasTopLevelEquals(IReadOnlyList<Token> toks)
    {
        int depth = 0;
        foreach (var t in toks)
        {
            if (t.IsSymbol('(') || t.IsSymbol('[')) depth++;
            else if (t.IsSymbol(')') || t.IsSymbol(']')) depth--;
            else if (depth == 0 && t.IsSymbol('=')) return true;
        }
        return false;
    }

    private void ValidateAggregateDefinition(IReadOnlyList<Token> tokens)
    {
        var d = new TokenCursor(tokens);
        if (d.AtEnd) throw new ParseException("aggregate definition cannot be empty", d.Here);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        do
        {
            if (d.AtEnd) throw new ParseException("trailing comma in aggregate definition", d.Here);
            var key = d.ExpectIdentifier();
            if (!AggregateKeys.Contains(key)) throw new ParseException($"unknown aggregate option \"{key}\"", d.Here);
            seen.Add(key);   // Postgres permits duplicate options (last value wins) — not a syntax error

            if (AggregateFlagKeys.Contains(key))
            {
                if (!d.AtEnd && !d.AtSymbol(',')) throw new ParseException($"aggregate option \"{key}\" does not take a value", d.Here);
                continue;
            }
            if (!d.MatchOperator("=")) throw new ParseException($"expected '=' after aggregate option \"{key}\"", d.Here);
            if (d.AtEnd || d.AtSymbol(',')) throw new ParseException($"missing value for aggregate option \"{key}\"", d.Here);
            var val = CaptureToTopLevelComma(d);
            if (key.Equals("PARALLEL", StringComparison.OrdinalIgnoreCase) && !ParallelValues.Contains(val.Trim()))
                throw new ParseException($"invalid PARALLEL value \"{val.Trim()}\"", d.Here);
            if ((key.Equals("FINALFUNC_MODIFY", StringComparison.OrdinalIgnoreCase) || key.Equals("MFINALFUNC_MODIFY", StringComparison.OrdinalIgnoreCase))
                && !AggregateModifyValues.Contains(val.Trim()))
                throw new ParseException($"invalid {key.ToUpperInvariant()} value \"{val.Trim()}\"", d.Here);
        } while (d.MatchSymbol(','));

        if (!seen.Contains("SFUNC")) throw new ParseException("aggregate definition requires SFUNC", d.Here);
        if (!seen.Contains("STYPE")) throw new ParseException("aggregate definition requires STYPE", d.Here);
    }

    private static string CaptureToTopLevelComma(TokenCursor c)
    {
        int m = c.Mark(), depth = 0;
        while (!c.AtEnd)
        {
            var t = c.Current!.Value;
            if (depth == 0 && t.IsSymbol(',')) break;
            if (t.IsSymbol('(') || t.IsSymbol('[')) depth++;
            else if (t.IsSymbol(')') || t.IsSymbol(']')) depth--;
            c.Advance();
        }
        return c.RenderRange(m, c.Mark());
    }
}
