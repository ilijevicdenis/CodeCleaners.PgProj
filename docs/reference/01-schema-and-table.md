# CREATE SCHEMA / CREATE TABLE

## CREATE SCHEMA

### Synopsis

```
CREATE SCHEMA schema_name [ AUTHORIZATION role_specification ] [ schema_element [ ... ] ]
CREATE SCHEMA AUTHORIZATION role_specification [ schema_element [ ... ] ]
CREATE SCHEMA IF NOT EXISTS schema_name [ AUTHORIZATION role_specification ]
CREATE SCHEMA IF NOT EXISTS AUTHORIZATION role_specification

where role_specification can be:

    user_name
  | CURRENT_ROLE
  | CURRENT_USER
  | SESSION_USER
```

### Schema-Diff Modeling (Persistent State)

- Schema name (identifier)
- `IF NOT EXISTS` flag

**Note:** Authorization/ownership are administrative metadata, not modeled for schema diffing.

---

## CREATE TABLE

### Synopsis

**Main form:**

```sql
CREATE [ [ GLOBAL | LOCAL ] { TEMPORARY | TEMP } | UNLOGGED ] TABLE [ IF NOT EXISTS ] table_name ( [
  { column_name data_type [ STORAGE { PLAIN | EXTERNAL | EXTENDED | MAIN | DEFAULT } ] [ COMPRESSION compression_method ] [ COLLATE collation ] [ column_constraint [ ... ] ]
    | table_constraint
    | LIKE source_table [ like_option ... ] }
    [, ... ]
] )
[ INHERITS ( parent_table [, ... ] ) ]
[ PARTITION BY { RANGE | LIST | HASH } ( { column_name | ( expression ) } [ COLLATE collation ] [ opclass ] [, ... ] ) ]
[ USING method ]
[ WITH ( storage_parameter [= value] [, ... ] ) | WITHOUT OIDS ]
[ ON COMMIT { PRESERVE ROWS | DELETE ROWS | DROP } ]
[ TABLESPACE tablespace_name ]
```

**Typed table form:**

```sql
CREATE [ [ GLOBAL | LOCAL ] { TEMPORARY | TEMP } | UNLOGGED ] TABLE [ IF NOT EXISTS ] table_name
    OF type_name [ (
  { column_name [ WITH OPTIONS ] [ column_constraint [ ... ] ]
    | table_constraint }
    [, ... ]
) ]
[ PARTITION BY { RANGE | LIST | HASH } ( { column_name | ( expression ) } [ COLLATE collation ] [ opclass ] [, ... ] ) ]
[ USING method ]
[ WITH ( storage_parameter [= value] [, ... ] ) | WITHOUT OIDS ]
[ ON COMMIT { PRESERVE ROWS | DELETE ROWS | DROP } ]
[ TABLESPACE tablespace_name ]
```

**Partitioned table form:**

```sql
CREATE [ [ GLOBAL | LOCAL ] { TEMPORARY | TEMP } | UNLOGGED ] TABLE [ IF NOT EXISTS ] table_name
    PARTITION OF parent_table [ (
  { column_name [ WITH OPTIONS ] [ column_constraint [ ... ] ]
    | table_constraint }
    [, ... ]
) ] { FOR VALUES partition_bound_spec | DEFAULT }
[ PARTITION BY { RANGE | LIST | HASH } ( { column_name | ( expression ) } [ COLLATE collation ] [ opclass ] [, ... ] ) ]
[ USING method ]
[ WITH ( storage_parameter [= value] [, ... ] ) | WITHOUT OIDS ]
[ ON COMMIT { PRESERVE ROWS | DELETE ROWS | DROP } ]
[ TABLESPACE tablespace_name ]
```

### Schema-Diff Modeling (Persistent State)

**Table identification:**
- Table name (qualified: schema.table)
- `IF NOT EXISTS` flag

**Column definitions:**
- Column name
- Data type (atomic or composite)
- `DEFAULT` expression (persisted; evaluated at insert)
- `GENERATED` columns:
  - `GENERATED ALWAYS AS (expression) STORED` (computed, materialized)
  - `GENERATED ALWAYS AS (expression) VIRTUAL` (computed, not stored)
  - `GENERATED { ALWAYS | BY DEFAULT } AS IDENTITY` (sequence-backed, with START/INCREMENT/etc.)
- `COLLATE` collation name
- `STORAGE` class: `PLAIN | EXTERNAL | EXTENDED | MAIN | DEFAULT`
- `COMPRESSION` method (e.g., `pglz`, `lz4`)

**Column constraints (inline):**
- `NOT NULL` / `NULL`
- `PRIMARY KEY` (with optional index method and WITH options)
- `UNIQUE` (with optional index method, including nulls distinct flag)
- `REFERENCES` (foreign key, with MATCH type, ON DELETE/UPDATE actions, deferrable mode)
- `CHECK` (boolean expression)

**Table constraints (top-level):**
- `PRIMARY KEY (columns)` with index method and WITH options
- `UNIQUE (columns)` with index method, nulls distinct, WITH options
- `FOREIGN KEY (columns) REFERENCES table(columns)` with MATCH, ON DELETE/UPDATE, deferrable
- `CHECK (expression)` with optional constraint name
- `EXCLUDE` (index-backed, with predicate, nulls not distinct flag)

**Table-level structural options:**
- `INHERITS (parent_table [, ...])` (table inheritance chain)
- `LIKE source_table [ like_option ... ]` (column/constraint copying from another table)
- `PARTITION BY { RANGE | LIST | HASH } (...)` (partitioning strategy and keys)
- `PARTITION OF parent_table FOR VALUES ...` (this table is a partition of another)
- Partition bound specification (`FOR VALUES partition_bound_spec`)

**Storage and runtime:**
- `WITH (storage_parameter [= value] [, ...])` (e.g., `fillfactor`, `autovacuum_*`, `toast_*`)
- `TABLESPACE tablespace_name` ⚠️ **Flag as administrative** — tablespace choices are infrastructure/deployment decisions, not schema semantics; diff tools should highlight but not enforce changes
- `ON COMMIT { PRESERVE ROWS | DELETE ROWS | DROP }` (for `TEMP` / `TEMPORARY` tables; affects behavior on transaction commit)
- `USING method` (index/access method, e.g., `heap`, `heap2`)

**Exclusions (not modeled for schema diffing):**
- Permissions, ownership, roles, grants
- Table type declarations (`UNLOGGED`, `TEMPORARY` / `TEMP`, `GLOBAL` / `LOCAL`) — these are transience/scope annotations, not persistent schema
- WITH OIDS / WITHOUT OIDS (legacy OID column management)

---

## Notes for Schema-Diff Tools

1. **Persistent schema** is defined by structure (columns, types, constraints, defaults, generated expressions), inheritance, partitioning strategy, and storage parameters — **not** by transience flags, permissions, or tablespace assignment.

2. **ON COMMIT** is semantic only for temporary tables; for permanent tables it has no effect and should not be modeled.

3. **TABLESPACE** is an administrative locality choice; diff tools should warn when it changes but not fail validation.

4. **GENERATED columns with IDENTITY** combine generation strategy with sequencing; the IDENTITY part (`START`, `INCREMENT`, `CACHE`, `CYCLE`, etc.) must be tracked as a constraint sub-option.

5. **Foreign keys, UNIQUE, and PRIMARY KEY** all support deferrable modes and match types; these are part of the constraint definition and must be modeled.

6. **EXCLUDE constraints** are rare but schema-critical; they define a predicate at the index level and must be preserved in diffing.
