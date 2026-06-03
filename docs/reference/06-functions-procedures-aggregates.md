# CREATE FUNCTION / PROCEDURE / AGGREGATE

PostgreSQL language constructs for user-defined functions, procedures, and aggregate functions.

---

## CREATE FUNCTION

### Synopsis

```sql
CREATE [ OR REPLACE ] FUNCTION
    name ( [ [ argmode ] [ argname ] argtype [ { DEFAULT | = } default_expr ] [, ...] ] )
    [ RETURNS rettype
      | RETURNS TABLE ( column_name column_type [, ...] ) ]
  { LANGUAGE lang_name
    | TRANSFORM { FOR TYPE type_name } [, ... ]
    | WINDOW
    | { IMMUTABLE | STABLE | VOLATILE }
    | [ NOT ] LEAKPROOF
    | { CALLED ON NULL INPUT | RETURNS NULL ON NULL INPUT | STRICT }
    | { [ EXTERNAL ] SECURITY INVOKER | [ EXTERNAL ] SECURITY DEFINER }
    | PARALLEL { UNSAFE | RESTRICTED | SAFE }
    | COST execution_cost
    | ROWS result_rows
    | SUPPORT support_function
    | SET configuration_parameter { TO value | = value | FROM CURRENT }
    | AS 'definition'
    | AS 'obj_file', 'link_symbol'
    | sql_body
  } ...
```

### Schema-Defining Clauses

**Parameter Modes:**
- `IN` (default) — input parameter
- `OUT` — output parameter (function returns composite row)
- `INOUT` — input/output parameter
- `VARIADIC` — variadic array argument

**Parameter Declaration:**
```
[ argmode ] [ argname ] argtype [ { DEFAULT | = } default_expr ]
```

**Return Type Options:**
- `RETURNS rettype` — simple scalar or composite type return
- `RETURNS SETOF rettype` — set-returning function
- `RETURNS TABLE ( column_name column_type [, ...] )` — explicitly named table columns

**Language:**
```
LANGUAGE lang_name    -- sql | c | internal | plpgsql | etc.
```

**Immutability (mutually exclusive):**
- `IMMUTABLE` — no database modifications, deterministic (safe for index expressions)
- `STABLE` — no database modifications, consistent within transaction
- `VOLATILE` (default) — may change within single table scan (no optimization)

**Function Body:**
- `AS 'definition'` — string literal, language-dependent (supports `$$...$$ dollar quoting)
- `AS 'obj_file', 'link_symbol'` — C language object file linking
- `sql_body` — SQL-only native block syntax:
  ```sql
  BEGIN ATOMIC
    statement;
    statement;
    ...
  END
  ```

---

## CREATE PROCEDURE

### Synopsis

```sql
CREATE [ OR REPLACE ] PROCEDURE
    name ( [ [ argmode ] [ argname ] argtype [ { DEFAULT | = } default_expr ] [, ...] ] )
  { LANGUAGE lang_name
    | TRANSFORM { FOR TYPE type_name } [, ... ]
    | [ EXTERNAL ] SECURITY INVOKER | [ EXTERNAL ] SECURITY DEFINER
    | SET configuration_parameter { TO value | = value | FROM CURRENT }
    | AS 'definition'
    | AS 'obj_file', 'link_symbol'
    | sql_body
  } ...
```

### Schema-Defining Clauses

**Parameters:**
- `IN` (default) — input parameter
- `OUT` — output parameter (returned in CALL return tuple)
- `INOUT` — input/output parameter
- `VARIADIC` — variadic array argument
- `DEFAULT | =` — default expression (all subsequent params must also have defaults)

**Language:**
```
LANGUAGE lang_name    -- sql (default if sql_body used) | c | internal | plpgsql | etc.
```

**Procedure Body:**
- `AS 'definition'` — string literal (supports `$$...$$ dollar quoting)
- `AS 'obj_file', 'link_symbol'` — C language object file linking
- `sql_body` — SQL-only native block syntax:
  ```sql
  BEGIN ATOMIC
    statement;
    statement;
    ...
  END
  ```

**Security Context:**
- `SECURITY INVOKER` (default) — executes with caller's privileges
- `SECURITY DEFINER` — executes with owner's privileges; **cannot contain transaction control**

**Configuration:**
- `SET parameter { TO value | = value | FROM CURRENT }` — set configuration parameter for procedure duration

