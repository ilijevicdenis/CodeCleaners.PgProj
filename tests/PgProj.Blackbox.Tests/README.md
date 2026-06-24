# PgProj.Blackbox.Tests

Heavy **blackbox** tests for the `pgproj` CLI. They treat the tool as an opaque binary: the harness
shells out to the published `PgProj.Cli.dll` and asserts only on the three observable outputs —
**exit code**, **stdout/stderr**, and the **actual database state** (via Npgsql, used only to arrange
and verify, never to drive the tool). No engine type is referenced.

Both **happy-path** and **failure-with-recovery** scenarios are covered across the surface: build,
analyze, script, publish (greenfield / incremental / dry-run), validate, extract, compare, drift,
pull, data-compare, snapshot/verify/pkg, the PL/pgSQL test runner, and the rename refactor log.

## Running

The DB-backed tests need the two-server Docker harness (a **source** and a **target** PostgreSQL).
Bring it up and export the connection env vars, then run:

```powershell
# from tests/blackbox-db — starts source :15432 (seeded) + target :15433 (empty) and sets the env
. .\blackbox-db.ps1 -Export        # dot-sourced so PGPROJ_SOURCE_CONNECTION / PGPROJ_TARGET_CONNECTION stick

cd ..\..
dotnet test tests/PgProj.Blackbox.Tests -c Debug
```

Without the env vars the DB tests **skip** (the CLI-only tests still run), so the project stays green
in `dotnet test PgProj.slnx` on a machine with no containers.

| Skip attribute | Runs when |
|---|---|
| `[CliFact]`  | the CLI is built (no database needed) |
| `[LiveFact]` | the CLI is built **and** `PGPROJ_SOURCE_CONNECTION` + `PGPROJ_TARGET_CONNECTION` are set |

## How the failure/recovery tests work

Each failure test triggers a documented failure mode and then applies the documented recovery and
shows it clears. The headline cases:

| Failure | Exit | Recovery proven |
|---|---|---|
| SQL syntax error | 3 | fix the SQL → 0 |
| duplicate object | 3 | remove the dupe → 0 |
| analysis gate (`--rule PG006=error`) | 4 | `--no-analyze` / drop the override → 0 |
| target-version gate (`NULLS NOT DISTINCT` on PG14) | 4 | raise `TargetPostgresVersion` → 0 |
| missing project reference | 5 | remove the dangling reference → 0 |
| possible data loss (`DROP COLUMN`) | 9 | `--allow-data-loss` → 0, column dropped |
| mid-deploy DDL error (NOT NULL on populated table) | 7 | **transaction rolled back** (column absent) → `--smart-defaults` → 0, data preserved |
| connection refused | ≠0 | — |

`rename` is proven to preserve data: a row survives the `ALTER TABLE … RENAME` the refactor log
produces on the live target.
