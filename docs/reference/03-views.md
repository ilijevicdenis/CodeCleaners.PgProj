# CREATE VIEW / CREATE MATERIALIZED VIEW

## CREATE VIEW

### Synopsis

```sql
CREATE [ OR REPLACE ] [ TEMP | TEMPORARY ] [ RECURSIVE ] VIEW name [ ( column_name [, ...] ) ]
    [ WITH ( view_option_name [= view_option_value] [, ... ] ) ]
    AS query
    [ WITH [ CASCADED | LOCAL ] CHECK OPTION ]
```

### Schema-Defining Clauses

| Clause | Purpose |
|--------|---------|
| **OR REPLACE** | Replace existing view; new query must generate same columns (names, order, types); may add new columns to end |
| **TEMP \| TEMPORARY** | Session-scoped view, auto-dropped at session end; cannot be schema-qualified |
| **RECURSIVE** | Recursive view; **requires explicit column name list** |
| **Column List** `( column_name [, ...] )` | Optional; deduced from query if omitted; **required for RECURSIVE** |
| **WITH ( view_option_name = view_option_value )** | View options: `check_option` (local\|cascaded), `security_barrier`, `security_invoker` |
| **CHECK OPTION** | Controls INSERT/UPDATE/MERGE on updatable views: **LOCAL** (check this view only) or **CASCADED** (check view + base views) |
| **AS query** | SELECT or VALUES providing view rows |

---

## CREATE MATERIALIZED VIEW

### Synopsis

```sql
CREATE MATERIALIZED VIEW [ IF NOT EXISTS ] table_name
    [ (column_name [, ...] ) ]
    [ USING method ]
    [ WITH ( storage_parameter [= value] [, ... ] ) ]
    [ TABLESPACE tablespace_name ]
    AS query
    [ WITH [ NO ] DATA ]
```

### Schema-Defining Clauses

| Clause | Purpose |
|--------|---------|
| **IF NOT EXISTS** | Suppress error if view exists; issue notice instead |
| **Column List** `( column_name [, ...] )` | Optional; deduced from query if omitted |
| **USING method** | Table access method (must be TABLE type); default: `default_table_access_method` — **admin** |
| **WITH ( storage_parameter = value )** | Storage parameters (all CREATE TABLE parameters supported) — **admin** |
| **TABLESPACE tablespace_name** | Target tablespace; default: `default_tablespace` — **admin** |
| **WITH [ NO ] DATA** | `WITH DATA` (default, populate at creation) or `WITH NO DATA` (empty, requires REFRESH) |
| **AS query** | SELECT, TABLE, or VALUES command (runs in security-restricted context) |

---

## Notes

- **Views vs Materialized Views**: Views are virtual (query at read-time); materialized views are physical tables (query once, persist).
- **OR REPLACE limitation**: Standard views cannot use `OR REPLACE` with recursive or temporary modifiers.
- **CHECK OPTION**: Only on automatically updatable views (single base table, simple column selection).
- **Recursive views**: Define base case + recursive case in UNION; column list explicit.
- **Admin clauses**: USING, TABLESPACE, WITH storage parameters require superuser or explicit privilege.
