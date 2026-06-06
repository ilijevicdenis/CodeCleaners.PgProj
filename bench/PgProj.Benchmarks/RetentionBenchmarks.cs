using BenchmarkDotNet.Attributes;
using PgProj.Core.Syntax;

namespace PgProj.Benchmarks;

/// <summary>
/// Layer 5 — steady-state STABILITY, not per-op speed (audit Rec 3). One measured op is
/// <see cref="Repeats"/> full parse→build→release pipelines over the same large bucket, with the pooled
/// token buffer returned each time. This is the soak the per-op suites structurally cannot express:
/// <list type="bullet">
///   <item><see cref="PipelineBenchmarks"/> measures ONE pipeline, so a slow cross-parse retention leak
///   (a buffer that holds a previous file's interned strings, a model never released) is invisible —
///   its bytes/op looks identical whether or not memory is reclaimed between ops.</item>
///   <item>Here, <c>Allocated</c> should scale ~linearly with <see cref="Repeats"/> AND, crucially, the
///   <c>Gen2</c>/LOH columns should stay flat as Repeats grows. A regression in the
///   <c>ReleaseTokens</c> pooling contract or a retained reference shows as gen2/LOH growth here while
///   every other suite stays green.</item>
/// </list>
/// Pair with <c>dotnet run -c Release -- retention</c> for a fast, GC-mode-independent heap-flatness read.
/// </summary>
[MemoryDiagnoser]
public class RetentionBenchmarks
{
    [Params(50, 200)]
    public int Repeats;

    private string _sql = "";

    [GlobalSetup]
    public void Setup() => _sql = CorpusWorkload.All;   // 1.35 MB → LOH-heavy, the realistic worst case

    // The full per-file path DatabaseProject runs in a loop: parse, lower to a model, return the pooled
    // buffer. Summing a result keeps the JIT from eliding the work.
    [Benchmark]
    public long ParseBuildReleaseLoop()
    {
        long sum = 0;
        for (int i = 0; i < Repeats; i++)
        {
            var parsed = new PgParser().Parse(_sql);
            sum += new ModelBuilder("public").Build(parsed).Tables.Count;
            parsed.ReleaseTokens();
        }
        return sum;
    }
}
