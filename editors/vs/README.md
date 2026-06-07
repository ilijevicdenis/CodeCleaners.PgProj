# PgProj — Visual Studio experience (EP-VS #25)

Two routes give Visual Studio users the same database-project experience SSDT gives SQL Server users.
The issue (and the SSDT-parity backlog) recommends starting light: get `.pgproj` building cleanly via
the SDK before investing in a full project system — even Microsoft ships the modern experience in VS
Code first, and SDK-style VS support is still preview.

| Route | What | Status | Buildable in this repo? |
|---|---|---|---|
| **A** | `.pgproj` builds/publishes via the **MSBuild SDK** (`PgProj.Sdk`), opened by VS's built-in generic project support | **Done + validated** | ✅ yes (`dotnet build` / `dotnet pack`) |
| **B** | A **VSIX project system** (this folder) — Solution Explorer object tree, property pages, Publish dialog, Schema Compare window, `.sql` IntelliSense via LSP | **Scaffolded, not built** | ❌ no — needs Visual Studio + the VS SDK |

---

## Route A — `.pgproj` is a clean `dotnet build` citizen (validated)

Lives in [`src/PgProj.Sdk`](../../src/PgProj.Sdk/). It is an MSBuild **project SDK**, NuGet-packable,
that routes Restore/Build/Clean/Rebuild/**Publish** to the `pgproj` CLI. Because VS 2022 can open and
build any SDK-style project, this alone gives a Visual Studio user **Open → Build → Rebuild → Clean →
Publish** on a `.pgproj` from Solution Explorer, with no custom extension installed.

- **Build** → `pgproj build` → `bin/<Name>.model.json` + `bin/<Name>.pgpkg`.
- **Publish** (right-click → Publish, or `-t:Publish`) → `pgproj publish` against a connection, or an
  offline create-script preview when no connection is set.
- Packaged on NuGet as `PgProj.Sdk` (carries the CLI under `tools/`), so a project anywhere can say
  `<Project Sdk="PgProj.Sdk/0.1.0" DefaultTargets="Build">`.

See [`src/PgProj.Sdk/README.md`](../../src/PgProj.Sdk/README.md) for the full property/verb reference
and the validation notes (model artifact is byte-identical to the CLI's; `.pgpkg` matches once the
build timestamp is pinned).

**Validated here** (Windows, .NET 10 SDK, no VS SDK):

```powershell
# Build a sample .pgproj through the SDK (emits model + .pgpkg).
dotnet build sample/SampleDb/SampleDb.pgproj -c Release

# Pack the SDK into a .nupkg (Sdk/Sdk.props + Sdk/Sdk.targets + tools/PgProj.Cli.dll).
dotnet pack src/PgProj.Sdk -c Release -o artifacts/sdk-pack

# Consume the package: a .pgproj using Sdk="PgProj.Sdk/0.1.0" builds via the packaged CLI.
dotnet build <pkg-consumer>/PkgDb.pgproj -c Release    # -> bin/PkgDb.model.json + PkgDb.pgpkg
```

---

## Route B — the VSIX project system (scaffolded in this folder; NOT built here)

> **This project cannot be built in this repository / a headless environment.** It targets the classic
> .NET Framework and the **Visual Studio SDK** (`Microsoft.VSSDK.BuildTools`,
> `Microsoft.VisualStudio.*`, `Microsoft.VisualStudio.ProjectSystem.SDK`). Building it requires
> **Windows + Visual Studio 2022 (17.x) with the "Visual Studio extension development" workload**.
> It is intentionally **kept out of `PgProj.slnx`** and is **not** part of `dotnet test`, so it can
> never break the cross-platform build/test gates. It has its **own** solution,
> [`PgProj.VisualStudio.sln`](./PgProj.VisualStudio.sln).
>
> Everything below is a **scaffold**: it lays out the structure, registrations, and the
> engine-integration seams, with `// SCAFFOLD` / `TODO` markers where the VS-SDK-dependent body goes.
> The package versions in the `.csproj` are placeholders to pin when the VS SDK is available. The
> author **could not build or run it** in the environment where it was scaffolded.

### Layout

```
editors/vs/
  PgProj.VisualStudio.sln                      standalone solution (NOT in PgProj.slnx)
  PgProj.VisualStudio/
    PgProj.VisualStudio.csproj                 VSIX project (.NET Framework 4.7.2 + VS SDK)
    source.extension.vsixmanifest              VSIX manifest (package + project-type + MEF assets)
    PgProjCommands.vsct                        command table (Publish / Schema Compare on the project menu)
    PgProjPackage.cs                           AsyncPackage entry point; registers everything
    PgProjGuids.cs                             stable package / project-type / command-set GUIDs
    ProjectSystem/
      PgProjProjectFactory.cs                  factory for *.pgproj
      PgProjUnconfiguredProject.cs             flavored project + Solution Explorer object tree
    Properties/
      PgProjBuildPropertiesPage.cs             build output
      PgProjDatabasePropertiesPage.cs          database / publish settings
      PgProjSqlCmdVariablesPage.cs             SQLCMD variables grid
      PgProjTargetPlatformPage.cs              target PostgreSQL version
    Commands/
      PublishCommand.cs                        Publish dialog → engine
      SchemaCompareCommand.cs                  opens the Schema Compare window
    LanguageClient/
      PgProjLanguageClient.cs                  ILanguageClient: launches `pgproj serve` (LSP)
      PgProjContentDefinition.cs               binds .sql → the PgProj content type
    ToolWindows/
      SchemaCompareToolWindow.cs               renders the engine's diff JSON
```

### Design principle — no logic in the extension

The extension is **presentation + plumbing only**. Build, publish, compare, and the model tree all
come from the existing engine, exactly as the VS Code extension (#24) consumes them:

- **Build / Clean / Publish** delegate to the Route-A MSBuild SDK targets (or `pgproj` directly), so
  there is one publish/compare code path shared across CLI, SDK, VS Code, and VS.
- **Solution Explorer object tree** is a view over `pgproj model-tree --format json` (EP-RPC).
- **Schema Compare window** renders the structured diff from `pgproj compare … --format json`
  (EP-SCHEMACOMPARE).
- **Property pages** read/write the `.pgproj` `<PropertyGroup>` / `<SqlCmdVariable>` items the engine
  and SDK already understand.

### `.sql` IntelliSense — how the LSP client attaches

`LanguageClient/PgProjLanguageClient.cs` implements VS's `ILanguageClient`. Its `ActivateAsync`
spawns the **same stock LSP server** the VS Code extension uses — `pgproj serve` over STDIO
(JSON-RPC 2.0 with `Content-Length` framing; see [`docs/LSP_LANGUAGE_SERVER.md`](../../docs/LSP_LANGUAGE_SERVER.md))
— and returns its `stdout`/`stdin` as the VS `Connection`. Details:

- The client is exported via **MEF** as `ILanguageClient` and matched to the `PgProjSql` content type
  (`PgProjContentDefinition.cs` maps the `.sql` extension to it) — VS activates it automatically for
  SQL editors in a PgProj solution.
- The solution/workspace folder is passed as the optional positional arg to `pgproj serve` so the
  server resolves the `.pgproj` before the LSP `initialize` arrives (it also reads `rootUri` from the
  handshake).
- The server advertises full-text sync, definition, hover, and completion (`.`/space triggers) at
  `initialize` — those capabilities are the contract, identical to what VS Code drives. No
  client-specific code lives in the server.
- Server logs go to **stderr**; `stdout` is the protocol wire and carries nothing else.

This mirrors the doc's two-client story: VS Code uses `vscode-languageclient`; Visual Studio uses
`ILanguageClient`. Both spawn `pgproj serve` and wire stdin/stdout.

### Building it (when you have the VS SDK)

```text
1. Install Visual Studio 2022 with the "Visual Studio extension development" workload.
2. Open editors/vs/PgProj.VisualStudio.sln in Visual Studio.
3. Pin the placeholder VS-SDK package versions in PgProj.VisualStudio.csproj, restore, build.
4. F5 launches the VS Experimental Instance (/rootsuffix Exp) with the extension loaded.
```

### Follow-ups (Route B is a skeleton)

- Pin real VS-SDK package versions and restore against the VS SDK feeds.
- Implement the CPS hierarchy that projects the model tree into Solution Explorer.
- Build the WPF views (publish dialog, Schema Compare control) over the engine JSON.
- Wire the property pages to `IVsBuildPropertyStorage` for read/write persistence.
- `slngen`-style grouping for multiple `.pgproj`s (issue task 4).
- VS Apex / VS SDK UI tests (open solution, Build/Publish from Solution Explorer, completion in `.sql`).
