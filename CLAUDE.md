# CodeCleaners.PgProj

PostgreSQL project tooling: a hand-written SQL parser + semantic model, a deploy/sync engine, and
a local CLI (`PgProj.Cli`, a local test tool). Validated **locally** with `dotnet test` — there is
no GitHub CI by design (see the hard rule below).

## CI/CD on GitHub — USER DECISION ONLY (hard rule)

**Touching any *online* GitHub infrastructure for CI/CD is the user's decision, never the
assistant's.** Do NOT, on your own initiative, create, edit, enable, disable, trigger, re-run, or
delete any of:

- GitHub Actions workflows (`.github/workflows/*`) or composite actions (`.github/actions/*`)
- workflow runs, hosted/self-hosted runners, environments, Actions secrets, or Actions settings
- branch protection, required-status-checks, or merge-queue configuration
- anything via the `gh` CLI or the GitHub API that changes CI/CD state online

Act on these **only** when the user explicitly instructs it in that request. The project is
intentionally validated locally (`dotnet test`), not via paid GitHub Actions minutes. If a task
appears to need CI/CD, surface it and ask — do not implement it. (The Azure DevOps template under
`ci/azure-devops/` is an opt-in artifact and likewise must not be wired to run without the user
saying so.)

## Build & test (local)

```bash
dotnet build PgProj.slnx -c Release
dotnet test  tests/PgProj.Core.Tests -c Release   # full suite; keep 100% green (round-trip parity)
```

Parser/engine performance work is benchmark-gated: `bench/PgProj.Benchmarks` (BenchmarkDotNet,
`[MemoryDiagnoser]`); `dotnet run --project bench/PgProj.Benchmarks -c Release -- alloc` is a fast
bytes/op probe. No perf change ships without a measured allocation delta.

## Performance dashboard — publish gains on commit

The progress dashboard lives at **`docs/parser-performance.md`** (GitHub-rendered, with the
`docs/parser-perf-*.svg` charts) and is linked from the README.

**When a commit produces a measured allocation gain, update the dashboard in that same commit:**

1. Confirm the gain with numbers — the `bench -- alloc` probe and/or a BDN A/B
   (`PipelineBenchmarks`); allocation (bytes/op) is the metric, not noisy ShortRun wall-clock.
2. Append the new stage to the §1 "All"-bucket journey (tag + MB/op) and refresh the §2 per-bucket
   chart + both tables and the cumulative % / headline. Regenerate the SVGs the same way they were
   produced (PowerShell emitting `docs/parser-perf-journey.svg` / `parser-perf-buckets.svg`).
3. Keep the [[parser-perf-optimizations]] memory's win log in sync.

No measured gain, or a delta within benchmark noise → **do not touch** the dashboard. This is a
**manual step performed as part of the commit** — do NOT add a git hook, GitHub Action, or any CI
job to run benchmarks or publish stats automatically (benchmarks take minutes, and the CI/CD hard
rule above stands). "Publish" here means committing the updated Markdown + SVGs to the repo, which
renders on GitHub; it does not mean any online CI/CD.
