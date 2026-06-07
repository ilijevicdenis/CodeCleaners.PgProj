# CodeCleaners.PgProj — Delivery Progress Tracker

> **This is the master progress report.** It tracks every delivery milestone toward
> SSDT-for-PostgreSQL parity and is the single place to read "where are we now". It is **updated on
> every delivered milestone** (see [Update contract](#update-contract) below).

**Last updated:** 2026-06-07 (M1–M4 + M6 complete; M5 foundation done, GUI clients deferred → #27) ·
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
| **M5** | SSDT Parity — Editor UI | parity #27 | #31 *(clients #24/#25/#26 → #27)* | 1 / 1 | ✅ | [milestone/5](https://github.com/ilijevicdenis/CodeCleaners.PgProj/milestone/5) |
| **M6** | Performance & Engine Backlog | perf #12 | #8, #10 | 2 / 2 | ✅ | [milestone/6](https://github.com/ilijevicdenis/CodeCleaners.PgProj/milestone/6) |

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

### M5 · SSDT Parity — Editor UI ✅ *(foundation; GUI clients deferred)*

The "same experience" UI stream (#27). The **.NET language-service foundation that every editor attaches
to is delivered**; the GUI-client epics are deferred (by decision) to a dedicated effort with the proper
editor toolchain (Node + `@vscode/test-electron`; the VS SDK + Apex UI tests) where they can be
E2E-validated — they live on under the SSDT-parity tracker [#27] and `docs/SSDT_PARITY_BACKLOG.md`.

- ✅ **#31** — EP-LSP: resident language service (`pgproj serve` / LSP) for live parsing
  *(delivered; `PgProj.Lsp` + `pgproj serve` STDIO LSP host — didOpen/didChange→debounced publishDiagnostics
  [verdict identical to build], definition/hover/completion; pure handlers unit-tested [16 tests]; doc at
  `docs/LSP_LANGUAGE_SERVER.md`. This is the backend every editor client attaches to.)*
- ⏸️ **#24** — EP-VSCODE: VS Code extension (primary UI) — *deferred → #27 (TypeScript; needs Node/`@vscode/test-electron`)*
- ⏸️ **#25** — EP-VS: Visual Studio experience — *deferred → #27 (VSIX/C#; needs VS SDK + Apex UI tests)*
- ⏸️ **#26** — EP-DESIGNER: graphical table designer — *deferred → #27 (P2; UI on top of #24/#25)*

### M6 · Performance & Engine Backlog ✅

Open items from the perf/deploy-sync tracker (#12). Benchmark-gated (bytes/op); corpus byte-identical. **Complete (2/2).**

- ✅ **#8** — reduce `ModelBuilder` allocation *(delivered; lazy table-constraint lists — All 16.16→16.06 MB/op, Table bucket 1.77→1.66; dashboard stage #24. Safe-by-construction, no footgun.)*
- ✅ **#10** — profile the comparer / diff path (`CompareBenchmarks`) *(delivered; representative `CompareBenchmarks` + comparer fast-paths — −19% to −40% allocated/op across the compare path; behavior-preserving.)*

---

## 3. Delivery log

Newest first. One line per delivered issue/milestone; this is the audit trail of what shipped when.

| Date | Milestone | Item | Commit | Notes |
|------|-----------|------|--------|-------|
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
