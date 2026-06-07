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

## Lab Notes

- 2026-06-07 — A `.pgproj` using the terse `<Project Sdk="PgProj.Sdk/x">` form needs `DefaultTargets="Build"` or a plain `dotnet build` no-ops (it runs Restore then nothing). Why: our custom SDK isn't Microsoft.NET.Sdk, so nothing sets the default target to `Build`; `dotnet msbuild -t:Build` works but `dotnet build` relies on DefaultTargets. How to apply: the scaffolder/template/CLI emit `DefaultTargets="Build"` now; keep it on any generated/example manifest.
- 2026-06-07 — The SDK's MSBuild `Build`/`Publish` targets must NOT write any `*.sql` under the project tree (incl. bin/), because the CLI re-globs `**/*.sql` itself and does NOT honor the SDK's `Exclude="bin/**"` MSBuild item — the deploy `.sql` then parses as a duplicate of every object it creates. Why: `DatabaseProject.ResolveSqlFiles` globs the whole project dir; its only escape hatches are the `<Build … Exclude>` it reads from the .pgproj XML and a leading-`_` filename skip. How to apply: the Publish/preview script defaults to `bin/_<Name>.deploy.sql` (leading `_` → CLI skips it). Don't drop generated SQL into the tree without a `_` prefix.
- 2026-06-07 — XML comments can't contain `--` (MSB4024). MSBuild props/targets comments full of CLI examples (`--project`, `--connection`, `--no-transaction`) silently break the import. How to apply: in `.props`/`.targets` comments write flags without the double-dash (e.g. "the project flag", "no-transaction switch").
- 2026-06-07 — To pack a NuGet that carries arbitrary staged files (here the published CLI under `tools/`), use `TargetsForTfmSpecificContentInPackage` + `TfmSpecificPackageFile`, not static `<None Pack="true">` (which gave NU5017 "no content"). Gather a glob into its OWN private item first, then map with `%(YourItem.RecursiveDir)` — batching the glob alongside other items in one `TfmSpecificPackageFile` group corrupts `%(RecursiveDir)` (files landed under `tools/Sdk.props/`). Also: a trailing `\` before a closing `&quot;` on an `<Exec>` command line escapes the quote — keep stage dirs without a trailing separator.
- 2026-06-07 — `PgProj.Sdk.csproj` (packs the SDK) and `editors/vs/` (the VSIX, needs the VS SDK) are deliberately NOT in `PgProj.slnx` and NOT in `dotnet test`. The slnx is explicit, so they stay out; `dotnet pack src/PgProj.Sdk` is the only way to produce the nupkg. Don't add either to the slnx — the VSIX can't build with `dotnet`/headless and would break the suite.
- 2026-06-07 — VS Code extension E2E (`editors/vscode`): run `npm run test:e2e` (from `editors/vscode`; it does `npm run compile` + the e2e tsc first via `pretest:e2e`). It downloads a VS Code build and launches a real host — needs a desktop session (on headless Linux wrap in `xvfb-run`). Unit gate is `npm run test:unit` (vitest, no host). Two gotchas burned us: (1) **the repo path has a SPACE** (`…\Code cleaners\…`) and `@vscode/test-electron` splits `extensionTestsPath` on it → `Cannot find module 'c:\repos\Code'`; a junction doesn't help (canonicalised back). `test/e2e/runTest.ts` now auto-mirrors the build + sample workspace to a space-free temp dir, so `npm run test:e2e` works from the repo — keep that shim. (2) The extension's runtime entry is the **esbuild bundle `dist/extension.js`**, not `out/` — a STALE bundle silently drops newly-added commands/handlers and the E2E fails confusingly (missing command, hung webview); always `npm run compile` (or it runs via `pretest:e2e`) before an E2E run. Healthy result: 8 passing, 1 pending (the live-hover test self-skips unless the configured `pgproj.cliPath` engine is runnable).
