# Editor JSON contract (EP-RPC)

The `pgproj` CLI is the single engine behind both a future VS Code extension (EP-VSCODE) and a VS
experience (EP-VS). To let those UIs (and CI) consume results without scraping text, every targeted
verb emits a **stable, versioned JSON payload** under `--format json`. This is the editor-backend
contract from issue #17.

## Transport decision

**Per-invocation `--format json` now.** Each verb runs, prints one JSON document to stdout, and
exits — simple, stateless, trivially testable, and exactly what CI wants. A long-running
`pgproj serve` STDIO JSON-RPC host (and NDJSON streaming-progress events for long publishes) is a
**deferred follow-up**: the DTOs here are designed to drop into that host unchanged (they already
live in `PgProj.Core/Contracts`, not the CLI), so adopting a server later is additive.

## Versioning

Every payload carries a top-level `schemaVersion` (currently **`1.0`**). Additive, backwards-
compatible changes (new optional fields) keep the major; a breaking change (renamed/removed field or
changed meaning) bumps it. Editors should refuse a major they do not understand. The constant lives in
`PgProj.Core/Contracts/JsonContract.cs`. Wire format: camelCase property names, `null` fields omitted,
enums emitted as their string names (never integers).

## Verbs covered (first wave — offline)

| Verb | Command | Payload |
|------|---------|---------|
| build | `pgproj build <proj> --format json` | `BuildReportDto` (summary + diagnostics + embedded model tree) |
| analyze | `pgproj analyze <proj> --format json` | `AnalyzeReportDto` (diagnostics + gate verdict) |
| compare | `pgproj compare <proj> -c <conn> --format json` | `CompareReportDto` (ordered change list) |
| publish (dry-run) | `pgproj publish <proj> -c <conn> --dry-run --format json` | `PublishPlanDto` (plan + deploy script) |
| model-tree | `pgproj model-tree <proj> --format json` | `ModelTreeDto` (every object + source positions) |

Text output is **byte-identical** when `--format json` is absent.

## Diagnostics shape (shared across verbs)

One shape an editor maps straight onto a Problems-panel entry:

```json
{ "ruleId": "PG001", "severity": "Warning", "message": "...", "target": "afd.f",
  "file": "Functions/afd.f.sql", "line": 1, "col": 1 }
```

- `severity` ∈ `Info | Warning | Error`.
- `file` is project-relative (null when the finding has no file anchor).
- `line`/`col` are **1-based**, `0` when unknown.
- Build diagnostics use `ruleId: "BUILD"`; analyzer findings use the `PGxxx` rule ids and resolve
  their `file:line:col` from the source-position index.

## Model tree

`ModelTreeDto.nodes` enumerates **every** object the model holds — schemas, tables (with their
columns as `children`), indexes, views/materialized views, sequences, functions, and every generic
raw object (type, domain, trigger, rule, policy, extension, operator, aggregate, cast, collation,
FDW/server/foreign table, text-search objects, …). Each node carries `kind`, `schema`, `name`,
`qualifiedName`, and a `file`/`line`/`col` source anchor for tree views and go-to-definition.

## Where the contract lives

- DTOs + version: `src/PgProj.Core/Contracts/ContractDtos.cs`, `JsonContract.cs`
- Builders: `ContractBuilder.cs` (verb payloads), `ModelTreeBuilder.cs`, `ContractMappers.cs`
- Source positions: `SourcePositionIndex.cs` (re-parses project files, maps object identity → file:line:col)
- CLI wiring: `src/PgProj.Cli/Program.cs` (`--format json` branch per verb)
- Conformance tests: `tests/PgProj.Core.Tests/JsonContractTests.cs` (unit, golden field-sets) and
  `JsonContractIntegrationTests.cs` (live Postgres, gated on `PGPROJ_TEST_CONNECTION`).
