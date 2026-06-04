# PgProj.Benchmarks

BenchmarkDotNet harness for the pgproj parser/build hot path (see `PARSER_PERFORMANCE_AUDIT.md` §5).
Every suite has `[MemoryDiagnoser]`, so each result reports **bytes/op** alongside **ns/op** — the
gate the audit asks for ("no merge without numbers").

## Run

```powershell
cd bench/PgProj.Benchmarks
dotnet run -c Release -- --filter *BuildBenchmarks*   # rec #1: serial Build vs parallel BuildAsync
dotnet run -c Release -- --filter *Tokenize*          # layer 1: tokenizer allocations
dotnet run -c Release -- --filter *Parse*             # layer 2: full grammar + re-tokenization
dotnet run -c Release -- --filter *                   # everything
```

Add `--job short` for a fast (noisier) pass while iterating; drop it for publishable numbers.
Release is mandatory — BenchmarkDotNet refuses to run a non-optimized build.

## Suites

| Suite                | Layer | What it isolates |
|----------------------|-------|------------------|
| `TokenizeBenchmarks` | 1     | `Tokenizer.Tokenize` and `+ OperatorLexer.Merge` — per-token string/record allocs (§1a/§1b), the second merge pass (§1c). |
| `ParseBenchmarks`    | 2     | `PgParser.Parse` — dispatch `params`/`ToUpper` allocs (§1e/§1f) and raw-statement re-tokenization (§1c/§4). |
| `BuildBenchmarks`    | 3     | `DatabaseProject.Build` vs `BuildAsync` over an on-disk multi-file project, swept over file counts. |

Tokenize/Parse are driven by the repo's PG18 corpus (`tests/corpus/*.jsonl`), bucketed by statement
kind (`Table` / `Raw` / `Select` / `All`) via the `Bucket` param so each optimization is attributable
to the workload shape it targets. Build generates a synthetic project sized by the `FileCount` param.

## Baseline (rec #1, 13900 / 24 physical cores, `--job short`)

End-to-end build, serial → parallel: ~0.6× at 1 file (scheduling overhead), 1.5× at 10, **3.7× at 50,
4.5× at 200**. Allocation is within ~5% either way — parallelism buys wall-clock, not bytes. Numbers
scale with core count and project size; re-run on the target box for ground truth.
