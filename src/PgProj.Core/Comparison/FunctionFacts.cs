using System;
using System.Linq;
using System.Text.RegularExpressions;
using PgProj.Core.Model;

namespace PgProj.Core.Comparison;

/// <summary>
/// Reads the few function attributes the structured diff (issue #53) needs out of a
/// <see cref="FunctionDefinition"/> whose only modelled state is the full CREATE body. Today that's the
/// volatility class (IMMUTABLE / STABLE / VOLATILE) and a best-effort argument-type list for the
/// <c>ALTER FUNCTION …(args)</c> target. The function model is intentionally body-verbatim, so these are
/// lightweight regex probes over the canonicalized body — never a full re-parse.
/// </summary>
public static class FunctionFacts
{
    // Word-bounded so "IMMUTABLE" in a comment-free canonical body matches but a column named e.g.
    // "stable_id" does not. The canonical body is already lower-cased (NormalizeBody), but we probe the
    // raw body case-insensitively so callers can use either.
    private static readonly Regex Immutable = new(@"\bimmutable\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Stable    = new(@"\bstable\b",    RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Volatile  = new(@"\bvolatile\b",  RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The volatility class declared in the function body. Postgres defaults to VOLATILE when none is
    /// stated, so an absent keyword reports <see cref="FunctionVolatility.Volatile"/> — that way a source
    /// that drops an explicit STABLE and a catalog reconstruction that omits the default both converge.
    /// </summary>
    public static FunctionVolatility Volatility(FunctionDefinition f) => VolatilityOf(f.Body);

    public static FunctionVolatility VolatilityOf(string body)
    {
        if (string.IsNullOrEmpty(body)) return FunctionVolatility.Unknown;
        if (Immutable.IsMatch(body)) return FunctionVolatility.Immutable;
        if (Stable.IsMatch(body)) return FunctionVolatility.Stable;
        if (Volatile.IsMatch(body)) return FunctionVolatility.Volatile;
        return FunctionVolatility.Volatile; // Postgres default when unspecified
    }

    /// <summary>
    /// The function body with the volatility keyword neutralised, so two bodies that differ ONLY in
    /// volatility canonicalize equal. This lets the comparer say "same logic, just an attribute moved"
    /// and route the change to <see cref="AlterFunctionAttributesChange"/> instead of a blunt replace.
    /// </summary>
    public static string BodyWithoutVolatility(string body)
    {
        if (string.IsNullOrEmpty(body)) return string.Empty;
        var s = Immutable.Replace(body, " ");
        s = Stable.Replace(s, " ");
        s = Volatile.Replace(s, " ");
        return s;
    }

    /// <summary>
    /// Best-effort bare argument-type list for the <c>ALTER FUNCTION name(args)</c> target. Prefers the
    /// modelled <see cref="FunctionDefinition.ArgTypes"/>; falls back to "" (Postgres accepts an empty
    /// parameter list for a no-arg function, and ALTER resolves a unique name when unambiguous).
    /// </summary>
    public static string ArgTypeList(FunctionDefinition f)
    {
        if (!string.IsNullOrWhiteSpace(f.ArgTypes)) return f.ArgTypes.Trim();
        return string.Empty;
    }
}
