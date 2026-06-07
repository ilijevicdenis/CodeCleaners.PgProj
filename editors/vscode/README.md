# PostgreSQL Database Projects (VS Code)

An SSDT-style declarative database-project experience for **PostgreSQL**, mirroring Microsoft's
**SQL Database Projects** extension but driven by the `pgproj` engine. Build, analyze, compare, and
publish `.pgproj` projects without leaving the editor — with build/analysis findings in the
**Problems** panel, a schema/object tree in the **Projects** view, and **live** diagnostics, hover,
go-to-definition, and completion as you type (via the `pgproj serve` language server).

> EP-VSCODE #24. The Publish dialog and Schema Compare diff grid are full webviews; the table
> designer remains a follow-up (see *Follow-ups* below).

## Requirements

This extension is a thin UI over the `pgproj` engine. You need:

- **The `pgproj` engine.** Either a `pgproj` executable on your `PATH`, or point the
  `pgproj.cliPath` setting at the engine. You may point it directly at the built CLI **DLL**
  (`src/PgProj.Cli/bin/Debug/net10.0/PgProj.Cli.dll`) — the extension then runs it via the .NET host.
- **The .NET SDK** (.NET 10), when `pgproj.cliPath` is a `.dll` (the extension invokes `dotnet <dll>`),
  exactly as the SQL Database Projects extension depends on the .NET SDK / DacFx.

### Settings

| Setting | Default | Purpose |
|---------|---------|---------|
| `pgproj.cliPath` | `pgproj` | Path to the engine executable, or a `*.dll` to run via `dotnet`. |
| `pgproj.dotnetPath` | `dotnet` | The .NET host used when `cliPath` ends in `.dll`. |
| `pgproj.lsp.debounceMs` | `150` | Debounce window (ms) for the live language server diagnose pass. |

Example `settings.json` pointing at a locally built engine:

```json
{
  "pgproj.cliPath": "C:/repos/Postgres-database-project/src/PgProj.Cli/bin/Debug/net10.0/PgProj.Cli.dll"
}
```

## Features

- **Projects panel** — discovers every `.pgproj` in the workspace and renders
  *project → object-kind folders → objects → columns* from the engine's `model-tree --format json`.
  Clicking an object navigates to its `file:line`. Auto-refreshes after a build and on `.pgproj` changes.
- **Commands** (context-menu on a project + command palette):
  - **Build** — runs `pgproj build --format json`; pushes diagnostics to Problems.
  - **Publish** — a webview dialog: connection picker, SQLCMD-variable grid, options
    (`--allow-drops`, `--no-transaction`), **Save as Profile** (`.pgpublish.json`, never the secret),
    **Generate Script** (dry-run), and **Publish** (dry-run preview → confirm → deploy).
  - **Generate Script** — opens the full create script (`pgproj script`) in an editor.
  - **Validate** — `pgproj validate` against a throwaway database.
  - **Run Code Analysis** — `pgproj analyze --format json`; findings → Problems.
  - **Schema Compare** — a webview: source/target endpoint pickers + a checkable diff rendered from
    `pgproj compare --source X --target Y -o diff.json`, with Script/Apply over the checked subset.
  - **Add Object…** — Table / View / Function from a built-in template (EP-TEMPLATES will replace these).
  - **Set Target Version** — edits `<TargetPostgresVersion>` in the `.pgproj`.
  - **Edit Project File**, **New Project**, **Open Project**.
- **Live language features** — opening a `.sql` file starts the `pgproj serve` language server
  (`vscode-languageclient` over STDIO), giving as-you-type diagnostics, hover, go-to-definition, and
  completion with the **same** accept/reject verdict as `pgproj build`.
- **Problems integration** — build + analysis diagnostics from the JSON contract are mapped to
  `vscode.Diagnostic`s with the correct `file:line:col`, so errors show as squiggles and Problems
  entries (the build-time path); the language server owns the live, per-keystroke path.

## How it talks to the engine

Every command shells out to the `pgproj` CLI and consumes its **versioned JSON contract**
(`docs/JSON_CONTRACT.md`, `schemaVersion 1.0`): `build`, `analyze`, `compare`,
`publish --dry-run`, and `model-tree`, each with `--format json`. All spawning is funnelled through
`src/engine/engine.ts` (a single `PgProjEngine`), so arg-building and JSON parsing are unit-tested with
a mocked spawner. The extension refuses a JSON `schemaVersion` major it does not understand.

## Development

```bash
npm install
npm run compile      # tsc type-check + esbuild bundle -> dist/extension.js
npm run lint
npm run test:unit    # Vitest, mocked engine (no VS Code host needed)
npm run test:e2e     # @vscode/test-electron (downloads VS Code; needs net + .NET SDK)
```

Press **F5** in this folder to launch an Extension Development Host with the sample
`sample/AllFeaturesDb` project loaded.

## Follow-ups

- **Table designer** — EP-DESIGNER (#26).
- **Subset apply** — the Schema Compare "Apply (selected)" currently scripts the checked subset for
  the user to run; a first-class engine verb that applies a specific change-id subset is pending.
- **Marketplace packaging** + `.code-workspace` multi-project polish.
