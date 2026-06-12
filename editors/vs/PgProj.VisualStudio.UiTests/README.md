# PgProj.VisualStudio.UiTests — E2E smoke tests for the installed VS extension

FlaUI + DTE harness that launches the **installed main-instance VS 2026** on a scratch `.pgproj`
solution and asserts the PostgreSQL editor experience end-to-end:

| Check | Mechanism | Asserts |
|---|---|---|
| Syntax coloring | UIA TextPattern foreground colors | `CREATE` painted differently from an identifier |
| IntelliSense | DTE `Edit.ListMembers` + UIA popup scan | `public.` offers the project's tables |
| Semantic check | DTE buffer edit + Error List | unresolved relation reaches the Error List, **unsaved** |

Design: all *actions* go through DTE COM automation (attached to the launched PID via the ROT —
no synthesized keyboard/mouse, no focus stealing); FlaUI/UIA is read-only. The harness only ever
kills the devenv it launched itself.

## Running — preferably in a dedicated VM (Hyper-V)

UI automation against the main VS instance is best run in an isolated Windows session. VM setup
checklist:

1. Windows 11 (a trimmed image like Tiny11 works), interactive desktop session, auto-logon if the
   run should be unattended. **Do not let the session lock** during runs (UIA reads keep working,
   but VS renders nothing useful for screenshots on a locked desktop).
2. **VS 2026 Community** — workloads: *.NET desktop development* is enough to host the extension;
   add *Visual Studio extension development* only if you also build the VSIX inside the VM.
3. **.NET 10 SDK** (runs the test project and the VSIX-bundled `pgproj` CLI the LSP shells out to).
4. Clone the repo, then from `editors/vs/`:
   - `setup-local-sdk-feed.cmd` — registers the local `PgProj.Sdk` NuGet feed (VS cannot load a
     `.pgproj` without it; see the 2026-06-11 lab notes).
   - `build-vsix.cmd` (or copy a built vsix in) and `install-pgproj.cmd` — installs the classic
     extension and purges the MEF `ComponentModelCache` (critical on VS 2026 — see the 2026-06-12
     lab note about per-user extensions being skipped by `/updateconfiguration`).
   - Start VS once manually so the first-run MEF rebuild happens, then close it.
5. Run: `dotnet test editors\vs\PgProj.VisualStudio.UiTests`
   - On failure the test drops a full-screen PNG into `%TEMP%\pgproj-uitest-failure-*.png` and
     reports all three checks in one message.
6. **Real-database mode (recommended):** start the sample DB (`tests\sample-db\sample-db.ps1`,
   Docker on the HOST — from the VM use the host's IP) and set
   `PGPROJ_UITEST_DB = Host=<host>;Port=15432;Username=postgres;Password=pgproj;Database=sampledb`.
   The fixture then scaffolds the scratch solution by running `pgproj extract` (the VSIX-bundled
   CLI — the exact payload VS uses) against the real database instead of hand-written files; the
   checks adapt to the extracted objects (`sales.v_open_orders` etc.). Unset → hand-rolled
   two-file project, no DB required.

The project is deliberately **not** in `PgProj.slnx` and not part of the default `dotnet test`
sweep (same policy as the VSIX projects — needs an installed VS + desktop session).

## Status (2026-06-12): GREEN — and the harness found and fixed two product bugs

The suite passes end-to-end - **115/115** in ~5 minutes - in the Hyper-V VM against the installed
product with the real sample database. Along the way it root-caused the originally reported "everything dead" state (see the
CLAUDE.md lab notes for the full story):

1. **Coloring** — the buffer's file-load language detection overwrote the `pgsql` content type;
   fixed with `VsBufferDetectLangSID=false` in `PgSqlEditorFactory`.
2. **IntelliSense + diagnostics** — the LSP broker only activates clients whose content type
   derives from `CodeRemoteContentDefinition.CodeRemoteContentTypeName`; `pgsql` now does.
3. Plus: capability-based project identification in the factory (CPS hierarchies), and the factory
   logs every claim/decline to the ActivityLog (`devenv /log` — the fixture passes it).

Harness lessons baked in: DTE-over-ROT by pid moniker (no `GetActiveObject` on VS 2026); the
Error List is read via UIA (LSP diagnostics never reach legacy `ErrorItems`); the completion check
types the trigger char as real input into the harness-owned VS and matches popup items by Name
(they don't expose as `ListItem`); `AppendLine` re-collapses the caret to the end (DTE `Insert`
can leave it at the start).

## The iterate loop (host ↔ VM)

Edit on the host → `scp` changed harness sources to `C:\pgproj\payload\UiTests\` → ssh
`vm-run-tests.ps1`. Product changes additionally need the VSIX rebuilt with the host VS 2026
MSBuild, `scp` to `C:\pgproj\payload\`, then `vm-setup.ps1` (reinstall + MEF purge) before the run.
