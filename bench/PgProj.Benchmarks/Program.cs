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
///   dotnet run -c Release -- retention           # steady-state heap-flatness probe (LOH/gen2 leak check)
///
/// All BenchmarkDotNet suites run under <see cref="BenchConfig"/> (Workstation GC + uniform rigor), so
/// each result reports bytes/op alongside ns/op under the runtime the CLI actually ships — the gate the
/// audit asks for ("no merge without numbers").
/// </summary>
public static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "alloc") { AllocProbe(); return; }
        if (args.Length > 0 && args[0] == "modelalloc") { ModelAllocProbe(); return; }
        if (args.Length > 0 && args[0] == "modeltypes") { ModelTypes(args.Length > 1 ? args[1] : "All"); return; }
        if (args.Length > 0 && args[0] == "alloctypes") { AllocTypes(args.Length > 1 ? args[1] : "All"); return; }
        if (args.Length > 0 && args[0] == "retention") { RetentionProbe(args.Length > 1 ? args[1] : "All"); return; }
        if (args.Length > 0 && args[0] == "buildwall") { BuildWallProbe(); return; }
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, new BenchConfig());
    }

    // Deterministic wall-clock probe for the parallel build (DOP-tuning gate): generates on-disk
    // projects of several file counts, then reports the MEDIAN of many BuildAsync iterations (median
    // beats mean for scheduler jitter). Serial Build() is printed as the reference line. Wall-clock —
    // not bytes/op — is the metric here, because a worker-count change barely moves allocation.
    private static void BuildWallProbe()
    {
        Console.WriteLine($"BuildAsync wall-clock probe — cores={Environment.ProcessorCount}");
        foreach (var n in new[] { 2, 10, 24, 50, 200 })
        {
            var dir = Path.Combine(Path.GetTempPath(), "pgproj_wall_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var proj = Path.Combine(dir, "Bench.pgproj");
                File.WriteAllText(proj, """
                    <Project><PropertyGroup><Name>Bench</Name><DefaultSchema>app</DefaultSchema></PropertyGroup>
                    <ItemGroup><Build Include="**/*.sql" /></ItemGroup></Project>
                    """);
                File.WriteAllText(Path.Combine(dir, "00_schema.sql"), "CREATE SCHEMA app;");
                for (var i = 0; i < n; i++)
                    File.WriteAllText(Path.Combine(dir, $"t{i:D4}.sql"), $$"""
                        CREATE TABLE app.t{{i:D4}} (
                            id          bigint PRIMARY KEY,
                            parent_id   bigint REFERENCES app.t0000 (id),
                            code        varchar(32) NOT NULL,
                            name        text NOT NULL DEFAULT 'unnamed',
                            amount      numeric(18,4) NOT NULL DEFAULT 0,
                            is_active   boolean NOT NULL DEFAULT true,
                            created_at  timestamptz NOT NULL DEFAULT now(),
                            payload     jsonb,
                            CONSTRAINT ck_t{{i:D4}}_amount CHECK (amount >= 0)
                        );
                        CREATE INDEX ix_t{{i:D4}}_code ON app.t{{i:D4}} (code);
                        """);

                var project = PgProj.Core.Project.DatabaseProject.Load(proj);
                for (var i = 0; i < 5; i++) _ = project.BuildAsync().GetAwaiter().GetResult();   // warm up + JIT

                var iters = n >= 200 ? 40 : 120;
                var par = new double[iters];
                for (var i = 0; i < iters; i++)
                {
                    var sw = Stopwatch.StartNew();
                    _ = project.BuildAsync().GetAwaiter().GetResult();
                    par[i] = sw.Elapsed.TotalMilliseconds;
                }
                var ser = new double[iters];
                for (var i = 0; i < iters; i++)
                {
                    var sw = Stopwatch.StartNew();
                    _ = project.Build();
                    ser[i] = sw.Elapsed.TotalMilliseconds;
                }
                Array.Sort(par); Array.Sort(ser);
                Console.WriteLine($"  files={n + 1,4}  BuildAsync median={par[iters / 2],8:N3} ms  min={par[0],8:N3} ms   |  serial Build median={ser[iters / 2],8:N3} ms");
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
            }
        }
    }

    // Allocation-by-TYPE for the parse+model pipeline, via the runtime's GC AllocationTick events
    // (fire ~every 100 KB allocated, attributing a type). Statistical, but over many iterations it
    // ranks the top allocating types — the signal used to pick the next optimization target.
    private static void AllocTypes(string bucket)
    {
        var sql = CorpusWorkload.Buckets[bucket];
        Console.WriteLine($"Alloc-by-type — Parse+Model, bucket '{bucket}' ({sql.Length:N0} chars)");
        using var listener = new TypeAllocListener();
        for (int i = 0; i < 10; i++) { var p = new PgParser().Parse(sql); new ModelBuilder().Build(p); p.ReleaseTokens(); }   // warm + pool
        listener.Reset();
        for (int i = 0; i < 400; i++) { var p = new PgParser().Parse(sql); new ModelBuilder().Build(p); p.ReleaseTokens(); }
        System.Threading.Thread.Sleep(300);                                                 // flush events
        listener.Report(25);
    }

    // Allocation-by-TYPE for the MODEL-BUILD stage only: the bucket is parsed ONCE up front, then only
    // ModelBuilder.Build is looped under the listener — so the ranked types are exactly the model-stage
    // allocations (AddTable, DeriveRaw, the records), with parse churn excluded.
    private static void ModelTypes(string bucket)
    {
        var sql = CorpusWorkload.Buckets[bucket];
        var parsed = new PgParser().Parse(sql);
        Console.WriteLine($"Alloc-by-type — Model-build only, bucket '{bucket}' ({sql.Length:N0} chars)");
        using var listener = new TypeAllocListener();
        for (int i = 0; i < 10; i++) new ModelBuilder("public").Build(parsed);   // warm
        listener.Reset();
        for (int i = 0; i < 400; i++) new ModelBuilder("public").Build(parsed);
        System.Threading.Thread.Sleep(300);
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

    // Steady-state stability probe (audit Rec 3): parse+build+release the same large bucket many times,
    // forcing a full blocking gen2 compacting collection before and after, then read the LOH/UOH and total
    // heap size via GC.GetGCMemoryInfo() (GC-mode-independent, the modern way to see what's RETAINED rather
    // than merely allocated). A healthy result is FLAT — after-delta ≈ the pool's steady working set, not
    // growing with the iteration count. A growing LOH/heap delta is the signal of a leak or a broken
    // ReleaseTokens pooling contract that no per-op bytes/op number can catch. Runs in seconds.
    private static void RetentionProbe(string bucket)
    {
        const int iters = 1000;
        var sql = CorpusWorkload.Buckets[bucket];
        Console.WriteLine($"Retention probe — Parse+Model+Release, bucket '{bucket}' ({sql.Length:N0} chars) × {iters:N0}");

        static void Run(string s) { var p = new PgParser().Parse(s); new ModelBuilder().Build(p); p.ReleaseTokens(); }

        for (int i = 0; i < 20; i++) Run(sql);                        // warm + settle the ArrayPool buckets
        var before = Settle();
        for (int i = 0; i < iters; i++) Run(sql);
        var after = Settle();

        double MB(long b) => b / 1024.0 / 1024.0;
        long lohBefore = before.GenerationInfo[^1].SizeAfterBytes;    // last generation = LOH/UOH
        long lohAfter = after.GenerationInfo[^1].SizeAfterBytes;
        Console.WriteLine($"  LOH/UOH  before={MB(lohBefore),8:N1} MB  after={MB(lohAfter),8:N1} MB  delta={MB(lohAfter - lohBefore),7:N1} MB");
        Console.WriteLine($"  Heap     before={MB(before.HeapSizeBytes),8:N1} MB  after={MB(after.HeapSizeBytes),8:N1} MB  delta={MB(after.HeapSizeBytes - before.HeapSizeBytes),7:N1} MB");
        Console.WriteLine($"  Fragmented after={MB(after.FragmentedBytes),8:N1} MB   (gen2 collections during run: {GC.CollectionCount(2)})");
        Console.WriteLine("  PASS when LOH + heap deltas stay flat across iteration counts (re-run with a larger bucket to confirm).");
    }

    // Full blocking, compacting gen2 collection + finalizer drain, then snapshot. This is the only place a
    // forced GC.Collect is legitimate: we want to observe what survives a real collection, not allocations.
    private static GCMemoryInfo Settle()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        return GC.GetGCMemoryInfo();
    }

    // A lightweight allocation/time probe so before/after deltas can be read in seconds rather than a
    // full BenchmarkDotNet run. Single-threaded, server GC off effects ignored — compare within one run.
    private static void AllocProbe()
    {
        var sql = CorpusWorkload.All;
        var tk = Tokenizer.Tokenize(sql);
        Console.WriteLine($"Corpus 'All' bucket: {sql.Length:N0} chars; tokens={tk.Count:N0}; chars/token={sql.Length/(double)tk.Count:N2}; presize(len/4+16)={sql.Length/4+16:N0} (slack {100.0*((sql.Length/4+16)-tk.Count)/tk.Count:N0}%)");
        Measure("Tokenize+Merge", () => { var p = OperatorLexer.MergeInPlace(Tokenizer.TokenizePooled(sql)); var c = p.Count; p.Return(); return c; });
        Measure("Parse         ", () => { var p = new PgParser().Parse(sql); var c = p.Statements.Count; p.ReleaseTokens(); return c; });
        Measure("Parse+Model   ", () => { var p = new PgParser().Parse(sql); var c = new ModelBuilder().Build(p).Tables.Count; p.ReleaseTokens(); return c; });
    }

    // Model-build-only bytes/op, parsing factored out (same isolation as ModelBuildBenchmarks, which
    // BenchmarkDotNet cannot run from inside a git worktree — it finds >1 PgProj.Benchmarks.csproj and
    // refuses). Each bucket is parsed ONCE; the measured op is only `new ModelBuilder("public").Build`.
    // GC.GetAllocatedBytesForCurrentThread() is a deterministic per-thread counter (not statistical
    // sampling), so the per-op number is exact for a single-threaded loop — the cleanest model-stage gate.
    private static void ModelAllocProbe()
    {
        Console.WriteLine("Model-build-only bytes/op (parse factored out) — GC.GetAllocatedBytesForCurrentThread");
        foreach (var bucket in new[] { "Table", "Raw", "Select", "All" })
        {
            var parsed = new PgParser().Parse(CorpusWorkload.Buckets[bucket]);
            MeasureModel(bucket, parsed);
        }
    }

    private static void MeasureModel(string bucket, ParseResult parsed)
    {
        Func<int> op = () => new ModelBuilder("public").Build(parsed).Tables.Count;
        for (int i = 0; i < 30; i++) op();                      // warm up + JIT
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

        const int iters = 500;
        long before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iters; i++) op();
        sw.Stop();
        long after = GC.GetAllocatedBytesForCurrentThread();

        double bytesPerOp = (after - before) / (double)iters;
        double usPerOp = sw.Elapsed.TotalMicroseconds / iters;
        int tables = 0; foreach (var st in parsed.Statements) if (st is CreateTableStatement) tables++;
        Console.WriteLine($"  {bucket,-7} {bytesPerOp,12:N0} B/op  {usPerOp,10:N1} us/op  (tables={tables})");
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
