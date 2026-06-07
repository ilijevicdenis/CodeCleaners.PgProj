using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PgProj.Lsp.Protocol;
using PgProj.Lsp.Server;
using Xunit;

namespace PgProj.Lsp.Tests;

/// <summary>
/// End-to-end over the STDIO transport (no process): we pre-frame a session's client→server messages into an
/// input stream, run the server to stdin-EOF, then parse the framed server→client output. This exercises the
/// real reader/writer + dispatch, asserting the lifecycle handshake and a didChange→publishDiagnostics flow.
/// </summary>
public sealed class LspServerEndToEndTests
{
    private static byte[] Frame(IEnumerable<string> jsons)
    {
        using var ms = new MemoryStream();
        var w = new LspMessageWriter(ms);
        foreach (var j in jsons) w.WriteAsync(j).GetAwaiter().GetResult();
        return ms.ToArray();
    }

    private static IReadOnlyList<JsonRpcMessage> Drain(byte[] output)
    {
        using var ms = new MemoryStream(output);
        var r = new LspMessageReader(ms);
        var msgs = new List<JsonRpcMessage>();
        while (true)
        {
            var body = r.ReadAsync().GetAwaiter().GetResult();
            if (body is null) break;
            msgs.Add(JsonRpcMessage.FromJson(body));
        }
        return msgs;
    }

    [Fact]
    public async Task Initialize_didOpen_didChange_publishes_diagnostics_then_shutdown_exit()
    {
        using var tp = new TempProject();
        var rel = "t.sql";
        tp.WriteSql(rel, "CREATE TABLE public.t (id int);\n");
        var uri = tp.UriFor(rel);

        var session = new[]
        {
            JsonRpcMessage.Request("1", "initialize", new InitializeParams { RootUri = DocumentUriOf(tp.Dir) }).ToJson(),
            JsonRpcMessage.Notification("initialized").ToJson(),
            JsonRpcMessage.Notification("textDocument/didOpen", new DidOpenTextDocumentParams
            {
                TextDocument = new TextDocumentItem { Uri = uri, Version = 1, Text = "CREATE TABLE public.t (id int);\n" },
            }).ToJson(),
            JsonRpcMessage.Notification("textDocument/didChange", new DidChangeTextDocumentParams
            {
                TextDocument = new VersionedTextDocumentIdentifier { Uri = uri, Version = 2 },
                ContentChanges = new[] { new TextDocumentContentChangeEvent { Text = "CREATE TABLE public.t (id int" } },
            }).ToJson(),
            JsonRpcMessage.Request("2", "shutdown").ToJson(),
            JsonRpcMessage.Notification("exit").ToJson(),
        };

        using var input = new MemoryStream(Frame(session));
        using var output = new MemoryStream();
        using var server = new LspServer(input, output, debounceMs: 20);
        var exit = await server.RunAsync();

        Assert.Equal(0, exit); // exit after shutdown → 0

        var msgs = Drain(output.ToArray());

        // initialize result advertises capabilities.
        var init = msgs.First(m => m.Id?.ToString() == "1");
        var caps = init.Result!["capabilities"]!;
        Assert.True(caps["definitionProvider"]!.GetValue<bool>());
        Assert.True(caps["hoverProvider"]!.GetValue<bool>());

        // a publishDiagnostics for the broken buffer (version 2) with at least one error.
        var publishes = msgs.Where(m => m.Method == "textDocument/publishDiagnostics").ToList();
        Assert.NotEmpty(publishes);
        var last = publishes[^1];
        var diags = last.Params!["diagnostics"]!.AsArray();
        Assert.NotEmpty(diags);

        // shutdown got a (successful, error-free) response.
        Assert.Contains(msgs, m => m.Id?.ToString() == "2" && m.Error is null);
    }

    [Fact]
    public async Task Request_before_initialize_is_rejected()
    {
        var session = new[]
        {
            JsonRpcMessage.Request("9", "textDocument/hover", new TextDocumentPositionParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = "file:///x.sql" },
                Position = new Position(0, 0),
            }).ToJson(),
            JsonRpcMessage.Notification("exit").ToJson(),
        };

        using var input = new MemoryStream(Frame(session));
        using var output = new MemoryStream();
        using var server = new LspServer(input, output);
        await server.RunAsync();

        var msgs = Drain(output.ToArray());
        var resp = msgs.First(m => m.Id?.ToString() == "9");
        Assert.NotNull(resp.Error);
        Assert.Equal(LspErrorCodes.ServerNotInitialized, resp.Error!.Code);
    }

    private static string DocumentUriOf(string dir) => new System.Uri(dir + System.IO.Path.DirectorySeparatorChar).AbsoluteUri;
}
