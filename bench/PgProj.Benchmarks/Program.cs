using BenchmarkDotNet.Running;

namespace PgProj.Benchmarks;

/// <summary>
/// Entry point for the pgproj parser benchmarks (audit §5). Run from the bench project directory:
///
///   dotnet run -c Release                       # pick a benchmark interactively
///   dotnet run -c Release -- --filter *Build*    # end-to-end Build vs BuildAsync (rec #1)
///   dotnet run -c Release -- --filter *Tokenize* # tokenizer allocations (layer 1)
///   dotnet run -c Release -- --filter *Parse*    # full grammar (layer 2)
///   dotnet run -c Release -- --filter *          # everything
///
/// MemoryDiagnoser is on every suite, so each result reports bytes/op alongside ns/op — the gate the
/// audit asks for ("no merge without numbers").
/// </summary>
public static class Program
{
    public static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
