using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PgProj.Lsp.Debounce;
using PgProj.Lsp.Handlers;
using PgProj.Lsp.Protocol;
using PgProj.Lsp.Workspace;

namespace PgProj.Lsp.Server;

/// <summary>
/// The thin STDIO LSP host: it owns the framed reader/writer, the document store, the debounce scheduler and
/// a <see cref="LanguageService"/>, and dispatches the documented method subset. All ANALYSIS lives in
/// <see cref="LanguageService"/> (pure); this class only marshals JSON-RPC ↔ handler calls and pumps
/// <c>publishDiagnostics</c>. It is constructed over arbitrary streams, so a test drives it with in-memory
/// pipes (no process) and the CLI drives it with stdin/stdout.
/// </summary>
public sealed class LspServer : IDisposable
{
    private readonly LspMessageReader _reader;
    private readonly LspMessageWriter _writer;
    private readonly DocumentStore _store = new();
    private readonly DebouncedAnalysisScheduler _scheduler;
    private LanguageService _service;

    private bool _initialized;
    private volatile bool _shutdownRequested;

    public LspServer(Stream input, Stream output, int debounceMs = 150)
    {
        _reader = new LspMessageReader(input);
        _writer = new LspMessageWriter(output);
        _scheduler = new DebouncedAnalysisScheduler(debounceMs);
        _service = new LanguageService(_store);
    }

    /// <summary>The document store (exposed for tests/diagnostics).</summary>
    public DocumentStore Documents => _store;

