using BenchmarkDotNet.Attributes;
using PgProj.Core.Syntax;

namespace PgProj.Benchmarks;

/// <summary>
/// Layer 2b — model-build only. Isolates <see cref="ModelBuilder.Build"/> (and its hot spot
/// <c>AddTable</c>) from parsing: the corpus bucket is parsed once in <see cref="Setup"/>, so each
/// benchmarked op measures only the lowering of a fixed <see cref="ParseResult"/> into a fresh
/// <see cref="PgProj.Core.Model.DatabaseModel"/>. This is the path the profiling audit flagged as the
/// #1 real CPU consumer (<c>ModelBuilder.AddTable</c>, PROFILING_PLAN.md) but which neither the
/// Tokenize nor Parse suites cover. <c>Table</c> is the meaningful bucket (AddTable); <c>Raw</c> shows
/// the DeriveRaw re-tokenization, <c>All</c> the representative mix.
///
/// Iteration counts (5 warmup / 15 measured) and GC mode come from BenchConfig — uniform across all
/// suites, so this no longer carries its own [MediumRunJob]. The large All bucket needs the higher
/// iteration count for a stable ns/op; bytes/op is the gate either way.
/// </summary>
[MemoryDiagnoser]
public class ModelBuildBenchmarks
{
    [Params("Table", "Raw", "Select", "All")]
    public string Bucket = "All";

    private ParseResult _parsed = null!;

    [GlobalSetup]
    public void Setup() => _parsed = new PgParser().Parse(CorpusWorkload.Buckets[Bucket]);

    // Fresh ModelBuilder + DatabaseModel per op (matches how Build()/ParseOne lower one file): the
    // measured cost is the lowering + model allocation, with parsing factored out into Setup.
    [Benchmark]
    public int BuildModel() => new ModelBuilder("public").Build(_parsed).Tables.Count;
}
