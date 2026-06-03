using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace PgProj.Core.Tests;

/// <summary>
/// Aggregate corpus checks (the per-case TDD tests are generated under Corpus/*.g.cs).
/// Docker-free: only the parser runs here. The corpus is ground-truth-verified against postgres:18
/// by tools/pg-oracle.ps1; these tests measure how the parser fares against that spec.
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

        var rows = cases.Select(c => (c, v: CorpusData.Verdict(c.Sql))).ToList();

        int okParsed  = rows.Count(r => r.c.Expect == "ok"    && r.v == CorpusVerdict.Parsed);
        int okError   = rows.Count(r => r.c.Expect == "ok"    && r.v == CorpusVerdict.Error);
        int okEmpty   = rows.Count(r => r.c.Expect == "ok"    && r.v == CorpusVerdict.Empty);
        int errError  = rows.Count(r => r.c.Expect == "error" && r.v == CorpusVerdict.Error);
        int errParsed = rows.Count(r => r.c.Expect == "error" && r.v == CorpusVerdict.Parsed);
        int errEmpty  = rows.Count(r => r.c.Expect == "error" && r.v == CorpusVerdict.Empty);
        int okTotal   = rows.Count(r => r.c.Expect == "ok");
        int errTotal  = rows.Count(r => r.c.Expect == "error");

        _out.WriteLine($"corpus: {cases.Count} cases across {cases.Select(c => c.Category).Distinct().Count()} categories");
        _out.WriteLine($"  expect=ok    ({okTotal}): parsed={okParsed}  REJECTED(false)={okError}  unmodeled(empty)={okEmpty}");
        _out.WriteLine($"  expect=error ({errTotal}): caught={errError}  missed(parsed)={errParsed}  missed(empty)={errEmpty}");
        if (okTotal > 0)  _out.WriteLine($"  positive parse rate: {100.0 * okParsed / okTotal:F1}%");
        if (errTotal > 0) _out.WriteLine($"  negative catch rate:  {100.0 * errError / errTotal:F1}%");

        foreach (var g in rows.GroupBy(r => r.c.Category).OrderBy(g => g.Key))
        {
            int cok = g.Count(r => r.c.Expect == "ok");
            int cokp = g.Count(r => r.c.Expect == "ok" && r.v == CorpusVerdict.Parsed);
            int cer = g.Count(r => r.c.Expect == "error");
            int cerc = g.Count(r => r.c.Expect == "error" && r.v == CorpusVerdict.Error);
            _out.WriteLine($"  {g.Key,-32} ok {cokp,4}/{cok,-4}  err {cerc,4}/{cer,-4}");
        }
    }
}
