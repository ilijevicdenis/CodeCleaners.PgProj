using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Order;

namespace PgProj.Benchmarks;

/// <summary>
/// The one place GC mode and statistical rigor for every A/B suite are declared (audit Rec 1/2). It is
/// passed to <see cref="BenchmarkDotNet.Running.BenchmarkSwitcher"/> in <see cref="Program"/>, so every
/// benchmark runs under the SAME job — replacing the ad-hoc per-class <c>[MediumRunJob]</c> attributes
/// that used to give different suites different iteration counts.
///
/// <para>Why these knobs, for THIS project:</para>
/// <list type="bullet">
///   <item><b>Workstation, non-concurrent GC</b> — <c>PgProj.Cli</c> ships Workstation GC, so the gate
///   must measure that. Blocking (non-background) GC collects on small gen0 budgets, so
///   <see cref="MemoryDiagnoser"/> sees every collection and the bytes/op figure the project gates on is
///   precise. (The old host-level Server GC degraded exactly this — audit F1.)</item>
///   <item><b>MemoryRandomization</b> — the large "All"-bucket result strings are heap-layout sensitive;
///   randomizing layout per iteration stops a layout coincidence from masquerading as a real A/B delta.</item>
///   <item><b>5 warmup / 15 measured iterations</b> — uniform rigor: the large buckets were
///   noise-dominated under ShortRun, which is why two suites had bolted on <c>[MediumRunJob]</c>.</item>
///   <item><b>JSON + GitHub-markdown exporters</b> — the full JSON is the committable baseline for a real
///   before/after file diff; the markdown pastes straight into <c>docs/parser-performance.md</c>.</item>
/// </list>
///
/// <para>Set <c>PGPROJ_BENCH_BOTHGC=1</c> to add a second Server+Background (DATAS) job, so a change can
/// be checked under the parallel <c>BuildAsync</c>'s likely server-class runtime without slowing the
/// default gate. DATAS (adaptive heap count) is the net9+ Server GC default; the env var makes it explicit.</para>
/// </summary>
public sealed class BenchConfig : ManualConfig
{
    public BenchConfig()
    {
        // Inherit the default loggers / columns / exporters / analysers / validators, then layer ours.
        Add(DefaultConfig.Instance);

        var workstation = Job.Default
            .WithGcServer(false)
            .WithGcConcurrent(false)        // blocking GC → deterministic, clean allocation accounting
            .WithWarmupCount(5)
            .WithIterationCount(15)
            .WithMemoryRandomization()      // shuffle heap layout per iteration → kills false alloc/time deltas
            .WithId("Workstation");
        AddJob(workstation);

        if (System.Environment.GetEnvironmentVariable("PGPROJ_BENCH_BOTHGC") == "1")
        {
            var serverDatas = Job.Default
                .WithGcServer(true)
                .WithGcConcurrent(true)
                .WithEnvironmentVariable("DOTNET_GCDynamicAdaptationMode", "1")   // DATAS on (net9+ default)
                .WithWarmupCount(5)
                .WithIterationCount(15)
                .WithId("ServerDATAS");
            AddJob(serverDatas);
        }

        AddDiagnoser(MemoryDiagnoser.Default);                 // bytes/op + gen0/1/2 — meaningful under Workstation GC
        AddColumn(StatisticColumn.Median, StatisticColumn.P95); // tail behavior, not just the mean
        AddExporter(JsonExporter.Full);                        // committable A/B baseline (full report JSON)
        AddExporter(MarkdownExporter.GitHub);                  // paste into docs/parser-performance.md
        WithOrderer(new DefaultOrderer(SummaryOrderPolicy.FastestToSlowest));
    }
}
