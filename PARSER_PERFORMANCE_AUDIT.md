# Parser Performance Audit — pgproj DDL/DML Parser

**Goal:** make parsing as fast as possible. This is an audit + concrete proposal; no code changed.

**Date:** 2026-06-04

**Bottom line up front:** The single highest-leverage change is **wiring the deploy/build CLI to the already-existing parallel `BuildAsync`** (it exists, is correct, and is currently unused — the CLI and ReverseSync both call the serial `Build()`). After that, the biggest *per-parse* win is eliminating string allocations in the tokenizer (one heap `string` per token today, plus per-token `Token` record allocations) and a **second full tokenization pass** that happens for most statements. These are measurable, low-risk wins on a CPU-bound, embarrassingly-parallel workload.

---

## 1. Hot-path inventory

### 1a. The source is held as `string` and re-substringed per token — every token is a heap allocation
`Tokenizer` keeps `private readonly string _s` (`Tokenizer.cs:15`) and produces each token's `Value` via `_s.Substring(...)`:
- `Tokenizer.cs:102` dollar tag, `:108`/`:114` dollar body, `:135` `ReadQuoted` returns `sb.ToString()`, `:149`/`:155` `ReadNumber`, `:162` `ReadWord`, and `:65` `c.ToString()` for **every single symbol char** (`(`, `)`, `,`, `;`, `.`, every operator char).

Every token therefore allocates a `string`. For a typical DDL corpus, symbols and words dominate, so this is the #1 allocation source. `c.ToString()` at `:65` is the worst offender — a fresh 1-char heap string for each punctuation character, and there are a lot of them (`( ) , ; .` plus each operator char before `OperatorLexer` re-merges).

### 1b. `Token` is a `record` (reference type) — one heap object per token, on top of the string
`Token.cs:17`: `public sealed record Token(...)`. A `record` is a **class**, so each token is a separate GC allocation holding `Kind`, a `string` reference, and an `int`. Combined with 1a, every token costs *two* allocations (the string + the record). `List<Token>` in `Run()` (`Tokenizer.cs:28`) is created **without a capacity hint**, so it reallocates/copies its backing array repeatedly (1→4→8→16…) as it grows to thousands of entries.

### 1c. Second full tokenization pass per statement (multi-pass scanning)
The same text is tokenized more than once on the common path:
- `PgParser.cs:37` tokenizes the whole file once.
- `ModelBuilder.DeriveRaw` (`ModelBuilder.cs:110`) **re-tokenizes `sourceText`** — and `sourceText` is itself reconstructed by `Token.Render(segment)` (`PgParser.cs:48`, `:56`). So for every `RawCreateStatement`/`UnsupportedStatement` (a large share of real DDL: COMMENT, GRANT, CREATE EXTENSION, triggers, policies, publications…), the pipeline does: tokenize → render tokens back to a string → tokenize that string again.
- `ValidateTableTail` (`PgParser.cs:197`) tokenizes the CREATE TABLE tail a third time (after it too was captured via `CaptureRest`→`Token.Render`).
- `OperatorLexer.Merge` (`PgParser.cs:37`) is a *second linear pass* over the whole token list right after tokenization, and it allocates **new** `Token` objects for merged runs (`OperatorLexer.cs:43`) plus does `run += tokens[j].Value` string concatenation in a loop (`:39`) — O(n²) string building for long operator runs (rare, but the per-merge allocation is not).

### 1d. `Token.Render` rebuilds strings constantly, and it's on the main path
`Token.Render(IReadOnlyList<Token>)` (`Token.cs:46`) allocates a `StringBuilder` and walks tokens for **every** statement's `SourceText` (`PgParser.cs:48`, `:57`), every `ParseTypeName` (`PgParser.cs:363`), `CaptureExpression`, `ParseParenExpression`, `CaptureRest`, `ParseCastType` (`:538`), etc. Each call is a `StringBuilder` + final `ToString()`. `Render()` per-token (`Token.cs:33`) also does `Value.Replace("\"","\"\"")` / `Replace("'","''")` which allocates even when there's nothing to escape.

