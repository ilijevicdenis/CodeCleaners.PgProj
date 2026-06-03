using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace PgProj.Core.Tests;

/// <summary>
/// Aggregate corpus checks (the per-case TDD tests are generated under Corpus/*.g.cs). Docker-free:
/// only the hand-written parser + semantic analyzer run here, measured against the corpus spec.
/// </summary>
public class CorpusTests
{
    private readonly ITestOutputHelper _out;
    public CorpusTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Corpus_files_are_wellformed_with_unique_ids()
    {
        var cases = CorpusData.LoadAll();
        var dupes = cases.GroupBy(c => c.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(dupes.Count == 0, $"duplicate corpus ids: {string.Join(", ", dupes.Take(20))}");
    }

    [Fact]
    public void Coverage_report()
    {
        var cases = CorpusData.LoadAll();
        if (cases.Count == 0) { _out.WriteLine("corpus empty"); return; }

        var rows = cases.Select(c => (c, pass: CorpusData.Passes(c))).ToList();
        int total = rows.Count;
        int pass = rows.Count(r => r.pass);
        int okTotal = rows.Count(r => r.c.Expect == "ok");
        int okPass = rows.Count(r => r.c.Expect == "ok" && r.pass);
        int errTotal = rows.Count(r => r.c.Expect == "error");
        int errPass = rows.Count(r => r.c.Expect == "error" && r.pass);

        _out.WriteLine($"corpus: {pass}/{total} ({100.0 * pass / total:F1}%) handled correctly");
        _out.WriteLine($"  valid SQL accepted:   {okPass}/{okTotal} ({100.0 * okPass / okTotal:F1}%)");
        _out.WriteLine($"  invalid SQL rejected: {errPass}/{errTotal} ({100.0 * errPass / errTotal:F1}%)");

        foreach (var g in rows.GroupBy(r => r.c.Category).OrderBy(g => g.Key))
            _out.WriteLine($"  {g.Key,-32} {g.Count(r => r.pass),4}/{g.Count(),-4}");
    }
}
