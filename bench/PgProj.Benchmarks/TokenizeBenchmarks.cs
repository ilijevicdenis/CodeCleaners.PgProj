using BenchmarkDotNet.Attributes;
using PgProj.Core.Parsing;
using PgProj.Core.Syntax;

namespace PgProj.Benchmarks;

/// <summary>
/// Layer 1 — tokenize-only. Isolates the per-token <c>Substring</c>/<c>c.ToString()</c> allocations
/// (audit §1a/§1b) and the <see cref="OperatorLexer"/> second pass (§1c-merge). MemoryDiagnoser
/// reports bytes/op so the §2/§3/§5/§8 tokenizer optimizations can be gated on a measured delta.
/// </summary>
[MemoryDiagnoser]
public class TokenizeBenchmarks
{
    [Params("Table", "Raw", "Select", "All")]
    public string Bucket = "All";

    private string _sql = "";

    [GlobalSetup]
    public void Setup() => _sql = CorpusWorkload.Buckets[Bucket];

    [Benchmark(Baseline = true)]
    public int Tokenize() => Tokenizer.Tokenize(_sql).Count;

    [Benchmark]
    public int TokenizeAndMerge() => OperatorLexer.Merge(Tokenizer.Tokenize(_sql)).Count;
}
