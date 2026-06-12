# tests/sample-db — the standing local PostgreSQL for real-database testing

One dockerized PostgreSQL 18 (`pgproj-sample-db`, port **15432**) that powers every "real" test
layer in the repo:

| Consumer | How |
|---|---|
| `tests/PgProj.Core.Tests` integration tests | `PGPROJ_TEST_CONNECTION` (admin conn; tests create + drop their own throwaway databases). With it set the suite runs **25,086 / 0 skipped** instead of ~22.5k + 2,558 skips. |
| VS E2E harness (`editors/vs/PgProj.VisualStudio.UiTests`) | `PGPROJ_UITEST_DB` → the fixture `pgproj extract`s the seeded `sampledb` into the scratch solution VS opens — the project under test is a real database extract. |
| Ad-hoc CLI work | `extract` / `compare` / `publish` / `validate` / `drift` against `sampledb`. |

## Usage

```powershell
.\sample-db.ps1          # up + wait healthy + print connection strings
.\sample-db.ps1 -Reset   # wipe and re-seed (init scripts run only on a fresh data dir)
.\sample-db.ps1 -Down    # stop
```

Connection strings (stable, fixed port):

- admin / integration tests: `Host=localhost;Port=15432;Username=postgres;Password=pgproj;Database=postgres`
- seeded sample database:    `Host=localhost;Port=15432;Username=postgres;Password=pgproj;Database=sampledb`

From a **Hyper-V test VM**: Docker stays on the host; replace `localhost` with the host's IP on the
virtual switch and allow TCP 15432 through the host firewall (one-liner printed by `sample-db.ps1`).
No nested virtualization needed.

## The seed (`init/01_sample_schema.sql`)

Three schemas (`sales`, `inventory`, `audit`) deliberately touching the object kinds the tooling
round-trips: identity PKs, FKs, checks, defaults (incl. `CURRENT_USER` — which caught a real
validator false positive on day one), an enum + a domain, sequences, partial/expression indexes,
views + a materialized view, SQL + PL/pgSQL functions, a trigger, RLS policy, comments, and a
little data so `extract`/`drift` have something to chew on. Extend it whenever a new object kind
needs E2E coverage — `-Reset` re-seeds.
