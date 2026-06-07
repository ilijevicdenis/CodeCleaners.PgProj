# Resident language service (`pgproj serve`) — EP-LSP

`pgproj serve` is a long-running **Language Server Protocol** host: the same parser/semantic engine the
batch CLI uses, hosted so an editor gets live diagnostics, go-to-definition, hover, and completion **as you
type** — with the exact same accept/reject verdict as `pgproj build` / `pgproj analyze`. It is the
companion to the per-invocation `--format json` contract (EP-RPC, see `JSON_CONTRACT.md`): use `--format
json` for build/analyze/compare/CI; use `serve` for an interactive editor.

This is a **hosting/transport layer over the existing engine**, not new parsing logic. Diagnostics come
from the build (`DatabaseProject.BuildAsync` → `UnifiedDiagnostics`); definition/hover/completion are backed
by the model tree (`ModelTreeBuilder`) and its `SourcePositionIndex` source anchors.

## Transport

- **STDIO**, JSON-RPC 2.0 with the standard LSP base-protocol framing: each message is
  `Content-Length: <n>\r\n\r\n` followed by exactly `<n>` UTF-8 bytes of JSON. (Bare-LF line endings in the
  header are also tolerated.)
- **stdout is the wire** — the server writes nothing else to it. The CLI redirects any stray engine
  `Console.Out` to stderr for the lifetime of the loop, so log/diagnostic chatter never corrupts the
  protocol.
- Run it as: `pgproj serve [<workspace-dir>] [--debounce <ms>]`. The workspace dir is optional — the
  `rootUri`/`rootPath` from the LSP `initialize` request is used when present.

## Methods implemented

| Method | Kind | Behaviour |
|--------|------|-----------|
| `initialize` | request | Resolves the workspace `.pgproj` (from `rootUri`/`rootPath` or the CLI arg) and returns `ServerCapabilities` (full-text sync, definition, hover, completion with `.`/space triggers). |
| `initialized` | notification | No-op. |
| `shutdown` | request | Flushes in-flight diagnostics, returns `null`. |
| `exit` | notification | Stops the loop; exit code 0 if it followed `shutdown`, else 1. |
| `textDocument/didOpen` | notification | Stores the buffer, schedules a debounced diagnose. |
| `textDocument/didChange` | notification | Updates the buffer (sync kind = **Full**: the last change carries the whole new text), schedules a debounced diagnose. |
| `textDocument/didClose` | notification | Drops the buffer and publishes an empty diagnostic set for it. |
| `textDocument/publishDiagnostics` | server→client notification | The diagnose result (see below). |
| `textDocument/definition` | request | `Location` of the defining `file:line`, or `null`. |
| `textDocument/hover` | request | A markdown `Hover` card naming the resolved object + where it is defined. |
| `textDocument/completion` | request | A `CompletionList` of project symbols in scope. |

Any request before `initialize` is rejected with JSON-RPC error `-32002` (ServerNotInitialized); an unknown
method gets `-32601` (MethodNotFound); malformed JSON gets `-32700` (ParseError).

## Diagnostics — identical verdict to the batch path

On `didOpen`/`didChange`, the server (after a debounce window) re-analyses:

- **With a `.pgproj`** — it runs `DatabaseProject.BuildAsync` with an *open-buffer overlay*
  (`ObjectContentTransform`): every open document's UNSAVED text is substituted for its on-disk text; all
  other files parse from disk. The build's `UnifiedDiagnostics` whose file anchor is the edited document
  (plus any file-less build findings) are projected to LSP `Diagnostic`s. This reuses the engine's exact
  diagnostic pipeline — parser errors *and* duplicate-definition findings — so the live verdict equals
  `pgproj build` for the saved tree, while picking up in-flight edits.
- **Without a project** (a loose buffer) — it runs the build's single-file pieces directly
  (`PgParser` + `ModelBuilder` + the same duplicate scan), so a parser reject here is the reject the batch
  path would produce.

Engine line/col are 1-based; LSP is 0-based — converted at the wire boundary (`LineIndex`). A finding with
an unknown anchor (line 0) is placed at the top of the file so it is still surfaced. The diagnostic `code`
carries the engine ruleId (`BUILD`, `PGxxx`); severity maps Error/Warning/Info → 1/2/3.

The published notification carries the document `version` it was computed against, and a run superseded by a
newer edit never publishes (see debounce/cancellation), so a stale verdict can never overwrite a fresh one.

## Definition / hover / completion — model-tree backed

All three resolve the identifier under the cursor (a dotted `[A-Za-z0-9_.]` run) against the **model tree**
built from the project (or the union of open buffers when there is no project):

- **definition** → the matching node's `SourcePositionIndex` anchor (`file:line:col`) as an LSP `Location`.
- **hover** → a markdown card: object kind + qualified name + "defined in file:line".
- **completion** → after a `schema.`/`table.` dotted prefix, that container's members (a schema's objects, a
  table's columns); otherwise every top-level object (schemas/tables/views/sequences/functions/raw objects)
  plus a small SQL keyword set.

## Debounce & cancellation

`DebouncedAnalysisScheduler` is per-document: each edit cancels the document's pending/in-flight run and
starts a fresh one after a quiet window (default 150 ms, `--debounce`). A burst of keystrokes therefore
triggers exactly one analysis (the last), and an analysis still running when a newer edit lands has its
`CancellationToken` tripped and its result discarded — it never publishes. The token threads into
`BuildAsync`, so a superseded parse is abandoned promptly. On `shutdown` the scheduler drains so the last
diagnostics flush before the loop stops.

## Architecture — handlers vs transport (testability)

The analysis is decoupled from the STDIO loop so handlers are unit-testable with synthetic payloads, no
process:

```
src/PgProj.Lsp/
  Protocol/    JsonRpc.cs (loose message + codes), MessageStream.cs (Content-Length reader/writer), LspTypes.cs (LSP DTOs)
  Workspace/   LineIndex (offset↔line/col, word-at), DocumentStore/LiveDocument, DocumentUri, WorkspaceProject (overlay loader)
  Debounce/    DebouncedAnalysisScheduler (debounce + supersede-on-newer-edit)
  Handlers/    LanguageService  ← PURE: documents + project model → diagnostics/definition/hover/completion
  Server/      LspServer        ← thin STDIO dispatch loop over arbitrary streams
src/PgProj.Cli/Program.cs       ← `serve` verb: stdin/stdout → LspServer
```

`LanguageService` has no transport, no timers, no stdin/stdout — every method takes a snapshot and returns a
result, so tests drive it directly. `LspServer` is constructed over arbitrary streams, so an integration
test pumps it with in-memory framed bytes. No new NuGet dependencies — the JSON-RPC reader/writer is
hand-rolled on `System.Text.Json`.

## How editor clients attach (EP-VSCODE #24 / EP-VS #25)

The server is a stock LSP server over STDIO, so any LSP client library attaches by spawning the process and
wiring its stdin/stdout:

- **VS Code (EP-VSCODE #24)** — `vscode-languageclient`: a `ServerOptions` with
  `command: "pgproj", args: ["serve"], transport: TransportKind.stdio`, and a `DocumentSelector` of
  `{ language: "sql" }` (or a `pgsql`/`.sql` file pattern). The extension passes the workspace folder as
  `rootUri` in `initialize`. The #24 UI suite (headless `@vscode/test-electron`) exercises squiggle/hover/
  definition against this exact server.
- **Visual Studio (EP-VS #25)** — an `ILanguageClient` implementation whose `ActivateAsync` returns the
  `pgproj serve` process's stream pair.

No client-specific code lives in the server; the capabilities advertised at `initialize` are the contract.
