# SSDT-for-PostgreSQL — Feature Parity Backlog

> Goal: give PostgreSQL developers the **same declarative-database-project experience** that SQL
> Server developers get from **SQL Server Data Tools (SSDT) / the Microsoft.Build.Sql SDK / the SQL
> Database Projects extension** — including the **same UI**, so a Visual Studio (and VS Code) user
> feels at home.
>
> Source of truth for the target feature set: Microsoft Learn *SQL Database Projects* docs
> (overview, *SQL Projects Tools* feature matrix, *SQL Database Projects extension* UX).
> The **parser/engine is done** — this backlog covers everything *around* it needed to reach parity.

---

## 1. Terminology map (SQL Server → PostgreSQL)

| SQL Server / SSDT | pgproj equivalent | Status |
|---|---|---|
| `.sqlproj` (SDK-style `Microsoft.Build.Sql`) | `.pgproj` (`PgProj.Sdk`) | ✅ exists |
| `.dacpac` (portable, referenceable build artifact) | `*.model.json` today → **`.pgpkg`** (proposed portable package) | ⚠️ partial |
| `SqlPackage` CLI | `pgproj` CLI | ✅ exists |
| `Microsoft.SqlServer.DacFx` (.NET library) | `PgProj.Core` | ✅ exists |
| SSDT (Visual Studio) | **VS / VS Code extension** | ❌ missing |
| SQL Database Projects extension (VS Code) | **pgproj VS Code extension** | ❌ missing |
| Schema Compare | `compare` / `drift` engine | ⚠️ engine yes, UI/two-way no |
| Publish profile (`.publish.xml`) | **publish profile** (`.pgpublish.json`) | ❌ missing |
| SQLCMD variables `$(Var)` | **project variables** `$(Var)` | ❌ missing |
| Pre/Post-deployment scripts | **pre/post-deploy scripts** | ❌ missing |
| Target platform (`Sql160`…) | `TargetPostgresVersion` (16/17/18) | ⚠️ parsed, not enforced |
| Code analysis rules (SA-rules) | `PgAnalyzer` (PG001–PG009) | ⚠️ 7 rules, not configurable |
| Database/Project/Package references | — | ❌ missing |

Legend: ✅ done · ⚠️ partial · ❌ not started.

---

## 2. What's already done (do not rebuild)

The headless engine is in good shape. These epics are **complete** and only need UI/profile plumbing on top:

- **EP-BUILD** — parse all `.sql` → in-memory model + JSON artifact (`build`). Parallel, deterministic.
- **EP-COMPARE** — dependency-ordered project↔live diff (`compare`).
- **EP-PUBLISH** — transactional + phased-parallel deploy, `--allow-drops` safety, dry-run (`publish`, `script`).
- **EP-VALIDATE** — throwaway-DB preflight, rollback + drop (`validate`).
- **EP-EXTRACT** — reverse-engineer a live DB into a buildable project (`extract`).
- **EP-DRIFT** — reverse-sync live DB changes back into source files (`drift`, `pull`).
- **EP-ANALYZE** — static AST safety rules (`analyze`, PG001–PG009).
- **EP-SDK** — `PgProj.Sdk` MSBuild props/targets so `dotnet build` builds a `.pgproj`.
- **EP-PARSER** — 100% accept/reject parity vs PostgreSQL 18 on a 21,743-statement corpus.

**Delivered during the M-waves (this §3 list lagged the code — confirmed by the 2026-06-07 M7 audit, see
[`reference/PROGRESS.md`](reference/PROGRESS.md) M7):**

