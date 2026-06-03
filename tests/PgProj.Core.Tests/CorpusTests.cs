using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PgProj.Core.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace PgProj.Core.Tests;

/// <summary>
/// Drives the PostgreSQL test corpus (tests/corpus/*.jsonl) through the parser.
///
/// The corpus is ground-truth-verified against postgres:18 by tools/pg-oracle.ps1 (the `expect`
/// field is what real PostgreSQL does). These tests measure how the pgproj parser fares against
/// that spec and gate regressions. They are docker-free: only the parser runs here.
///
/// Verdict of the parser on a case:
///   parsed - produced >=1 statement and zero diagnostics
///   error  - produced >=1 diagnostic (a parse rejection)
///   empty  - recognized nothing (no statement, no diagnostic) -- an unmodeled construct
///
/// Regression gate: a case with expect=ok that the parser rejects (verdict=error) is a FALSE
/// REJECTION -- the parser choked on valid PostgreSQL. Known false rejections are listed in
/// tests/corpus/_baseline.json; any NEW one fails the build. Regenerate the baseline with
/// PGPROJ_CORPUS_WRITE_BASELINE=1 after intentionally accepting the current parser behavior.
/// </summary>
public class CorpusTests
{
    private readonly ITestOutputHelper _out;
    public CorpusTests(ITestOutputHelper output) => _out = output;

    public sealed record Case(string Id, string Category, string Sql, string Expect, string? Ref, string? Note, string? Txn);

    private enum Verdict { Parsed, Error, Empty }

