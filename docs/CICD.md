# CI/CD integration

pgproj is a headless CLI, so it drops into any pipeline. This page is the contract a pipeline
relies on: the **stable, classified exit codes** each verb emits, and how to wire the supplied
GitHub Action, Azure DevOps template, and container image.

## Exit-code contract

Every `pgproj` verb returns a process exit code drawn from a fixed taxonomy
(`src/PgProj.Core/Cli/ExitCode.cs`). These values are a **public contract**: an existing code's
numeric value never changes; new classes are only appended. The
`ExitCodeContractTests` suite parses `ExitCode.cs` reflectively and fails the build if any constant
is undocumented here, so this table is guaranteed to stay in lockstep with the code.

| Code | Name | Meaning | Typical verb(s) |
|-----:|------|---------|-----------------|
| `0`  | `Success` | The command completed successfully. Treat as a pass. | all |
| `1`  | `Error` | Unclassified/unexpected error (an exception escaped). Fail the stage; surface stderr. | all (catch-all) |
| `2`  | `Usage` | Malformed command line: unknown verb, missing argument, or missing connection string. A config bug. | all |
| `3`  | `BuildError` | The project failed to build into a model (parse errors / diagnostics). No artifact produced. | `build`, and any verb that builds first |
| `4`  | `AnalysisBlocked` | The static-analysis gate blocked: an analyzer error, or (under `--strict`) a warning. Nothing written/deployed. | `build`, `publish`, `validate`, `analyze` |
| `5`  | `ReferenceError` | A reference (project/DB reference or external artifact) could not be resolved. Distinct from a SQL build error. | reference-resolving verbs |
| `6`  | `Drift` | Schema drift detected **and** gated on via `--fail-on-drift` (or `compare --fail-on-changes`). Detection succeeded; the non-zero code is the "out of sync" signal. | `compare`, `drift` |
| `7`  | `DeployError` | The deploy failed while applying changes to the live database (server-side error during execution). | `publish` |
| `8`  | `ValidationFailed` | Shadow validation failed: the project did not apply cleanly to a throwaway PostgreSQL database. | `validate` |
| `9`  | `DataLossBlocked` | The publish was blocked by the possible-data-loss gate (`BlockOnPossibleDataLoss`, on by default). Re-run with `--allow-data-loss` (or `--allow-drops`) to proceed. | `publish` |

Convention (loose, not enforced): `0` success; `1`–`2` usage/unexpected; `3`–`5` build-time problems
caught before touching a server; `6`–`9` outcomes that required talking to (or comparing against) a
live database.

## Reproducibility gate: `pgproj verify`

