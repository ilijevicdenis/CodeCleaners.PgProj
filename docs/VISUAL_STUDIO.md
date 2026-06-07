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

## Route B — VSIX project system ⏳ (scaffolded, not buildable here)

A full Visual Studio extension lives under [`editors/vs/`](../editors/vs/README.md): a `.pgproj`
project flavor (Solution Explorer object tree), property pages (build output, database settings,
SQLCMD variables, target platform), a Publish dialog, a Schema Compare window, and an `ILanguageClient`
that launches `pgproj serve` for `.sql` IntelliSense/go-to-definition.

> Route B targets .NET Framework + the **Visual Studio SDK** and can only be built inside **Visual
> Studio 2022 with the extension-development workload**. It is a **scaffold** (structure + integration
> seams marked `// SCAFFOLD`), kept in its own `editors/vs/PgProj.VisualStudio.sln`, **excluded from
> `PgProj.slnx`** and from `dotnet test`. It was **not** built/run in the headless environment where
> it was scaffolded.

The extension holds **no engine logic**: build/publish delegate to the Route-A SDK targets, the object
tree is a view over `pgproj model-tree --format json`, and Schema Compare renders
`pgproj compare … --format json`.

## How `.sql` IntelliSense attaches (both editors)

`pgproj serve` is a stock LSP server over STDIO (see [`docs/LSP_LANGUAGE_SERVER.md`](./LSP_LANGUAGE_SERVER.md)).

- **VS Code (#24):** `vscode-languageclient` spawns `pgproj serve` with `transport: stdio`,
  `DocumentSelector { language: "sql" }`.
- **Visual Studio (#25):** `PgProjLanguageClient : ILanguageClient` — `ActivateAsync` spawns
  `pgproj serve` and returns its stdout/stdin pair; MEF binds it to the `.sql` content type.

Both pass the workspace folder so the server resolves the `.pgproj`; the capabilities advertised at
`initialize` (diagnostics, definition, hover, completion) are the contract. No client-specific code
lives in the server.
