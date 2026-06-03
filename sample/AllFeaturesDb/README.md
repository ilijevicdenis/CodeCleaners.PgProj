# AllFeaturesDb — PostgreSQL 18 feature showcase / parser stress-test

A single, self-consistent SSDT-style `pgproj` project that exercises (nearly) **every
PostgreSQL 18 DDL feature** in one coherent schema. Its two purposes:

1. A worked example of the full DDL surface, one-object-per-file like `sample/SampleDb`.
2. A **parser stress-test artifact**: feeding it to `pgproj build` surfaces exactly which
   constructs the `pgproj` AST parser models vs. flags. Diagnostics are *expected here* —
   they are the signal.

All SQL is drawn from corpus statements verified against `postgres:18`, then assembled into
one dependency-satisfiable schema (default schema `afd`, plus `reporting`).

## Verification

**Applies cleanly to postgres:18: YES** — all **58** object files apply to a fresh database
in dependency order with `ON_ERROR_STOP=1`, **zero errors**, first try.

Reproduce:

```powershell
# from this folder; uses the running pgproj-pg18 container
./_verify.ps1
```

The script drops/recreates the `allfeatures` database, then pipes every file listed in
`_apply_order.txt` through `psql -v ON_ERROR_STOP=1` inside the container.

**No features were dropped for infeasibility.** Two environment notes:

- `Types/afd.shell_type.sql` is left as a **shell type** (bare `CREATE TYPE name;`). A full
  base type needs C-language I/O functions that can't be compiled here; the shell form is
  valid DDL and applies.
- `ForeignDataWrappers/afd.dummy_fdw.sql` is intentionally **handler-less** (`NO HANDLER NO
  VALIDATOR`) so the FDW + server + foreign table apply without a contrib `.so`. The foreign
  table is therefore definable but not queryable — by design.
- `Extensions/btree_gist.sql` uses `btree_gist`, which ships in the standard postgres:18
  contrib set; it backs the `EXCLUDE` constraint on `afd.room_booking`.

## `pgproj` parse result

```
pgproj build sample/AllFeaturesDb/AllFeaturesDb.pgproj
  Building project 'AllFeaturesDb' (58 file(s), default schema 'afd')
  schemas=2 tables=5 indexes=6 views=3 sequences=1 functions=10
  Build failed with 2 problem(s):
    - Expected ')' but found 'INCLUDE'.   (afd.customers: table-level PRIMARY KEY (...) INCLUDE (...))
    - Expected ')' but found 'DEFERRED'.  (afd.orders:    FK ... DEFERRABLE INITIALLY DEFERRED)
```

- **Modelled first-class** (parsed into the typed model): 2 schemas, 5 tables, 6 indexes,
  3 views, 1 sequence, 10 functions. Everything else in the project (types, domains,
  collations, conversions, casts, operators, operator classes, text-search objects, triggers,
  event trigger, rule, RLS policy, aggregate, procedure, FDW/server/foreign table,
  publication, statistics, comments) is captured by the **raw-object mechanism** rather than
  the typed model — see `docs/reference/COVERAGE.md`.
- **Flagged: 2** valid-PG18 constructs the AST parser does not yet accept inside a
  `CREATE TABLE` body:
  1. a **table-level `PRIMARY KEY (...) INCLUDE (...)`** clause, and
  2. **`DEFERRABLE INITIALLY DEFERRED`** on a table-level `FOREIGN KEY`.

  Both are deliberately retained — they are the artifact's reason to exist (real parser gaps,
  candidates for new `LIM-` entries). Because `pgproj build`/`script` gate on parse problems,
  `pgproj script` declines to emit `_full_create.sql` until those two forms parse; that gate
  behaviour is itself part of the recorded signal.

## Feature coverage

| Area | Features exercised | Files |
|---|---|---|
| Schemas | two schemas | `Schemas/` |
| Extension | `btree_gist` (contrib) | `Extensions/` |
| Types | ENUM, composite, RANGE, shell | `Types/` |
| Domains | base type, NOT NULL, CHECK (named + `VALUE`), DEFAULT | `Domains/` |
| Collation | ICU, non-deterministic | `Collations/` |
| Conversion | built-in conversion fn | `Conversions/` |
| Sequence | AS/INCREMENT/MIN/MAX/START/CACHE/CYCLE | `Sequences/` |
| Table columns | char/varchar/text, numeric/real/double/money, bool, date/time/timestamptz/interval, inet/macaddr, bit/bit varying, uuid, arrays (incl 2-D), json/jsonb/xml, composite/enum/domain/range/geometric/tsvector types | `Tables/afd.customers.sql` |
| Column generation | `DEFAULT`, `serial`/`bigserial`, identity `ALWAYS` & `BY DEFAULT`, `GENERATED ALWAYS AS (...) STORED`, sequence-`nextval` default | `Tables/` |
| Constraints | column + named table CHECK, composite PK **+ INCLUDE**, UNIQUE (+ INCLUDE, + `NULLS NOT DISTINCT`), composite FK (`MATCH`, `ON DELETE/UPDATE`, `DEFERRABLE`), `EXCLUDE USING gist` (+ WHERE) | `Tables/` |
| Table shapes | RANGE-partitioned + partition + DEFAULT partition, `INHERITS`, `OF` composite type, `LIKE ... INCLUDING` | `Tables/` |
| Indexes | btree, unique, partial (`WHERE`), expression + opclass + ordering, `INCLUDE`, GIN (jsonb + array, storage param), GiST | `Indexes/` |
| Views | plain, updatable `WITH CASCADED CHECK OPTION`, materialized (`WITH DATA`) | `Views/` |
| Functions | SQL & PL/pgSQL; `IN/OUT/INOUT/VARIADIC` + defaults; `RETURNS` scalar/SETOF/TABLE; IMMUTABLE/STABLE/STRICT/SECURITY DEFINER/PARALLEL; new-style `RETURN`; trigger & event-trigger fns | `Functions/` |
| Procedure | `IN`/`INOUT` args, PL/pgSQL body | `Procedures/` |
| Aggregate | SFUNC/STYPE/INITCOND | `Aggregates/` |
| Cast | `WITH FUNCTION ... AS ASSIGNMENT` | `Casts/` |
| Operator | binary op + COMMUTATOR/HASHES/MERGES | `Operators/` |
| Operator class | btree opclass with OPERATOR + FUNCTION entries | `OperatorClasses/` |
| Text search | dictionary (template) + configuration (COPY) + `ALTER ... ALTER MAPPING` | `TextSearch/` |
| Triggers | row BEFORE UPDATE `WHEN`, statement AFTER multi-event | `Triggers/` |
| Event trigger | `ddl_command_end` + `WHEN TAG IN` | `EventTriggers/` |
| Rule | `ON DELETE ... DO INSTEAD NOTHING` | `Rules/` |
| RLS | `ENABLE ROW LEVEL SECURITY` + `CREATE POLICY` (USING + WITH CHECK) | `Policies/` |
| FDW stack | handler-less FDW + SERVER + FOREIGN TABLE (+OPTIONS) | `ForeignDataWrappers/`, `ForeignServers/`, `ForeignTables/` |
| Publication | `FOR TABLE ... WITH (publish=...)` | `Publications/` |
| Statistics | `ndistinct, dependencies, mcv` | `Statistics/` |
| Comments | SCHEMA/TABLE/COLUMN/VIEW/MATVIEW/FUNCTION/TYPE/DOMAIN/INDEX/SEQUENCE/TRIGGER | `Comments/` |

`_apply_order.txt` is the authoritative dependency order; `_verify.ps1` consumes it.
