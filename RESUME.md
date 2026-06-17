# RESUME — PgProj SSDT-parity backlog (session 2026-06-13)

Working branch: `milestone/m7-ssdt-parity` (PgProj repo at
`C:\repos\Code cleaners\Postgres-database-project`). GitHub: `ilijevicdenis/CodeCleaners.PgProj`.

## Environment brought up this session (still running)

- **Hyper-V VM `PgProject_TestEnv`** started (the FlaUI/DTE VS-E2E environment). State: Running.
- **Docker Desktop** started; **sample-db** container `pgproj-sample-db` (postgres:18, port 15432) up.
  - Live-test connection: `Host=localhost;Port=15432;Username=postgres;Password=pgproj;Database=postgres`
    → set `PGPROJ_TEST_CONNECTION` to it to run the live-DB tests (else they skip).
  - Bumped the container to `max_connections=400` in `tests/sample-db/docker-compose.yml` (the full
    parallel live suite was saturating the default 100). Needs `sample-db.ps1 -Reset` only if recreated.

## DONE this session (build green, 0 warnings; full Core suite 25,130 pass / 0 fail / 0 skip WITH the live DB)

### #137 — EP-DEPLOY CONCURRENTLY / lock-minimizing deploy (committed: NO, ready to commit)
- New `RunsOutsideTransaction` flag on `SchemaChange`; `Concurrent` on `CreateIndexChange`/`DropIndexChange`
  (→ `CONCURRENTLY`); `NotValid` on `AddForeignKeyChange`/`AddCheckConstraintChange` (→ `NOT VALID`); new
  `ValidateConstraintChange` (separate `VALIDATE CONSTRAINT` pass, runs outside the txn).
- `LockMinimizer.Apply` (idempotent) rewrites the change list; `SqlEmitter.CreateIndex(ix, concurrent)`.
- `DeployScriptGenerator`: `ConcurrentIndexOperations` option applies the transform + partitions
  outside-txn steps after `COMMIT` (no empty BEGIN/COMMIT when only concurrent steps exist); ADD COLUMN
  DEFAULT <PG11 warning; INVALID-index note; lock note in verbose.
- `PublishService`: `ConcurrentIndexOperations` on profile/plan; `PlanAsync` transforms `changes` so the
  phased apply matches; `HasNonTransactionalSteps` routes apply to `PhasedDeployer` (rejects the
  concurrent + pre/post-deploy-scripts combo with a clear error).
- `RiskAnalyzer`: blocking vs non-blocking strategy per op + INVALID-index warning + ValidateConstraint safe.
- CLI flag `--concurrent-indexes` / `--minimize-locks`. Tests: `DeployScriptConcurrent137Tests` (DB-free)
  + a live shadow-DB round-trip (concurrent deploy reaches same end-state as transactional).

## DONE earlier this session

### #81 — EP-ANALYSIS+ new rules (committed: NO, ready to commit)
- `src/PgProj.Core/Analysis/PgAnalyzer.cs`: **PG015** (uppercase identifier → case-fold/forced-quoting
  footgun) + **PG016** (identifier > 63 bytes → silent truncation), on table + column names.
- Tests: `tests/PgProj.Core.Tests/PgAnalyzerTests.cs`. Doc: `docs/ANALYSIS_RULES.md`.
- #81 stays OPEN on GitHub as an ongoing rule backlog (more rules can follow).

### #140 — EP-PROFILE full DacDeployOptions-equivalent family (committed: NO, ready to commit)
- `PublishProfileOptions` (in `src/PgProj.Core/Deployment/PublishProfile.cs`) gained nullable:
  BlockOnPossibleDataLoss, DropConstraintsNotInSource, DropIndexesNotInSource, GenerateSmartDefaults,
  ScriptNewConstraintValidation, AllowTableRecreation, CommandTimeoutMs, LockTimeoutMs,
  ExcludeObjectTypes, DoNotDropObjectTypes + an `IsEmpty` gate. Serialized via the existing secret-free
  ProfileDto path (camelCase, omit-null).
- `DeployScriptGenerator`/`DeployOptions`: new behaviors — GenerateSmartDefaults (type-aware DEFAULT for
  a NOT NULL add via `SmartDefaultFor`), ScriptNewConstraintValidation=false → FK/CHECK `NOT VALID`,
  AllowTableRecreation=false → RecreateRawObjectChange commented out, DoNotDropObjectTypes → per-kind
  DROP suppression in the filter. (BlockOnPossibleDataLoss gate + timeouts already existed.)
- `PublishService`: `PublishPlanOptions` carries the resolved family; `PlanAsync` wires them into
  `DeployOptions`; `ResolveDropSuppression` maps granular flags → suppressed type tokens; timeouts
  flow into `PhasedDeployer` (new ctor params → per-session `SET statement_timeout/lock_timeout`).
- CLI (`src/PgProj.Cli/Program.cs`): resolvers (`BuildPublishPlanOptions`) with CLI>profile>default;
  new flags `--allow-data-loss --allow-table-recreation --smart-defaults --no-validate-constraints
  --no-drop-constraints --no-drop-indexes --no-drop-type --exclude-type --command-timeout --lock-timeout`.
  **Publish now blocks on possible data loss by default** → new `ExitCode.DataLossBlocked = 9`
  (documented in `docs/CICD.md`; frozen-contract test updated). `--allow-data-loss` opts out.
