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
