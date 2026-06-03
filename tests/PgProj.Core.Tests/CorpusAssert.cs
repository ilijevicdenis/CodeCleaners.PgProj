using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// The assertion every generated per-case corpus test calls. A case passes when the toolchain does
/// the PostgreSQL-correct thing — see <see cref="CorpusData.Evaluate"/>, which combines the
/// hand-written PgParser (with legacy fallback) and the semantic analyzer. An "ok" case must parse
/// and analyse cleanly; an "error" case must be rejected by the parser or the analyzer.
/// </summary>
public static class CorpusAssert
{
    public static void Parses(string sql, string expect)
    {
        var (parsedClean, hasError) = CorpusData.Evaluate(sql);
        if (expect == "ok")
        {
            if (parsedClean) return;
            Assert.Fail("expected a clean parse + analysis, but the statement was rejected");
        }
        else
        {
            if (hasError) return;
            Assert.Fail("expected a parse or semantic error, but the statement was accepted");
        }
    }
}
