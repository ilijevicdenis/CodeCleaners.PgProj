# CodeCleaners.PgProj — Delivery Progress Tracker

> **This is the master progress report.** It tracks every delivery milestone toward
> SSDT-for-PostgreSQL parity and is the single place to read "where are we now". It is **updated on
> every delivered milestone** (see [Update contract](#update-contract) below).

**Last updated:** 2026-06-07 (M1 wave delivered) · **Streams:** semantic core (EP-SEMCORE #41) ·
SSDT parity (#27) · performance (#12)

Legend: ✅ delivered · 🟡 in progress · ⬜ not started · ⛔ blocked.

---

## 1. Roadmap at a glance

Milestones map to the three tracking epics. GitHub milestones mirror this table (the issues are the
deliverable subtasks; **complex issues are broken into smaller subtasks when implemented**).

| # | Milestone | Stream | Issues | Done | Status | GitHub |
|---|-----------|--------|--------|------|--------|--------|
| **M1** | Determinism & Diagnostics Foundations | cross-cutting | #59, #49, #36, #37, #60, #62, #63 | 4 / 7 | 🟡 | [milestone/1](https://github.com/ilijevicdenis/CodeCleaners.PgProj/milestone/1) |
| **M2** | Semantic Core — Identity & Foundations | EP-SEMCORE | #42, #43, #44, #45, #46, #39 | 0 / 6 | ⬜ | [milestone/2](https://github.com/ilijevicdenis/CodeCleaners.PgProj/milestone/2) |
| **M3** | Semantic Core — Binding & Validation | EP-SEMCORE | #47, #48, #50, #51 | 0 / 4 | ⬜ | [milestone/3](https://github.com/ilijevicdenis/CodeCleaners.PgProj/milestone/3) |
| **M4** | Diff, Risk, Deploy & Incremental | EP-SEMCORE | #52, #53, #54, #55, #56, #57, #58, #61, #64 | 0 / 9 | ⬜ | [milestone/4](https://github.com/ilijevicdenis/CodeCleaners.PgProj/milestone/4) |
| **M5** | SSDT Parity — Editor UI | parity #27 | #31, #24, #25, #26 | 0 / 4 | ⬜ | [milestone/5](https://github.com/ilijevicdenis/CodeCleaners.PgProj/milestone/5) |
| **M6** | Performance & Engine Backlog | perf #12 | #8, #10 | 0 / 6 † | ⬜ | [milestone/6](https://github.com/ilijevicdenis/CodeCleaners.PgProj/milestone/6) |

† M6 only carries the two *open* perf items; most of the allocation campaign already landed on `main`
(see [`docs/parser-performance.md`](../parser-performance.md) and tracker #12).

**Already shipped (not milestoned — do not rebuild):** the headless engine — `build`, `compare`,
`publish`, `validate`, `extract`, `drift`, `analyze`, `PgProj.Sdk`, and **100% parser accept/reject
parity vs PostgreSQL 18** on the 21,743-statement corpus. Parity-stream Phase-1 engine epics
(`.pgpkg`, project variables, pre/post-deploy scripts, references, JSON-RPC) landed via waves 1–2; see
[`docs/SSDT_PARITY_BACKLOG.md`](../SSDT_PARITY_BACKLOG.md) §2.

---

## 2. Milestones in detail

Each issue is a deliverable subtask. Sub-bullets are the smaller subtasks a complex issue is split
into when work starts — fill them in (and check them off) as the issue is implemented.

### M1 · Determinism & Diagnostics Foundations 🟡

The reproducibility & quality base everything else leans on.

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
- 🟡 **#60** — testing: golden-file scripts + CanonicalHash-stability + StableId-under-rename
  *(golden-file determinism delivered 2026-06-07 — deploy-script + model-JSON goldens with regen via
  `PGPROJ_UPDATE_GOLDEN`. CanonicalHash-stability + StableId-under-rename tests are **blocked on #42**
  (M2 Object Identity Model) and land with it)*
- ⬜ **#62** — determinism: normalize source line endings so build artifacts are byte-reproducible
  across CRLF/LF checkouts *(golden test currently folds escaped EOLs as a stopgap)*
- ⬜ **#63** — diagnostics: populate & surface related locations end-to-end (completes #49)

### M2 · Semantic Core — Identity & Foundations ⬜

EP-SEMCORE foundational concepts + Phases 1–2. Unblocks the rest of the semantic core.

- ⬜ **#42** — Object Identity Model (`ObjectId`/`StableId`) + `CanonicalHash` (Phase 9) — *keystone for
  deterministic rename detection*
- ⬜ **#43** — `PostgresVersionProfile` (capabilities / catalog-queries / object-capabilities)
- ⬜ **#44** — `IProjectObject` extensibility contract + object-kind registry
- ⬜ **#45** — Phase 1: project model loading hardening (source-file/location persistence + glob/exclude)
- ⬜ **#46** — Phase 2: global symbol table (identity entries, overload-keyed funcs, reverse lookup, search_path)
- ⬜ **#39** — simpler project format

### M3 · Semantic Core — Binding & Validation ⬜

EP-SEMCORE Part A, Phases 3–8.

- ⬜ **#47** — Phases 3–4: semantic binding → Bound AST → Typed Semantic Model
- ⬜ **#48** — Phase 5: validation depth (type safety, overload resolution, view/trigger/constraint validity)
- ⬜ **#50** — Phases 6–7: dependency graph (Hard/Soft/Runtime edges) + circular-dependency detection
- ⬜ **#51** — Phase 8: canonical model hardening (column-order, body canonicalization, type aliases)

### M4 · Diff, Risk, Deploy & Incremental ⬜

EP-SEMCORE Parts B+C, Phases 10–15 & 18.

- ⬜ **#52** — Phase 10: schema snapshots (`schema.snapshot` artifact — versioning + staleness)
- ⬜ **#53** — Phase 11: identity-based diff (StableId rename detection + field-level deltas)
- ⬜ **#54** — Phase 12: Deployment Risk Analyzer (Safe/Warning/Dangerous/DataLoss/Blocking)
- ⬜ **#55** — Phase 13: deployment planning (computed topo-sort, edge-class aware, cycle→skeleton-then-alter)
- ⬜ **#56** — Phase 14: option- & version-aware script generation (prefer-ALTER, idempotent, timeouts, verbose)
- ⬜ **#57** — Phase 15: incremental analysis & object cache (reverse-dep invalidation)
- ⬜ **#58** — Phase 18: options & profiles (comparison equivalence + ComparisonProfile + block-on-data-loss)

### M5 · SSDT Parity — Editor UI ⬜

The "same experience" UI stream (#27). Depends on the engine JSON contract (already landed) and the
semantic core for live features.

- ⬜ **#31** — EP-LSP: resident language service (`pgproj serve` / LSP) for live parsing
- ⬜ **#24** — EP-VSCODE: VS Code extension (primary UI)
- ⬜ **#25** — EP-VS: Visual Studio experience
- ⬜ **#26** — EP-DESIGNER: graphical table designer

### M6 · Performance & Engine Backlog ⬜

Open items from the perf/deploy-sync tracker (#12). Benchmark-gated (bytes/op); the corpus must stay
byte-identical.

- ⬜ **#8** — reduce `ModelBuilder` allocation (AddTable hotspot) — *gated on #7 production-metric spike*
- ⬜ **#10** — profile the comparer / diff path (`CompareBenchmarks`) — *gated on #7*

---

## 3. Delivery log

Newest first. One line per delivered issue/milestone; this is the audit trail of what shipped when.

| Date | Milestone | Item | Commit | Notes |
|------|-----------|------|--------|-------|
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