---

## CREATE AGGREGATE

### Synopsis

```sql
CREATE [ OR REPLACE ] AGGREGATE name ( [ argmode ] [ argname ] arg_data_type [ , ... ] ) (
    SFUNC = sfunc,
    STYPE = state_data_type
    [ , SSPACE = state_data_size ]
    [ , FINALFUNC = ffunc ]
    [ , FINALFUNC_EXTRA ]
    [ , FINALFUNC_MODIFY = { READ_ONLY | SHAREABLE | READ_WRITE } ]
    [ , COMBINEFUNC = combinefunc ]
    [ , SERIALFUNC = serialfunc ]
    [ , DESERIALFUNC = deserialfunc ]
    [ , INITCOND = initial_condition ]
    [ , MSFUNC = msfunc ]
    [ , MINVFUNC = minvfunc ]
    [ , MSTYPE = mstate_data_type ]
    [ , MSSPACE = mstate_data_size ]
    [ , MFINALFUNC = mffunc ]
    [ , MFINALFUNC_EXTRA ]
    [ , MFINALFUNC_MODIFY = { READ_ONLY | SHAREABLE | READ_WRITE } ]
    [ , MINITCOND = minitial_condition ]
    [ , SORTOP = sort_operator ]
    [ , PARALLEL = { SAFE | RESTRICTED | UNSAFE } ]
)
```

### Schema-Defining Clauses

**Core Aggregate State (required):**
- `SFUNC = sfunc` — state transition function (called for each input row)
- `STYPE = state_data_type` — aggregate state value data type

**Final Computation:**
- `FINALFUNC = ffunc` — final function to compute aggregate result from state
- `FINALFUNC_EXTRA` — pass internal aggregate state metadata to finalfunc
- `FINALFUNC_MODIFY = { READ_ONLY | SHAREABLE | READ_WRITE }` — finalfunc side effects declaration

**Initialization:**
- `INITCOND = initial_condition` — initial state value (default NULL)
- `SSPACE = state_data_size` — expected size of state data (bytes, for planner)

**Moving Aggregate Support (for window functions and frame exclusions):**
- `MSFUNC = msfunc` — forward state transition function (moving-aggregate mode)
- `MINVFUNC = minvfunc` — inverse state transition function (remove rows from aggregate)
- `MSTYPE = mstate_data_type` — state data type for moving-aggregate mode
- `MSSPACE = mstate_data_size` — expected size of moving-aggregate state
- `MFINALFUNC = mffunc` — final function for moving-aggregate mode
- `MFINALFUNC_EXTRA` — pass internal state metadata to mfinalfunc
- `MFINALFUNC_MODIFY = { READ_ONLY | SHAREABLE | READ_WRITE }` — mfinalfunc side effects
- `MINITCOND = minitial_condition` — initial state value for moving-aggregate mode

**Distributed Aggregation (for parallel query execution):**
- `COMBINEFUNC = combinefunc` — combine partial states (partial aggregate results)
- `SERIALFUNC = serialfunc` — serialize state for transmission (parallel execution)
- `DESERIALFUNC = deserialfunc` — deserialize state after transmission
- `PARALLEL = { SAFE | RESTRICTED | UNSAFE }` — parallelization safety

**Sorting Support:**
- `SORTOP = sort_operator` — sort operator for MIN/MAX-like aggregates (e.g., `<` for MIN, `>` for MAX)

---

## Common Patterns

### Immutable SQL Function (Deterministic)

```sql
CREATE OR REPLACE FUNCTION compute_tax(amount numeric, rate numeric)
RETURNS numeric
LANGUAGE SQL
IMMUTABLE
AS $$
  SELECT amount * rate;
$$;
```

### State-Modifying Procedure

```sql
CREATE OR REPLACE PROCEDURE transfer_funds(
  IN from_account integer,
  IN to_account integer,
  IN amount numeric
)
LANGUAGE SQL
AS $$
  UPDATE accounts SET balance = balance - amount WHERE id = from_account;
  UPDATE accounts SET balance = balance + amount WHERE id = to_account;
$$;
```

### Aggregate with State and Final Function

```sql
CREATE OR REPLACE AGGREGATE sum_product (numeric, numeric) (
  SFUNC = sum_product_sfunc,
  STYPE = numeric,
  INITCOND = 0,
  FINALFUNC = sum_product_final
);
```
