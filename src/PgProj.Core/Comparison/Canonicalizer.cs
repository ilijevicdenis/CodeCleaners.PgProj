using System;
using System.Text.RegularExpressions;

namespace PgProj.Core.Comparison;

/// <summary>
/// The single source of truth for reducing a piece of SQL (a view/function body, a raw object's
/// DDL, an expression, a default) to its <em>canonical form</em> — the meaning-preserving spelling
/// that is insensitive to whitespace, comment-free reformatting, punctuation spacing, dollar-quote
/// tags, literal casts and trailing semicolons.
/// <para>
/// This logic used to live as private statics on <see cref="SchemaComparer"/>; it was lifted here so
/// the <c>Model/Identity/CanonicalHash</c> derivation (issue #42, Phase 9) hashes byte-for-byte the
/// same canonical text the comparer diffs on. Keeping one implementation is what guarantees
/// "CanonicalHash unchanged ⇔ comparer sees no body diff".
/// </para>
/// <para>
/// NOTE: this is the Phase-9 canonical basis built on the comparer's existing normalizers. Full
/// Phase-8 canonical-model hardening (issue #51) — a parse-and-reprint canonical AST that also
/// normalises keyword case, alias forms and argument ordering — will later refine
/// <see cref="NormalizeBody"/>/<see cref="NormalizeRawBody"/>; CanonicalHash inherits that for free.
/// </para>
/// </summary>
public static class Canonicalizer
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    // Canonicalize dollar-quote tags ($function$ -> $$) so a hand-written function body matches the
    // catalog's pg_get_functiondef rendering, which picks its own tag.
    private static readonly Regex DollarTag = new(@"\$[A-Za-z0-9_]*\$", RegexOptions.Compiled);

    // pg_get_viewdef adds a result-type cast to literals (0 -> 0::bigint). Strip casts on numeric/
    // string LITERALS only (not column/expression casts), so a view round-trips with zero diff.
    private static readonly Regex LiteralCast = new(@"(\b\d+(?:\.\d+)?|'[^']*')::[a-z0-9_]+", RegexOptions.Compiled);

    // Reconcile punctuation spacing: our Token.Render is tight ("a,b" / "x=y") while pg_get_viewdef
    // is spaced ("a, b" / "x = y"). A space is only meaningful between two word characters.
    private static readonly Regex PunctSpace = new(@"\s*([^\w\s])\s*", RegexOptions.Compiled);

    // `IF NOT EXISTS` is an idempotency hint on CREATE, not part of the object's definition.
    private static readonly Regex IfNotExists = new(@"\bif\s+not\s+exists\b", RegexOptions.Compiled);

    // Strip explicit casts so a project default ('active') matches the catalog's
    // ('active'::character varying).
    private static readonly Regex CastSuffix = new(@"::\s*""?[A-Za-z][A-Za-z0-9_ ]*""?(\[\])?", RegexOptions.Compiled);

    /// <summary>Collapse whitespace, trim, and lower-case OUTSIDE string literals. The base normalizer
    /// everything else builds on. The content of <c>'…'</c> literals (<c>''</c> doubling respected) keeps
    /// its case: <c>'ACTIVE'</c> and <c>'active'</c> are different VALUES, and the old whole-string
    /// lowercase made a case-only literal edit in a default/CHECK/view/function invisible to schema
    /// compare — a silent no-deploy (audit P1).</summary>
    public static string NormalizeText(string s)
    {
        var t = Whitespace.Replace(s.Trim(), " ");
        if (t.IndexOf('\'') < 0) return t.ToLowerInvariant();   // fast path: no literal anywhere

        var buf = new System.Text.StringBuilder(t.Length);
        var inLiteral = false;
        for (var i = 0; i < t.Length; i++)
        {
            var ch = t[i];
            if (ch == '\'')
            {
                if (inLiteral && i + 1 < t.Length && t[i + 1] == '\'') { buf.Append("''"); i++; continue; }
                inLiteral = !inLiteral;
                buf.Append(ch);
                continue;
            }
            buf.Append(inLiteral ? ch : char.ToLowerInvariant(ch));
        }
        return buf.ToString();
    }

    /// <summary>Body comparison for verbatim objects: case-, whitespace-, punctuation-spacing-,
    /// dollar-tag-, literal-cast- and trailing-`;`-agnostic.</summary>
    public static string NormalizeBody(string s)
        => PunctSpace.Replace(LiteralCast.Replace(NormalizeText(DollarTag.Replace(s, "$$$$")), "$1"), "$1").TrimEnd(';', ' ');

    /// <summary>Raw single-statement DDL additionally ignores identifier quoting and the
    /// <c>IF NOT EXISTS</c> idempotency hint.</summary>
    public static string NormalizeRawBody(string s) =>
        Whitespace.Replace(IfNotExists.Replace(NormalizeBody(s.Replace("\"", "")), " "), " ").Trim();

    /// <summary>
    /// Canonical form of a TRIGGER definition for round-trip comparison (issue #61). Builds on
    /// <see cref="NormalizeRawBody"/> and additionally reconciles the two ways the same trigger is spelled
    /// by a hand-written source vs <c>pg_get_triggerdef</c>:
    /// <list type="bullet">
    ///   <item><c>EXECUTE PROCEDURE</c> ⇄ <c>EXECUTE FUNCTION</c> (exact synonyms since PG11);</item>
    ///   <item>the redundant extra parens the catalog wraps the WHEN predicate in
    ///     (<c>WHEN ((a IS DISTINCT FROM b))</c> vs source <c>WHEN (a IS DISTINCT FROM b)</c>).</item>
    /// </list>
    /// A genuine body change (different timing/event/predicate/function) still produces a different form, so
    /// a changed trigger continues to diff.
    /// </summary>
    public static string NormalizeTriggerBody(string s)
    {
        var b = NormalizeRawBody(s).Replace("execute procedure", "execute function");
        b = CanonicalizeTriggerEvents(b);
        return CollapseWhenDoubleParens(b);
    }

    // pg_get_triggerdef renders the fired events in a fixed catalog order (insert, delete, update, truncate)
    // regardless of how the source wrote them, so an unchanged "AFTER INSERT OR UPDATE OR DELETE" would
    // otherwise phantom-diff against the catalog's "insert or delete or update". Sort the OR-separated event
    // list (the segment between the timing keyword and the " on <table>") into a stable order on both sides.
    private static string CanonicalizeTriggerEvents(string b)
    {
        var evStart = -1;
        foreach (var t in new[] { "instead of ", "after ", "before " })
        {
            var i = b.IndexOf(t, StringComparison.Ordinal);
            if (i >= 0) { evStart = i + t.Length; break; }
        }
        if (evStart < 0) return b;
        var onIdx = b.IndexOf(" on ", evStart, StringComparison.Ordinal);
        if (onIdx < 0) return b;
        var events = b.Substring(evStart, onIdx - evStart).Split(" or ");
        if (events.Length < 2) return b;
        for (var i = 0; i < events.Length; i++) events[i] = events[i].Trim();
        Array.Sort(events, StringComparer.Ordinal);
        return b[..evStart] + string.Join(" or ", events) + b[onIdx..];
    }

    // Peel ONE redundant balanced paren pair immediately following `when(` so `when((expr))` ≡ `when(expr)`.
    // Only the pair whose inner content is itself fully parenthesised is removed (the exact catalog shape);
    // a single-paren `when(expr)` and any non-enclosing form is left untouched.
    private static string CollapseWhenDoubleParens(string b)
    {
        const string marker = "when(";
        var idx = b.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return b;
        var open = idx + marker.Length - 1; // index of the first '(' after `when`
        if (open + 1 >= b.Length || b[open + 1] != '(') return b; // not a double-paren
        // Find the matching close for the OUTER '(' at `open`.
        var depth = 0; var outerClose = -1;
        for (var i = open; i < b.Length; i++)
        {
            if (b[i] == '(') depth++;
            else if (b[i] == ')') { depth--; if (depth == 0) { outerClose = i; break; } }
        }
        if (outerClose < 0) return b;
        // The char just before the outer close must be ')' (the inner pair's close) for this to be a
        // genuine double-wrap; and the inner '(' at open+1 must match that inner ')'.
        if (b[outerClose - 1] != ')') return b;
        // Verify the inner pair is balanced and encloses everything between (open+1 .. outerClose-1).
        depth = 0; var innerOk = true;
        for (var i = open + 1; i < outerClose; i++)
        {
            if (b[i] == '(') depth++;
            else if (b[i] == ')') { depth--; if (depth == 0 && i != outerClose - 1) { innerOk = false; break; } }
        }
        if (!innerOk || depth != 0) return b;
        // Remove the outer pair: keep [.. open] + inner content (open+1 .. outerClose-1) + [outerClose+1 ..].
        return b[..open] + "(" + b[(open + 2)..(outerClose - 1)] + ")" + b[(outerClose + 1)..];
    }

    /// <summary>Canonicalize a column/expression default: drop explicit casts, then <see cref="NormalizeText"/>.</summary>
    public static string NormalizeDefault(string? d) =>
        string.IsNullOrWhiteSpace(d) ? string.Empty : NormalizeText(CastSuffix.Replace(d, string.Empty));

    /// <summary>
    /// Canonical form of a scalar SQL EXPRESSION (a column/expression default or a CHECK predicate),
    /// for the semantic CanonicalHash (issue #51). Beyond <see cref="NormalizeDefault"/> it additionally:
    /// <list type="bullet">
    /// <item>strips a <em>redundant balanced outer paren pair</em> repeatedly, so <c>a&gt;0</c>,
    ///   <c>(a&gt;0)</c> and <c>((a&gt;0))</c> collapse to one form;</item>
    /// <item>normalises punctuation spacing (<c>a &gt; 0</c> ⇒ <c>a&gt;0</c>) via the same
    ///   <see cref="PunctSpace"/> rule the body normalizer uses.</item>
    /// </list>
    /// Deterministic and idempotent: only an <em>enclosing</em> pair whose match is the final char is
    /// removed (never <c>(a) + (b)</c>, whose first '(' closes mid-string), so meaning is preserved.
    /// <para>NOTE: this is intentionally NOT wired into <see cref="SchemaComparer"/> — the comparer's
    /// verdicts (and the golden deploy script / model JSON) stay byte-identical. It feeds only the
    /// model's canonical-form accessors / CanonicalHash, where <c>a&gt;0</c> ≡ <c>(a&gt;0)</c> is desired.</para>
    /// </summary>
    public static string NormalizeExpression(string? e)
    {
        if (string.IsNullOrWhiteSpace(e)) return string.Empty;
        // Reuse the default pipeline (cast-strip + whitespace/case) then tighten punctuation spacing and
        // peel redundant outer parens. Order matters: strip parens AFTER spacing so " ( a > 0 ) " trims.
        var s = PunctSpace.Replace(NormalizeDefault(e), "$1");
        return StripRedundantOuterParens(s);
    }

    // Remove an enclosing '(' ... ')' pair iff the '(' at index 0 is matched by the ')' at the last
    // index (i.e. the whole string is parenthesised). Repeats for nested redundant pairs. A string like
    // "(a)>(b)" is left untouched because the opening paren closes before the end.
    private static string StripRedundantOuterParens(string s)
    {
        while (s.Length >= 2 && s[0] == '(' && s[^1] == ')')
        {
            var depth = 0;
            var enclosing = true;
            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] == '(') depth++;
                else if (s[i] == ')')
                {
                    depth--;
                    // Depth hit 0 before the final char ⇒ the leading '(' is NOT the outermost wrapper.
                    if (depth == 0 && i < s.Length - 1) { enclosing = false; break; }
                }
            }
            if (!enclosing || depth != 0) break;
            s = s[1..^1];
        }
        return s;
    }
}
