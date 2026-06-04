using BenchmarkDotNet.Attributes;
using PgProj.Core.Syntax;

namespace PgProj.Benchmarks;

/// <summary>
/// Layer 2c — the real per-file pipeline: parse a fixed SQL string THEN lower it into a fresh
/// <see cref="PgProj.Core.Model.DatabaseModel"/>, both inside one measured op (this is exactly what
/// <c>DatabaseProject.ParseOne</c> does per file). It exists because neither <see cref="ParseBenchmarks"/>
/// (parse only — never reads <c>SourceText</c>) nor <see cref="ModelBuildBenchmarks"/> (reuses one
/// pre-parsed result, so a lazily-rendered <c>SourceText</c> is cached after the first op) can honestly
/// score a change that <b>moves</b> work between parse and model — e.g. lazy <c>SourceText</c>
/// rendering. Combined bytes/op is the gate for any such change, so the win can't hide by shifting cost
/// from one stage to the other.
/// </summary>
[MemoryDiagnoser]
public class PipelineBenchmarks
{
    [Params("Table", "Raw", "Select", "All")]
    public string Bucket = "All";

    private string _sql = "";

    [GlobalSetup]
    public void Setup() => _sql = CorpusWorkload.Buckets[Bucket];

    [Benchmark]
    public int ParseAndBuild()
    {
        var parsed = new PgParser().Parse(_sql);
        return new ModelBuilder("public").Build(parsed).Tables.Count;
    }
}
