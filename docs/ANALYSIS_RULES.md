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
| PGV### | Error | Syntax newer than the project's `TargetPostgresVersion` (version gating, EP-TARGET) |

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

`pgproj` discovers every **public, parameterless-constructible** `IPgRule` and runs it alongside the
built-ins. Pack rule ids participate in the same `rules` config (enable/severity) and SARIF output. Notes:

- Each pack loads in an isolated `AssemblyLoadContext` that shares `PgProj.Core` with the host (so the
  `IPgRule` type unifies) but resolves the pack's own private dependencies next to its DLL.
- Duplicate ids are dropped (first loaded wins); an empty id or an unloadable/missing pack is an error.
- Implementations must be deterministic and side-effect-free — they run per file, potentially in parallel.
