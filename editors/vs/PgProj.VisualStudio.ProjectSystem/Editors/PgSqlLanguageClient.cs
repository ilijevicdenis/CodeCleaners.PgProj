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

        public Task<Connection> ActivateAsync(CancellationToken token)
        {
            var cliDll = Path.Combine(
                Path.GetDirectoryName(typeof(PgSqlLanguageClient).Assembly.Location), "tools", "PgProj.Cli.dll");
            if (!File.Exists(cliDll))
                return Task.FromResult<Connection>(null);

            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{cliDll}\" serve",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            var process = Process.Start(psi);
            return Task.FromResult(process is null
                ? null
                : new Connection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream));
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
