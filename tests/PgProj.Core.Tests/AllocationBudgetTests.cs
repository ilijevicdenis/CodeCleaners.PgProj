using System;
using System.Linq;
using System.Text;
using PgProj.Core.Parsing;
using PgProj.Core.Syntax;
using Xunit;
using Xunit.Abstractions;

namespace PgProj.Core.Tests;

/// <summary>
/// Turns the CLAUDE.md perf gate ("no merge without a measured allocation delta") from a manual
/// discipline into a failing local test (audit Rec 4). It runs entirely in-process under <c>dotnet test</c>
/// — NO benchmark run, NO GitHub CI — honoring the project's local-validation rule.
///
/// <para>Allocation here is measured with <see cref="GC.GetAllocatedBytesForCurrentThread"/>, which is
/// EXACT and deterministic for a given input + code (independent of machine, GC mode, or timing). So the
/// budgets can be tight. They are expressed <b>per input char</b>, not as an absolute, so the corpus can
/// keep growing (it is append-only) without retuning — only a real per-op regression trips them. When a
/// change legitimately lowers allocation, ratchet the ceiling DOWN in the same commit; never silently up.</para>
///
/// <para>This is the steady-state, warm-pool path (parse → build → release, as <c>DatabaseProject</c> runs
/// per file). The exact bytes/char headroom is generous enough to tolerate corpus-mix drift but will catch
/// a gross regression (a re-introduced per-token Substring, a lost pooling path, an eager render).</para>
/// </summary>
public class AllocationBudgetTests
{
    private readonly ITestOutputHelper _out;
    public AllocationBudgetTests(ITestOutputHelper output) => _out = output;

    // Measured warm baselines on the current corpus (2026-06-07): tokenize+merge ≈ 1.80 B/char,
    // parse+build+release ≈ 14.6 B/char. Ceilings carry headroom for corpus-mix drift; tighten on a win.
    private const double TokenizeBytesPerCharCeiling = 3.0;
    private const double ParseBuildBytesPerCharCeiling = 20.0;

    [Fact]
    public void TokenizeAndMerge_stays_under_allocation_budget()
    {
        var sql = CorpusAll();
        if (sql.Length < 50_000) { _out.WriteLine($"corpus too small ({sql.Length} chars) — skipping"); return; }

        double perChar = MeasureBytesPerChar(sql, s =>
        {
            var t = OperatorLexer.MergeInPlace(Tokenizer.TokenizePooled(s));
            int n = t.Count;
            t.Return();
            return n;
        });

        _out.WriteLine($"tokenize+merge: {perChar:F3} B/char over {sql.Length:N0} chars (ceiling {TokenizeBytesPerCharCeiling})");
        Assert.True(perChar <= TokenizeBytesPerCharCeiling,
            $"tokenize allocation {perChar:F3} B/char exceeds budget {TokenizeBytesPerCharCeiling} B/char — investigate before merging");
    }

    [Fact]
    public void ParseBuildRelease_stays_under_allocation_budget()
    {
        var sql = CorpusAll();
        if (sql.Length < 50_000) { _out.WriteLine($"corpus too small ({sql.Length} chars) — skipping"); return; }

        double perChar = MeasureBytesPerChar(sql, s =>
        {
            var parsed = new PgParser().Parse(s);
            int n = new ModelBuilder("public").Build(parsed).Tables.Count;
            parsed.ReleaseTokens();
            return n;
        });

        _out.WriteLine($"parse+build+release: {perChar:F3} B/char over {sql.Length:N0} chars (ceiling {ParseBuildBytesPerCharCeiling})");
        Assert.True(perChar <= ParseBuildBytesPerCharCeiling,
            $"parse+build allocation {perChar:F3} B/char exceeds budget {ParseBuildBytesPerCharCeiling} B/char — investigate before merging");
    }

    // Warm (JIT + settle the ArrayPool), force a clean slate, then measure exact thread allocations / op.
    private static double MeasureBytesPerChar(string sql, Func<string, int> op)
    {
        for (int i = 0; i < 20; i++) op(sql);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

        const int iters = 30;
        long before = GC.GetAllocatedBytesForCurrentThread();
        long sink = 0;
        for (int i = 0; i < iters; i++) sink += op(sql);
        long after = GC.GetAllocatedBytesForCurrentThread();
        GC.KeepAlive(sink);

        double bytesPerOp = (after - before) / (double)iters;
        return bytesPerOp / sql.Length;
    }

    /// <summary>The valid (expect=ok) corpus statements joined into one realistic multi-statement file —
    /// the same shape the benchmarks' "All" bucket uses, assembled from the test project's corpus loader.</summary>
    private static string CorpusAll()
    {
        var sb = new StringBuilder();
        foreach (var c in CorpusData.LoadAll())
            if (c.Expect == "ok") sb.Append(c.Sql).Append(";\n");
        return sb.ToString();
    }
}
