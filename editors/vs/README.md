# PgProj — Visual Studio experience (EP-VS #25)

Two routes give Visual Studio users the same database-project experience SSDT gives SQL Server users.

| Route | What | Status | Buildable in this repo? |
|---|---|---|---|
| **A** | `.pgproj` builds/publishes via the **MSBuild SDK** (`PgProj.Sdk`), opened by VS's built-in generic SDK-style project support | **Done + validated** | ✅ yes (`dotnet build` / `dotnet pack`) |
| **B** | A **two-extension hybrid** for VS 2026+: a modern **VisualStudio.Extensibility** OOP extension (Publish & Schema Compare commands, a Schema Compare tool window, `.sql` IntelliSense via an LSP provider) **plus** a classic in-proc **VSSDK project system** that authors the `.pgproj` project type (CPS registration, File→New→Project template, Add New Item templates per database object, Solution Explorer tree) | **OOP: builds + packages headless. Project system: real CPS project type + templates implemented; compiles + packages a `.vsix` with VS 2026 MSBuild; runtime/F5 verification pending (manual).** | ⚠️ partly — OOP via `dotnet build`; the project system needs the VS 2026 full MSBuild (`dotnet` cannot build a classic VSIX) |

> **Why two extensions (the hybrid).** The out-of-process VisualStudio.Extensibility model has **no API to
> author a custom project type** — Project Query only reads/modifies *existing* projects. So the `.pgproj`
> project flavour (own Solution Explorer object tree + property pages + File→New→Project template) is
> carried by a **separate classic in-proc VSSDK extension** (`PgProj.VisualStudio.ProjectSystem`, net472),
> while the modern OOP extension (`PgProj.VisualStudio`, net10) carries everything the OOP model *can* do
> (commands, tool window, `.sql` language server). They ship as two VSIXes and share the engine
> (`PgProj.Core`/`PgProj.Lsp`). (Route A's `PgProj.Sdk` independently makes `.pgproj` an Open/Build/Publish
> citizen via VS's generic SDK-project support, so even without the project-system extension installed a VS
> user can build/publish a `.pgproj`.)

