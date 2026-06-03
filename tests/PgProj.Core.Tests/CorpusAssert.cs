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

    /// <summary>
    /// Verdict check via real PostgreSQL execution, for cases the static engine cannot decide without
    /// false positives. Only invoked when a database is configured (see <see cref="DbFactAttribute"/>).
    /// </summary>
    public static async System.Threading.Tasks.Task MatchesPostgres(string sql, string expect, bool solo)
    {
        var actual = await CorpusDb.ErrorsAsync(sql, solo) ? "error" : "ok";
        Assert.True(actual == expect, $"PostgreSQL verdict was '{actual}', expected '{expect}'");
    }
}
