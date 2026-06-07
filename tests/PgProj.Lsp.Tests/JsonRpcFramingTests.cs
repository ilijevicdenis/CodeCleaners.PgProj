using System.IO;
using System.Text;
using System.Threading.Tasks;
using PgProj.Lsp.Protocol;
using Xunit;

namespace PgProj.Lsp.Tests;

/// <summary>The base-protocol framing must round-trip a Content-Length message and parse a JSON-RPC body.</summary>
public sealed class JsonRpcFramingTests
{
    [Fact]
    public async Task Writer_then_reader_round_trips_a_content_length_message()
    {
        var payload = JsonRpcMessage.Request("1", "initialize", new InitializeParams { RootUri = "file:///tmp/x" }).ToJson();

        using var ms = new MemoryStream();
        await new LspMessageWriter(ms).WriteAsync(payload);

        // The framed bytes must start with the Content-Length header and a blank line.
        var raw = Encoding.UTF8.GetString(ms.ToArray());
        Assert.StartsWith("Content-Length: ", raw);
        Assert.Contains("\r\n\r\n", raw);

        ms.Position = 0;
        var body = await new LspMessageReader(ms).ReadAsync();
        Assert.Equal(payload, body);

        var msg = JsonRpcMessage.FromJson(body!);
        Assert.Equal("initialize", msg.Method);
        Assert.True(msg.IsRequest);
    }

    [Fact]
    public async Task Reader_returns_null_at_clean_end_of_stream()
    {
        using var ms = new MemoryStream();
        Assert.Null(await new LspMessageReader(ms).ReadAsync());
    }

    [Fact]
    public async Task Reader_reads_two_back_to_back_messages()
    {
        using var ms = new MemoryStream();
        var w = new LspMessageWriter(ms);
        await w.WriteAsync(JsonRpcMessage.Notification("a").ToJson());
        await w.WriteAsync(JsonRpcMessage.Notification("b").ToJson());

        ms.Position = 0;
        var r = new LspMessageReader(ms);
        Assert.Equal("a", JsonRpcMessage.FromJson((await r.ReadAsync())!).Method);
        Assert.Equal("b", JsonRpcMessage.FromJson((await r.ReadAsync())!).Method);
        Assert.Null(await r.ReadAsync());
    }
}