- Tests: `DeployScriptOptions140Tests.cs` (DB-free), `PublishProfileTests.cs` (round-trip/IsEmpty/
  ResolveDropSuppression), `DeployScriptIntegrationTests.cs` (live smart-defaults round-trip).

## #136 — EP-REFACTOR persisted `.pgrefactorlog` (CORE DONE this session, committed)
- Artifact `Refactoring/RefactorLog` (`{operation,objectType,oldName,newName}`, JSON, append-only,
  conventional `<project>.pgrefactorlog`). Deploy planner consumes it BY DEFAULT via
  `ComparerOptions.RefactorLog` (seeds the rename pre-pass; `MergePlans` makes the explicit log win over
  the structural heuristic). New `RenameColumnChange` + `SetTableSchemaChange`; column renames handled in
  `CompareTables`. `RiskAnalyzer` → renames/moves are Safe. CLI `rename`/`move-schema` rewrite `.sql`
  (word-boundary qualified-name replace) + append the log (`RefactorEngine`). Tests: `RefactorLogTests`
  + a live shadow-DB data-preservation proof.
- **`.pgpkg` packing DONE:** the log is packed as `refactorlog.json` (not part of `sourceChecksum`),
  `PgPkgBuilder.FromBuild` populates it, and `publish` from a `.pgpkg` consumes it
  (`PublishPlanOptions.RefactorLog`). Tests in `PgPkgRoundTripTests`.
- **STILL OPEN in #136 (follow-up):** the **`expand-wildcards`** (SELECT *) refactor command (needs view
  column resolution from the semantic model; pure source hygiene, no deploy-safety impact). Column-rename
  CLI authoring (the `.sql` rewrite for a column) is also deferred — the deploy planner already *consumes*
  logged column renames; only the `rename`-command rewrite is table-scoped today.

## REMAINING OPEN ISSUES (priority from GitHub labels)

- **(#136 follow-ups above: pgpkg packing + expand-wildcards + column-rename .sql authoring.)**

- ~~#136 EP-REFACTOR persisted `.pgrefactorlog` — HIGH~~ (core delivered; see above). Original notes:
  Committed artifact `{operation, oldStableId,
  newStableId, objectType}` packed into `.pgpkg` (extend `Packaging/PgPkg.cs`). Seed
  `IdentityDiffEngine`/`SchemaComparer` StableId old→new map from it BY DEFAULT so renames deploy as
  `ALTER ... RENAME`/`SET SCHEMA` (rename change records already exist in `IdentityDiffChanges.cs`, gated
  today behind `ComparerOptions.DetectRenames`). CLI `rename`/`move-schema`/`expand-wildcards` rewrite
  `.sql` + append the log atomically. `RiskAnalyzer` reclassifies a logged rename as data-safe. Shadow-DB
  test proves a logged rename preserves rows vs the no-log drop+create baseline.
- **#134 EP-EXTRACT table data / BACPAC — MED.** `--all-table-data`/`--table-data` → COPY dumps in a
  `data/` section of `.pgpkg`; FK-ordered load after schema; setval + OVERRIDING SYSTEM VALUE.
- **#132 EP-DATACOMPARE — MED.** `pgproj data-compare --source --target`; row buckets
  Different/Only-in-*/Identical; FK-topo INSERT/UPDATE/DELETE (or ON CONFLICT) DML; txn-wrapped apply.
- **#133 EP-REF NuGet `PackageReference` — MED.** `dotnet pack` the `.pgpkg` into a `.nupkg`; teach
  `ReferenceResolver` to restore + load a `PackageReference`'s `.pgpkg` as reference-only composite-model
  objects (no deploy DDL for them).
- **#139 EP-UNITTEST PL/pgSQL — MED.** `pgproj test` runs pgTAP/SQL stubs in BEGIN…ROLLBACK on the
  shadow DB; predefined conditions (Row Count, Scalar, Empty/NotEmpty, Exec Time, Expected Schema, Data
  Checksum, Inconclusive) + expected-SQLSTATE; scaffolder for stubs.
- **#142 EP-PKG project snapshot CLI — LOW.** `pgproj snapshot create/compare/revert/import` over the
  built project model → timestamped read-only `Project_YYYYMMDD_HH-MM-SS.pgpkg` under `Snapshots/`.
- Blocked tail: **#102 / #108** need server-side C functions (range/base-type + LANGUAGE/TRANSFORM/USER
  MAPPING introspection); **#112/#119/#120** editable VS table designer need interactive VS.

## Build & test commands
```
dotnet build PgProj.slnx -c Release
PGPROJ_TEST_CONNECTION='Host=localhost;Port=15432;Username=postgres;Password=pgproj;Database=postgres' \
  dotnet test tests/PgProj.Core.Tests -c Release --no-build
```
(CLAUDE.md hard rule: never touch GitHub CI/CD without explicit user instruction.)