    /// <summary>
    /// Runs the read→dispatch loop until the peer closes stdin or sends <c>exit</c>. Returns the process exit
    /// code per the LSP spec: 0 if <c>exit</c> followed a <c>shutdown</c>, else 1.
    /// </summary>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            string? json;
            try { json = await _reader.ReadAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            if (json is null) break; // stdin closed

            JsonRpcMessage msg;
            try { msg = JsonRpcMessage.FromJson(json); }
            catch { await Respond(JsonRpcMessage.ErrorFor(null, LspErrorCodes.ParseError, "Invalid JSON.")).ConfigureAwait(false); continue; }

            if (msg.Method == "exit") return _shutdownRequested ? 0 : 1;

            // Per the spec, the server rejects any request other than `initialize` before it is initialized.
            if (!_initialized && msg.IsRequest && msg.Method != "initialize")
            {
                await Respond(JsonRpcMessage.ErrorFor(msg.Id, LspErrorCodes.ServerNotInitialized, "Server not initialized.")).ConfigureAwait(false);
                continue;
            }

            try { await DispatchAsync(msg, ct).ConfigureAwait(false); }
            catch (Exception ex) when (msg.IsRequest)
            {
                await Respond(JsonRpcMessage.ErrorFor(msg.Id, LspErrorCodes.InternalError, ex.Message)).ConfigureAwait(false);
            }
        }
        return _shutdownRequested ? 0 : 1;
    }

    private async Task DispatchAsync(JsonRpcMessage msg, CancellationToken ct)
    {
        switch (msg.Method)
        {
            case "initialize":
                HandleInitialize(msg);
                await Respond(JsonRpcMessage.ResultFor(msg.Id, new InitializeResult { Capabilities = new ServerCapabilities() })).ConfigureAwait(false);
                break;

            case "initialized":
                break; // notification — nothing to do

            case "shutdown":
                _shutdownRequested = true;
                // Let any debounced diagnostics in flight publish before we stop the loop.
                await _scheduler.DrainAsync().ConfigureAwait(false);
                await Respond(JsonRpcMessage.ResultFor(msg.Id, (object?)null)).ConfigureAwait(false);
                break;

            case "textDocument/didOpen":
            {
                var p = Decode<DidOpenTextDocumentParams>(msg);
                if (p is not null)
                {
                    _store.Open(p.TextDocument.Uri, p.TextDocument.Text, p.TextDocument.Version);
                    ScheduleDiagnostics(p.TextDocument.Uri);
                }
                break;
            }

            case "textDocument/didChange":
            {
                var p = Decode<DidChangeTextDocumentParams>(msg);
                if (p is not null && p.ContentChanges.Count > 0)
                {
                    // Sync kind = Full → the last change carries the whole new document text.
                    var text = p.ContentChanges[^1].Text;
                    _store.Change(p.TextDocument.Uri, text, p.TextDocument.Version);
                    ScheduleDiagnostics(p.TextDocument.Uri);
                }
                break;
            }

            case "textDocument/didClose":
            {
                var p = Decode<DidCloseTextDocumentParams>(msg);
                if (p is not null)
                {
                    _scheduler.Cancel(p.TextDocument.Uri);
                    _store.Close(p.TextDocument.Uri);
                    // Clear diagnostics for the closed document.
                    await PublishAsync(new PublishDiagnosticsParams { Uri = p.TextDocument.Uri }).ConfigureAwait(false);
                }
                break;
            }

            case "textDocument/definition":
            {
                var p = Decode<TextDocumentPositionParams>(msg);
                var loc = p is null ? null : await _service.DefinitionAsync(p.TextDocument.Uri, p.Position, ct).ConfigureAwait(false);
                await Respond(JsonRpcMessage.ResultFor(msg.Id, (object?)loc)).ConfigureAwait(false);
                break;
            }

            case "textDocument/hover":
            {
                var p = Decode<TextDocumentPositionParams>(msg);
                var hover = p is null ? null : await _service.HoverAsync(p.TextDocument.Uri, p.Position, ct).ConfigureAwait(false);
                await Respond(JsonRpcMessage.ResultFor(msg.Id, (object?)hover)).ConfigureAwait(false);
                break;
            }

            case "textDocument/completion":
            {
                var p = Decode<TextDocumentPositionParams>(msg);
                var list = p is null ? new CompletionList() : await _service.CompletionAsync(p.TextDocument.Uri, p.Position, ct).ConfigureAwait(false);
                await Respond(JsonRpcMessage.ResultFor(msg.Id, list)).ConfigureAwait(false);
                break;
            }

            default:
                if (msg.IsRequest)
                    await Respond(JsonRpcMessage.ErrorFor(msg.Id, LspErrorCodes.MethodNotFound, $"Unknown method '{msg.Method}'.")).ConfigureAwait(false);
                break;
        }
    }

    private void HandleInitialize(JsonRpcMessage msg)
    {
        var p = Decode<InitializeParams>(msg);
        var rootPath = p?.RootPath ?? (p?.RootUri is { } u ? DocumentUri.ToPath(u) : null);
        var projectFile = WorkspaceProject.FindProjectFile(rootPath);
        _service = new LanguageService(_store, projectFile);
        _initialized = true;
    }

    /// <summary>Debounced re-parse → publishDiagnostics for one document (dropping a stale-version result).</summary>
    private void ScheduleDiagnostics(string uri)
    {
        _scheduler.Schedule(uri, async token =>
        {
            var result = await _service.DiagnoseAsync(uri, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); // superseded by a newer edit → do not publish a stale verdict
            await PublishAsync(new PublishDiagnosticsParams
            {
                Uri = result.Uri,
                Version = result.Version,
                Diagnostics = result.Diagnostics,
            }).ConfigureAwait(false);
        });
    }

    private Task PublishAsync(PublishDiagnosticsParams p) =>
        Notify("textDocument/publishDiagnostics", p);

    private Task Notify(string method, object @params) =>
        _writer.WriteAsync(JsonRpcMessage.Notification(method, @params).ToJson());

    private Task Respond(JsonRpcMessage msg) => _writer.WriteAsync(msg.ToJson());

    private static T? Decode<T>(JsonRpcMessage msg) => LspJson.Deserialize<T>(msg.Params);

    public void Dispose() => _scheduler.Dispose();
}
