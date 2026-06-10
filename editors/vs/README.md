# PgProj — Visual Studio experience (EP-VS #25)

Two routes give Visual Studio users the same database-project experience SSDT gives SQL Server users.

| Route | What | Status | Buildable in this repo? |
|---|---|---|---|
| **A** | `.pgproj` builds/publishes via the **MSBuild SDK** (`PgProj.Sdk`), opened by VS's built-in generic SDK-style project support | **Done + validated** | ✅ yes (`dotnet build` / `dotnet pack`) |
| **B** | A **two-extension hybrid** for VS 2026+: a modern **VisualStudio.Extensibility** OOP extension (Publish & Schema Compare commands, a Schema Compare tool window, `.sql` IntelliSense via an LSP provider) **plus** a classic in-proc **VSSDK project system** that authors the `.pgproj` project type (File→New→Project, Solution Explorer tree, property pages) | **OOP: builds + packages headless. Project system: compiles + packages a `.vsix` with VS 2026 MSBuild; runtime/F5 still unproven (scaffold).** | ⚠️ partly — OOP via `dotnet build`; the project system needs the VS 2026 full MSBuild (`dotnet` cannot build a classic VSIX) |

> **Why two extensions (the hybrid).** The out-of-process VisualStudio.Extensibility model has **no API to
> author a custom project type** — Project Query only reads/modifies *existing* projects. So the `.pgproj`
> project flavour (own Solution Explorer object tree + property pages + File→New→Project template) is
> carried by a **separate classic in-proc VSSDK extension** (`PgProj.VisualStudio.ProjectSystem`, net472),
> while the modern OOP extension (`PgProj.VisualStudio`, net10) carries everything the OOP model *can* do
> (commands, tool window, `.sql` language server). They ship as two VSIXes and share the engine
> (`PgProj.Core`/`PgProj.Lsp`). (Route A's `PgProj.Sdk` independently makes `.pgproj` an Open/Build/Publish
> citizen via VS's generic SDK-project support, so even without the project-system extension installed a VS
> user can build/publish a `.pgproj`.)

---

## Route A — `.pgproj` is a clean `dotnet build` citizen (validated)

