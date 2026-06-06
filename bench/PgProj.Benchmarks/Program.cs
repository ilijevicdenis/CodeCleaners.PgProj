using System.Diagnostics;
using System.Diagnostics.Tracing;
using BenchmarkDotNet.Running;
using PgProj.Core.Parsing;
using PgProj.Core.Syntax;

namespace PgProj.Benchmarks;

/// <summary>
/// Entry point for the pgproj parser benchmarks (audit §5). Run from the bench project directory:
///
///   dotnet run -c Release                       # pick a benchmark interactively
///   dotnet run -c Release -- --filter *Build*    # end-to-end Build vs BuildAsync (rec #1)
///   dotnet run -c Release -- --filter *Tokenize* # tokenizer allocations (layer 1)
///   dotnet run -c Release -- --filter *Parse*    # full grammar (layer 2)
///   dotnet run -c Release -- --filter *          # everything
///   dotnet run -c Release -- alloc               # fast bytes/op + ns/op probe (no BenchmarkDotNet)
///
/// MemoryDiagnoser is on every suite, so each result reports bytes/op alongside ns/op — the gate the
/// audit asks for ("no merge without numbers").
/// </summary>
public static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "alloc") { AllocProbe(); return; }
        if (args.Length > 0 && args[0] == "alloctypes") { AllocTypes(args.Length > 1 ? args[1] : "All"); return; }
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }

    // Allocation-by-TYPE for the parse+model pipeline, via the runtime's GC AllocationTick events
    // (fire ~every 100 KB allocated, attributing a type). Statistical, but over many iterations it
    // ranks the top allocating types — the signal used to pick the next optimization target.
    private static void AllocTypes(string bucket)
    {
        var sql = CorpusWorkload.Buckets[bucket];
        Console.WriteLine($"Alloc-by-type — Parse+Model, bucket '{bucket}' ({sql.Length:N0} chars)");
        using var listener = new TypeAllocListener();
        for (int i = 0; i < 10; i++) new ModelBuilder().Build(new PgParser().Parse(sql));   // warm
        listener.Reset();
        for (int i = 0; i < 400; i++) new ModelBuilder().Build(new PgParser().Parse(sql));
        System.Threading.Thread.Sleep(300);                                                 // flush events
        listener.Report(25);
    }

    private sealed class TypeAllocListener : EventListener
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, (long bytes, long count)> _agg = new();
        private volatile bool _on;

        protected override void OnEventSourceCreated(EventSource src)
        {
            if (src.Name == "Microsoft-Windows-DotNETRuntime")
                EnableEvents(src, EventLevel.Verbose, (EventKeywords)0x1);   // GC keyword → AllocationTick
        }

        public void Reset() { lock (_lock) _agg.Clear(); _on = true; }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if (!_on || e.EventName is not ("GCAllocationTick_V4" or "GCAllocationTick_V3" or "GCAllocationTick_V2")) return;
            var names = e.PayloadNames; if (names is null) return;
            string? type = null; long amount = 0;
            for (int i = 0; i < names.Count; i++)
            {
                if (names[i] == "TypeName") type = e.Payload![i] as string;
                else if (names[i] == "AllocationAmount64") amount = Convert.ToInt64(e.Payload![i]);
            }
            if (type is null) return;
            lock (_lock) { var cur = _agg.TryGetValue(type, out var v) ? v : default; _agg[type] = (cur.bytes + amount, cur.count + 1); }
        }

        public void Report(int top)
        {
            _on = false;
            lock (_lock)
            {
                long total = 0; foreach (var v in _agg.Values) total += v.bytes;
                Console.WriteLine($"  sampled total ≈ {total / 1024.0 / 1024.0:N1} MB across {_agg.Count} types");
                Console.WriteLine($"  {"%",6}  {"MB",9}  ticks  type");
                foreach (var kv in _agg.OrderByDescending(k => k.Value.bytes).Take(top))
                    Console.WriteLine($"  {100.0 * kv.Value.bytes / total,5:N1}%  {kv.Value.bytes / 1024.0 / 1024.0,9:N2}  {kv.Value.count,5}  {kv.Key}");
            }
        }
    }

    // A lightweight allocation/time probe so before/after deltas can be read in seconds rather than a
    // full BenchmarkDotNet run. Single-threaded, server GC off effects ignored — compare within one run.
    private static void AllocProbe()
    {
        var sql = CorpusWorkload.All;
        var tk = Tokenizer.Tokenize(sql);
        Console.WriteLine($"Corpus 'All' bucket: {sql.Length:N0} chars; tokens={tk.Count:N0}; chars/token={sql.Length/(double)tk.Count:N2}; presize(len/4+16)={sql.Length/4+16:N0} (slack {100.0*((sql.Length/4+16)-tk.Count)/tk.Count:N0}%)");
        Measure("Tokenize+Merge", () => OperatorLexer.Merge(Tokenizer.Tokenize(sql)).Count);
        Measure("Parse         ", () => new PgParser().Parse(sql).Statements.Count);
        Measure("Parse+Model   ", () => new ModelBuilder().Build(new PgParser().Parse(sql)).Tables.Count);
    }

    private static void Measure(string label, Func<int> op)
    {
        for (int i = 0; i < 20; i++) op();                       // warm up + JIT
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

        const int iters = 200;
        long before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iters; i++) op();
        sw.Stop();
        long after = GC.GetAllocatedBytesForCurrentThread();

        double bytesPerOp = (after - before) / (double)iters;
        double usPerOp = sw.Elapsed.TotalMicroseconds / iters;
        Console.WriteLine($"{label}  {bytesPerOp,12:N0} B/op  {usPerOp,10:N1} us/op");
    }
}
