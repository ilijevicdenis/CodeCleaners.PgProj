using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PgProj.Core.Deployment;

/// <summary>
/// The Postgres analogue of SSDT's SQLCMD variables: <c>$(Name)</c> tokens resolved at publish time.
/// A resolver holds the final, merged variable map (project defaults overlaid by profile/CLI overrides,
/// see <see cref="Build"/>) and substitutes tokens in a body, erroring on any unresolved <c>$(X)</c>
/// with a <c>file:line</c> diagnostic so a deploy never silently emits an un-expanded token.
/// </summary>
/// <remarks>
/// Escaping: a literal <c>$(</c> in a script is written <c>$$(</c> — the doubled dollar collapses to a
/// single <c>$</c> and the following <c>(</c> is left verbatim (no substitution attempted). This mirrors
/// SQLCMD's own <c>$(</c> escaping convention and is the one documented/tested rule.
/// </remarks>
public sealed class SqlCmdVariableResolver
{
    private readonly IReadOnlyDictionary<string, string> _values;

    private SqlCmdVariableResolver(IReadOnlyDictionary<string, string> values) => _values = values;

    /// <summary>The fully-resolved variable map (ordered by name for deterministic banners).</summary>
    public IReadOnlyDictionary<string, string> Values => _values;

    public bool IsEmpty => _values.Count == 0;

    /// <summary>
    /// Merges variable sources by precedence (lowest first): project <paramref name="defaults"/> &lt;
    /// <paramref name="profile"/> (future publish-profile, currently always empty) &lt; CLI
    /// <paramref name="cliOverrides"/>. Names are case-insensitive (SQLCMD semantics).
    /// </summary>
    public static SqlCmdVariableResolver Build(
        IReadOnlyDictionary<string, string>? defaults = null,
        IReadOnlyDictionary<string, string>? profile = null,
        IReadOnlyDictionary<string, string>? cliOverrides = null)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var src in new[] { defaults, profile, cliOverrides })
        {
            if (src is null) continue;
            foreach (var kv in src) merged[kv.Key] = kv.Value;   // later source wins
        }
        // Re-key into a deterministically-ordered dictionary so banners are stable.
        var ordered = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in merged.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            ordered[kv.Key] = kv.Value;
        return new SqlCmdVariableResolver(ordered);
    }

    /// <summary>
    /// Substitutes every <c>$(Name)</c> token in <paramref name="body"/> with its resolved value.
    /// <paramref name="origin"/> is used only to build diagnostics (e.g. the script's file name).
    /// Throws <see cref="SqlCmdVariableException"/> on the first unresolved token, reporting its
    /// 1-based line and column within <paramref name="body"/>.
    /// </summary>
    public string Substitute(string body, string origin)
    {
        if (string.IsNullOrEmpty(body) || body.IndexOf('$') < 0) return body;

        var sb = new StringBuilder(body.Length);
        var line = 1;
        var col = 1;

        for (var i = 0; i < body.Length;)
        {
            var c = body[i];

            // Escape: "$$(" -> literal "$(" with no substitution of the following name.
            if (c == '$' && i + 2 < body.Length && body[i + 1] == '$' && body[i + 2] == '(')
            {
                sb.Append("$(");
                Advance(ref line, ref col, '$');
                Advance(ref line, ref col, '$');
                Advance(ref line, ref col, '(');
                i += 3;
                continue;
            }

            // Token start: "$(" ... ")".
            if (c == '$' && i + 1 < body.Length && body[i + 1] == '(')
            {
                var close = body.IndexOf(')', i + 2);
                var newline = body.IndexOf('\n', i + 2);
                // A token must close on the same line; an unterminated "$(" is an unresolved token.
                if (close < 0 || (newline >= 0 && newline < close))
                    throw new SqlCmdVariableException(
                        $"{origin}({line},{col}): unterminated variable token '$(' — expected a closing ')'.");

                var name = body.Substring(i + 2, close - (i + 2)).Trim();
                if (name.Length == 0 || !_values.TryGetValue(name, out var value))
                {
                    var known = _values.Count == 0 ? "(none declared)" : string.Join(", ", _values.Keys);
                    throw new SqlCmdVariableException(
                        $"{origin}({line},{col}): unresolved variable '$({name})'. " +
                        $"Declare it as a <SqlCmdVariable> or pass --var {name}=<value>. Known: {known}.");
                }

                sb.Append(value);
                // Advance position cursor over the whole "$(...)" span (no newlines inside it).
                for (var k = i; k <= close; k++) Advance(ref line, ref col, body[k]);
                i = close + 1;
                continue;
            }

            sb.Append(c);
            Advance(ref line, ref col, c);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>Renders the resolved map as deploy-script banner comment lines (no trailing newline).</summary>
    public IEnumerable<string> BannerLines()
    {
        if (_values.Count == 0)
        {
            yield return "-- SQLCMD variables: (none)";
            yield break;
        }
        yield return "-- SQLCMD variables:";
        foreach (var kv in _values)
            yield return $"--   {kv.Key} = {kv.Value}";
    }

    private static void Advance(ref int line, ref int col, char c)
    {
        if (c == '\n') { line++; col = 1; }
        else col++;
    }
}

/// <summary>Thrown when a <c>$(Var)</c> token cannot be resolved (carries a file:line:col diagnostic).</summary>
public sealed class SqlCmdVariableException : Exception
{
    public SqlCmdVariableException(string message) : base(message) { }
}