### 1e. Keyword / type recognition uses `HashSet<string>` with `OrdinalIgnoreCase` — fine, but case folding allocates
The `HashSet<string>(StringComparer.OrdinalIgnoreCase)` lookups (e.g. `CommandKeywords`, `TypeKeywords`, `ColumnConstraintStart`) are O(1) and case-insensitive without allocating — good. **But** the hot dispatch repeatedly does `c.Advance().Value.ToUpperInvariant()` to switch on a keyword: `PgParser.Commands.cs:22`, `PgParser.Expressions.cs:51/64/163/476/479`, `PgParser.Select.cs:102/208`, `ModelBuilder.cs:171`, plus `ToLowerInvariant()` in `ColumnBaseType` (`PgParser.cs:468`), `ValidateTypeModifiers` (`:379`), `IsBuiltinTypeWord` (`PgParser.Ddl.cs:317`). Each is a fresh allocated string purely to feed a `switch`. These can switch on the original span/string with an ordinal-ignore-case comparison instead.

### 1f. LINQ / closures / `params` arrays on the parser hot path
- `TokenCursor.AtAnyWord(params string[] words)` (`TokenCursor.cs:55`) and `LookaheadWords(params string[])` / `MatchWords(params string[])` (`:62`,`:82`) **allocate a `string[]` on every call**. These are called *pervasively* in the dispatch and expression grammar (e.g. `PgParser.cs:76` `AtAnyWord("SELECT","WITH","VALUES","TABLE","INSERT","UPDATE","DELETE","MERGE","TRUNCATE")` runs for every statement; `MatchWords("IF","NOT","EXISTS")`, `MatchWords("OR","REPLACE")`, etc.). This is one of the most frequent allocations in the whole parser.
- `col.Constraints.Any(c => c is CollateConstraint)` (`PgParser.cs:454`,`:456`) — LINQ + closure per column.
- `ValidateColumnReferences` (`PgParser.cs:697`) `t.Columns.Select(...)` + a local `Check` closure capturing `defined`.
- `ParseIdentifierList` `ids.Contains(id, StringComparer.OrdinalIgnoreCase)` (`PgParser.cs:778`) is O(n²) over the alias list (fine for small lists).
- `DatabaseProject.FindDuplicates` uses `GroupBy(...ToLowerInvariant()).Where(g=>g.Count()>1)` four times (`DatabaseProject.cs:192–201`) — post-build, not per-token, lower priority.

### 1g. `TokenCursor` lookahead is allocation-free (good)
`Peek`, `Current`, `Match*` (`TokenCursor.cs:27–79`) just index into the list and bump `_i` — no copying. The one exception is `Range()` (`:39`) which builds a `List<Token>`, but it's only used for sub-expression source recovery, not the hot loop. Lookahead itself is fine; the cost is the `params string[]` wrappers noted in 1f, not the cursor mechanics.

### 1h. Char-by-char dispatch uses `char.IsWhiteSpace`/`IsLetter`/`IsLetterOrDigit` and `"…".IndexOf(c)`
The inner loop (`Tokenizer.cs:29–66`) is an if/else chain calling `char.IsWhiteSpace` (Unicode-aware, slower than ASCII check), and operator/radix membership tests use `"xXoObB".IndexOf(...)` (`:144`), `OpChars.IndexOf(c)` (`OperatorLexer.cs:18`), `"EBXebx".IndexOf(...)` (`Expressions.cs:280`) — linear scans of a string per char. `SearchValues<char>` (.NET 8+) replaces these with a vectorized membership test.

---

## 2. Concurrency angle

**The workflow does parse many SQL files in a per-file loop** — this is the deploy/build/sync hot path:
- `DatabaseProject.Build()` (`DatabaseProject.cs:106–112`) — `foreach (file) new PgParser().Parse(File.ReadAllText(file))`, **serial**.
- This serial `Build()` is what the CLI deploy/build actually calls: `Program.cs:54` and `Program.cs:350`, and `ReverseSync.cs:30` (`project.Build().Model`), and `MapProjectFiles` (`ReverseSync.cs:124–139`, another serial per-file parse loop), and `CatalogBuilder`/`Program.cs:286` analyzer loop.

**There is already a correct parallel implementation that nobody calls:** `DatabaseProject.BuildAsync` (`DatabaseProject.cs:125–143`) uses `Parallel.ForEachAsync` with `MaxDegreeOfParallelism = Environment.ProcessorCount`, a fresh `PgParser` + `ModelBuilder` per file (no shared mutable state — `:149` comment confirms), writes results into a **pre-indexed array slot per file** (`parts[item.Index]`), and `Merge` (`:161–181`) walks the array **in sorted-file order** so the model is byte-identical to the serial build. This is exactly the right pattern for CPU-bound, embarrassingly-parallel work, and it preserves deploy ordering. **It is the textbook correct design and it's dead code.**