> **One install, though.** Two *extensions* does not mean two *installs* — but **how** you get to one
> install depends on the channel (see [Distribution & install](#distribution--install) below). The short
> version: locally, run `editors/vs/install-pgproj.cmd` (installs both vsixes in one go); on the Visual
> Studio Marketplace, ship the reference **extension pack** for genuine one-click install.

### Distribution & install

The project type and the commands/LSP are **two extensions** by necessity (a project type must be a classic
in-proc net472 extension; the engine-linked commands/LSP must be a modern OOP net10 extension — different
hosts, different toolchains, one identity each). They cannot be merged into a single installable `.vsix`,
and a pack **cannot embed** the OOP extension: `VSIXInstaller` rejects a nested VisualStudio.Extensibility
extension with *"Cannot install a VisualStudio.Extensibility extension … Must unzip and call the finalizer."*
So "one install" is delivered per channel:

| Channel | Mechanism | Artifact | One-gesture install? |
|---|---|---|---|
| **Local — project type** | `VSIXInstaller` (unsigned, per-user) | `PgProj.VisualStudio.ProjectSystem.vsix` | ✅ `editors/vs/install-pgproj.cmd` |
| **Local — OOP commands/LSP** | **NOT** via `VSIXInstaller` (it can't finalize an OOP extension); use **F5** (experimental instance) or **VS → Manage Extensions → Install from disk** on the signed vsix | `PgProj.VisualStudio.vsix` (sign with `sign-oop.ps1`) | ⚠️ in-IDE only / F5 |
| **VS Marketplace** | a **reference** extension pack — lists both by Id, VS resolves each from the Marketplace and installs it individually (its own correct, signed+finalized flow) | `PgProj.VisualStudio.ExtensionPack.vsix` + the two listings | ✅ one click on the pack listing |
| **NuGet** | `PgProj.Sdk` MSBuild SDK (build/publish in VS/CLI/CI, **no extension**) **and** the `pgproj` CLI as a .NET tool | `PgProj.Sdk.<v>.nupkg`, `PgProj.Cli.<v>.nupkg` | n/a (not an IDE extension) |

> **Hard limit on local OOP install.** `VSIXInstaller.exe` (the command-line/double-click installer)
> **cannot install a VisualStudio.Extensibility (OOP) extension** — it bails with *"must unzip and call
> the finalizer instead"* regardless of signing or elevation. Only the VS IDE, the VS setup engine, and
> the Marketplace run that finalizer. So the OOP half (`PgProj.VisualStudio`) installs locally **only**
> through *VS → Manage Extensions → Install from disk* (sign it first with `sign-oop.ps1`) or via **F5**
> into the experimental instance. The classic project-system extension has no such limit.

- **Build everything:** `editors/vs/build-vsix.cmd [Debug|Release]` builds the two extensions + the
  reference pack in order, auto-detecting the VS 2026 MSBuild. (Batch scripts here are kept CRLF/ASCII —
  see the CLAUDE.md Lab Note; the Write tool's LF output silently breaks `cmd`.)
- **Make the project type loadable:** `editors/vs/setup-local-sdk-feed.cmd` (run once). A new `.pgproj`
  uses `<Project Sdk="PgProj.Sdk/0.1.0">`; without that SDK resolvable, VS errors *"the referenced SDK
  cannot be found"* and won't load the project. This packs `PgProj.Sdk` into a local NuGet feed and
  registers it, so the resolver restores it (publishing `PgProj.Sdk` to nuget.org later replaces this).
  Undo with `dotnet nuget remove source pgproj-local`.
- **Install locally:** `editors/vs/install-pgproj.cmd [Debug|Release]` — close VS first; it
  `/uninstall`s any prior PgProj extensions (so a same-version dev rebuild actually replaces, not "already
  installed") then installs the **project-system** extension. It does *not* install the OOP extension —
  VSIXInstaller can't (see the hard-limit note above); the script prints the F5 / Install-from-disk
  guidance for that half.
- **Sign the OOP extension** (for Manage Extensions → Install from disk): `editors/vs/sign-oop.ps1` —
  self-signs + trusts a dev cert and signs the vsix. Runtime note: the signer (OpenVsixSignTool) ships
  targeting .NET Core 2.1 (EOL, not installed); the script rolls it forward to the **latest installed**
  runtime via `DOTNET_ROLL_FORWARD=LatestMajor` — it never uses 2.1.
- **Marketplace pack:** `PgProj.VisualStudio.ExtensionPack` is now the **reference** form (no bundled
  vsixes). It is a Marketplace-only artifact — double-clicking it locally fails because the dependency Ids
  resolve against the Marketplace. Publish the two child extensions first; the pack's `<Dependency>` Ids +
  versions must match the published children. Auto-updates are automatic on the Marketplace (bump a version,
  VS updates the installed extension), so the local uninstall-first step is not needed there.
- **CLI as a .NET tool (NuGet):** `dotnet pack src/PgProj.Cli` → `PgProj.Cli.<v>.nupkg`, then
  `dotnet tool install -g PgProj.Cli` exposes the `pgproj` command for scripting/CI (build, compare,
  publish, snapshot, drift, …). The package bundles the engine (`PgProj.Core`/`PgProj.Lsp`/Npgsql); it is
  the same CLI the `PgProj.Sdk` SDK carries under `tools/` for MSBuild, just runnable on its own.

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

- **Commands** (the `.pgproj` **project-node context menu** — no Extensions-menu entries; visible
  *and* enabled only when the selection is a `.pgproj`): **Schema Compare** calls
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
    PgProj.VisualStudio.ProjectSystem.csproj   explicit-SDK-import csproj; VSSDK.BuildTools + VisualStudio.SDK + ProjectSystem.SDK (CPS)
    source.extension.vsixmanifest               VsPackage + ProjectType + MEF + ProjectTemplate/ItemTemplate assets; [17.0,19.0)
    PgProjCommands.vsct / PgProjGuids.cs        the .pgproj project context-menu GROUP (commands live in the OOP extension) + GUIDs
    PgProjPackage.cs                            thin AsyncPackage: pkgdef host + "PgProj project present" UIContext rule
    ProjectSystem/PgProjProjectType.cs          the CPS project type (ProjectTypeRegistration + MEF exports; no factory code)
    ProjectTemplates/PostgreSQL/...             File→New→Project: "PostgreSQL Database Project" (empty .pgproj)
    ItemTemplates/PostgreSQL/...                Add New Item: Schema/Table/View/Function/Procedure/Sequence/Trigger/Type/Policy
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

### What it now implements (Stages 1–3, headless-verified 2026-06-11)

- **CPS project type** (`ProjectSystem/PgProjProjectType.cs`): `ProjectTypeRegistrationAttribute`
  (from `Microsoft.VisualStudio.ProjectSystem.SDK` 17.9.380 — the attribute is in
  `ProjectSystem.VS.dll`, which the bare `Microsoft.VisualStudio.ProjectSystem` package does NOT
  carry) + the canonical Unconfigured/Configured MEF exports. The generated pkgdef points the
  project-type GUID at the CPS factory package and applies `Capabilities=PgProj` +
  `Language(VsTemplate)=PgProj`. No hand-written factory/hierarchy: CPS evaluates the MSBuild
  project (PgProj.Sdk), shows the item tree, and routes Build/Rebuild/Clean/Publish to the SDK's
  targets. The SDK side declares `<ProjectCapability Include="PgProj"/>` and a
  `Rules/ProjectItemsSchema.xaml` (Build/None/Folder item types, `.sql` content type) plus a no-op
  `CompileDesignTime` target for CPS design-time builds.
- **File→New→Project**: folder-based VSIX project template "PostgreSQL Database Project"
  (`ProjectTemplates/`), tagged SQL/Windows/Linux/Database for the VS 2019+ New Project dialog.
- **Add New Item**: nine folder-based item templates (`ItemTemplates/`) — Schema, Table, View,
  Function, Procedure, Sequence, Trigger, Type, RLS Policy — using `$fileinputname$` so a file
  named `app.customers.sql` yields `CREATE TABLE app.customers (…)`. **Database objects are grouped
  by schema** via the folder-per-schema convention (`public/`, `app/`, … with `schema.object.sql`
  files) that the project template documents and the Schema item template bootstraps.
- **Database controls only when a PgProj project is present**: a `ProvideUIContextRule` UIContext
  (`SolutionHasProjectCapability:PgProj`), and the OOP extension's commands sit on the .pgproj
  project context menu gated by `VisibleWhen`/`EnabledWhen` — the Extensions menu carries nothing.

> **Status: runtime/F5 verification pending (manual).** Everything above compiles and packages into
> the `.vsix` headless (template manifests generated, pkgdef registrations confirmed by inspection),
> but loading a `.pgproj` through CPS, the New Project listing, Add New Item filtering, and the
> context-menu merge of the two extensions need an interactive VS 2026 F5 pass. Stage 4+ (property
> pages as CPS XAML rules, WPF editors) is not started. The project is standalone (no engine
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
- **F5 verification pass** (manual, interactive VS 2026): install both VSIXes, New Project →
  "PostgreSQL Database Project", add items per schema folder, Build/Publish from Solution Explorer,
  Publish/Schema Compare on the project context menu (and absent from the Extensions menu).
- **Project system → Stage 4:** property pages as CPS XAML rules wired to the `.pgproj` XML
  (DefaultSchema, TargetPostgresVersion, publish settings); then Stage 5 WPF editors.