Lives in [`src/PgProj.Sdk`](../../src/PgProj.Sdk/). It is an MSBuild **project SDK**, NuGet-packable,
that routes Restore/Build/Clean/Rebuild/**Publish** to the `pgproj` CLI. Because VS 2022+ can open and
build any SDK-style project, this alone gives a Visual Studio user **Open → Build → Rebuild → Clean →
Publish** on a `.pgproj` from Solution Explorer, with no custom extension installed.

- **Build** → `pgproj build` → `bin/<Name>.model.json` + `bin/<Name>.pgpkg`.
- **Publish** (right-click → Publish, or `-t:Publish`) → `pgproj publish` against a connection, or an
  offline create-script preview when no connection is set.
- Packaged on NuGet as `PgProj.Sdk` (carries the CLI under `tools/`).

See [`src/PgProj.Sdk/README.md`](../../src/PgProj.Sdk/README.md) for the full property/verb reference.

**Route A is the project-system story.** The modern extension (Route B) deliberately does **not** ship
a custom `.pgproj` project flavour — see the architecture note below.

---

## Route B — the modern OOP extension (`PgProj.VisualStudio`, VS 2026+)

> **The OOP half of the hybrid.** Targets **VisualStudio.Extensibility** on **.NET 10** for
> **Visual Studio 2026 and newer**. It is intentionally **out of `PgProj.slnx`** and **out of
> `dotnet test`**, with its own solution [`PgProj.VisualStudio.slnx`](./PgProj.VisualStudio.slnx).
> It carries everything the out-of-process model *can* do; the `.pgproj` **project type** that the OOP
> model cannot author lives in the sibling classic extension — see
> [Project-system extension](#project-system-extension-pgprojvisualstudioprojectsystem) below.

The extension **links the engine in-process** — it `<ProjectReference>`s `PgProj.Core` and `PgProj.Lsp`
and calls them directly (no `pgproj` subprocess, no JSON round-trip). The engine remains the single place
build/compare/publish/LSP logic lives; the extension is the VS presentation over it.

- **Commands** (`Extensions` menu, enabled when a `.pgproj` is selected): **Schema Compare** calls
  `SchemaCompare.RunAsync`; **Publish** runs the same gates as the CLI (static analysis via
  `ContractBuilder.Analyze`, target-version via `TargetVersionAnalyzer`) and then the shared
  `PgProj.Core.Publishing.PublishService` (`PlanAsync` → `ApplyAsync`) — the **single** publish code
  path, so VS publish and CLI publish produce the identical deploy script and use the identical deploy
  strategy (including pre/post-deploy scripts + SQLCMD variables).
- **Schema Compare tool window** — a Remote UI control rendering the engine's `SchemaChangeSet` (view
  models built straight from the change objects).
- **`.sql` Language Server Provider** — hosts `PgProj.Lsp`'s `LspServer` **in-process** on one end of an
  in-memory `FullDuplexStream`, handing VS the other end as a duplex pipe. Same server the VS Code
  extension drives over STDIO; here the transport is an in-process pipe.

### Layout

```
editors/vs/
  PgProj.VisualStudio.slnx                     standalone solution for the OOP extension (NOT in PgProj.slnx)
  PgProj.VisualStudio/                         MODERN OOP extension (net10.0-windows)
    PgProj.VisualStudio.csproj                 SDK-style; Extensibility.* + ProjectRef PgProj.Core/.Lsp
    PgProjExtension.cs                          Extension entry; ExtensionConfiguration + DotnetTargetVersions (net10)
    string-resources.json                       localized command / server display names
    Engine/
      PgProjEngine.cs                           in-proc engine calls: CompareAsync + DeployAsync (PgProj.Core)
      PgProjContext.cs                          find the nearest .pgproj + resolve PGPROJ_CONNECTION
    Commands/
      PublishCommand.cs                         Publish → compare + deploy in-proc, streamed to an Output channel
      SchemaCompareCommand.cs                   Compare in-proc → tool window
    LanguageServer/
      PgProjLanguageServerProvider.cs           LanguageServerProvider + .sql DocumentType; hosts LspServer in-proc
    ToolWindows/
      SchemaCompareToolWindow.cs                ToolWindow hosting the Remote UI control
      SchemaCompareControl.cs / .xaml           Remote UI control + serialized data template
      SchemaCompareViewModel.cs                 view models + factory (built from the engine's SchemaChangeSet)
      SchemaCompareState.cs                     command → tool-window hand-off (latest view model)
  PgProj.VisualStudio.ProjectSystem/           CLASSIC in-proc VSSDK extension (net472) — the .pgproj project type
    PgProj.VisualStudio.ProjectSystem.csproj   explicit-SDK-import csproj; VSSDK.BuildTools + VisualStudio.SDK; builds the .vsix
    source.extension.vsixmanifest               VsPackage + ProjectType + MEF assets; InstallationTarget [17.0,19.0)
    PgProjCommands.vsct / PgProjGuids.cs        command table (compiled to an embedded .cto) + GUIDs
    PgProjPackage.cs                            AsyncPackage host
    ProjectSystem/PgProj*ProjectFactory.cs      .pgproj project factory + unconfigured project (scaffold)
    Properties/*PropertiesPage.cs               Build / Database / SqlCmdVariables / TargetPlatform property pages (scaffold)
    Commands/*Command.cs                        Publish + Schema Compare menu commands
    ToolWindows/SchemaCompareToolWindow.cs      tool window host
```

### `.sql` IntelliSense — how the LSP provider attaches

`LanguageServer/PgProjLanguageServerProvider.cs` extends `LanguageServerProvider`. A
`DocumentTypeConfiguration` (`pgsql`, based on `LanguageServerBaseDocumentType`) maps the `.sql`
extension to it, so VS offers the server when a `.sql` editor opens. `CreateServerConnectionAsync`
creates an in-memory `FullDuplexStream` pair, constructs `PgProj.Lsp.Server.LspServer` on one end
(running its read→dispatch loop on a background task), and returns the other end to VS as an
`IDuplexPipe`. No process is spawned — `LspServer` is explicitly designed to run over arbitrary streams
(the unit tests drive it the same way). When VS closes the pipe the server's reader EOFs and it disposes.
This is the modern, in-process equivalent of the old MEF `ILanguageClient` + `pgproj serve` subprocess.

### Building & running it

Build + package work **headless** with the .NET 10 SDK (the Extensibility build tools come from the
`Microsoft.VisualStudio.Extensibility.Build` NuGet package — no VS install needed to compile the `.vsix`):

```text
dotnet build editors/vs/PgProj.VisualStudio/PgProj.VisualStudio.csproj -c Debug
#  -> bin/Debug/net10.0-windows/PgProj.VisualStudio.dll + PgProj.VisualStudio.vsix
```

To run/debug it:

```text
1. Install Visual Studio 2026 with the "Visual Studio extension development" workload.
2. Open editors/vs/PgProj.VisualStudio.slnx in Visual Studio 2026 (it includes the engine projects so VS can restore them).
3. F5 launches the VS Experimental Instance with the extension loaded.
   - No `pgproj` on PATH is required — the engine (PgProj.Core/.Lsp) is linked in-process and packaged
     into the VSIX.
   - Set PGPROJ_CONNECTION to a PostgreSQL connection string for Publish / Schema Compare.
```

> **.NET 10 caveat — VSExtensibility [issue #544](https://github.com/microsoft/VSExtensibility/issues/544)**
> (Dec 2025): early VS 2026 builds failed to *select* the `net10.0` runtime for OOP extensions, so
> commands silently did not run. If that reproduces: use the Debug-menu **.NET runtime** picker to force
> net10.0 when F5-debugging; confirm the fix shipped in your VS build. The documented fallback is to drop
> the csproj `TargetFramework` + the `DotnetTargetVersions` line to `net8.0` — used only as a last resort,
> since this workspace standardizes on .NET 10.

### Compiles clean; runtime behavior to verify on F5

The project **compiles and packages** against `Microsoft.VisualStudio.Extensibility.Sdk` 17.14
(`dotnet build` → 0 warnings / 0 errors, emits the `.vsix`). What still needs a running VS 2026 to confirm:

- **Remote UI tool window** (`SchemaCompareControl.xaml` embedded data template + view models) — confirm
  the template binds and renders.
- **net10 runtime selection** — the *build* is net10; whether VS 2026 *runs* the extension on net10 is the
  #544 caveat above (use the Debug-menu runtime picker if commands don't fire).
- **In-process LSP lifetime** — confirm the in-memory `FullDuplexStream` pair shuts the `LspServer` down
  cleanly when VS closes a `.sql` editor (the background task disposes on reader EOF).

---

## Project-system extension (`PgProj.VisualStudio.ProjectSystem`)

The classic in-proc VSSDK extension (net472) that authors the **`.pgproj` project type** — the piece the
OOP model cannot do. It carries the project factory, property pages, command table (`.vsct`), and a tool
window, and packages them as a `Microsoft.VisualStudio.ProjectType` asset.

**Build — VS 2026 full MSBuild only** (`dotnet` cannot build a classic VSIX):

```text
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe" `
  editors/vs/PgProj.VisualStudio.ProjectSystem/PgProj.VisualStudio.ProjectSystem.csproj -t:Rebuild -restore -p:Configuration=Debug
#  -> bin/Debug/net472/PgProj.VisualStudio.ProjectSystem.vsix
```

Packaging required three SDK-style-VSSDK details (`Microsoft.VSSDK.BuildTools` 17.14.2120 does not
auto-wire them): an explicit `Microsoft.VsSDK.targets` import, **after** the SDK's `Sdk.targets` (so use
explicit `<Import Sdk="…">` not the `Sdk="…"` attribute, else `$(IntermediateOutputPath)` is empty →
VSSDK1207), and `<License>` referenced by its in-archive name + packaged as a `Content` item with
`IncludeInVSIX` (else VSSDK1310). See the CLAUDE.md Lab Note (2026-06-09) for the full recipe.

> **Status: scaffold.** It compiles and produces a valid `.vsix`, but the project factory / property
> pages are stubs. Stage 1+ (a real CPS project type that actually loads a `.pgproj`, File→New→Project,
> node ops, WPF editors, and F5 into the experimental instance) is **unproven** and needs interactive
> VS 2026 — it cannot be validated headless. The project is currently standalone (no engine
> `ProjectReference`; not in `PgProj.VisualStudio.slnx`).

### Follow-ups

- Bump `Microsoft.VisualStudio.Extensibility.*` to the VS 2026 (18.x) feed version when available.
- **Publish parity — DONE:** the shared `PgProj.Core.Publishing.PublishService` is now the single publish
  code path for both the CLI (`Program.Publish`) and this extension (gates + pre/post-deploy scripts +
  SQLCMD variables included). Remaining smaller gaps vs the CLI: reference resolution/validation on build,
  a `--parallel` toggle, and loading a `.pgpublish.json` profile (the extension uses defaults today).
- Replace the env-var connection with a Remote UI publish dialog (connection / profile / allow-drops /
  dry-run) once the dialog surface is wired.
- Let the Schema Compare window pick the target (project / `.pgpkg` / `.schema.snapshot` / live DB) and
  apply selected changes, not just project→DB.
- VS SDK UI tests (open solution, Publish/Compare from the Extensions menu, completion in `.sql`).
- **Project system → Stage 1:** make the `.pgproj` project factory load a real project (CPS object tree),
  add the File→New→Project template, wire the property pages to the `.pgproj` XML, and prove F5 in VS 2026.
