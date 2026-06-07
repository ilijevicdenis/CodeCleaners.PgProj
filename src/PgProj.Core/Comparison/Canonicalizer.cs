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

    /// <summary>Collapse whitespace, trim, lower-case. The base normalizer everything else builds on.</summary>
    public static string NormalizeText(string s) =>
        Whitespace.Replace(s.Trim(), " ").ToLowerInvariant();

    /// <summary>Body comparison for verbatim objects: case-, whitespace-, punctuation-spacing-,
    /// dollar-tag-, literal-cast- and trailing-`;`-agnostic.</summary>
    public static string NormalizeBody(string s)
        => PunctSpace.Replace(LiteralCast.Replace(NormalizeText(DollarTag.Replace(s, "$$$$")), "$1"), "$1").TrimEnd(';', ' ');

    /// <summary>Raw single-statement DDL additionally ignores identifier quoting and the
    /// <c>IF NOT EXISTS</c> idempotency hint.</summary>
    public static string NormalizeRawBody(string s) =>
        Whitespace.Replace(IfNotExists.Replace(NormalizeBody(s.Replace("\"", "")), " "), " ").Trim();

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