`pgproj verify <a.pgpkg> <b.pgpkg>` asserts two packages are THE SAME THING - canonical model,
embedded .sql sources, and manifest options (identity stamps excluded, so rebuilds of identical
sources pass). Exit `0` = equivalent, `6` (Drift) = any difference; `--format json` / `-o` emit
the structured report naming each drifting object/source/option. Wire it locally (consistent with
this repo's no-online-CI posture) wherever reproducibility matters:

- **build determinism** - build the same project twice, `verify` the two .pgpkg artifacts;
- **conversion proofs** - old layout vs new layout of the same database project;
- **extract round-trips** - package two extracts of the same database and `verify` them.

### How pipelines should branch

- **Pass/fail only** — the default for most steps. Any non-zero fails the stage.
- **Distinguish a lint failure from a hard error** — branch on `4` (analysis) vs `3` (build) to route
  the right team/notification.
- **Gate a PR on drift** — run `drift … --fail-on-drift` (or `compare --source X --target Y
  --fail-on-changes`; `compare` also accepts `--fail-on-drift` as an alias). A clean target returns
  `0`; any difference returns `6`. Without the flag, drift is reported but the verb still exits `0`
  (pure report).
- **Alert specifically on a failed production deploy** — `7` means the script generated but execution
  against the server failed; treat differently from `3`/`4`/`8` which never touched the target.
- **Pre-merge correctness** — `validate` exits `8` when real Postgres rejects the generated script even
  though the static gate passed.

Shell example (POSIX):

```sh
pgproj publish "$PROJECT" --connection "$PGPROJ_CONNECTION"
case $? in
  0)  echo "deployed" ;;
  4)  echo "blocked by analysis"; exit 1 ;;
  7)  echo "DEPLOY FAILED — alert on-call"; exit 1 ;;
  *)  echo "pgproj failed"; exit 1 ;;
esac
```

## GitHub Action

A composite action lives at `.github/actions/pgproj/action.yml`. It runs the CLI, surfaces the
classified code as a `::error::`/`::warning::` annotation, exposes it as the `exit-code` output, and
(for `build`) uploads the produced `model.json` as a workflow artifact.

```yaml
- uses: actions/setup-dotnet@v4
  with: { dotnet-version: '10.0.x' }
- run: dotnet publish src/PgProj.Cli/PgProj.Cli.csproj -c Release -o "$RUNNER_TEMP/pgproj"
- uses: ./.github/actions/pgproj
  with:
    project: sample/SampleDb/SampleDb.pgproj
    command: build
    strict: 'true'
    pgproj-command: dotnet ${{ runner.temp }}/pgproj/PgProj.Cli.dll
```

A full reference pipeline (build → PR dry-run preview → approval-gated deploy) is in
`.github/workflows/pgproj-example.yml`.

### Approval & dry-run gating (GitHub)

The deploy must never happen without a human in the loop:

1. **Build everywhere, deploy nowhere automatically.** The `build` job runs on every push/PR and
   produces the artifact. It never touches a server.
2. **Dry-run on PRs.** A `publish` step with `dry-run: 'true'` generates and prints the deploy script
   *without executing it* — reviewers see exactly what would change. (`--dry-run` always exits `0` on a
   successful generate.)
3. **Protected Environment for the real deploy.** Put the `publish` (no `--dry-run`) job in a job that
   declares `environment: production`. In **Settings → Environments → production**, add *Required
   reviewers*; the job will not start until a reviewer approves. This is the approval gate.
4. **Secrets, not inline.** Pass the connection string via `connection:` mapped from
   `${{ secrets.PGPROJ_CONNECTION }}`; the action feeds it through the `PGPROJ_CONNECTION` env var so it
   never appears on the command line or in logs.

## Container image

For any other CI (GitLab, Jenkins, CircleCI, local), use the `Dockerfile` at the repo root. It
compiles the solution, publishes `PgProj.Cli`, and sets the CLI as the entrypoint, so
`docker run … <verb> …` maps 1:1 to `pgproj <verb> …` — and the container's exit code is the
classified pgproj code, so a CI that fails on non-zero gets the contract for free.

```sh
docker build -t pgproj:latest .

# build (mount your repo at /work, the image WORKDIR):
docker run --rm -v "$PWD:/work" -w /work pgproj:latest build sample/SampleDb/SampleDb.pgproj

# publish dry-run (connection via env, never on the command line):
docker run --rm -v "$PWD:/work" -w /work -e PGPROJ_CONNECTION="$CONN" \
  pgproj:latest publish sample/SampleDb/SampleDb.pgproj --dry-run
```

## Validating the CI assets (for maintainers)

There is no Python on the build box, so validation is done with .NET and PowerShell:

- **Exit-code contract / docs sync** — `dotnet test tests/PgProj.Core.Tests -c Debug --filter ExitCodeContract`
  asserts every `ExitCode` constant is documented in this file and that the taxonomy is complete and
  numerically stable.
- **CLI smoke test** — the same suite (and any maintainer) can confirm the published CLI runs:
  `dotnet publish src/PgProj.Cli/PgProj.Cli.csproj -c Release -o <tmp>` then
  `dotnet <tmp>/PgProj.Cli.dll help` must exit `0`.
- **action.yml well-formed** — load it as YAML, e.g. in PowerShell:
  `Get-Content .github/actions/pgproj/action.yml -Raw` and parse with a YAML module, or rely on
  GitHub's own workflow linter on push. The shape mirrors the Azure DevOps template, so a change in
  one should be mirrored in the other.
- **Dockerfile** — `docker build -t pgproj:ci-test .` then
  `docker run --rm pgproj:ci-test help` (expect exit `0`). The published-CLI smoke test above covers
  the same publish path the image uses, so the Dockerfile can be validated even where Docker is
  unavailable by running that `dotnet publish` + `help` locally.
