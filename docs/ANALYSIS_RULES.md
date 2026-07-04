# Static analysis rules (EP-ANALYSIS+)

`pgproj analyze` runs static safety rules over the parsed AST. The same rules gate `build`/`publish`/
`validate` (skip with `--no-analyze`; escalate warnings to errors with `--strict`).

## Built-in rules

| Id | Default | What it flags |
|----|---------|---------------|
| PG001 | Warning | `SECURITY DEFINER` function without `SET search_path` |
| PG002 | Info | Dynamic SQL (`EXECUTE`) in a function body |
| PG003 | Warning | `UPDATE`/`DELETE` without a `WHERE` clause |
| PG004 | Warning | Schema mutation (`CREATE`/`ALTER`/`DROP`) inside a function body |
| PG005 | Info | Function without a declared volatility |
| PG006 | Info | Table without a `PRIMARY KEY` (skips `PARTITION OF`/typed/`LIKE` tables) |
| PG007 | Info | `SELECT *` in a view body |
| PG008 | Info | `numeric`/`decimal` column without precision/scale |
| PG009 | Info | `LIMIT` without `ORDER BY` |
| PG010 | Info | Blank-padded `char(n)`/`character(n)` column (use `text`/`varchar`) |
| PG011 | Info | `timestamp` without time zone column (use `timestamptz`) |
| PG012 | Info | `serial`/`bigserial`/`smallserial` column (use `GENERATED … AS IDENTITY`) |
| PG013 | Info | `money` column (locale-dependent; use `numeric`) |
| PG014 | Warning | Foreign key without a covering index (model-level — see below) |
| PG015 | Info | Identifier with uppercase letters (folds to lower-case unquoted, or forces quoting forever) |
| PG016 | Warning | Identifier longer than 63 bytes (PostgreSQL silently truncates it) |
| PG017 | Info | `json` column (raw text — no key dedup, no operators/GIN indexing; use `jsonb`) |
| PG019 | Info | Foreign key with no `ON DELETE`/`ON UPDATE` action (defaults to `NO ACTION` — model-level) |
| PG020 | Warning | `EXCEPTION WHEN OTHERS` in a function body (swallows every error; catch specific SQLSTATEs or re-RAISE) |
| PG021 | Warning | `SELECT … INTO` without `STRICT` in a function body (no row → unset, many rows → arbitrary; goes silent) |
| PG022 | Info | `ALTER` action pgproj parses but does **not** model — `OWNER TO`, `SET (storage/reloptions)`, `ENABLE/FORCE ROW LEVEL SECURITY`, `RENAME`, `SET SCHEMA`, partition attach, … (neither compared nor deployed; surfaced instead of silently dropped) |
| PG024 | Info | Duplicate index — same columns + predicate as another index (model-level) |
| PG025 | Info | Redundant index — its columns are a leading prefix of a wider index (model-level) |
| PGV### | Error | Syntax newer than the project's `TargetPostgresVersion` (version gating, EP-TARGET) |

PG001–PG013, PG015–PG017 and PG020–PG022 are **per-file** rules over the parsed AST (PG015/PG016
check table and column identifiers; PG020/PG021 scan the routine body; PG022 inspects each `ALTER`
statement's action list). **PG014, PG019, PG024 and PG025 are model-level rules**: they run once over
the merged project model, so they see relationships that span files (the FK in one file, its covering
index in another).

- **PG022** fires per unmodeled action, not per statement noise: a mixed `ALTER TABLE t ADD COLUMN c
  int, OWNER TO bob` flags only `OWNER` (the `ADD COLUMN` folds into the model). The structural table
  actions that DO fold — `ADD`/`DROP COLUMN`, `ADD CONSTRAINT`, `ALTER COLUMN TYPE`/`SET`/`DROP
  DEFAULT`/`SET`/`DROP NOT NULL` — never fire it. It's advisory (Info) by default; raise it to a
  warning per project if an ignored `RENAME`/`SET SCHEMA` should block the build.

- **PG014** coverage counts the primary key, unique constraints, and any non-partial index whose
  **leading columns** (any order) are exactly the FK's columns.
- **PG019** fires only when a foreign key declares *neither* `ON DELETE` *nor* `ON UPDATE` — an explicit
  `NO ACTION` (or any action) clears it. It's a "forgotten decision" nudge, not a defect.
- **PG024/PG025** compare every index-like object on a table (primary key, unique constraints, and
  explicit `CREATE INDEX`) by **b-tree-ordered** columns; only explicit indexes are ever flagged.
  PG024 needs an identical predicate too; partial indexes (`WHERE …`) never count as redundant (PG025)
  on either side because the predicate changes coverage.

## Configuring rules — `.pgproj.analysis.json`

A sidecar next to the `.pgproj`. Per-rule `enabled` + `severity`; precedence is **CLI > sidecar > rule default**.

```json
{
  "rulePacks": ["./build/Org.Rules.dll"],
  "rules": {
    "PG003": { "enabled": false },
    "PG005": { "severity": "error" },
    "ORG001": { "severity": "warning" }
  }
}
```

CLI overrides: `--rule PG003=off`, `--rule PG005=error` (repeatable; an unknown id is a usage error).
Output formats: human (default), `--format json`, `--format sarif` (for GitHub/Azure code scanning).

## External rule packs (#79)

Ship your own rules in a separate assembly — the DacFx contributor-model analogue.

1. Reference `PgProj.Core` and implement `IPgRule`:

   ```csharp
   using System.Collections.Generic;
   using PgProj.Core.Analysis;
   using PgProj.Core.Syntax;

   public sealed class NoUnloggedTables : IPgRule
   {
       public string Id => "ORG001";                       // stable, unique; avoid the PG/PGV prefixes
       public DiagnosticSeverity DefaultSeverity => DiagnosticSeverity.Warning;
       public string Title => "Unlogged tables are forbidden by org policy";
       public IEnumerable<Diagnostic> Analyze(ParseResult result)
       {
           foreach (var s in result.Statements)
               if (s is CreateTableStatement t /* && t.IsUnlogged */)
                   yield return new Diagnostic(Id, DefaultSeverity, "Unlogged table", $"{t.Schema}.{t.Name}");
       }
   }
   ```

2. Build the DLL and list it under `rulePacks` (paths resolve relative to the `.pgproj`).

2b. For a **cross-object rule** implement `IModelRule` instead — same shape, but `Analyze` receives the
   **merged `DatabaseModel`** (after every file is lowered and merged), so it can see relationships that
   span files, exactly like the built-in PG014. Both rule shapes can live in the same pack DLL.

`pgproj` discovers every **public, parameterless-constructible** `IPgRule` and `IModelRule` and runs
them alongside the built-ins. Pack rule ids participate in the same `rules` config (enable/severity)
and SARIF output. Notes:

- Each pack loads in an isolated `AssemblyLoadContext` that shares `PgProj.Core` with the host (so the
  `IPgRule` type unifies) but resolves the pack's own private dependencies next to its DLL.
- Duplicate ids are dropped (first loaded wins); an empty id or an unloadable/missing pack is an error.
- Implementations must be deterministic and side-effect-free — they run per file, potentially in parallel.
