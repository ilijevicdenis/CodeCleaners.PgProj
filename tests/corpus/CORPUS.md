# PostgreSQL test corpus — authoring contract

This corpus is a **verified specification of the PostgreSQL 18 language surface**, used to measure
and harden the `pgproj` parser. It is *data, not code*: each case is one JSON object describing a
snippet of SQL and whether PostgreSQL accepts it. The C# parser is then run against the whole corpus
to produce a coverage/gap report (`tests/PgProj.Core.Tests/CorpusTests.cs`).

> **Scope:** *server programming* — anything expressible as PostgreSQL syntax: DDL, DML, queries,
> expressions, data types, functions/operators, PL/pgSQL, triggers, rules, and the procedural/
> session commands. Pure runtime administration (replication setup, config files, OS-level ops) is
> out of scope **except** where it is a SQL statement with real grammar (then it's a parse target).

## File layout

```
tests/corpus/
  _fixture.sql      the shared schema the oracle preloads (DO NOT depend on anything not here)
  CORPUS.md         this file
  <category>-<NN>.jsonl   the cases, grouped by feature category and batch
```

One `.jsonl` file per `(category, batch)`. One JSON object per line. No trailing commas, no blank
lines inside (blank/`#`/`//` lines are ignored by the oracle but keep files clean).

## Case schema

```json
{"id":"ct-0007","category":"create-table","sql":"CREATE TABLE x (a int CHECK (a > 0))","expect":"ok","ref":"sql-createtable","note":"column check constraint"}
```

| field      | required | meaning |
|------------|----------|---------|
| `id`       | yes | unique across the WHOLE corpus. Convention: `<cat-abbrev>-<4 digits>`, e.g. `ct-0007`. |
| `category` | yes | the feature category (matches the file's category). |
| `sql`      | yes | the statement(s). May be multiple `;`-separated statements (all run in one rolled-back txn). Escaped as JSON (`\n`, `\"`). |
| `expect`   | yes | `"ok"` (PostgreSQL accepts & runs it) or `"error"` (PostgreSQL rejects it). |
| `ref`      | yes | the PG18 doc slug it exercises, e.g. `sql-createtable`, `functions-json`, `plpgsql-control-structures`. |
| `note`     | recommended | one short phrase: what grammar feature this case covers. |
| `txn`      | only if needed | `"none"` for statements that **cannot run inside a transaction block** (e.g. `VACUUM`, `CREATE INDEX CONCURRENTLY`, `CREATE DATABASE`, `REINDEX`, explicit `COMMIT`/`ROLLBACK`/`SAVEPOINT` tests). The oracle runs these unwrapped in a private clone. |

## What `ok` and `error` mean (ground truth = PostgreSQL 18)

* **`ok`** — the statement runs cleanly against the fixture DB inside `BEGIN; … ROLLBACK;` (no error).
  It must be **self-contained**: reference only fixture objects (see `_fixture.sql`) or objects the
  case itself creates earlier in the same `sql`. A statement that is syntactically valid but fails
  semantically (missing table, type mismatch) is **not** `ok` — Postgres errors, so it's `error` or,
  better, not in the corpus at all (we want clean positives).
* **`error`** — PostgreSQL rejects it. **Prefer true *syntax* errors** (SQLSTATE `42601`) — malformed
  grammar — over semantic errors, because the corpus is primarily a parser spec. A handful of
  representative semantic-rejection cases per category are fine (mark them in `note`).

## The oracle — verify BEFORE you commit cases

Every case MUST be confirmed by the ground-truth oracle (a real postgres:18 container). A case whose
`expect` disagrees with PostgreSQL is a **bug in the case** and must be fixed or dropped.

```powershell
# validate a whole file (exit 0 = every case's expect matches PostgreSQL)
pwsh tools/pg-oracle.ps1 -File tests/corpus/create-table-01.jsonl

# probe a single statement while drafting
pwsh tools/pg-oracle.ps1 -Sql "CREATE TABLE x (a int,)" -Expect error
```

Iterate until the file reports `N/N matched`. Only then is the file done.

## Quality bar (this is the point of the exercise)

* **Exhaustive over the grammar, not repetitive.** Walk the railroad diagram of the feature: every
  clause, option, keyword, default, ordering, and combination. One case per distinct grammar choice.
* **Both polarities.** For each feature include valid forms *and* the characteristic malformed forms
  (missing keyword, wrong order, illegal combo, unbalanced parens, bad option value).
* **Cover the long tail:** quoting (dollar-quote tags, doubled quotes, unicode escapes), schema-
  qualified vs bare names, `IF EXISTS`/`IF NOT EXISTS`, `OR REPLACE`, `CASCADE`/`RESTRICT`, option
  lists, whitespace/comment placement, case-insensitivity of keywords.
* **Self-contained & deterministic.** No `now()`-dependent results that vary, no random, no reliance
  on objects outside the fixture.
* **Unique ids, stable refs.** Don't reuse ids. Keep `ref` pointing at the real PG18 doc slug.

## How the parser is scored (FYI — you don't run this)

`CorpusTests` parses each `sql` with `AstParser` and records: `parsed` (statements, no diagnostics),
`error` (diagnostics), or `empty` (nothing recognized). It cross-tabs against `expect` to report
coverage and to gate regressions via `tests/corpus/_baseline.json`. Your job is only to produce
**PostgreSQL-verified cases**; the parser gaps they expose are the deliverable.
