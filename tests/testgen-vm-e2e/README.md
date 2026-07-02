# EP-TESTGEN Hyper-V E2E

End-to-end proof that `pgproj test generate` produces a **runnable** xUnit test project, exercised
**inside the Hyper-V test VM** (a clean environment, no dev tooling) against a real PostgreSQL.

The chain, run headless over ssh in the VM:

1. `pgproj extract` the seeded sample DB into a buildable project,
2. `pgproj test generate` — emit the standalone xUnit project,
3. `dotnet test` it.

The generated `PgDatabaseFixture` runs in its **env-var mode**: because the VM has no Docker daemon,
`PGPROJ_TEST_CONNECTION` is set to the host's PostgreSQL, so the fixture creates a **throwaway database**
there (and drops it afterwards) instead of spinning a Testcontainers container. On a machine/CI agent
*with* Docker, leaving `PGPROJ_TEST_CONNECTION` unset makes the same project spin its own container — no
code change.

## Run it

From the repo root on the host (the VM must be up and reachable — see the VM facts in the workspace
`CLAUDE.md` / memory):

```powershell
pwsh tests/testgen-vm-e2e/run-vm-testgen-e2e.ps1
```

Override the defaults if the VM IP / host-switch IP / DB creds differ:

```powershell
pwsh tests/testgen-vm-e2e/run-vm-testgen-e2e.ps1 -VmIp 192.168.127.177 -HostDbIp 192.168.112.1
```

The host runner publishes the CLI, ships it plus `vm-testgen-e2e.ps1` to the VM, runs the E2E over ssh,
and exits non-zero on any failure (including a vacuous "all skipped" pass). A green run prints e.g.
`E2E PASS -- Passed! - Failed: 0, Passed: 32, Skipped: 17`.

## Files

- `run-vm-testgen-e2e.ps1` — host driver (publish CLI, ship, invoke over ssh, report).
- `vm-testgen-e2e.ps1` — the in-VM script (extract → generate → assert shape → `dotnet test`).

Local, no-CI by design (see the repo CI/CD hard rule). The host prerequisites are the seeded sample DB
(`tests/sample-db/sample-db.ps1`) reachable from the VM and the ssh key for the VM.
