# Parser Performance — Allocation Reduction

**Headline: the pgproj parser now allocates `16.06 MB/op` to parse + build the corpus, down from `66.28 MB/op` — a `−75.8%` cut in managed allocations across 24 optimizations.**

Metric: **BenchmarkDotNet `PipelineBenchmarks.ParseAndBuild` allocated bytes/op (MB, lower is better)**, measured under **Workstation GC** (the runtime `PgProj.Cli` ships), over a corpus of PostgreSQL DDL/DML. Allocation is the deterministic, reportable number; wall-clock is omitted as noise (see [Caveats](#caveats)).

---

## 1. The full journey — "All" corpus bucket

Every merged optimization, in order. Allocation falls monotonically from baseline to the latest win.

![ParseAndBuild allocation, All corpus — 66.28 to 16.06 MB/op (−75.8%) over 24 optimizations; each stage labelled vertically inside its bar, prior effort in grey, recent in blue, the stability re-baseline in green, the allocation-floor pass in purple.](parser-perf-journey.svg)

> **Three efforts plus a re-baseline.** Wins **1–10 (`Base` → `ResCap`)** were a prior effort that took allocation from `66.28` to `31.22 MB/op`. Wins **11–18 (`NameList` → `Cursor`)** are the recent effort, taking it to `18.94 MB/op` — an additional `−39.3%` on top of an already-optimized parser (the single biggest step is the `Pool` win, `25.88 → 19.95`, `−22.9%` in one move). Stage **19 (`Stable`)** re-measures under **Workstation GC** — the GC the CLI actually ships, which makes the allocation number precise — and folds in two micro-optimizations, landing at `18.70 MB/op`. Wins **20–24 (`Retoken` → `LazyCons`)** are the allocation-floor pass: pooling the three transient re-tokenizations, a no-`StringBuilder` `ReadQuoted` fast path, replacing per-table validation `HashSet`s with linear scans, pre-sizing the string interner, and making a table's `Unique`/`ForeignKeys`/`Checks`/`OtherConstraints` lists allocate lazily on first touch (a constraint-free table now allocates none) — `18.70 → 16.06 MB/op`, a further `−14.1%`.

| Effort | Stages | Start → End (MB/op) | Reduction |
|---|---|---:|---:|
| Prior effort | `Base` → `ResCap` (1–10) | 66.28 → 31.22 | −52.9% |
| Recent effort | `NameList` → `Cursor` (11–18) | 31.22 → 18.94 | −39.3% |
| Stability re-baseline | `Stable` (19), Workstation GC | 18.94 → 18.70 | −1.3% |
| **Allocation-floor pass** | `Retoken` → `LazyCons` (20–24) | 18.70 → 16.06 | **−14.1%** |
| **Total** | `Base` → `LazyCons` (1–24) | **66.28 → 16.06** | **−75.8%** |

---

## 2. Recent effort across all four corpus buckets

The wins (11–23) improved **every workload shape**, not just the aggregate. The four buckets' absolute magnitudes differ by ~10× (`All` ≈ 31 MB vs `Table` ≈ 3 MB), so each is shown as **before (start #10) vs after the latest wins, normalized to its own start = 100%** — the shorter the coloured bar, the bigger the reduction.

![Recent + floor effort — allocation per corpus bucket, before (start #10, grey) vs after the latest wins (coloured), normalized to each bucket's start = 100%; the bucket name is labelled vertically inside each coloured bar.](parser-perf-buckets.svg)

Each coloured bar is the allocation **remaining** after the latest wins, as a % of that bucket's start (lower = better): `Select` 44% · `Raw` 49% · `All` 52% · `Table` 55%. `Select` benefits most (lazy clause lists + pooling hit it hardest); the floor pass then evened the field — `Table` and `Raw` moved sharply on the per-table `HashSet` removal and the re-tokenize pooling, which both concentrate there.

Absolute MB/op behind the normalized chart:

| stage | All | Raw | Select | Table |
|---|---:|---:|---:|---:|
| start (#10) | 31.22 | 19.25 | 11.09 | 3.20 |
| NameList | 30.48 | 18.97 | 10.64 | 3.19 |
| Render | 29.39 | 18.01 | 10.52 | 3.07 |
| DeadCR | 28.48 | 17.70 | 9.94 | 3.04 |
| LazySelQ | 26.79 | 17.02 | 8.95 | 3.02 |
| LazyAST | 25.88 | 16.71 | 8.37 | 3.00 |
| Pool | 19.95 | 12.40 | 5.23 | 2.43 |
| Render2+Cursor | 18.94 | 11.73 | 5.06 | 2.31 |
| Stable (#19) | 18.70 | 11.47 | 5.05 | 2.29 |
| Presize (#23) | 16.16 | 9.52 | 4.84 | 1.77 |
| **LazyCons (#24)** | **16.06** | **9.53** | **4.84** | **1.66** |
| **Δ vs start** | **−48.6%** | **−50.5%** | **−56.4%** | **−48.1%** |

---

## 3. Optimization reference

Each tag in the charts, the change it represents, and the resulting "All" allocation.

| # | Tag | Optimization | All MB/op |
|---|---|---|---:|
| 1 | `Base` | pre-grammar baseline | 66.28 |
| 2 | `Lazy` | lazy `SourceText` rendering | 60.41 |
| 3 | `Views` | per-statement `TokenSegment` views | 53.66 |
| 4 | `Spans` | `params ReadOnlySpan` matchers | 50.93 |
| 5 | `OpLex` | `OperatorLexer` in-place merge | 48.44 |
| 6 | `Intern` | per-file token interning | 40.82 |
| 7 | `Static` | static keyword/type interner | 40.22 |
| 8 | `Capture` | capture helpers → `RenderRange` | 39.36 |
| 9 | `Struct` | `Token` → readonly record struct | 32.97 |
| 10 | `ResCap` | residual capture-helper migration | 31.22 |
| 11 | `NameList` | name-list handover (no `AddRange` copy) | 30.48 |
| 12 | `Render` | `string.Create` render (no `StringBuilder`) | 29.39 |
| 13 | `DeadCR` | drop dead `ColumnRef.Parts` list | 28.48 |
| 14 | `LazySelQ` | lazy `SelectQuery` clause lists | 26.79 |
| 15 | `LazyAST` | lazy lists across AST nodes | 25.88 |
| 16 | `Pool` | `Token[]` `ArrayPool` pooling | 19.95 |
| 17 | `Render2` | `string.Create` render for all runs (no `StringBuilder` fallback) | 19.59 |
| 18 | `Cursor` | reuse the per-statement `TokenCursor` across the parse loop | 18.94 |
| 19 | `Stable` | Workstation-GC re-baseline + `DatabaseModel`/`OperatorLexer` micro-opts + allocation guard tests | 18.70 |
| 20 | `Retoken` | pool the raw-identity / table-tail / PL/pgSQL re-tokenizations (drop retained `List<Token>`) | 17.77 |
| 21 | `Quote` | `ReadQuoted` no-escape fast path — no `StringBuilder`, intern literal values | 17.25 |
| 22 | `NoSet` | drop per-table column-validation `HashSet`s (+`Select()`/`new[]`) for a linear scan | 16.74 |
| 23 | `Presize` | pre-size the per-file string interner (kill the doubling-resize churn) | 16.16 |
| 24 | `LazyCons` | lazy table constraint lists (`Unique`/`ForeignKeys`/`Checks`/`OtherConstraints` allocate on first touch) | 16.06 |

Rows 11–18 are the recent effort; row 19 re-baselines under the shipping GC and adds the local allocation-regression guards. Rows 20–24 are the allocation-floor pass — the directly-measured BDN endpoint is `LazyCons` (#24 = 16.06, the M6 model-build win); the three intermediate "All" points (20–22) are derived by chaining the in-process `bench -- alloc` probe ratio onto #19 (the same technique noted for #17), and each constituent change is independently allocation-gated by the probe + `AllocationBudgetTests`.

---

## Caveats

- **Values are measured BenchmarkDotNet allocated bytes/op**, converted to MB. Each journey point (§1) is that round's post-merge **"All"** number. (`Render2`, #17, is the one exception — its intermediate "All" value is derived by chaining the in-process probe ratio onto the measured `Pool` point; `Cursor`, #18 = 18.94, is the directly-measured combined BDN value for #17+#18.)
- **The `Pool`, `Render2`, `Cursor`, `Stable`, and floor (`Retoken`…`LazyCons`) points are warm-pool `ParseAndBuild_Pooled` measurements** — a real multi-file build with the `ArrayPool` already populated, not a synthetic micro-benchmark. The `LazyCons` (#24) endpoint is the directly-measured BDN value (All 16.06, Raw 9.53, Select 4.84, Table 1.66 MB/op); the win concentrates in the `Table` bucket (`1.77 → 1.66`), since constraint-free tables now allocate none of the four constraint lists.
- **GC basis switched at #19.** Points #1–18 are historical **Server-GC** measurements; from `Stable` (#19) onward (including the floor pass #20–24) the metric is taken under **Workstation GC**, the GC `PgProj.Cli` actually ships — declared per-job in `bench/PgProj.Benchmarks/BenchConfig.cs`. The switch is ~neutral on allocated bytes/op (the #18→#19 step, `18.94 → 18.70`, spans *both* the basis change and two micro-opts), so the trend is comparable across it. Allocated bytes/op is machine-independent (it counts bytes, not time), so the #19 and #23 numbers are directly comparable. Running the benchmark host under Server GC had been quietly degrading `MemoryDiagnoser`'s bytes/op precision; the re-baseline corrects that.
- **Allocation is now regression-gated locally**, not just by manual benchmarking: `AllocationBudgetTests` (per-char bytes/op ceilings, deterministic) and a steady-state retention probe (`dotnet run --project bench/PgProj.Benchmarks -- retention`, which confirmed a flat heap across 1000 parse→build→release cycles) fail the build / surface leaks before a regression ships. The SVGs here are regenerated by `docs/parser-perf-charts.ps1`.
- **Wall-clock is intentionally not charted.** ShortRun timings are noisy and unreliable for trend tracking. For context only: several of these wins were also ~5% faster in wall-clock, with large Gen2-collection reductions — but allocation is the metric we hold ourselves to here.
- **Allocation ≠ working set.** Lower bytes/op reduces GC pressure and Gen2 collections; it is not a direct memory-footprint or throughput guarantee.
