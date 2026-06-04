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
/// MediumRun (15 iterations) rather than ShortRun: the All bucket is large and its time was
/// noise-dominated under ShortRun — bytes/op is the gate, but a stable ns/op needs more iterations.
/// </summary>
[MemoryDiagnoser]
[MediumRunJob]
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