Findings and recommendations:
- **Quantified opportunity:** parsing is pure CPU, zero I/O contention after the file read, and each file is independent → near-linear speedup to core count. On an 8-core box a project with dozens/hundreds of `.sql` files should see roughly Nx throughput on the parse phase (minus the serial `Merge`, which is cheap list concatenation).
- **Do NOT** introduce `Task.Run`-per-file fire-and-forget, async-over-sync wrappers, or unbounded fan-out. `BuildAsync`'s body correctly returns `ValueTask.CompletedTask` (`:139`) — it does no fake awaiting; `Parallel.ForEachAsync` provides the bounded worker pool. Keep it that way. The CPU-bound body inside `Parallel.ForEachAsync` is the one acceptable use of that API here precisely because it does NOT await anything.
- **Shared mutable state to watch:** today there is none in `ParseOne` (fresh parser + builder + model per file). **This matters for the optimizations below:** if you add an interned-string table, a `FrozenDictionary` keyword cache, or an `ArrayPool` for token buffers, the keyword/intern tables must be **immutable statics** (read-only, thread-safe — `FrozenDictionary`/`FrozenSet`/`SearchValues` all are), and any pooled buffers must be rented/returned **within a single file's parse** (never shared across the parallel bodies). Do not introduce a process-wide mutable string-intern cache without making it a `ConcurrentDictionary` — and prefer per-parse interning so the parallel workers never contend.
- **Determinism / ordering:** already handled — `ResolveSqlFiles` sorts (`DatabaseProject.cs:94`), `BuildAsync` indexes by sorted position, `Merge` walks in order with "first-occurrence wins" for schemas (`:169`) and `AddRange` for the rest. The deploy-ordering regression guards in the repo (e.g. index-on-matview) operate on the *model*, which is order-identical to the serial build, so going parallel is safe. **Action:** add a regression test asserting `Build()` and `BuildAsync()` produce identical models/diagnostics, then switch the CLI/ReverseSync call sites to `BuildAsync`.
- **Also parallelize `MapProjectFiles`** (`ReverseSync.cs:124`) and the analyzer loop (`Program.cs:286`) with the same array-slot + ordered-merge pattern; they're independent per-file parses too.

---

## 3. Allocation / GC reduction (concrete .NET-10 moves)

1. **Make `Token` a `readonly struct`** (`Token.cs:17`). Kind (enum/byte) + value + int position is a small, immutable value — ideal as a struct. Eliminates one heap allocation per token. Caveat: it's currently a `record` with value equality and used as `Token?`; a `readonly record struct` keeps value semantics, and `Token?` becomes `Nullable<Token>`. Verify no code relies on reference identity (none seen — comparisons are by `Value`/`Kind`).

2. **Store token text as `ReadOnlyMemory<char>` (or offset+length) over the original source instead of `string`.** Keep the source `string` alive (already held at `Tokenizer.cs:15`) and have each token carry `(int start, int length)` or a `ReadOnlyMemory<char>` slice. This removes **every** `Substring` (1a) and the `c.ToString()` at `:65`. Keyword comparisons then run on `ReadOnlySpan<char>` via `MemoryExtensions.Equals(span, "SELECT", StringComparison.OrdinalIgnoreCase)` and `FrozenSet`/`FrozenDictionary` span lookups (.NET 8+ `GetAlternateLookup<ReadOnlySpan<char>>`), with no allocation. Materialize a real `string` only when a value escapes into the AST/model (identifiers, captured bodies). This is the largest GC win but the biggest change — gate it behind the benchmark.
   - *Note:* `ReadQuoted` (`:119`) and dollar bodies need a real string only when they contain doubled-quote escapes; for the common no-escape case you can still slice. Keep `string.Create`/`StringBuilder` only on the escape path.

3. **Pre-size collections.** `new List<Token>()` in `Tokenizer.Run` (`:28`) → `new List<Token>(sql.Length / 4)` (a token roughly every ~4 chars is a safe heuristic). `OperatorLexer.Merge` already sizes its result (`OperatorLexer.cs:22`) — good; mirror that everywhere a final size is known. Consider `ArrayPool<Token>` for the per-file token buffer rented/returned inside `ParseOne` (thread-local rental, never crossing the parallel boundary).

