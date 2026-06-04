using BenchmarkDotNet.Attributes;
using PgProj.Core.Syntax;

namespace PgProj.Benchmarks;

/// <summary>
/// Layer 2 — parse-only. Runs the full grammar over a fixed in-memory string (no file I/O), so it
/// captures the dispatch <c>params string[]</c> / <c>ToUpperInvariant</c> allocations (audit §1e/§1f)
/// and the re-tokenization of raw/unsupported statements (§1c/§4). The <c>Raw</c> bucket is where the
/// re-tokenization win should show up; <c>Select</c> is where the expression-grammar allocations live.
/// </summary>
[MemoryDiagnoser]
public class ParseBenchmarks
{
    [Params("Table", "Raw", "Select", "All")]
    public string Bucket = "All";

    private string _sql = "";

    [GlobalSetup]
    public void Setup() => _sql = CorpusWorkload.Buckets[Bucket];

    [Benchmark]
    public int Parse() => new PgParser().Parse(_sql).Statements.Count;
}
