// EP-VS #25 Route B — the .sql LSP client. SCAFFOLD (requires the VS SDK / LanguageServer.Client to build).
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

namespace PgProj.VisualStudio.LanguageClient
{
    /// <summary>
    /// Visual Studio LSP client for .sql files in a PgProj workspace. It spawns the SAME stock LSP
    /// server the VS Code extension (#24) uses — <c>pgproj serve</c> over STDIO (see
    /// docs/LSP_LANGUAGE_SERVER.md) — and hands VS its stdin/stdout stream pair. All diagnostics,
    /// definition, hover, and completion come from the engine; no language logic lives here.
    ///
    /// VS discovers this client via MEF: it is exported as <see cref="ILanguageClient"/> and matched
    /// to the "PgProjSql" content type (PgProjContentDefinition.cs maps .sql to it).
    /// </summary>
    [ContentType(PgProjContentDefinition.ContentTypeName)]
    [Export(typeof(ILanguageClient))]
    public sealed class PgProjLanguageClient : ILanguageClient
    {
        public string Name => "PgProj PostgreSQL Language Server";

        // We do not ship a settings/config file with the server yet.
        public IEnumerable<string> ConfigurationSections => null;
        public object InitializationOptions => null;
        public IEnumerable<string> FilesToWatch => null;
        public bool ShowNotificationOnInitializeFailed => true;

        public event AsyncEventHandler<EventArgs> StartAsync;
        public event AsyncEventHandler<EventArgs> StopAsync;

        /// <summary>
        /// Launches <c>pgproj serve</c> and returns its stream pair. The workspace folder (solution
        /// dir) is passed as the optional positional so the server can resolve the .pgproj before the
        /// LSP <c>initialize</c> arrives (it also reads rootUri from the handshake — see the doc).
        /// </summary>
        public async Task<Connection> ActivateAsync(CancellationToken token)
        {
            await Task.Yield();

            var (fileName, baseArgs) = ResolvePgProjCommand();
            var workspace = ResolveWorkspaceRoot();

            var info = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = baseArgs + "serve" + (workspace is null ? "" : $" \"{workspace}\""),
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true, // server logs go to stderr; stdout is the LSP wire.
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workspace ?? Environment.CurrentDirectory,
            };

            var process = new Process { StartInfo = info };
            if (!process.Start())
                return null;

            // VS reads server->client on StandardOutput and writes client->server on StandardInput.
            return new Connection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
        }

        public async Task OnLoadedAsync()
        {
            if (StartAsync != null)
                await StartAsync.InvokeAsync(this, EventArgs.Empty);
        }

        public Task OnServerInitializeFailedAsync(Exception e) => Task.CompletedTask;
        public Task OnServerInitializedAsync() => Task.CompletedTask;

        // ---- resolution helpers (scaffold) ---------------------------------------------------

        /// <summary>
        /// Resolve how to launch the server. Preference order:
        ///   1. a `pgproj` on PATH (the packaged CLI / global tool) → ("pgproj", "")
        ///   2. the CLI carried in the PgProj.Sdk NuGet package's tools/ → ("dotnet", "\"...PgProj.Cli.dll\" ")
        /// The packaged form is what ships; PATH is the dev convenience. (TODO: discover the tools/
        /// path from the project's resolved SDK package — stubbed here.)
        /// </summary>
        private static (string fileName, string baseArgs) ResolvePgProjCommand()
        {
            // SCAFFOLD: assume `pgproj` is on PATH. The real implementation should fall back to the
            // SDK-packaged `dotnet tools/PgProj.Cli.dll`.
            return ("pgproj", "");
        }

        /// <summary>The solution/workspace directory (TODO: get from the open solution / DTE). Stubbed.</summary>
        private static string ResolveWorkspaceRoot()
        {
            // SCAFFOLD: a real client gets this from the IVsSolution / the active .pgproj's directory.
            return null;
        }
    }
}
