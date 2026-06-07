using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

namespace PgProj.Lsp.Workspace;

/// <summary>One open editor buffer: its URI, current text + version, and a lazily-built <see cref="LineIndex"/>.</summary>
public sealed class LiveDocument
{
    public string Uri { get; }
    public string Text { get; private set; }
    public int Version { get; private set; }

    private LineIndex? _lines;

    public LiveDocument(string uri, string text, int version)
    {
        Uri = uri;
        Text = text;
        Version = version;
    }

    public LineIndex Lines => _lines ??= new LineIndex(Text);

    public void Update(string text, int version)
    {
        Text = text;
        Version = version;
        _lines = null; // invalidate — rebuilt on next access against the new text
    }
}

/// <summary>
/// The set of open documents, keyed by URI. Thread-safe so the (async) request loop and a debounced
/// analysis callback can both consult it. Holds only buffer state — no analysis — so it stays trivially
/// testable and the analysis layer can be driven directly from a snapshot.
/// </summary>
public sealed class DocumentStore
{
    private readonly ConcurrentDictionary<string, LiveDocument> _docs = new(StringComparer.Ordinal);

    public LiveDocument Open(string uri, string text, int version)
    {
        var doc = new LiveDocument(uri, text, version);
        _docs[uri] = doc;
        return doc;
    }

    public LiveDocument? Change(string uri, string text, int version)
    {
        if (_docs.TryGetValue(uri, out var doc)) { doc.Update(text, version); return doc; }
        return _docs[uri] = new LiveDocument(uri, text, version);
    }

    public void Close(string uri) => _docs.TryRemove(uri, out _);

    public LiveDocument? Get(string uri) => _docs.TryGetValue(uri, out var d) ? d : null;

    public IReadOnlyCollection<LiveDocument> All => (IReadOnlyCollection<LiveDocument>)_docs.Values;
}

/// <summary>
/// Translates between LSP <c>file://</c> URIs and local filesystem paths. Kept here (not inlined) because
/// the diagnostics path must turn a URI into the project-relative path the engine's diagnostics carry, and
/// the definition path must turn an engine file anchor back into a URI.
/// </summary>
public static class DocumentUri
{
    /// <summary>A <c>file://</c> URI → an absolute local path; a non-file/opaque URI is returned verbatim.</summary>
    public static string ToPath(string uri)
    {
        if (string.IsNullOrEmpty(uri)) return uri;
        if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.IsFile)
            return u.LocalPath;
        return uri;
    }

    /// <summary>An absolute local path → a <c>file://</c> URI.</summary>
    public static string FromPath(string path)
    {
        var full = Path.GetFullPath(path);
        return new Uri(full).AbsoluteUri;
    }
}
