using System.Diagnostics;
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
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }

    // A lightweight allocation/time probe so before/after deltas can be read in seconds rather than a
    // full BenchmarkDotNet run. Single-threaded, server GC off effects ignored — compare within one run.
    private static void AllocProbe()
    {
        var sql = CorpusWorkload.All;
        Console.WriteLine($"Corpus 'All' bucket: {sql.Length:N0} chars");
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
