# CREATE FOREIGN TABLE / FOREIGN DATA WRAPPER / SERVER / USER MAPPING

## CREATE FOREIGN TABLE

### Synopsis
```sql
CREATE FOREIGN TABLE [ IF NOT EXISTS ] table_name ( [
  { column_name data_type [ OPTIONS ( option 'value' [, ... ] ) ] [ COLLATE collation ] [ column_constraint [ ... ] ]
    | table_constraint
    | LIKE source_table [ like_option ... ] }
    [, ... ]
] )
[ INHERITS ( parent_table [, ... ] ) ]
  SERVER server_name
[ OPTIONS ( option 'value' [, ... ] ) ]

CREATE FOREIGN TABLE [ IF NOT EXISTS ] table_name
  PARTITION OF parent_table [ (
  { column_name [ WITH OPTIONS ] [ column_constraint [ ... ] ]
    | table_constraint }
    [, ... ]
) ]
{ FOR VALUES partition_bound_spec | DEFAULT }
  SERVER server_name
[ OPTIONS ( option 'value' [, ... ] ) ]
```

### Schema-Defining Clauses
- **Column Definition**: `column_name data_type [ OPTIONS ( option 'value' [, ... ] ) ] [ COLLATE collation ] [ column_constraint [ ... ] ]`
  - Individual column data type and constraints
  - FDW-specific column options via OPTIONS
  
- **SERVER** (Required): `SERVER server_name`
  - References existing foreign server managing this table
  
- **OPTIONS**: `OPTIONS ( option 'value' [, ... ] )`
  - Table-level options (FDW-dependent); overrides server OPTIONS
  
- **PARTITION OF**: `PARTITION OF parent_table [ ... ] { FOR VALUES partition_bound_spec | DEFAULT }`
  - Declares foreign table as partition of a partitioned table with bounds

---

## CREATE FOREIGN DATA WRAPPER

### Synopsis
```sql
CREATE FOREIGN DATA WRAPPER name
    [ HANDLER handler_function | NO HANDLER ]
    [ VALIDATOR validator_function | NO VALIDATOR ]
    [ OPTIONS ( option 'value' [, ... ] ) ]
```

### Schema-Defining Clauses
- **HANDLER** | **NO HANDLER**
  - Function to retrieve execution functions for foreign tables
  - Signature: no arguments, returns `fdw_handler`
  - Optional; if absent, foreign tables can only be declared (not accessed)
  
- **VALIDATOR** | **NO VALIDATOR**
  - Function to validate generic options (FDW, server, user mapping, foreign table)
  - Signature: `(text[], oid)` → (returns value ignored; errors via `ereport`)
  - Optional; if omitted, options not checked at creation time
  
- **OPTIONS**: `OPTIONS ( option 'value' [, ... ] )`
  - FDW-specific options (e.g., library path, connection defaults)

---

## CREATE SERVER

### Synopsis
```sql
CREATE SERVER [ IF NOT EXISTS ] server_name [ TYPE 'server_type' ] [ VERSION 'server_version' ]
    FOREIGN DATA WRAPPER fdw_name
    [ OPTIONS ( option 'value' [, ... ] ) ]
```

### Schema-Defining Clauses
- **TYPE**: `'server_type'`
  - Optional; informational string for FDW to understand server variant
  
- **VERSION**: `'server_version'`
  - Optional; informational string for FDW to understand server version
  
- **FOREIGN DATA WRAPPER** (Required): `fdw_name`
  - Names the FDW managing this server (requires `USAGE` on wrapper)
  
- **OPTIONS**: `OPTIONS ( option 'value' [, ... ] )`
  - Connection details (host, dbname, port, etc.); FDW-dependent

---

## CREATE USER MAPPING

### Synopsis
```sql
CREATE USER MAPPING [ IF NOT EXISTS ] FOR { user_name | USER | CURRENT_ROLE | CURRENT_USER | PUBLIC }
    SERVER server_name
    [ OPTIONS ( option 'value' [ , ... ] ) ]
```

### Schema-Defining Clauses
- **FOR** (Role Reference): one of:
  - `user_name` — Existing user
  - `USER` — Current session user name
  - `CURRENT_ROLE` — Current session role name
  - `CURRENT_USER` — Current session user name
  - `PUBLIC` — Public (default) mapping for any unmapped role
  
- **SERVER** (Required): `server_name`
  - Names existing server for the mapping
  
- **OPTIONS**: `OPTIONS ( option 'value' [ , ... ] )`
  - Credentials/auth details (user, password, etc.); FDW-dependent