- **EP-PKG / EP-VARS / EP-DEPLOYSCRIPTS / EP-REF / EP-RPC** — Phase-1 engine completeness (waves 1–2).
- **EP-TARGET** ✅ — version-aware validation (`TargetVersionAnalyzer` + `PgVersionCapabilities` table + `PGV###`; gate on build/publish/validate).
- **EP-PROFILE** ✅ — `.pgpublish.json` + `profile create` + `--profile` (CLI>profile>default; secret-free).
- **EP-SCHEMACOMPARE** ✅ — unified two-way `SchemaCompare` + selectable change set + `--output diff.json` + `--exclude`.
- **EP-TEMPLATES** ✅ — object templates + `add`/`new project` + `dotnet new` pack (`templates/`).
- **EP-VSCODE / EP-VS Route A / EP-DESIGNER (read+round-trip)** — delivered in M5 (#24/#25/#26/#31).
- **EP-ANALYSIS+** 🟡 — per-rule config + `--rule` + SARIF done; **open:** external rule packs, growing the rule set.

> **Reality check (2026-06-07):** the per-epic task checklists in §3 below are the *original* backlog and
> were not all re-ticked. Treat the list above + `PROGRESS.md` M7 as the source of truth for what's done;
> the genuinely-open work is **EP-ANALYSIS+ tail, EP-COVERAGE (introspection, tracked in `reference/COVERAGE.md`),
> EP-CICD (planning), EP-VS Route B, EP-DESIGNER deepening.**

---

## 3. The gap to SSDT parity — epics

Below, each epic mirrors a row (or rows) from the Microsoft *SQL Projects Tools* feature matrix.
Each has **user stories** (As a … I want … so that …) and **engineering tasks**. Priority:
**P0** = required for credible parity, **P1** = expected by SSDT users, **P2** = nice-to-have / advanced.

---

### EP-PKG — Portable build artifact (the `.dacpac` analogue) — **P0**

SSDT's whole model hinges on the `.dacpac`: one portable, versioned, referenceable file that is the
build output, the unit of deployment, and the unit of reference. pgproj emits a `bin/*.model.json`
today — good, but it needs to become a **stable, versioned, self-describing package** so it can be
referenced, shipped, and deployed without the source tree.

**User stories**
- As a DBA, I want `pgproj build` to produce a single portable package file so that I can hand the
  exact built schema to ops without shipping source.
- As a release engineer, I want to `pgproj publish` **from a package** (not just from source) so my
  CI builds once and deploys the identical artifact to many environments.
- As a tooling author, I want the package to carry a schema version + target PG version + checksum so
  drift between "what I built" and "what I deployed" is detectable.

**Tasks**
- [ ] Define the `.pgpkg` container (zip: `model.json` + `manifest.json` {name, pgVersion, toolVersion, createdUtc-from-caller, checksum} + origin `.sql` sources + pre/post scripts).
- [ ] `build` writes `.pgpkg` (keep `model.json` inside for inspection); add `--package`/`--no-package`.
- [ ] Teach `compare`/`publish`/`script`/`validate` to accept a `.pgpkg` **or** a `.pgproj` as source.
- [ ] `pgproj pkg inspect <file>` to dump the manifest + object inventory.
- [ ] Version-stamp injected by the caller (no `Date.now()` in deterministic code paths).

---

### EP-REF — Database / project / package references — **P0/P1**

SSDT lets a project reference another project, a `.dacpac`, or a NuGet package, with resolution of
cross-database names. Mirrors matrix rows *Project references / DACPAC references / Package references*.

**User stories**
- As an architect, I want project B to reference project A so that B's views can resolve A's tables at
  build time without copying A's source.
- As a platform team, I want to publish a shared "common schema" as a package and have downstream
  projects reference it by package id + version so upgrades flow through normal dependency bumps.
- As a developer, I want an unresolved cross-schema reference to **fail the build** with a clear
  message (not silently produce a broken deploy).

**Tasks**
- [ ] `<ItemGroup><PackageReference/>`, `<ProjectReference/>`, `<ArtifactReference Include="*.pgpkg"/>` in `.pgproj`.
- [ ] Reference resolver: load referenced model(s) into the build's semantic `Catalog` as *external* objects (visible to validation, **not** emitted to the deploy script).
- [ ] "same-database" vs "different-database/server" reference semantics (Postgres analogue: same DB other schema, vs FDW/`dblink` cross-DB) — document the supported subset; start with same-database/other-schema.
- [ ] NuGet packaging of `.pgpkg` (reuse `Microsoft.Build.Sql`'s package-reference pattern); restore during `dotnet build`.
- [ ] Build error codes for unresolved/circular references.

---

### EP-VARS — Project variables (SQLCMD-variable analogue) — **P0**

SSDT parameterizes deploys with `$(Var)` SQLCMD variables (e.g. environment name, linked-server name).
Matrix row *SQLCMD variables*. Postgres deploys need the same: tokenized values resolved at
publish/script time, overridable per environment.

**User stories**
- As a release engineer, I want `$(EnvSuffix)` tokens in my `.sql`/post-deploy scripts replaced at
  publish time so one project deploys to `app_dev` / `app_prod` schemas without edits.
- As a developer, I want default variable values in the project and overrides from a publish profile or
  CLI so local builds "just work" but CI can substitute.
- As an auditor, I want the resolved values printed in the deploy-script header so the artifact is
  self-documenting.

**Tasks**
- [ ] `<ItemGroup><SqlCmdVariable Include="EnvSuffix"><DefaultValue>dev</DefaultValue></SqlCmdVariable>` in `.pgproj`.
- [ ] Token scanner + substitution pass (`$(Name)`) applied to pre/post-deploy + any opted-in object scripts; **error on unresolved tokens**.
- [ ] Override precedence: CLI `--var Name=Value` > publish profile > project default.
- [ ] Echo resolved variables into the script banner (`IncludeHeader`).
- [ ] Decide scope: variables in object DDL (powerful, risky) vs only deploy scripts (safe). Recommend deploy-scripts + explicit opt-in for object files.

---

### EP-DEPLOYSCRIPTS — Pre / post-deployment scripts — **P0**

Matrix row *Pre-deployment and post-deployment scripts*. Idempotent data seeds, grants, refreshes that
run around the schema diff.

**User stories**
- As a developer, I want a `PreDeploy.sql` that runs before the schema changes and a `PostDeploy.sql`
  after, so I can seed lookup data and `REFRESH MATERIALIZED VIEW` as part of one publish.
- As a release engineer, I want pre/post scripts included in the generated script and inside the same
  transaction (configurable) so a failed seed rolls the whole deploy back.

**Tasks**
- [ ] `<None Include="PreDeploy.sql"><BuildAction>PreDeploy</BuildAction>` / `PostDeploy` item metadata (single each, like SSDT).
- [ ] Splice scripts into `DeployScriptGenerator`: pre → schema diff → post; honor `WrapInTransaction`.
- [ ] Run variable substitution (EP-VARS) on these scripts.
- [ ] Include them in the `.pgpkg` (EP-PKG) and the dry-run output.

---

### EP-TARGET — Target-platform enforcement (version-aware validation) — **P1**

> ✅ **DELIVERED** (M7 audit 2026-06-07, issue #66). `Analysis/TargetVersionAnalyzer.cs` + `Versioning/PgVersionCapabilities`/`SupportedFeatures` + `PGV###`; gate wired into build/publish/validate. Tests `TargetVersionTests`/`VersionProfileTests`. Tasks below are historical.

`TargetPostgresVersion` (16/17/18) is parsed but not enforced. SSDT blocks SQL2022-only syntax on a
SQL2017 target. Mirrors matrix row *Target platform*.

**User stories**
- As a developer targeting PG16, I want the build to **error** if I use a PG17/18-only feature so I
  don't ship something the production server can't run.
- As a team lead, I want to bump the target version in one place and have validation re-baseline.

**Tasks**
- [ ] Capability table: feature/syntax → minimum PG version (start with the highest-value deltas: `MERGE … RETURNING`, `JSON_TABLE`, new `GENERATED`/identity forms, `NULLS NOT DISTINCT`, etc.).
- [ ] Validation pass in build that flags AST nodes newer than `TargetPostgresVersion`.
- [ ] New analyzer category `PGV###` (version-gating) with file:line diagnostics.
- [ ] Wire target version into `validate` (spin the shadow DB on the matching Postgres image when available).

---

### EP-ANALYSIS+ — Configurable code analysis & extensibility — **P1**

> 🟡 **MOSTLY DELIVERED** (M7, issue #67). Done: per-rule config `Analysis/AnalysisConfig.cs` (`.pgproj.analysis.json`), CLI `--rule`, SARIF (`Analysis/SarifWriter.cs`), and **#79 external rule packs** (`IPgRule` + `RulePackLoader` isolated-`AssemblyLoadContext` discovery + `rulePacks` config; doc `docs/ANALYSIS_RULES.md`). **Open: #81** grow the rule set.

Matrix rows *Code analysis enable/disable GUI* + *run code analysis*. Today rules are all-or-nothing.
SSDT lets you enable/disable rules and set severity, plus third-party rule packs.

**User stories**
- As a developer, I want to disable a noisy rule or downgrade it to Info **per project** so analysis
  fits my team's standards.
- As a platform team, I want to ship a custom rule pack (org conventions) and have projects opt in.
- As a CI owner, I want analysis results as SARIF so they surface in GitHub/Azure code scanning.

**Tasks**
- [ ] Rule config in `.pgproj` or `.pgproj.analysis.json`: per-rule `enabled` + `severity` override.
- [ ] Honor config in `PgAnalyzer`; CLI `--rule PG003=off` overrides.
- [ ] Extensibility point: discover external rule assemblies (mirror DacFx's contributor model).
- [ ] SARIF output (`analyze --format sarif`) + a ruleset doc page.
- [ ] Grow the rule set (naming/casing consistency, missing PK, untyped `numeric`, etc.).

---

### EP-PROFILE — Publish profiles — **P1**

> ✅ **DELIVERED** (M7 audit 2026-06-07, issue #68). `Deployment/PublishProfile.cs` (secret-whitelisted `.pgpublish.json`), `profile create` verb, `--profile` on publish/script/compare (CLI>profile>default). Tests `PublishProfileTests`. Tasks below are historical.

Matrix rows *Publish profile creation* + *Load connection details and SQLCMD variables from profile*.
A reusable file capturing target connection + variables + publish options.

**User stories**
- As a release engineer, I want `prod.pgpublish.json` holding the target, variable overrides, and
  options (`allowDrops:false`, `transactional:true`) so `pgproj publish --profile prod` is one command.
- As a developer, I want to generate a profile from my current CLI flags so I stop retyping them.

**Tasks**
- [ ] `.pgpublish.json` schema: connection (sans secrets), variables, publish options, target PG version.
- [ ] `publish`/`script`/`compare` accept `--profile <file>`; CLI flags override profile values.
- [ ] `pgproj profile create` from current flags.
- [ ] Secret handling: connection string from env/secret store, never persisted in the profile.

---

### EP-SCHEMACOMPARE — First-class two-way Schema Compare — **P1**

> ✅ **DELIVERED** (M7 audit 2026-06-07, issue #69). `Comparison/SchemaCompare.cs` unified two-way API (source/target ∈ project/pkg/snapshot/live via `EndpointResolver`), selectable `SchemaChangeSet`, `--output diff.json`, `--exclude`. Tests `SchemaCompareTests`. Tasks below are historical.

The engine diffs both directions (`compare` = project→DB, `drift`/`pull` = DB→project), but SSDT
exposes a **single Schema Compare** surface with a reviewable, **selective** change list and apply in
either direction. Matrix rows *Schema comparison project↔database*.

**User stories**
- As a developer, I want a side-by-side diff (source vs target, either may be project/package/live DB)
  with each change checkable, so I can apply a subset and skip the rest.
- As a DBA, I want to export the diff as a script for review before anything touches the server.

**Tasks**
- [ ] Unify `compare`/`drift` behind one `SchemaCompare` API: source & target each ∈ {project, `.pgpkg`, live DB}.
- [ ] Structured, selectable change set (per-change include/exclude) → script or apply.
- [ ] `compare --source X --target Y --output diff.json` for the UI to render.
- [ ] Object-type include/exclude filters (skip permissions, skip extensions, etc.).

---

### EP-TEMPLATES — New-object templates & `dotnet new` — **P1**

> ✅ **DELIVERED** (M7 audit 2026-06-07, issue #70). `Templates/*` (`Scaffolder`/`TemplateCatalog`), `add`/`new project` verbs, `dotnet new` pack at `templates/PgProj.Templates.csproj`. Tests `TemplateTests`/`TemplateIntegrationTests`. Tasks below are historical.

Matrix rows *New object templates* + *Create new empty project / from existing database*.

**User stories**
- As a developer, I want `pgproj add table app.Customer` to scaffold a correctly-placed, correctly-named
  `.sql` file from a template so I follow project conventions automatically.
- As a developer, I want `dotnet new pgproj` and `dotnet new pgproj-table` so project/object creation
  matches the .NET muscle memory.

**Tasks**
- [ ] Object templates: table, view, function, procedure, trigger, sequence, type, schema, policy.
- [ ] `pgproj add <kind> <schema.name>` — scaffold into the right folder, open in `$EDITOR`.
- [ ] `dotnet new` template pack (`pgproj`, `pgproj-table`, …) published to NuGet.
- [ ] `pgproj new project <name>` (empty) and reuse `extract` for "from existing database".

---

### EP-VSCODE — VS Code extension (primary UI) — **P0**

This is the big one for "same UI". The modern SSDT-style experience *is* the **SQL Database Projects
extension for VS Code**. Replicate its surface for pgproj. Mirrors the entire *Visual Studio Code*
column of the matrix + the extension UX page.

**User stories**
- As a VS Code user, I want a **SQL Database Projects** view listing my `.pgproj`s with a tree of
  schemas/objects, so the experience matches the SQL Server extension I already know.
- As a developer, I want **right-click → Build / Publish / Schema Compare / Add object / Add reference**
  and command-palette equivalents, so I never drop to a terminal.
- As a developer, I want a **Publish dialog** (pick connection, enter/override SQLCMD variables, choose
  options, save as profile) identical in flow to the SQL Server one.
- As a developer, I want build errors and analysis findings in the **Problems** panel with file:line
  links from the parser diagnostics.

**Tasks**
- [ ] Scaffold a VS Code extension (`pgproj-vscode`, TypeScript) — depends on .NET SDK + the `pgproj` CLI/engine.
- [ ] **Projects panel** + tree provider (project → folders → objects), populated from the build model JSON.
- [ ] Context-menu + command-palette commands: New project, Open, Build, Publish, Generate script, Schema Compare, Validate, Add object (templates), Add reference, Edit project file, Set target version, Run code analysis.
- [ ] **Publish webview**: connection picker, SQLCMD-variable grid, options, "save as profile", "generate script".
- [ ] **Schema Compare webview**: source/target pickers, checkable diff, apply/script (EP-SCHEMACOMPARE).
- [ ] Diagnostics → VS Code **Problems** (parse a stable JSON diagnostics stream from the CLI: `--format json`).
- [ ] Bundle/locate the engine: ship the CLI as a tool or talk to a thin `pgproj serve` JSON-RPC backend (see EP-RPC).
- [ ] Marketplace packaging + `.code-workspace` multi-project support (mirror the MS workspace model).

---

### EP-RPC — Engine service surface for editors — **P0 (enabler for EP-VSCODE/EP-VS)**

SSDT tooling talks to DacFx in-process / via a service. To feed two editors without duplicating logic,
expose the engine over a stable JSON contract.

**User stories**
- As an extension author, I want one stable JSON-RPC/CLI-JSON contract (build, compare, publish-plan,
  analyze, model-tree) so the VS and VS Code UIs share one backend.

**Tasks**
- [ ] `--format json` on every CLI verb (stable, versioned schemas) **or** a `pgproj serve` STDIO JSON-RPC host.
- [ ] Model-tree endpoint (objects + positions) for tree views and go-to-definition.
- [ ] Streaming progress events for long publishes.
- [ ] Contract versioning + a conformance test (mirror the parser corpus discipline).

---

### EP-VS — Visual Studio experience — **P1/P2**

"Same UI so Visual Studio users have the same experience." Two routes — recommend starting with the
lighter one and the SDK, since even Microsoft ships the modern experience in VS Code first and SDK-style
VS support is still "preview".

**User stories**
- As a Visual Studio user, I want to open/build/publish a `.pgproj` from Solution Explorer with the same
  menu verbs as a SQL Server project, so my workflow doesn't change when the DB is Postgres.
- As a developer, I want IntelliSense/go-to-definition in `.sql` files driven by the project model.

**Tasks**
- [x] **Route A (faster): make `.pgproj` a clean `dotnet build` citizen** — `PgProj.Sdk` finished and **NuGet-packable** (`dotnet pack src/PgProj.Sdk` → carries the CLI under `tools/`); VS's generic project handling builds/cleans/**publishes** it. Build → model + `.pgpkg`; **Publish** is a real MSBuild target (`-t:Publish`, real/diff-dry-run/offline-preview shapes). Validated: sample `.pgproj` build (model byte-equal to the CLI's), pack, and a packaged-SDK consumer build. See `docs/VISUAL_STUDIO.md` / `src/PgProj.Sdk/README.md`.
- [~] **Route B (full): a VSIX project system / project flavor** — **scaffolded** under `editors/vs/` (own solution, excluded from `PgProj.slnx`; needs VS + the VS SDK to build — not buildable headless): project factory/flavor, the four property pages (build output, database settings, SQLCMD variables, target platform), Publish dialog command, Schema Compare window, `.vsct`/manifest. Bodies marked `// SCAFFOLD`. Table designer still deferred to EP-DESIGNER.
- [x] Language service for `.sql` IntelliSense from the project model — **VS `ILanguageClient` scaffold** (`editors/vs/.../LanguageClient/`) launches the existing `pgproj serve` LSP (#31), same server VS Code uses (`docs/LSP_LANGUAGE_SERVER.md`).
- [ ] `slngen`-style solution grouping for multiple `.pgproj`s (matrix row *Solution management*).

---

### EP-DESIGNER — Graphical table designer — **P2**

Matrix row *Graphical table designer* (SSDT-only today). High effort; the diff/round-trip engine makes
it feasible later (edit model → emit `.sql`).

**User stories**
- As a developer, I want a visual table editor (columns, types, keys, indexes) that round-trips to the
  declarative `.sql`, so I get SSDT's designer on Postgres.

**Tasks**
- [ ] Model→form→`.sql` round-trip backed by the existing emitter; start read-only, then editable.
- [ ] Postgres-specific surfaces designers usually miss: partitioning, identity/generated, RLS, EXCLUDE.

---

### EP-CICD — CI/CD integration — **P1**

Matrix *Command line tools / CI-CD* (GitHub `sql-action`, Azure DevOps task).

**User stories**
- As a DevOps engineer, I want a GitHub Action and an Azure DevOps task that build a `.pgproj` and
  publish the `.pgpkg`, so DB deploys live in my existing pipelines.

**Tasks**
- [ ] GitHub Action wrapping `pgproj build`/`publish` (artifact upload of `.pgpkg`).
- [ ] Azure DevOps task wrapper.
- [ ] Container image with the CLI for any-CI use; document `--dry-run` gating + approval flows.
- [ ] Stable non-zero exit codes per failure class (parse, analysis, drift, deploy).

---

## 4. Parity matrix (pgproj vs the Microsoft *SQL Projects Tools* table)

| Feature (MS matrix) | pgproj CLI/engine | pgproj VS Code (planned) | pgproj VS (planned) |
|---|---|---|---|
| Create new empty project | ⚠️ EP-TEMPLATES | ❌ EP-VSCODE | ❌ EP-VS |
| Create project from existing DB | ✅ `extract` | ❌ EP-VSCODE | ❌ EP-VS |
| Open existing SDK-style project | ✅ `.pgproj` | ❌ EP-VSCODE | ❌ EP-VS |
| Solution management | ❌ EP-VS (slngen-style) | n/a | ❌ EP-VS |
| Build | ✅ `build` | ❌ EP-VSCODE | ⚠️ EP-SDK |
| Publish to server | ✅ `publish` | ❌ EP-VSCODE | ❌ EP-VS |
| Publish to local dev instance | ✅ (any conn) | ❌ EP-VSCODE | ❌ EP-VS |
| Publish options/properties | ✅ flags | ❌ EP-VSCODE | ❌ EP-VS |
| Target platform updatable | ⚠️ EP-TARGET | ❌ EP-VSCODE | ❌ EP-VS |
| SQLCMD variables | ❌ EP-VARS | ❌ EP-VSCODE | ❌ EP-VS |
| Project references | ❌ EP-REF | ❌ EP-VSCODE | ❌ EP-VS |
| DACPAC/package references | ❌ EP-REF / EP-PKG | ❌ EP-VSCODE | ❌ EP-VS |
| Publish profile creation | ❌ EP-PROFILE | ❌ EP-VSCODE | ❌ EP-VS |
| Add `.sql` by dropping in folder | ✅ globbing | ❌ EP-VSCODE | ❌ EP-VS |
| Exclude `.sql` from build | ✅ (`_` prefix) → ⚠️ make explicit | ❌ EP-VSCODE | ❌ EP-VS |
| Pre/post-deploy scripts | ❌ EP-DEPLOYSCRIPTS | ❌ EP-VSCODE | ❌ EP-VS |
| New object templates | ❌ EP-TEMPLATES | ❌ EP-VSCODE | ❌ EP-VS |
| Organize into folders | ✅ | ✅ | ✅ |
| Schema compare project↔DB (both ways) | ✅ `compare`/`drift` | ❌ EP-SCHEMACOMPARE | ❌ EP-VS |
| Graphical table designer | ❌ EP-DESIGNER | ❌ EP-DESIGNER | ❌ EP-DESIGNER |
| Code analysis enable/disable GUI | ⚠️ EP-ANALYSIS+ | ❌ EP-VSCODE | ❌ EP-VS |
| Run code analysis | ✅ `analyze` | ❌ EP-VSCODE | ❌ EP-VS |
| Object rename/refactor | ❌ EP-VS | ❌ | ❌ EP-VS |
| IntelliSense from project model | ❌ EP-VS (lang service) | ❌ | ❌ EP-VS |

---

## 5. Suggested phasing

**Phase 1 — engine completeness (unblocks everything, all P0):**
EP-PKG → EP-VARS → EP-DEPLOYSCRIPTS → EP-REF → EP-RPC (`--format json` everywhere).

**Phase 2 — the primary UI (the "same experience"):**
EP-VSCODE (Projects panel, build/publish/compare, publish dialog) + EP-PROFILE + EP-SCHEMACOMPARE + EP-TEMPLATES.

**Phase 3 — hardening & breadth:**
EP-TARGET, EP-ANALYSIS+ (config + SARIF), EP-CICD.

**Phase 4 — Visual Studio depth:**
EP-VS Route A (SDK/NuGet, builds in VS) → Route B (VSIX project system, properties pages, designer EP-DESIGNER, IntelliSense).

---

## 6. Open questions (resolve before building)

- **Reference scope for Postgres**: SSDT's cross-database references don't map 1:1 (Postgres = one DB,
  many schemas; cross-DB needs FDW/dblink). Confirm we ship *same-database/other-schema* references
  first and defer cross-DB.
- **Variables in object DDL vs deploy-scripts only** — security/foot-gun tradeoff (EP-VARS).
- **Editor backend**: ship the CLI as a bundled tool vs a long-running `pgproj serve` (EP-RPC) — affects
  startup latency and packaging for both editors.
- **VS strategy**: is "builds cleanly via SDK + lightweight publish" (Route A) acceptable for v1, or is a
  full VSIX project system required to call it "same experience"? (Even Microsoft's SDK-style VS support
  is still preview.)
- **Artifact format**: zip-based `.pgpkg` vs keeping plain `model.json` + sidecar manifest.
