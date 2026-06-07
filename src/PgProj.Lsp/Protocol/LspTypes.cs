using System.Collections.Generic;

namespace PgProj.Lsp.Protocol;

// The subset of LSP structures this server speaks. LSP positions are ZERO-based (line + character); the
// engine's diagnostics/positions are ONE-based — the conversion is centralised in the workspace layer so
// only the wire boundary deals in zero-based coordinates.

/// <summary>A zero-based (line, character) source position, per the LSP spec.</summary>
public sealed record Position(int Line, int Character);

/// <summary>A half-open [start, end) span of <see cref="Position"/>s.</summary>
public sealed record Range(Position Start, Position End);

/// <summary>A document URI + a span within it.</summary>
public sealed record Location(string Uri, Range Range);

/// <summary>LSP diagnostic severity (1 = Error, 2 = Warning, 3 = Information, 4 = Hint).</summary>
public enum LspSeverity { Error = 1, Warning = 2, Information = 3, Hint = 4 }

/// <summary>One LSP diagnostic. <see cref="Code"/> carries the engine ruleId (e.g. <c>BUILD</c>, <c>PG001</c>).</summary>
public sealed record LspDiagnostic
{
    public required Range Range { get; init; }
    public LspSeverity Severity { get; init; }
    public string? Code { get; init; }
    public string Source { get; init; } = "pgproj";
    public required string Message { get; init; }
}

/// <summary>The <c>textDocument/publishDiagnostics</c> notification payload.</summary>
public sealed record PublishDiagnosticsParams
{
    public required string Uri { get; init; }
    public int? Version { get; init; }
    public IReadOnlyList<LspDiagnostic> Diagnostics { get; init; } = new List<LspDiagnostic>();
}

// ---- lifecycle ------------------------------------------------------------------------------

public sealed record InitializeParams
{
    public string? RootUri { get; init; }
    public string? RootPath { get; init; }
    public int? ProcessId { get; init; }
}

public sealed record TextDocumentSyncOptions
{
    public bool OpenClose { get; init; } = true;
    /// <summary>1 = full document text on each change (we re-parse the whole buffer; the parser is fast enough).</summary>
    public int Change { get; init; } = 1;
}

public sealed record CompletionOptions
{
    public IReadOnlyList<string> TriggerCharacters { get; init; } = new[] { ".", " " };
}

public sealed record ServerCapabilities
{
    public TextDocumentSyncOptions TextDocumentSync { get; init; } = new();
    public bool DefinitionProvider { get; init; } = true;
    public bool HoverProvider { get; init; } = true;
    public CompletionOptions CompletionProvider { get; init; } = new();
}

public sealed record ServerInfo
{
    public string Name { get; init; } = "pgproj-language-server";
    public string? Version { get; init; }
}

public sealed record InitializeResult
{
    public required ServerCapabilities Capabilities { get; init; }
    public ServerInfo ServerInfo { get; init; } = new();
}

// ---- text document sync ---------------------------------------------------------------------

public sealed record TextDocumentItem
{
    public required string Uri { get; init; }
    public string LanguageId { get; init; } = "sql";
    public int Version { get; init; }
    public required string Text { get; init; }
}

public sealed record DidOpenTextDocumentParams
{
    public required TextDocumentItem TextDocument { get; init; }
}

public sealed record VersionedTextDocumentIdentifier
{
    public required string Uri { get; init; }
    public int Version { get; init; }
}

public sealed record TextDocumentContentChangeEvent
{
    /// <summary>Full-document text (sync kind = Full); range-based incremental changes are not negotiated.</summary>
    public required string Text { get; init; }
}

public sealed record DidChangeTextDocumentParams
{
    public required VersionedTextDocumentIdentifier TextDocument { get; init; }
    public IReadOnlyList<TextDocumentContentChangeEvent> ContentChanges { get; init; } =
        new List<TextDocumentContentChangeEvent>();
}

public sealed record TextDocumentIdentifier
{
    public required string Uri { get; init; }
}

public sealed record DidCloseTextDocumentParams
{
    public required TextDocumentIdentifier TextDocument { get; init; }
}

// ---- position-driven features ---------------------------------------------------------------

public sealed record TextDocumentPositionParams
{
    public required TextDocumentIdentifier TextDocument { get; init; }
    public required Position Position { get; init; }
}

public sealed record MarkupContent
{
    public string Kind { get; init; } = "markdown";
    public required string Value { get; init; }
}

public sealed record Hover
{
    public required MarkupContent Contents { get; init; }
    public Range? Range { get; init; }
}

/// <summary>LSP CompletionItemKind subset we emit (Module=9, Field=5, Function=3, Class=7, Keyword=14).</summary>
public enum CompletionItemKind { Function = 3, Field = 5, Class = 7, Module = 9, Keyword = 14, Struct = 22 }

public sealed record CompletionItem
{
    public required string Label { get; init; }
    public CompletionItemKind Kind { get; init; }
    public string? Detail { get; init; }
    public string? InsertText { get; init; }
}

public sealed record CompletionList
{
    public bool IsIncomplete { get; init; }
    public IReadOnlyList<CompletionItem> Items { get; init; } = new List<CompletionItem>();
}
