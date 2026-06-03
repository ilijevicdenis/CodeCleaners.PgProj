using System.Linq;
using PgProj.Core.Parsing;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// The assertion every generated per-case corpus test calls. A case passes when the parser does
/// the PostgreSQL-correct thing: an "ok" statement parses with zero diagnostics and yields at least
/// one statement; an "error" statement produces a diagnostic.
/// </summary>
public static class CorpusAssert
{
    public static void Parses(string sql, string expect)
    {
        var p = new AstParser();
        var script = p.Parse(sql);
        var parsed = p.Diagnostics.Count == 0 && script.Statements.Count > 0;

        if (expect == "ok")
        {
            if (parsed) return;
            var why = p.Diagnostics.Count > 0
                ? "parser rejected it: " + string.Join(" | ", p.Diagnostics)
                : "parser produced no statement (construct not modelled)";
            Assert.Fail($"expected a clean parse but {why}");
        }
        else // expect == "error"
        {
            if (p.Diagnostics.Count > 0) return;
            Assert.Fail("expected a parse error but the parser accepted it");
        }
    }
}
