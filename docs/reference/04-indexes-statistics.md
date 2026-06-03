# CREATE INDEX / CREATE STATISTICS

## CREATE INDEX

### Synopsis

```sql
CREATE [ UNIQUE ] INDEX [ CONCURRENTLY ] [ [ IF NOT EXISTS ] name ] ON [ ONLY ] table_name [ USING method ]
    ( { column_name | ( expression ) } [ COLLATE collation ] [ opclass [ ( opclass_parameter = value [, ... ] ) ] ] [ ASC | DESC ] [ NULLS { FIRST | LAST } ] [, ...] )
    [ INCLUDE ( column_name [, ...] ) ]
    [ NULLS [ NOT ] DISTINCT ]
    [ WITH ( storage_parameter [= value] [, ... ] ) ]
    [ TABLESPACE tablespace_name ]
    [ WHERE predicate ]
```

### Schema-Defining Clauses

| Clause | Details |
|--------|---------|
| **UNIQUE** | Enforce uniqueness constraint |
| **USING method** | Index method: `btree` (default), `hash`, `gist`, `spgist`, `gin`, `brin` |
| **column_name** | Simple column reference |
| **( expression )** | Computed index (e.g., `(lower(title))`) |
| **COLLATE collation** | Sort collation for ordering |
| **opclass** | Operator class (e.g., `int4_ops`); usually auto-selected |
| **opclass_parameter** | Method-specific tuning parameters |
| **ASC \| DESC** | Sort direction (ASC default); useful for multicolumn indexes |
| **NULLS FIRST \| NULLS LAST** | NULL ordering (`NULLS LAST` default for ASC, `NULLS FIRST` for DESC) |
| **INCLUDE** | Non-key columns in leaf tuples (enables index-only scans); not used in searches |
| **NULLS DISTINCT \| NULLS NOT DISTINCT** | For unique indexes: treat NULLs as distinct (default) or not |
| **WITH ( storage_parameter )** | Index-method-specific tuning (e.g., `fillfactor`, `fastupdate`) |
| **WHERE predicate** | Partial index on subset matching predicate |

---

## CREATE STATISTICS

### Synopsis

**Univariate:**
```sql
CREATE STATISTICS [ [ IF NOT EXISTS ] statistics_name ]
    ON ( expression )
    FROM table_name
```

**Multivariate (2+ columns/expressions):**
```sql
CREATE STATISTICS [ [ IF NOT EXISTS ] statistics_name ]
    [ ( statistics_kind [, ... ] ) ]
    ON { column_name | ( expression ) }, { column_name | ( expression ) } [, ...]
    FROM table_name
```

### Schema-Defining Clauses

| Clause | Details |
|--------|---------|
| **statistics_kind** | `ndistinct` (n-distinct), `dependencies` (functional dependency), `mcv` (most-common values); default = all three |
| **column_name** | Table column (multivariate requires ≥2) |
| **( expression )** | Expression (univariate or multivariate) |
| **IF NOT EXISTS** | Suppress error if statistics object already exists |