    private static string CorpusDir => _corpusDir.Value;
    private static readonly Lazy<string> _corpusDir = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PgProj.slnx")))
            dir = dir.Parent;
        if (dir is null) throw new DirectoryNotFoundException("repo root (PgProj.slnx) not found");
        return Path.Combine(dir.FullName, "tests", "corpus");
    });

    private static IReadOnlyList<Case> LoadAll()
    {
        var cases = new List<Case>();
        if (!Directory.Exists(CorpusDir)) return cases;
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        foreach (var file in Directory.EnumerateFiles(CorpusDir, "*.jsonl")
                     .Where(f => !Path.GetFileName(f).StartsWith('_'))
                     .OrderBy(f => f))
        {
            int n = 0;
            foreach (var raw in File.ReadLines(file))
            {
                n++;
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//")) continue;
                Case? c;
                try { c = JsonSerializer.Deserialize<Case>(line, opts); }
                catch (JsonException ex)
                {
                    throw new FormatException($"{Path.GetFileName(file)}:{n}: invalid JSON: {ex.Message}");
                }
                if (c is null || string.IsNullOrWhiteSpace(c.Id) || string.IsNullOrWhiteSpace(c.Sql)
                    || string.IsNullOrWhiteSpace(c.Expect))
                    throw new FormatException($"{Path.GetFileName(file)}:{n}: case needs id, sql, expect");
                if (c.Expect is not ("ok" or "error"))
                    throw new FormatException($"{Path.GetFileName(file)}:{n}: expect must be ok|error (got {c.Expect})");
                cases.Add(c);
            }
        }
        return cases;
    }

    private static Verdict Parse(string sql)
    {
        var p = new AstParser();
        var script = p.Parse(sql);
        if (p.Diagnostics.Count > 0) return Verdict.Error;
        return script.Statements.Count > 0 ? Verdict.Parsed : Verdict.Empty;
    }

    private static string BaselinePath => Path.Combine(CorpusDir, "_baseline.json");

    private static HashSet<string> LoadBaseline()
    {
        if (!File.Exists(BaselinePath)) return new HashSet<string>(StringComparer.Ordinal);
        var ids = JsonSerializer.Deserialize<string[]>(File.ReadAllText(BaselinePath)) ?? Array.Empty<string>();
        return new HashSet<string>(ids, StringComparer.Ordinal);
    }

    [Fact]
    public void Corpus_files_are_wellformed_with_unique_ids()
    {
        var cases = LoadAll();
        var dupes = cases.GroupBy(c => c.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(dupes.Count == 0, $"duplicate corpus ids: {string.Join(", ", dupes.Take(20))}");
    }

    [Fact]
    public void Parser_has_no_unbaselined_false_rejections()
    {
        var cases = LoadAll();
        if (cases.Count == 0) return; // nothing landed yet — stay green

        var baseline = LoadBaseline();
        var newFalseRejections = cases
            .Where(c => c.Expect == "ok" && Parse(c.Sql) == Verdict.Error && !baseline.Contains(c.Id))
            .ToList();

        if (newFalseRejections.Count > 0)
        {
            var sample = string.Join("\n", newFalseRejections.Take(30)
                .Select(c => $"  {c.Id} [{c.Category}]: {c.Sql.Replace("\n", " ")}"));
            Assert.Fail($"{newFalseRejections.Count} valid statements newly rejected by the parser " +
                        $"(add to tests/corpus/_baseline.json only if intentional):\n{sample}");
        }
    }

    [Fact]
    public void Coverage_report()
    {
        var cases = LoadAll();
        if (cases.Count == 0) { _out.WriteLine("corpus empty"); return; }

        var rows = cases.Select(c => (c, v: Parse(c.Sql))).ToList();

        int okParsed  = rows.Count(r => r.c.Expect == "ok"    && r.v == Verdict.Parsed);
        int okError   = rows.Count(r => r.c.Expect == "ok"    && r.v == Verdict.Error);
        int okEmpty   = rows.Count(r => r.c.Expect == "ok"    && r.v == Verdict.Empty);
        int errError  = rows.Count(r => r.c.Expect == "error" && r.v == Verdict.Error);
        int errParsed = rows.Count(r => r.c.Expect == "error" && r.v == Verdict.Parsed);
        int errEmpty  = rows.Count(r => r.c.Expect == "error" && r.v == Verdict.Empty);
        int okTotal   = rows.Count(r => r.c.Expect == "ok");
        int errTotal  = rows.Count(r => r.c.Expect == "error");

        _out.WriteLine($"corpus: {cases.Count} cases across {cases.Select(c => c.Category).Distinct().Count()} categories");
        _out.WriteLine($"  expect=ok    ({okTotal}): parsed={okParsed}  REJECTED(false)={okError}  unmodeled(empty)={okEmpty}");
        _out.WriteLine($"  expect=error ({errTotal}): caught={errError}  missed(parsed)={errParsed}  missed(empty)={errEmpty}");
        if (okTotal > 0)  _out.WriteLine($"  positive parse rate: {100.0 * okParsed / okTotal:F1}%");
        if (errTotal > 0) _out.WriteLine($"  negative catch rate:  {100.0 * errError / errTotal:F1}%");

        _out.WriteLine("\nper-category (parsed/ok , caught/err):");
        foreach (var g in rows.GroupBy(r => r.c.Category).OrderBy(g => g.Key))
        {
            int cok = g.Count(r => r.c.Expect == "ok");
            int cokp = g.Count(r => r.c.Expect == "ok" && r.v == Verdict.Parsed);
            int cer = g.Count(r => r.c.Expect == "error");
            int cerc = g.Count(r => r.c.Expect == "error" && r.v == Verdict.Error);
            _out.WriteLine($"  {g.Key,-32} ok {cokp,4}/{cok,-4}  err {cerc,4}/{cer,-4}");
        }

        // Opt-in: rewrite the baseline to the current set of false rejections.
        if (Environment.GetEnvironmentVariable("PGPROJ_CORPUS_WRITE_BASELINE") == "1")
        {
            var ids = rows.Where(r => r.c.Expect == "ok" && r.v == Verdict.Error)
                          .Select(r => r.c.Id).OrderBy(x => x).ToArray();
            File.WriteAllText(BaselinePath,
                JsonSerializer.Serialize(ids, new JsonSerializerOptions { WriteIndented = true }));
            _out.WriteLine($"\nwrote baseline with {ids.Length} known false-rejection ids");
        }
    }
}
