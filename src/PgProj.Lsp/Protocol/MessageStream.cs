using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PgProj.Lsp.Protocol;

/// <summary>
/// Reads LSP base-protocol messages off a byte stream using the standard <c>Content-Length</c> framing:
/// each message is a small set of <c>Header: value\r\n</c> lines, a blank <c>\r\n</c>, then exactly
/// <c>Content-Length</c> UTF-8 bytes of JSON. Only <c>Content-Length</c> is required; other headers
/// (e.g. <c>Content-Type</c>) are tolerated and ignored. Decoupled from the JSON-RPC layer so the framing
/// can be round-tripped in isolation by the unit tests (no process, no transport assumptions).
/// </summary>
public sealed class LspMessageReader
{
    private readonly Stream _stream;

    public LspMessageReader(Stream stream) => _stream = stream;

    /// <summary>
    /// Reads the next framed message body as a UTF-8 string, or null at clean end-of-stream (the peer
    /// closed stdin). Throws <see cref="InvalidDataException"/> on a malformed header block.
    /// </summary>
    public async Task<string?> ReadAsync(CancellationToken ct = default)
    {
        int contentLength = -1;

        // Header section: CRLF-terminated lines until a blank line. Read byte-by-byte so we never consume
        // into the body (the body is raw, length-delimited, not line-delimited).
        while (true)
        {
            var line = await ReadHeaderLineAsync(ct).ConfigureAwait(false);
            if (line is null) return null;            // EOF before any header → stream closed
            if (line.Length == 0) break;              // blank line → end of headers

            var colon = line.IndexOf(':');
            if (colon <= 0) continue;                 // tolerate odd lines rather than crash the loop
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(value, out var len))
                contentLength = len;
        }

        if (contentLength < 0)
            throw new InvalidDataException("LSP message header had no Content-Length.");

        var body = new byte[contentLength];
        var read = 0;
        while (read < contentLength)
        {
            var n = await _stream.ReadAsync(body.AsMemory(read, contentLength - read), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("Stream closed mid-message body.");
            read += n;
        }
        return Encoding.UTF8.GetString(body);
    }

    /// <summary>Reads one CRLF-terminated header line (sans the terminator), or null at EOF.</summary>
    private async Task<string?> ReadHeaderLineAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        var any = false;
        while (true)
        {
            var n = await _stream.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (n == 0) return any ? sb.ToString() : null;   // EOF
            any = true;
            var c = (char)one[0];
            if (c == '\n') return TrimCr(sb);                // tolerate bare LF as well as CRLF
            sb.Append(c);
        }
    }

    private static string TrimCr(StringBuilder sb)
    {
        if (sb.Length > 0 && sb[^1] == '\r') sb.Length--;
        return sb.ToString();
    }
}

/// <summary>
/// Writes LSP base-protocol messages with <c>Content-Length</c> framing. Serialization is serialized by a
/// lock so concurrent producers (the request loop and the debounce timer publishing diagnostics) never
/// interleave bytes on the wire.
/// </summary>
public sealed class LspMessageWriter
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LspMessageWriter(Stream stream) => _stream = stream;

    /// <summary>Frames and writes one JSON body, flushing the stream so the peer sees it promptly.</summary>
    public async Task WriteAsync(string json, CancellationToken ct = default)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(header, ct).ConfigureAwait(false);
            await _stream.WriteAsync(payload, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
