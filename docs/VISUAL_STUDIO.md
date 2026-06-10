# Visual Studio experience (EP-VS #25)

Give Visual Studio users the same database-project experience SSDT gives SQL Server users, via two
routes. Start light (the SDK builds in VS) before a full project system.

## Route A — `.pgproj` builds & publishes via the MSBuild SDK ✅ (validated)

`PgProj.Sdk` is an MSBuild **project SDK** (NuGet-packable) that routes Restore/Build/Clean/Rebuild/
**Publish** to the `pgproj` CLI. Visual Studio 2022's built-in support for SDK-style projects then
opens, builds, cleans, and publishes a `.pgproj` from Solution Explorer — no extension required.

- **Build** → `bin/<Name>.model.json` + `bin/<Name>.pgpkg` (the `.dacpac` analogue + portable package).
- **Publish** (right-click → Publish) → `pgproj publish` to a server, or an offline create-script preview.
- Packaged as `PgProj.Sdk` (carries the CLI under `tools/`): `<Project Sdk="PgProj.Sdk/0.1.0" DefaultTargets="Build">`.

Reference + validation: [`src/PgProj.Sdk/README.md`](../src/PgProj.Sdk/README.md). The model artifact
is byte-identical to a direct `pgproj build -o`; the `.pgpkg` matches once `PGPROJ_BUILD_STAMP` is pinned.

```powershell
dotnet build sample/SampleDb/SampleDb.pgproj -c Release      # build a sample -> model + .pgpkg
dotnet pack  src/PgProj.Sdk -c Release -o artifacts/sdk-pack # produce PgProj.Sdk.0.1.0.nupkg
```

## Route B — VS extensions (two-extension hybrid for VS 2026+)

Route B lives under [`editors/vs/`](../editors/vs/README.md) (the authoritative doc) and is a
**two-extension hybrid**:

- **`PgProj.VisualStudio`** — a modern **VisualStudio.Extensibility** OOP extension (net10): Publish +
  Schema Compare commands, a Remote-UI Schema Compare tool window, and `.sql` IntelliSense via an
  in-process LSP provider. The engine (`PgProj.Core`/`PgProj.Lsp`) is linked **in-process** (no
  `pgproj` subprocess); publish goes through the same shared `PublishService` as the CLI, so the
  deploy script and strategy are identical. **Builds and packages headless** with the .NET 10 SDK.
- **`PgProj.VisualStudio.ProjectSystem`** — a classic in-proc **VSSDK** extension (net472) that
  authors the `.pgproj` **project type** (the one thing the OOP model has no API for). It compiles and
  packages a `.vsix` with the VS 2026 full MSBuild, but the project factory / property pages are
  **stubs** — a *structural scaffold, not a working project system*. Stage 1+ (actually loading a
  `.pgproj`, File→New→Project, property pages, F5) is unproven and needs interactive VS 2026.

> Both extensions are intentionally **excluded from `PgProj.slnx`** and from `dotnet test`; the OOP
> extension has its own `editors/vs/PgProj.VisualStudio.slnx`. Even with neither installed, Route A
> alone makes a `.pgproj` an Open/Build/Publish citizen in Visual Studio.

## How `.sql` IntelliSense attaches (both editors)

`pgproj serve` is a stock LSP server over STDIO (see [`docs/LSP_LANGUAGE_SERVER.md`](./LSP_LANGUAGE_SERVER.md)).

- **VS Code (#24):** `vscode-languageclient` spawns `pgproj serve` with `transport: stdio`,
  `DocumentSelector { language: "sql" }`.
- **Visual Studio (#25):** the OOP extension's `PgProjLanguageServerProvider` hosts `LspServer`
  **in-process** over an in-memory `FullDuplexStream` pair — no subprocess; same server, different
  transport.

Both pass the workspace folder so the server resolves the `.pgproj`; the capabilities advertised at
`initialize` (diagnostics, definition, hover, completion) are the contract. No client-specific code
lives in the server.