4. **`FrozenSet<string>` / `FrozenDictionary` for the static keyword tables.** `CommandKeywords`, `TypeKeywords`, `ColumnConstraintStart`, `Persistence`, `ExtractFields`, `TypeContinuations`, etc. are read-only and built once — convert `HashSet`→`FrozenSet` (`.ToFrozenSet(StringComparer.OrdinalIgnoreCase)`) and the `ParseCommand` switch's keyword routing to a `FrozenDictionary<string,Handler>` or keep the `switch` but feed it the original string (no `ToUpperInvariant`). Frozen collections have faster lookups and are immutable → safe across the parallel workers.

5. **Replace `"…".IndexOf(c)` membership tests with `SearchValues<char>`** (`Tokenizer.cs:144`, `OperatorLexer.cs:18`, `Expressions.cs:280`/`:151`). `private static readonly SearchValues<char> OpChars = SearchValues.Create("+-*/<>=~!@#%^&|?:");` then `OpChars.Contains(c)` — vectorized, allocation-free.

6. **Kill the `params string[]` allocations in `TokenCursor`.** Add non-`params` overloads for the common arities (`AtAnyWord(string,string)`, `…(string,string,string)`, up to ~9) or take `ReadOnlySpan<string>` so call sites pass a stack/static array. The 9-word `AtAnyWord` at `PgParser.cs:76` and the dozens of `MatchWords("IF","NOT","EXISTS")`-style calls are the heaviest. Alternatively cache the static word arrays as `private static readonly string[]` fields.

7. **Avoid `ToUpperInvariant()/ToLowerInvariant()` purely to switch.** Switch on the original string with explicit ordinal-ignore-case (`string.Equals(v, "DO", OrdinalIgnoreCase)`) or a `FrozenDictionary` keyed ordinal-ignore-case. Affects `Commands.cs:22`, the `ToUpperInvariant` sites in `Expressions.cs`/`Select.cs`, `ColumnBaseType` (`PgParser.cs:468`), `ValidateTypeModifiers` (`:379`).

8. **Eliminate the redundant re-tokenization (1c).** Have `SplitStatements` carry the **token slice** for each statement so `ModelBuilder.DeriveRaw` and `ValidateTableTail` consume tokens directly instead of `Token.Render(...)→re-tokenize`. Keep `SourceText` as a lazily-materialized string only when actually needed for emit/round-trip. This removes a whole tokenize+render pass for the large class of raw/unsupported statements.

9. **`Token.Render` per-token escape (`Token.cs:35–38`)** — guard the `Replace` with a `Contains` check (`Value.Contains('"') ? … : Value`) so the no-escape common case doesn't allocate.

---

## 4. Prioritized recommendations

