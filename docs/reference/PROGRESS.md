# CodeCleaners.PgProj — Delivery Progress Tracker

> **This is the master progress report.** It tracks every delivery milestone toward
> SSDT-for-PostgreSQL parity and is the single place to read "where are we now". It is **updated on
> every delivered milestone** (see [Update contract](#update-contract) below).

**Last updated:** 2026-06-24 (M1–M6 complete; **M7 substantially delivered** — and the VS experience is
now VALIDATED IN THE INSTALLED PRODUCT: a 115-scenario VM E2E suite (FlaUI+DTE, real sample database)
found and fixed three silent editor-chain breaks (per-user MEF cache, content-type overwrite, CodeRemote
LSP gate), live diagnostics reached build parity (+ cross-file invalidation), navigation/completion are
alias-aware and column-precise, and a file-level Sync-with-Database command shipped) ·
**Streams:** semantic core (EP-SEMCORE #41) · SSDT parity (#27) · performance (#12)

Legend: ✅ delivered · 🟡 in progress · ⬜ not started · ⛔ blocked · ⏸️ deferred.

---

## 1. Roadmap at a glance

Milestones map to the three tracking epics. GitHub milestones mirror this table (the issues are the
deliverable subtasks; **complex issues are broken into smaller subtasks when implemented**).

| # | Milestone | Stream | Issues | Done | Status | GitHub |
|---|-----------|--------|--------|------|--------|--------|
| **M1** | Determinism & Diagnostics Foundations | cross-cutting | #59, #49, #36, #37, #60, #62, #63 | 7 / 7 | ✅ | [milestone/1](https://github.com/ilijevicdenis/CodeCleaners.PgProj/milestone/1) |
| **M2** | Semantic Core — Identity & Foundations | EP-SEMCORE | #42, #43, #44, #45, #46, #39 | 6 / 6 | ✅ | [milestone/2](https://github.com/ilijevicdenis/CodeCleaners.PgProj/milestone/2) |
| **M3** | Semantic Core — Binding & Validation | EP-SEMCORE | #47, #48, #50, #51 | 4 / 4 | ✅ | [milestone/3](https://github.com/ilijevicdenis/CodeCleaners.PgProj/milestone/3) |
| **M4** | Diff, Risk, Deploy & Incremental | EP-SEMCORE | #52, #53, #54, #55, #56, #57, #58, #61, #64 | 9 / 9 | ✅ | [milestone/4](https://github.com/ilijevicdenis/CodeCleaners.PgProj/milestone/4) |
| **M5** | SSDT Parity — Editor UI | parity #27 | #31, #24, #25, #26 | 4 / 4 | ✅ | [milestone/5](https://github.com/ilijevicdenis/CodeCleaners.PgProj/milestone/5) |
| **M6** | Performance & Engine Backlog | perf #12 | #8, #10 | 2 / 2 | ✅ | [milestone/6](https://github.com/ilijevicdenis/CodeCleaners.PgProj/milestone/6) |
| **M7** | SSDT Parity — Engine, Tooling & Coverage | parity #27 / coverage | 8 epics in scope (EP-CICD #71 removed); #66–#70,#72,#111,#112 | 7 / 8 | 🟡 | [milestone/7](https://github.com/ilijevicdenis/CodeCleaners.PgProj/milestone/7) |

**Already shipped (not milestoned — do not rebuild):** the headless engine — `build`, `compare`,
`publish`, `validate`, `extract`, `drift`, `analyze`, `PgProj.Sdk`, and **100% parser accept/reject
parity vs PostgreSQL 18** on the 21,743-statement corpus. Parity-stream Phase-1 engine epics
(`.pgpkg`, project variables, pre/post-deploy scripts, references, JSON-RPC) landed via waves 1–2; see
[`docs/SSDT_PARITY_BACKLOG.md`](../SSDT_PARITY_BACKLOG.md) §2.

---

## 2. Milestones in detail

Each issue is a deliverable subtask. Sub-bullets are the smaller subtasks a complex issue is split
into when work starts — fill them in (and check them off) as the issue is implemented.

### M1 · Determinism & Diagnostics Foundations ✅

The reproducibility & quality base everything else leans on. **Complete (7/7).**

- ✅ **#59** — deterministic raw-object ordering across the parallel introspection merge
  *(delivered 2026-06-07, commit `2200b6c`; two introspections of the same DB now yield byte-identical
  models + deploy scripts)*
- ✅ **#49** — unify diagnostics into one compiler-style type (file/line/col/code/severity/related)
  *(delivered 2026-06-07, commit `949b766`; unified `Diagnostic` in `src/PgProj.Core/Diagnostics/`,
  producers migrated, JSON contract kept stable, 8 unit tests. Follow-up [#63](https://github.com/ilijevicdenis/CodeCleaners.PgProj/issues/63):
  populate & surface `Related[]` end-to-end)*
- ✅ **#36** — round-trip idempotency: phantom diffs + typed-table fidelity for raw object kinds
  *(delivered 2026-06-07; identity-only compare for canonical-reconstruction kinds, typed-table
  `OF type` fidelity, aggregate identity double-schema bug fixed. Scoped to the declared kinds; the
  rest split to [#61](https://github.com/ilijevicdenis/CodeCleaners.PgProj/issues/61) (raw kinds) +
  [#64](https://github.com/ilijevicdenis/CodeCleaners.PgProj/issues/64) (finely-modelled), both M4)*
- ✅ **#37** — integration tests on throwaway databases (isolation)
  *(delivered 2026-06-07; `ThrowawayDatabaseFixture` per-class DB create/drop, 5 integration classes
  migrated off fragile shared-DB cleanup)*
- ✅ **#60** — testing: golden-file scripts + CanonicalHash-stability + StableId-under-rename
  *(golden-file determinism delivered in M1; CanonicalHash-stability + StableId-under-rename tests
  landed with #42's `ObjectIdentityTests` in the M2 merge — fully covered)*
- ✅ **#62** — determinism: normalize source line endings so build artifacts are byte-reproducible
  *(delivered in M2 merge; load-time LF normalization via `SourceReader`, CRLF/LF determinism test,
  golden stopgap simplified)*
- ✅ **#63** — diagnostics: populate & surface related locations end-to-end
  *(delivered in M2 merge; duplicate-def diagnostics carry a `RelatedLocation`, `related` on the DTO,
  `ProjectBuildResult` carries unified `Diagnostic`)*

### M2 · Semantic Core — Identity & Foundations ✅

EP-SEMCORE foundational concepts + Phases 1–2. Unblocks the rest of the semantic core. **Complete (6/6).**

- ✅ **#42** — Object Identity Model (`ObjectId`/`StableId`/`CanonicalHash`, Phase 9) — *keystone for
  deterministic rename detection* *(delivered in M2 merge; `Model/Identity/` + `Comparison/Canonicalizer`
  + `IdentityDiff` classifier for #53; 20 tests. Follow-up: full Phase-8 canonical form refines it (#51))*
- ✅ **#43** — `PostgresVersionProfile` (capabilities / catalog-queries / object-capabilities)
  *(delivered in M2 merge; all of `LiveDatabaseReader`'s catalog SQL moved behind the profile, comparer
  asks `ObjectCapabilities`. Per-version query/ALTER overrides are the next increment)*
- ✅ **#44** — `IProjectObject` extensibility contract + object-kind registry
  *(delivered; one contract per kind + `ProjectObjectRegistry`, and **every per-kind switch collapsed into
  the single `ObjectKindRegistry` table** — `RawObjectMeta`, `SchemaCompareObjectType.OfKind`, the
  `LiveDatabaseReader` reader fan-out, and `ModelBuilder.BuildRaw` name-parsing all read it. Adding a kind
  that reuses existing styles = one registry row. Golden tests byte-identical; PG18 suite green. Follow-on:
  per-kind ownership of GenerateSql/Validate + `CatalogBuilder` symbol registration is a later deepening)*
- ✅ **#45** — Phase 1: project model loading hardening (source positions persisted + real glob/exclude)
  *(delivered in M2 merge; positions persisted at build → no re-parse, `**`/`<Exclude>` globbing)*
- ✅ **#46** — Phase 2: global symbol table (identity entries, overload-keyed funcs, reverse lookup, search_path)
  *(delivered in M2 merge; `SymbolTable`/`SymbolEntry` with StableId, signature overloads, reverse index)*
- ✅ **#39** — simpler project format *(delivered in M2 merge; SDK auto-includes `**/*.sql`, `EnableDefaultSqlItems` opt-out)*

### M3 · Semantic Core — Binding & Validation ✅

EP-SEMCORE Part A, Phases 3–8. **Complete (4/4).**

- ✅ **#47** — Phases 3–4: semantic binding → Bound AST → Typed Semantic Model
  *(delivered; `Semantics/Binding/` — refs resolve to `SymbolEntry`, exprs carry `ResolvedType`, view/CTE
  column inference, query API by symbol/location/reference for IDE features)*
- ✅ **#48** — Phase 5: validation depth (type safety, overload resolution, view/trigger/constraint validity)
  *(delivered; over the typed model with related locations; prove-before-erroring + managed-schema scoping
  → no false positives)*
- ✅ **#50** — Phases 6–7: dependency graph (Hard/Soft/Runtime edges) + circular-dependency detection
  *(delivered; `Semantics/Dependencies/`, Tarjan-SCC cycles naming the full path, reverse-closure API for Phase 15)*
- ✅ **#51** — Phase 8: canonical model hardening (column-order, body canonicalization, type aliases)
  *(delivered; broadened TypeNormalizer, canonical expression form, canonicalization in the model, gated column-order)*

### M4 · Diff, Risk, Deploy & Incremental ✅

EP-SEMCORE Parts B+C, Phases 10–15 & 18. **Complete (9/9).**

- ✅ **#52** — Phase 10: schema snapshots *(delivered; `schema.snapshot` offline compare endpoint + staleness + `pgproj snapshot` CLI)*
- ✅ **#53** — Phase 11: identity-based diff *(delivered; gated rename detection + structured function/unique/sequence/enum deltas)*
- ✅ **#54** — Phase 12: Deployment Risk Analyzer *(delivered; Safe/Warning/Dangerous/DataLoss/Blocking + rationale + rewrite/lock flags)*
- ✅ **#55** — Phase 13: deployment planning *(delivered; topo-sort over the dependency graph, edge-class aware, skeleton-then-alter for hard cycles)*
- ✅ **#56** — Phase 14: option- & version-aware script generation *(delivered; idempotent IF EXISTS, timeouts, verbose, include/exclude, ObjectCapabilities ALTER paths; defaults byte-identical)*
- ✅ **#57** — Phase 15: incremental analysis & object cache *(delivered; CanonicalHash-keyed cache, reverse-closure invalidation)*
- ✅ **#58** — Phase 18: options & profiles *(delivered; comparison-equivalence options, ComparisonProfile, block-on-data-loss gate wired to risk)*
- ✅ **#64** — round-trip idempotency (finely-modelled) *(delivered with #53/#61)*
- ✅ **#61** — round-trip idempotency (remaining raw kinds) *(cast/operator/op-class/op-family + Trigger [event-order canonicalized] + function-comment [types-only signature] — all round-trip clean against PG18, in the guard)*

### M5 · SSDT Parity — Editor UI ✅

The "same experience" UI stream (#27). **Complete (4/4).**

- ✅ **#31** — EP-LSP: resident language service (`pgproj serve` / LSP) for live parsing
  *(delivered; `PgProj.Lsp` + `pgproj serve` STDIO LSP host — didOpen/didChange→debounced publishDiagnostics
  [verdict identical to build], definition/hover/completion; pure handlers unit-tested [16 tests]; doc at
  `docs/LSP_LANGUAGE_SERVER.md`. This is the backend every editor client attaches to.)*
- ✅ **#24** — EP-VSCODE: VS Code extension (primary UI) *(`editors/vscode/` — LSP client to `pgproj serve`, projects
  tree, commands, Publish + Schema-Compare webviews, diagnostics→Problems. 62 vitest tests, tsc/eslint clean, `.vsix`
  packages; E2E green [8 pass/1 pending] via a space-free-temp runner shim.)*
- ✅ **#25** — EP-VS: Visual Studio experience *(Route A: `.pgproj` builds/publishes via `PgProj.Sdk`, `dotnet pack` →
  consumable nupkg; Route B VSIX scaffolded in its own solution [needs the VS SDK to build]; LSP client for VS. `docs/VISUAL_STUDIO.md`.)*
- ✅ **#26** — EP-DESIGNER: graphical table designer *(designer webview + engine `describe-table`/`emit-table` verbs —
  `.sql` generation stays in `SqlEmitter`; byte-stable round-trip proven over the corpus.)*

> Editor UIs are validated to the extent runnable here (unit + `tsc` + `.vsix` package + the .NET engine/round-trip layer
> + the VS Code E2E); a full VSIX build + VS Apex UI tests need the Visual Studio SDK host.

### M6 · Performance & Engine Backlog ✅

Open items from the perf/deploy-sync tracker (#12). Benchmark-gated (bytes/op); corpus byte-identical. **Complete (2/2).**

- ✅ **#8** — reduce `ModelBuilder` allocation *(delivered; lazy table-constraint lists — All 16.16→16.06 MB/op, Table bucket 1.77→1.66; dashboard stage #24. Safe-by-construction, no footgun.)*
- ✅ **#10** — profile the comparer / diff path (`CompareBenchmarks`) *(delivered; representative `CompareBenchmarks` + comparer fast-paths — −19% to −40% allocated/op across the compare path; behavior-preserving.)*

### M7 · SSDT Parity — Engine, Tooling & Coverage 🟡

Opened as a 55-issue backlog from [`docs/SSDT_PARITY_BACKLOG.md`](../SSDT_PARITY_BACKLOG.md) §3 + the
open **Introspect** rows in [`COVERAGE.md`](./COVERAGE.md), then **audited against the code**: both source
docs were stale — **4 of the 9 epics had already been delivered during the M-waves** (the boxes were never
checked). Those 4 epics + the already-done child tasks of the partial epics were **closed with evidence
comments** (27 issues). Then the remaining engine work was built & verified vs PG18, EP-CICD was removed,
and base types/transforms were backlogged → **6 of 8 in-scope epics done** (EP-CICD #71 removed). Each epic
is a `tracking` issue; tasks are linked children.

**Delivered (already on `main`; closed after audit):**
- ✅ **#66** — **EP-TARGET**: target-platform enforcement — `TargetVersionAnalyzer` + `PgVersionCapabilities`/`SupportedFeatures` table + `PGV###`; gate wired into build/publish/validate. Tests `TargetVersionTests`/`VersionProfileTests`.
- ✅ **#68** — **EP-PROFILE**: publish profiles — `Deployment/PublishProfile.cs` (secret-whitelisted) + `profile create` + `--profile` (CLI>profile>default). Tests `PublishProfileTests`.
- ✅ **#69** — **EP-SCHEMACOMPARE**: unified two-way `Comparison/SchemaCompare.cs` + selectable `SchemaChangeSet` + `--output diff.json` + `--exclude`. Tests `SchemaCompareTests`.
- ✅ **#70** — **EP-TEMPLATES**: `Templates/*` + `add`/`new project` verbs + `dotnet new` pack at `templates/`. Tests `TemplateTests`/`TemplateIntegrationTests`.
- ✅ **#67** — **EP-ANALYSIS+**: config (#77), `--rule` (#78), SARIF (#80), **external rule packs (#79** — `IPgRule` + `RulePackLoader`), and rules **PG006/PG008**. Doc `docs/ANALYSIS_RULES.md`. Epic closed; **#81** (grow the rule set) stays open as ongoing backlog.

**Open / in progress:**
- 🟡 **#72** — **EP-COVERAGE**: live-server introspection. **Done:** matview (#100), COMMENT ON (#105), aggregate (#106), cast/operator/op-class/family (#107), EXCLUDE (#98), EVENT TRIGGER tags (#104), POLICY `TO` roles (#103), USER MAPPING + **LANGUAGE** (#108), PARTITION/INHERITS (#99), index opclass/ordering (#101), expression statistics (#110), **TEXT SEARCH PARSER/TEMPLATE (#109)** — plus (already in code) collation/conversion/FDW/server/foreign-table/TS-config+dict/range/column-statistics/publications. **Backlog (genuinely need C functions in the PG server — in practice shipped via an extension):** base types (#102), transforms (#108 tail). All shipped work verified against PG18 on `milestone/m7-ssdt-parity`.
- ❌ **#71** — **EP-CICD**: **removed from M7 scope** (user decision). #97 (stable exit-code contract) was already delivered (`ExitCode.cs` + `ExitCodeContractTests` + `docs/CICD.md`) → closed done; the unbuilt GitHub Action / Azure DevOps task / container-image tickets (#94/#95/#96) closed as not-planned; the opt-in `ci/azure-devops/` template removed. The CLI stays CI-friendly via the documented exit codes + `--fail-on-changes`/`--dry-run`.
- ✅ **#111** — **EP-VS**: Visual Studio Route B VSIX + slngen grouping — all six children #113–#118 delivered 2026-06-11: real CPS `.pgproj` project type + templates + VS-loadable SDK (#113), `.vsct`/manifest wiring (#117), four property pages as `PgProj.Sdk` CPS rules incl. `PgProjPublishVariables`→`--var` (#114), modal Publish dialog (#115), interactive Schema Compare window with pickers/checkable diff/Generate Script/Apply (#116), and `pgproj sln new|add|list` solution grouping (#118, `PgProj.Core.Solutions`, 15 tests). Both VSIXes build headless-green (`editors/vs/build-vsix.cmd`); the runtime F5 pass in VS 2026 is manual (`editors/vs/README.md` follow-ups).
- ⬜ **#112** — **EP-DESIGNER**: editable designer + PG-specific surfaces — children #119–#120 (M5 #26 shipped read/round-trip; this deepens to editable + partitioning/identity/RLS/EXCLUDE).

> As each remaining child ships on `milestone/m7-ssdt-parity`, follow the
> [Update contract](#update-contract): tick its box, recompute the M7 `Done` count in §1, add a §3 row,
> and close the issue.

#### Next wave — prepared & ready to implement (opened 2026-06-24)

Scoped, unblocked tickets forming the next implementation push (designer #112/#119/#120 stays parked;
#102/#108 stay `blocked` pending C functions in the server). All on milestone **M7**:

- ✅ **#133 EP-REF** — NuGet `.pgpkg` package references — *delivered 2026-06-24*. All three children done:
  - ✅ **#147** — `dotnet pack` a project's built `.pgpkg` into a consumable `.nupkg` (`pgpkg/<Name>.pgpkg`, id/version) — `7344705`.
  - ✅ **#148** — `ReferenceResolver` resolves a `PackageReference` from the restored NuGet global packages folder, **reference-only** (no deploy DDL); unresolvable → PGREF006 — `b110bc3`.
  - ✅ **#149** — consumer/inlined deploy parity proven on the live shadow DB — `c445f8d`.
- ✅ **#150** (child of #139 EP-UNITTEST) — `pgproj test` **stub scaffolder** (pre/test/post from the semantic model, leading-`_` naming) — *delivered 2026-06-24, `b83e94b`*. Completes EP-UNITTEST #139.
- ✅ **#151** (child of #134 EP-EXTRACT) — `.pgpkg`-embedded `data/` **COPY section** (BACPAC-analogue variant) — *delivered 2026-06-24, `0a06a3d`*. Completes EP-EXTRACT #134. Solved the COPY-vs-`GENERATED ALWAYS AS IDENTITY` blocker by relaxing the column to `BY DEFAULT` around the load; verified end-to-end against live PG18.
- ✅ **#152** (child of #136 EP-REFACTOR) — **`expand-wildcards`** command (`SELECT *` → explicit columns, model-resolved) — *delivered 2026-06-24, `7809b6d`*. Completes EP-REFACTOR #136.

---

## 3. Delivery log

Newest first. One line per delivered issue/milestone; this is the audit trail of what shipped when.

| Date | Milestone | Item | Commit | Notes |
|------|-----------|------|--------|-------|
| 2026-06-24 | M7+ | **EP-REF: NuGet `.pgpkg` package references done** (#133, closes #147/#148/#149) — `dotnet pack` packs a project's built `.pgpkg` into a `.nupkg` under `pgpkg/<Name>.pgpkg` (id=name, version=`<Version>`); `ReferenceResolver` resolves a `<PackageReference>` from the restored NuGet global packages folder (`NuGetPackageLocator`) and loads it **reference-only** — objects widen validation/binding but never enter the comparer's model, so no deploy DDL; unresolvable → `PGREF006`. CLI/SDK: pack via `dotnet pack`, consume via `<PackageReference>` after `dotnet restore` | `7344705`, `b110bc3`, `c445f8d` | `dotnet pack` smoke (nupkg carries `pgpkg/AllFeaturesDb.pgpkg`, id/version correct); reference resolution tests (resolve from temp feed, reference-only never-emitted, unresolvable PGREF006); **live shadow-DB parity** (consumer-with-package deploys schema-identical to the inlined baseline; cross-schema view queryable). Reference/packaging suites 40/0 with PG18. |
| 2026-06-24 | M7+ | **EP-EXTRACT: `.pgpkg`-embedded data COPY section done** (#151, closes #134) — the schema+data BACPAC analogue: `PgPkg` gains a `data/` section (index + FK-ordered COPY payload per table, outside the source checksum); `DataExporter.ExportCopyAsync` (`COPY (SELECT … ORDER BY key) TO STDOUT`) + `CopyDataLoader` (`COPY … FROM STDIN`, then `setval`). Solves the COPY-vs-`GENERATED ALWAYS AS IDENTITY` blocker by relaxing the column to `BY DEFAULT` around the load and restoring `ALWAYS`. CLI: `extract --package <out.pgpkg> [--all-table-data|--table-data …]` produces it; `publish <pkg>` loads the data after schema | `0a06a3d` | New `CopyDataSectionTests` (DB-free format round-trip + checksum isolation; **live export→pack→unpack→load** reproduces rows + ALWAYS-identity ids + next-id sequence) + a live CLI extract→publish smoke. Packaging/data suites 24/0 with PG18. |
| 2026-06-24 | M7+ | **EP-REFACTOR: expand-wildcards done** (#152, closes #136) — `pgproj expand-wildcards <project> <schema.view>` resolves a view's `SELECT *` / `alias.*` to an explicit, model-derived column list and rewrites the `.sql` in place + records it in `.pgrefactorlog`. `WildcardExpander` does minimal comment/string/dollar-quote/paren-aware surgery — only top-level star tokens change, the rest stays byte-identical (so `count(*)` and `'*'` literals are never touched); bare `*` over multiple sources is alias-qualified | `7809b6d` (branch `feature/m7-tail-children`) | New `ExpandWildcardsTests` (single-table, joined/aliased `t.*`, two-source bare `*`, count/literal safety, no-star + missing-view errors). Solution build green; 35 refactor/test-suite tests pass. |
| 2026-06-24 | M7+ | **EP-UNITTEST: test stub scaffolder done** (#150, closes #139) — `pgproj test scaffold <project> <schema.object>` builds the model and emits a pre/test/post unit-test stub for a function/procedure/trigger to `Tests/_schema.object.test.sql`; leading `_` keeps it out of the build glob while `.test.sql` keeps it discoverable by `pgproj test`. Functions get a scalar-assert body, procedures a `CALL`, triggers a DML-fires note; arg types render as `NULL::type` | `b83e94b` (branch `feature/m7-tail-children`) | New `TestScaffolderTests` (6: function/procedure/trigger shapes, no-arg, unknown/unqualified errors) + CLI smoke (scaffold → build still excludes the stub). |
| 2026-06-24 | M7+ | **Blackbox test suite** — new `tests/PgProj.Blackbox.Tests` drives the `pgproj` CLI end-to-end against a live dockerized source+target (`tests/blackbox-db`): basics, build/publish happy + failure-recovery, data-compare, extract+sync, refactor, snapshot/package, test-runner; plus a DB-backed VS Code E2E (`db.test.ts`, `runTest.ts` forwards the Docker connection strings + built CLI dll) and VS UiTests tooling scenarios. Wired into `PgProj.slnx`. | `5fa4f87` | Closed the four fully-delivered epics this validates: **#132** EP-DATACOMPARE (`f2d6330`), **#137** EP-DEPLOY (`5fbd9f1`), **#140** EP-PROFILE (`5fbd9f1`), **#142** EP-PKG (`0f81c34`). Commented blackbox coverage on the still-open tails #139/#134/#136. |
| 2026-06-17 | M7 | **EP-ANALYSIS+: six new analyzer rules** (#81) — PG017 `json` column (prefer `jsonb`), PG019 FK with no `ON DELETE`/`ON UPDATE` action, PG020 `EXCEPTION WHEN OTHERS`, PG021 `SELECT … INTO` without `STRICT` (per-file); PG024 duplicate index + PG025 redundant (leading-prefix) index (model-level, b-tree-ordered, explicit-only, partial-aware) | `278518b` | New `PgAnalyzerTests` (PG017/PG020/PG021) + `ModelAnalyzerTests` (PG019/PG024/PG025 incl. column-order/predicate/partial negatives); registry-consistency extended; documented in `docs/ANALYSIS_RULES.md`. Full DB-free suite 22,615 pass / 0 fail. **#81 stays open** as the ongoing rule backlog. |
| 2026-06-13 | M7+ | **EP-UNITTEST: PL/pgSQL unit-test runner done** (#139, core) — `pgproj test <project> --connection [--deploy]` discovers `*.test.sql`, runs each inside its own `BEGIN … ROLLBACK` (single-transaction scope, residue-free), reporting passed/failed/inconclusive with a non-zero exit (new code 10 `TestFailed`). `PgUnitRunner` ships an assertion prelude (the predefined conditions: assert, row count, scalar, empty/not-empty, column-type/expected-schema, data checksum, plus `pgproj_inconclusive`) and an `-- @expect-sqlstate:` directive for negative/expected-exception tests; `--deploy` applies the project schema to a throwaway shadow DB first | `97db767` | New `PgUnitRunnerTests` (conditions pass/fail, expected-SQLSTATE incl. wrong/none, inconclusive, single-transaction-scope residue check, directive parser) + a CLI `test --deploy` smoke. Full suite 25,164 / 0. **Open in #139:** the stub scaffolder. |
| 2026-06-13 | M7+ | **EP-EXTRACT: table-data extract done** (#134, core) — `pgproj extract --all-table-data` / `--table-data schema.table` adds an FK-ordered `Scripts/PostDeploy.sql` data seed to the extracted project (excluded from Build, wired as the PostDeploy script, so a normal publish loads schema then data); `DataExporter` emits INSERT batches parents-before-children, writes identity columns with `OVERRIDING SYSTEM VALUE`, and `setval`-corrects each identity/serial sequence past the loaded rows | `8a9fe6e` | New `DataExporterTests` — a live extract→load round-trip proving rows + identity + next-id sequence reproduce, and `--table-data` selection — plus a CLI smoke (extracted project builds). Full suite 25,156 / 0. **Open in #134:** the `.pgpkg`-embedded `data/` (COPY) variant. |
| 2026-06-13 | M7+ | **EP-DATACOMPARE: row-level data compare + sync done** (#132) — `pgproj data-compare --source --target [--tables] [-o diff.json\|sync.sql] [--apply]`: `DataCompare` reads both live models, picks each table's key (PK, else a UNIQUE constraint), streams rows and buckets them Different / Only-in-Source / Only-in-Target / Identical with per-column diffs; keyless / one-sided tables are reported as skipped. `GenerateSyncScript` emits a deterministic DELETE→INSERT→UPDATE script (children-first deletes, parents-first writes) that `--apply` runs on the target in one transaction; a shared SQL-literal formatter backs both the diff key and the DML | `f2d6330` | New `DataCompareTests` (all four categories, keyless skip, identical-in-sync, literal formatting, and a live compare→apply→re-compare round-trip) + a CLI smoke. Full suite with PGPROJ_TEST_CONNECTION. |
| 2026-06-13 | M7+ | **EP-PKG: project-snapshot CLI done** (#142) — `pgproj snapshot create` writes a timestamped read-only `Project_YYYYMMDD_HH-MM-SS.pgpkg` of the BUILT project model under `Snapshots/` (no DB); `compare <A> <B>` runs the unified two-way Schema Compare over snapshot/package/project specs; `revert <project> <snapshot>` reverse-syncs the `.sql` back to a snapshot (destructive removals gated by `--allow-deletes`, like `pull`); `import` registers an external `.pgpkg` as a read-only selectable compare source. Distinct from the live-DB `.schema.snapshot` (the bare `snapshot --connection` form is preserved via subcommand dispatch) | `0f81c34` | New `ProjectSnapshotTests` (create→compare self = in-sync, create→revert = no-op, compare detects a real edit) + a manual CLI smoke of all four subcommands. Full suite 25,149 / 0. |
| 2026-06-13 | M7+ | **EP-REFACTOR: persisted `.pgrefactorlog` (core)** (#136) — committed `{operation,objectType,oldName,newName}` artifact (`Refactoring/RefactorLog`); the deploy planner consumes it BY DEFAULT (its presence is the opt-in) via `ComparerOptions.RefactorLog`, seeding the rename pre-pass so a logged table rename → `ALTER … RENAME`, schema move → `SET SCHEMA`, column rename → `RENAME COLUMN` (new `RenameColumnChange`/`SetTableSchemaChange`) instead of DROP+CREATE; `RiskAnalyzer` classifies them data-safe; CLI `rename`/`move-schema` rewrite the `.sql` (definition + qualified refs, word-boundary-safe) AND append the log atomically (`RefactorEngine`) | `f9feb52` | New `RefactorLogTests` (12: consumption per kind + stale-guard + authoring rewrite + round-trip) + a live shadow-DB proof (logged rename keeps rows; no-log baseline drops them). Full suite 25,143 / 0. The log is also **packed into `.pgpkg`** (`refactorlog.json`, outside the source checksum) and consumed on publish-from-package. **Open in #136:** only `expand-wildcards`. |
| 2026-06-13 | M7+ | **EP-DEPLOY: CONCURRENTLY / lock-minimizing deploy done** (#137) — `ConcurrentIndexOperations` option (CLI `--concurrent-indexes`/`--minimize-locks`, profile, `.pgpkg`): `LockMinimizer` rewrites index create/drop → `CONCURRENTLY` and named FK/CHECK adds → `NOT VALID` + a separate `VALIDATE CONSTRAINT` pass; new `RunsOutsideTransaction` flag partitions those steps after `COMMIT` and routes apply through `PhasedDeployer` (autocommit). RiskAnalyzer reports blocking vs non-blocking + INVALID-index note; ADD COLUMN DEFAULT version-gate comment (<PG11) | `5fbd9f1` | New `DeployScriptConcurrent137Tests` (DB-free) + a live shadow-DB round-trip proving the concurrent deploy reaches the same end-state as the transactional one (re-compare empty). Full suite green with PGPROJ_TEST_CONNECTION. |
| 2026-06-13 | M7+ | **EP-PROFILE: full DacDeployOptions-equivalent family done** (#140) — `PublishProfileOptions` gains BlockOnPossibleDataLoss, granular Drop{Constraints,Indexes}NotInSource + DoNotDropObjectTypes, GenerateSmartDefaults, ScriptNewConstraintValidation (NOT VALID), AllowTableRecreation, CommandTimeout/LockTimeout (session SETs incl. PhasedDeployer), ExcludeObjectTypes — all nullable, CLI>profile>default; publish blocks on data loss by default (new exit 9 `DataLossBlocked`, `--allow-data-loss` opts out) | `5fbd9f1` | New DeployScriptOptions140Tests + PublishProfile round-trip/IsEmpty + a live smart-defaults round-trip; full suite 25,123 pass / 0 fail with PGPROJ_TEST_CONNECTION. |
| 2026-06-13 | M7+ | **EP-ANALYSIS+: PG015/PG016 added** (#81) — PG015 flags uppercase identifiers (case-fold/forced-quoting footgun), PG016 flags identifiers > 63 bytes (silent truncation), on table + column names | `5fbd9f1` | 2 new PgAnalyzerTests; documented in docs/ANALYSIS_RULES.md. #81 stays open as an ongoing rule backlog. |
| 2026-06-12 | M7+ | **EP-BUILD done** (#135) - .pgproj SuppressWarnings + TreatWarningsAsErrors via shared BuildWarningPolicy in ContractBuilder.Analyze; `--verbose` structured diagnostics with file:line | `861a13c` | 7 tests; CLI gate and in-proc editor path identical by construction. |
| 2026-06-12 | M7+ | **EP-PKG verify done** (#138) - `pgproj verify <a.pgpkg> <b.pgpkg>`: model+sources+options equivalence, stamps excluded, exit 0/6, JSON+text - the DacpacVerify analogue | `e63417c` | 8 tests incl. live extract round-trip; documented in docs/CICD.md as the local reproducibility gate. |
| 2026-06-12 | M7+ | **EP-DEPLOYREPORT done** (#141) - `pgproj deploy-report` / `publish --report-only`: apply-free planned-change report from the shared publish plan; per-op RiskAnalyzer verdicts, blocksOnDataLoss gate, pre/post + strategy, JSON+XML | `221f503` | Integration-tested vs real PG incl. target-unchanged guarantee; suites 25,091 Core + 28 LSP. |
| 2026-06-12 | - | **Analyzer fixes** (#65) - PG004 matches through TEMP/OR REPLACE/UNIQUE/... modifiers; PG009 checks view bodies | `9396875` | 11 regression cases. |
| 2026-06-12 | M7 | **VS experience validated in the installed product + hardened** — three silent editor-chain breaks fixed (per-user MEF cache skipped by `/updateconfiguration`; VsBufferDetectLangSID content-type overwrite; CodeRemote base required for LSP activation); PostgreSQL classifier; live diagnostics = build verdict (reference/semantic gate + cross-file invalidation); alias-aware completion/F12/hover + column-precise definition; `pgproj sync-file` + in-VS file-level Sync-with-Database (diff + take-DB/push-local/cancel); CURRENT_USER validator false positive | `d9e75dd`, `9343c5b`, `ca59e68`, `077dbde` on `milestone/m7-ssdt-parity` | Validated by a NEW 115-scenario real-user E2E suite (FlaUI+DTE-over-ROT, Hyper-V VM, dockerized PG18 sample DB `tests/sample-db`): **round 10 = 115/115**. Engine suites green: 25,086 Core (0 skips with PGPROJ_TEST_CONNECTION) + 28 LSP. This closes the "manual F5 pass outstanding" follow-up from EP-VS #111. |
| 2026-06-11 | M7 | **EP-VS done** (#111) — CPS `.pgproj` project type + templates + VS-loadable SDK (#113), `.vsct`/manifest (#117), four property pages as SDK CPS rules + `PgProjPublishVariables`→`--var` (#114), modal Publish dialog (#115), interactive Schema Compare window (#116), `pgproj sln` solution grouping (#118) | `a1fa294`, `a59737e`, `9be4cb2`, `1e7422f` on `milestone/m7-ssdt-parity` | Headless: both VSIXes 0-warn/0-err, SDK packs with rules, full suite 22,526 pass / 0 fail (15 new `SolutionGroupingTests`); `--var` forwarding verified end-to-end via offline dry-run. Manual F5 pass in VS 2026 outstanding (README follow-ups). |
| 2026-06-08 | M7 | **EP-COVERAGE introspection** — EXCLUDE (#98), EVENT TRIGGER tags (#104), POLICY `TO` roles (#103), USER MAPPING + LANGUAGE (#108), PARTITION/INHERITS (#99), index opclass/ordering (#101), expression statistics (#110), TS PARSER/TEMPLATE (#109) | `8e2c387`…`ff1de45` on `milestone/m7-ssdt-parity` | One reader per kind (+ a parser fix for `CREATE USER MAPPING`); each verified vs PG18 by the deploy→read-back→reparse→re-deploy round-trip; goldens regenerated. #104 (event-trigger tags) and #101 (index ordering) also fixed latent silent phantom diffs. Full suite 25,044 pass / 0 fail. |
| 2026-06-08 | M7 | **EP-ANALYSIS+ done** — external rule packs (#79: `IPgRule`+`RulePackLoader`) + PG006 missing-PK / PG008 untyped-numeric (#81 partial); epic #67 closed | `f3a7639`, `f734255` | 7 rule-pack tests + analyzer tests; doc `docs/ANALYSIS_RULES.md`. #81 stays open as ongoing rule backlog. |
| 2026-06-08 | M7 | **Complete SQLSTATE table** — `PgErrorCodes` (262 codes / 43 classes from PostgreSQL `errcodes.txt`); `validate`/`publish` errors now enriched (`42704 undefined_object (class 42: …)`) | `e0a28ef` | 11 tests; central test `DropSampleSql` now drops the global `afd_plpgsql` language. Full suite 25,055 pass / 0 fail. |
| 2026-06-08 | M7 | **EP-CICD removed** (user decision) — closed #71/#94/#95/#96 (not planned); #97 exit-code contract closed done; opt-in `ci/azure-devops/` template removed | `37309d8` | Exit-code contract kept (`ExitCode.cs` + `ExitCodeContractTests` + `docs/CICD.md`). **Backlog:** base types (#102) + transforms (#108 tail) — need C functions in the PG server (labelled `blocked`). |
| 2026-06-07 | M7 | **Audit & reconcile** — 4 epics (#66 TARGET, #68 PROFILE, #69 SCHEMACOMPARE, #70 TEMPLATES) + done child tasks were already on `main`; closed 27 redundant issues with evidence | branch `milestone/m7-ssdt-parity` | The opened M7 backlog was sourced from stale `SSDT_PARITY_BACKLOG.md`/`COVERAGE.md`; audit against the code found most of it shipped in the M-waves. Docs refreshed (this file + COVERAGE.md + backlog). Remaining open: #67 (#79,#81), #72 (#98,#99,#101–104,#108–110), #71, #111, #112. |
| 2026-06-07 | M5 | #24 VS Code extension + #25 Visual Studio + #26 table designer (**completes M5 & SSDT parity**) | merge of `milestone/m5-editor-clients` | Two waves of worktree agents + hand-resolved merges. VS Code extension finished (LSP client, webviews, 62 vitest tests, .vsix, E2E green via a space-free-temp runner shim); VS Route-A SDK build/publish/pack validated + Route-B VSIX scaffolded; designer with engine-backed `.sql` round-trip (.NET 22,477 pass). Editor UIs validated to the extent runnable here. |
| 2026-06-07 | M6 | #8 lazy table-constraint lists + #10 comparer fast-paths (**completes M6**) | merge of `milestone/m6-performance` | Two safe, BDN-measured allocation wins: parse+build All 16.16→16.06 MB/op (dashboard stage #24); comparer −19..−40%/op on a now-representative CompareBenchmarks. Corpus + goldens byte-identical; full suite green vs PG18. |
| 2026-06-07 | M4 | #61 round-trip tail closed (trigger event order + function-comment types-only signature) | merge of `fix/61-trigger-comment-roundtrip` | Completes M4 (9/9). Both gaps verified fixed against PG18; round-trip guard now covers all raw kinds AllFeaturesDb exercises. |
| 2026-06-07 | M5 | M5 closed: #31 delivered; GUI clients #24/#25/#26 deferred → #27 | — | The .NET language-service foundation is the M5 deliverable; the editor GUIs are a separate toolchain effort tracked under the SSDT-parity epic. |
| 2026-06-07 | M5 | #31 EP-LSP resident language service (`pgproj serve`) | merge of `milestone/m5-editor-ui` | New `PgProj.Lsp` + `serve` verb; STDIO LSP over the engine (debounced diagnostics = build verdict; definition/hover/completion). 16 LSP tests; corpus/goldens unaffected. Editor-client epics #24/#25/#26 remain (TypeScript/VSIX, separate toolchain). |
| 2026-06-07 | M4 | #52 #53 #54 #55 #56 #57 #58 #64 + #61 (partial) — snapshots, identity diff, risk, planning, gen, incremental, options, round-trip | merge of `milestone/m4-diff-deploy` | Three dependency-ordered worktree-agent waves. PG18 suite 22,471 pass / 0 fail / 0 skip; allocation neutral; defaults byte-identical. #61 trigger + function-comment round-trip gaps remain (tracked). |
| 2026-06-07 | M3 | #47 #48 #50 #51 — binding, validation, dependency graph, canonical hardening (**completes M3**) | merge of `milestone/m3-binding-validation` | Two worktree-agent waves (#47/#51 then #48/#50 on the bound model). PG18 suite 22,363 pass / 0 fail / 0 skip; goldens byte-identical. Parse allocation +0.35% (tightened from +1.16% — ColumnRef single-slot, trigger detail behind one ref). |
| 2026-06-07 | M2 | #44 IProjectObject contract + object-kind registry (**completes M2**) | merge of `feature/44-iprojectobject-registry` | Contract + registry; every per-kind switch (RawObjectMeta, SchemaCompareObjectType, LiveDatabaseReader fan-out, ModelBuilder.BuildRaw) collapsed into one ObjectKindRegistry table. Golden byte-identical; PG18 suite 22,301 pass / 0 fail / 0 skip. |
| 2026-06-07 | M2 + M1 | #42 #43 #45 #46 #39 (M2) + #60 #62 #63 (M1 finishers) | merge of `milestone/m2-semantic-core` | Two dependency-phased worktree-agent waves + hand-resolved integration (unified #63/#45 position tracking onto `SourcePositionIndex`). **M1 now complete (7/7).** Validated against PG18: 22,294 pass / 0 fail / 0 skip; allocation neutral. M2 5/6 — #44 remains. |
| 2026-06-07 | M1 | #49 unify diagnostics + #37 throwaway-DB isolation + #36 round-trip (scoped) + #60 golden-file | merge of `milestone/m1-foundations` | M1 wave delivered via 4 worktree agents + integration fixes (aggregate identity double-schema bug). Validated against PG18: 22,217 pass / 0 fail / 0 skip; allocation footprint unchanged (16.94 MB/op). #60 golden-file done; its hash/rename tests deferred to #42. Remaining M1: #62, #63. |
| 2026-06-07 | M1 | #59 deterministic raw-object ordering | `2200b6c` | Sort `model.Objects` by (kind, schema, name, identity) after the parallel merge; DB-free + live regression tests; full suite green (22,195 pass against PG18). |

---

## Update contract

**On every delivered milestone item, update this document in the same PR/commit that delivers it:**

1. Flip the issue's box to ✅ in §2 and add the delivery date + commit.
2. Recompute the milestone's `Done` count and `Status` (🟡 once any item ships, ✅ when all do) in §1.
3. Add a row to the §3 delivery log (date, milestone, item, commit, one-line note).
4. Bump **Last updated** at the top.
5. Close the GitHub issue and, when the last item ships, close the GitHub milestone.

This is a **manual step performed as part of the delivery commit** — no git hook, GitHub Action, or
CI job runs it (the repo is validated locally; see the CI/CD hard rule in `CLAUDE.md`). For a
**complex issue, break it into smaller subtasks** (the sub-bullets in §2) and check them off as you go,
so progress is visible before the whole issue closes.

**Sources of truth:** EP-SEMCORE epic #41 · parity tracker #27 + [`docs/SSDT_PARITY_BACKLOG.md`](../SSDT_PARITY_BACKLOG.md) ·
perf tracker #12 + [`docs/parser-performance.md`](../parser-performance.md).
