// EP-VS — the LSP client for PostgreSQL .sql buffers in the MAIN VS instance. The server is the
// engine's own LSP (`pgproj serve`, PgProj.Lsp over stdio) run from the VSIX-bundled CLI — the
// same net472→net10 bridge the Import Database command uses. It provides live PostgreSQL
// diagnostics (parse + project-model errors as you type), completion, hover, go-to-definition and
// find-all-references, all driven by the real project build with unsaved-buffer overlay.
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;

namespace PgProj.VisualStudio.ProjectSystem.Editors
{
    [Export(typeof(ILanguageClient))]
    [ContentType(PgSqlContentType.Name)]
    internal sealed class PgSqlLanguageClient : ILanguageClient
    {
        public string Name => "PgProj PostgreSQL Language Server";

        public IEnumerable<string> ConfigurationSections => null;

        public object InitializationOptions => null;

        public IEnumerable<string> FilesToWatch => null;

        public bool ShowNotificationOnInitializeFailed => true;

        public event AsyncEventHandler<EventArgs> StartAsync;
#pragma warning disable CS0067 // the server lives for the VS session; the platform never asks us to stop
        public event AsyncEventHandler<EventArgs> StopAsync;
#pragma warning restore CS0067

        public async Task<Connection> ActivateAsync(CancellationToken token)
        {
            var cliDll = Path.Combine(
                Path.GetDirectoryName(typeof(PgSqlLanguageClient).Assembly.Location), "tools", "PgProj.Cli.dll");
            if (!File.Exists(cliDll))
                return null;

            // Hand the server the solution directory as its workspace root (`serve <dir>`): VS's
            // generic LSP client does not reliably send a usable rootUri in `initialize`, and
            // without a root the server cannot find the .pgproj — diagnostics then silently
            // degrade to parse-only (no unresolved-reference checks, no model-driven completion).
            var workspaceRoot = await TryGetSolutionDirectoryAsync(token);

            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = workspaceRoot is null
                    ? $"\"{cliDll}\" serve"
                    : $"\"{cliDll}\" serve \"{workspaceRoot}\"",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            var process = Process.Start(psi);
            return process is null
                ? null
                : new Connection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
        }

        private static async Task<string> TryGetSolutionDirectoryAsync(CancellationToken token)
        {
            try
            {
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                var solution = (Microsoft.VisualStudio.Shell.Interop.IVsSolution)
                    Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider.GetService(
                        typeof(Microsoft.VisualStudio.Shell.Interop.SVsSolution));
                if (solution is null) return null;
                solution.GetSolutionInfo(out var dir, out _, out _);
                return string.IsNullOrEmpty(dir) ? null : dir.TrimEnd('\\');
            }
            catch
            {
                return null; // no solution context → the server falls back to the initialize rootUri
            }
        }

        public async Task OnLoadedAsync()
        {
            if (StartAsync is { } start)
                await start.InvokeAsync(this, EventArgs.Empty);
        }

        public Task OnServerInitializedAsync() => Task.CompletedTask;

        public Task<InitializationFailureContext> OnServerInitializeFailedAsync(ILanguageClientInitializationInfo initializationState) =>
            Task.FromResult<InitializationFailureContext>(null);
    }
}
