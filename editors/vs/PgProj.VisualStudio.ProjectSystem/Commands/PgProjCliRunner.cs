// EP-VS — runs the bundled pgproj CLI from the in-proc extension. The engine is net10 and this
// assembly is net472, so engine work goes through the CLI process: this VSIX carries a
// framework-dependent publish of PgProj.Cli under tools\ (same payload the PgProj.Sdk nupkg ships).
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace PgProj.VisualStudio.ProjectSystem.Commands
{
    /// <summary>The outcome of one CLI invocation.</summary>
    internal sealed class CliResult
    {
        public int ExitCode { get; set; }
        public string Output { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
        public bool Success => ExitCode == 0;
    }

    /// <summary>
    /// Locates and runs the <c>pgproj</c> CLI off the UI thread. Resolution order: the VSIX-bundled
    /// <c>tools\PgProj.Cli.dll</c> (run as <c>dotnet &lt;dll&gt;</c>), then a <c>pgproj</c> on PATH
    /// (the dotnet-tool install).
    /// </summary>
    internal static class PgProjCliRunner
    {
        /// <summary>The bundled CLI dll, when this extension was packaged with one.</summary>
        private static string BundledCliDll =>
            Path.Combine(Path.GetDirectoryName(typeof(PgProjCliRunner).Assembly.Location), "tools", "PgProj.Cli.dll");

        /// <summary>Human-readable description of the CLI that will run (for error messages).</summary>
        public static string Describe()
            => File.Exists(BundledCliDll) ? $"dotnet \"{BundledCliDll}\"" : "pgproj (PATH)";

        /// <summary>Runs <c>pgproj &lt;arguments&gt;</c> and captures output. Never throws for a non-zero exit.</summary>
        public static Task<CliResult> RunAsync(string arguments, int timeoutMs = 600_000)
        {
            return Task.Run(() =>
            {
                var bundled = BundledCliDll;
                var (fileName, prefix) = File.Exists(bundled)
                    ? ("dotnet", $"\"{bundled}\" ")
                    : ("pgproj", string.Empty);

                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = prefix + arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };

                var result = new CliResult();
                try
                {
                    using var process = Process.Start(psi);
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(timeoutMs))
                    {
                        try { process.Kill(); } catch { /* already gone */ }
                        result.ExitCode = -1;
                        result.Error = $"pgproj timed out after {timeoutMs / 1000}s.";
                        return result;
                    }
                    result.ExitCode = process.ExitCode;
                    result.Output = stdout.GetAwaiter().GetResult();
                    result.Error = stderr.GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    result.ExitCode = -1;
                    result.Error = $"Could not start the pgproj CLI ({Describe()}): {ex.Message}";
                }
                return result;
            });
        }
    }
}
