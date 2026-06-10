// EP-VS #25 Route B (modern). The `.sql` language server provider — runs the engine's LSP server
// IN-PROCESS. Instead of spawning `pgproj serve`, it hosts PgProj.Lsp's LspServer on one end of an
// in-memory full-duplex stream and hands VS the other end as a duplex pipe. Same server code the VS Code
// extension drives over STDIO; here the transport is an in-process pipe.
using System.Diagnostics;
using System.IO.Pipelines;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;
using Microsoft.VisualStudio.Extensibility.LanguageServer;
using Microsoft.VisualStudio.RpcContracts.LanguageServerProvider;
using Nerdbank.Streams;
using PgProj.Lsp.Server;

namespace PgProj.VisualStudio.LanguageServer;

/// <summary>
/// Provides PostgreSQL <c>.sql</c> language features by hosting the engine's <see cref="LspServer"/>
/// in-process. VS activates this when a <c>.sql</c> document (mapped by <see cref="PgSqlDocumentType"/>)
/// is opened.
/// </summary>
[VisualStudioContribution]
internal sealed class PgProjLanguageServerProvider : LanguageServerProvider
{
    // The engine's default LSP debounce (ms) — matches the `pgproj serve` CLI default.
    private const int DefaultDebounceMs = 150;

    /// <summary>
    /// Custom document type binding the <c>.sql</c> extension to this provider. Based on
    /// <see cref="LanguageServerBaseDocumentType"/> so the server is offered SQL editors specifically.
    /// </summary>
    [VisualStudioContribution]
    public static DocumentTypeConfiguration PgSqlDocumentType => new("pgsql")
    {
        FileExtensions = [".sql"],
        BaseDocumentType = LanguageServerBaseDocumentType,
    };

    public PgProjLanguageServerProvider(ExtensionCore container, VisualStudioExtensibility extensibilityObject, TraceSource traceSource)
        : base(container, extensibilityObject)
    {
    }

    /// <inheritdoc/>
    public override LanguageServerProviderConfiguration LanguageServerProviderConfiguration =>
        new("%PgProj.LanguageServer.DisplayName%",
            [DocumentFilter.FromDocumentType(PgSqlDocumentType)]);

    /// <inheritdoc/>
    public override Task<IDuplexPipe?> CreateServerConnectionAsync(CancellationToken cancellationToken)
    {
        // Full-duplex in-memory pair: what VS writes to vsStream is readable on serverStream and vice
        // versa. The server reads its input from, and writes its output to, the SAME serverStream end.
        var (vsStream, serverStream) = FullDuplexStream.CreatePair();

        var server = new LspServer(serverStream, serverStream, DefaultDebounceMs);

        // Run the server until VS closes the pipe (its read loop EOFs), then dispose both it and the stream.
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await server.RunAsync();
                }
                catch (Exception)
                {
                    // The server stopped (pipe closed / shutdown). Nothing actionable here.
                }
                finally
                {
                    server.Dispose();
                    await serverStream.DisposeAsync();
                }
            },
            CancellationToken.None);

        // VS reads server output via the reader and writes server input via the writer of the other end.
        var pipe = new DuplexPipe(vsStream.UsePipeReader(), vsStream.UsePipeWriter());
        return Task.FromResult<IDuplexPipe?>(pipe);
    }

    /// <inheritdoc/>
    public override Task OnServerInitializationResultAsync(
        ServerInitializationResult startState,
        LanguageServerInitializationFailureInfo? initializationFailureInfo,
        CancellationToken cancellationToken)
    {
        if (startState == ServerInitializationResult.Failed)
        {
            this.Enabled = false;
        }

        return Task.CompletedTask;
    }

    /// <summary>Minimal <see cref="IDuplexPipe"/> over the VS-facing end of the in-memory stream pair.</summary>
    private sealed class DuplexPipe(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;

        public PipeWriter Output { get; } = output;
    }
}