| # | Optimization | Impact | Effort | Risk | Needs benchmark? |
|---|---|---|---|---|---|
| **1** | **Switch CLI `Build`/`ReverseSync`/`MapProjectFiles` call sites to the existing `BuildAsync` (parallel per-file)** | **High** (≈ core-count speedup on multi-file projects) | **Low** (it's already written + correct) | **Low** (model is order-identical; add a `Build==BuildAsync` parity test) | No — just the parity test |
| 2 | Tokenizer: stop allocating a `string` per token — slice `ReadOnlyMemory<char>`/offset+length over source; fix `c.ToString()` at `:65` | High (largest per-parse GC reduction) | High | Med (touches AST value materialization) | Yes |
| 3 | Make `Token` a `readonly record struct` | High | Med | Med (`Token?`→nullable struct; verify no ref-identity use) | Yes |
| 4 | Remove redundant 2nd/3rd tokenize passes (`DeriveRaw`, `ValidateTableTail`) by passing token slices | Med-High | Med | Low | Yes |
| 5 | Pre-size `List<Token>` (`:28`); `ArrayPool<Token>` per-file buffer | Med | Low | Low | Yes (confirm pooling helps) |
| 6 | `params string[]` → fixed-arity / `ReadOnlySpan` overloads in `TokenCursor` | Med | Low | Low | Light |
| 7 | `HashSet`→`FrozenSet`/`FrozenDictionary` for static keyword tables | Low-Med | Low | Low | Light |
| 8 | `SearchValues<char>` for operator/radix membership | Low-Med | Low | Low | Light |
| 9 | Drop `ToUpper/ToLowerInvariant`-to-switch allocations | Low | Low | Low | No |
| 10 | Parallelize remaining per-file loops (analyzer `Program.cs:286`) | Med (workflow-dependent) | Low | Low | No |

**Single highest-leverage change: #1.** A correct, deterministic parallel build already exists (`DatabaseProject.BuildAsync`) and is unused; the deploy/build/sync entry points all call the serial `Build()`. Wiring them to `BuildAsync` is near-zero risk (add a parity test) and delivers the biggest wall-clock win on real multi-file projects before touching a single line of the tokenizer. The tokenizer allocation work (#2/#3/#4) is the next tier and multiplies the per-core throughput, but it should be **justified by benchmarks** because the struct/span conversions ripple into the AST.

---

## 5. Benchmark plan

Use **BenchmarkDotNet** (add a `PgProj.Benchmarks` console project; `[MemoryDiagnoser]` to capture allocations/op, not just time). Measure three layers separately so each change is attributable:

1. **Tokenize-only:** `Tokenizer.Tokenize(sql)` and `OperatorLexer.Merge(Tokenizer.Tokenize(sql))` — isolates 1a/1b/1c-merge/1h. Report `ns/op` and `bytes/op`. This is where #2/#3/#5/#8 must show movement.
2. **Parse-only:** `new PgParser().Parse(sql)` over a fixed in-memory string (no file I/O) — captures the grammar + `params`/`ToUpper` allocations (1d/1e/1f) and the re-tokenization (#4).
3. **End-to-end build:** `DatabaseProject.Build()` vs `BuildAsync()` over a real multi-file project directory — proves #1's speedup and confirms model parity. Run at several file counts (1, 10, 50, 200) and degrees of parallelism to show scaling and pick a sane `MaxDegreeOfParallelism` (default `ProcessorCount` is reasonable; verify it isn't oversubscribed when files are tiny).

**Corpus:** the repo already has a **PG18 test-corpus effort** (oracle/fixture/harness, `CorpusData.cs`, `CorpusTestGenerator.cs`) — feed those corpus `.sql` cases straight into the tokenize/parse benchmarks as the representative workload. Bucket by statement kind (CREATE TABLE vs raw/COMMENT/GRANT vs SELECT-heavy) since the re-tokenization win (#4) is concentrated in the raw/unsupported bucket and the expression-allocation wins (#6/#7/#9) in the SELECT/expression bucket. Establish a baseline commit, then gate each optimization on a measured `bytes/op` and `ns/op` delta — no merge without numbers.

---

## Relevant files
- `src/PgProj.Core/Parsing/Tokenizer.cs` — hot loop, per-token `Substring`/`c.ToString()` allocations (lines 28, 65, 102, 108, 114, 135, 144, 149, 155, 162)
- `src/PgProj.Core/Parsing/Token.cs` — `record` (heap) token; `Render` allocations (lines 17, 33–39, 46–62)
- `src/PgProj.Core/Syntax/OperatorLexer.cs` — 2nd pass, `run +=` concat, new tokens (lines 18, 22, 39, 43)
- `src/PgProj.Core/Syntax/TokenCursor.cs` — `params string[]` allocations (lines 55, 62, 82)
- `src/PgProj.Core/Syntax/PgParser.cs` — dispatch `AtAnyWord`, re-tokenize tail (lines 37, 76, 197), `ToLowerInvariant`-to-switch (379, 468)
- `src/PgProj.Core/Syntax/PgParser.Expressions.cs` — `ToUpperInvariant`/`IndexOf` allocations (lines 51, 64, 151, 163, 280, 476, 479)
- `src/PgProj.Core/Syntax/PgParser.Commands.cs` — `ToUpperInvariant`-to-switch (line 22); `CommandKeywords` (line 12)
- `src/PgProj.Core/Syntax/ModelBuilder.cs` — **re-tokenizes `sourceText`** in `DeriveRaw` (line 110)
- `src/PgProj.Core/Project/DatabaseProject.cs` — serial `Build` (106–112) vs **unused correct parallel `BuildAsync`** (125–188)
- `src/PgProj.Cli/Program.cs` — deploy/build call sites use serial `Build()` (lines 54, 350, 286)
- `src/PgProj.Core/Sync/ReverseSync.cs` — serial `Build()` (line 30) + serial per-file loop `MapProjectFiles` (124–139)
