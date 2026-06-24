using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace PgProj.Blackbox.Tests.Infrastructure;

/// <summary>Result of running the <c>pgproj</c> binary: exit code + captured streams.</summary>
public sealed record CliResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
    public string All => StdOut + StdErr;

    /// <summary>True when either stream contains <paramref name="text"/> (case-insensitive).</summary>
    public bool Mentions(string text) =>
        All.Contains(text, StringComparison.OrdinalIgnoreCase);

    public override string ToString() =>
        $"exit={ExitCode}\n--- stdout ---\n{StdOut}\n--- stderr ---\n{StdErr}";
}

/// <summary>
/// Runs the published <c>pgproj</c> CLI as an external process — the blackbox boundary. Nothing here
/// references the engine assemblies; we only know the binary's path, the arguments, and the three
/// observable outputs (exit code, stdout, stderr). The CLI dll is located by walking up to the repo
/// root (the folder holding PgProj.slnx) and probing <c>src/PgProj.Cli/bin/&lt;cfg&gt;/net10.0</c>.
/// </summary>
public static class PgProjCli
{
    private static readonly string CliDll = LocateCliDll();

    /// <summary>The resolved CLI dll path (for diagnostics in skip messages).</summary>
    public static string DllPath => CliDll;

    public static bool Available => CliDll.Length > 0 && File.Exists(CliDll);

    public static CliResult Run(string args, string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? env = null, int timeoutMs = 120_000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Path.GetTempPath(),
        };
        psi.ArgumentList.Add(CliDll);
        foreach (var a in SplitArgs(args)) psi.ArgumentList.Add(a);
        if (env is not null)
            foreach (var (k, v) in env) psi.Environment[k] = v;

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException($"pgproj {args} did not exit within {timeoutMs} ms.");
        }
        p.WaitForExit(); // flush async readers
        return new CliResult(p.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>
    /// Minimal arg splitter honouring double-quotes so callers can pass connection strings and paths
    /// with spaces as one token (e.g. <c>--connection "Host=...;Database=x"</c>).
    /// </summary>
    private static IEnumerable<string> SplitArgs(string args)
    {
        var token = new StringBuilder();
        bool inQuotes = false, has = false;
        foreach (var ch in args)
        {
            if (ch == '"') { inQuotes = !inQuotes; has = true; }
            else if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (has) { yield return token.ToString(); token.Clear(); has = false; }
            }
            else { token.Append(ch); has = true; }
        }
        if (has) yield return token.ToString();
    }

    private static string LocateCliDll()
    {
        // Prefer the configuration the tests were built in, then fall back to the other.
        var configs = IsDebug ? new[] { "Debug", "Release" } : new[] { "Release", "Debug" };
        var dir = AppContext.BaseDirectory;
        for (var probe = new DirectoryInfo(dir); probe is not null; probe = probe.Parent)
        {
            if (!File.Exists(Path.Combine(probe.FullName, "PgProj.slnx"))) continue;
            foreach (var cfg in configs)
            {
                var candidate = Path.Combine(probe.FullName, "src", "PgProj.Cli", "bin", cfg, "net10.0", "PgProj.Cli.dll");
                if (File.Exists(candidate)) return candidate;
            }
            break; // found repo root; the CLI just isn't built — return empty so tests skip cleanly
        }
        return "";
    }

    private static bool IsDebug =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<DebuggableAttribute>()?.IsJITTrackingEnabled ?? false;
}
