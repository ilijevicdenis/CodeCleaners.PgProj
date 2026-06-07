using System;
using System.Text;
using System.Text.RegularExpressions;

namespace PgProj.Core.Project;

/// <summary>
/// A compiled MSBuild-style glob for matching project-relative file paths (separators normalised to
/// <c>/</c>). Supports the three wildcards SSDT/.sqlproj item globs use:
/// <list type="bullet">
/// <item><c>**</c> — any number of path segments (including none); <c>**/*.sql</c> matches a file in
/// the project root <em>and</em> at any depth.</item>
/// <item><c>*</c> — any run of characters within a single segment (does not cross <c>/</c>).</item>
/// <item><c>?</c> — exactly one character within a single segment.</item>
/// </list>
/// Matching is case-insensitive (Postgres folds identifiers; project files are matched the same way the
/// loader compares paths). This replaces the old "<c>**</c> ⇒ AllDirectories, single <c>*</c> ⇒
/// TopDirectoryOnly" heuristic with real glob semantics, and is what powers <c>&lt;Exclude&gt;</c>/<c>Remove</c>.
/// </summary>
public sealed class GlobMatcher
{
    private readonly Regex _regex;

    private GlobMatcher(Regex regex) => _regex = regex;

    public bool IsMatch(string relativePath) => _regex.IsMatch(relativePath.Replace('\\', '/'));

    /// <summary>Compiles a glob pattern (any directory separator) into a matcher.</summary>
    public static GlobMatcher Compile(string pattern)
    {
        var glob = pattern.Replace('\\', '/').Trim();
        var sb = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        // "**" → any number of segments. Swallow a following "/" so "**/x" also matches
                        // "x" at the root (the slash after ** is optional).
                        i++;
                        if (i + 1 < glob.Length && glob[i + 1] == '/') i++;
                        sb.Append("(?:.*/)?");
                    }
                    else
                    {
                        sb.Append("[^/]*"); // single * stays within a segment
                    }
                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
        sb.Append('$');
        return new GlobMatcher(new Regex(sb.ToString(),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled));
    }
}
