# CREATE TRIGGER / EVENT TRIGGER / RULE

## CREATE TRIGGER

### Synopsis

```sql
CREATE [ OR REPLACE ] [ CONSTRAINT ] TRIGGER name { BEFORE | AFTER | INSTEAD OF } { event [ OR ... ] }
    ON table_name
    [ FROM referenced_table_name ]
    [ NOT DEFERRABLE | [ DEFERRABLE ] [ INITIALLY IMMEDIATE | INITIALLY DEFERRED ] ]
    [ REFERENCING { { OLD | NEW } TABLE [ AS ] transition_relation_name } [ ... ] ]
    [ FOR [ EACH ] { ROW | STATEMENT } ]
    [ WHEN ( condition ) ]
    EXECUTE { FUNCTION | PROCEDURE } function_name ( arguments )

where event can be one of:
    INSERT
    UPDATE [ OF column_name [, ... ] ]
    DELETE
    TRUNCATE
```

### Schema-Defining Clauses

- **Timing**: `BEFORE`, `AFTER`, `INSTEAD OF` (views only)
- **Events**: `INSERT`, `UPDATE [ OF column_name [, ...] ]`, `DELETE`, `TRUNCATE`
- **Granularity**: `FOR EACH ROW` (per-row), `FOR EACH STATEMENT` (per-statement, default)
- **Condition**: `WHEN ( condition )` — fires trigger only if condition is true
  - Row-level: can reference `OLD.column_name`, `NEW.column_name`
  - Not supported on `INSTEAD OF` triggers
- **Action**: `EXECUTE { FUNCTION | PROCEDURE } function_name ( arguments )`
- **Constraint Trigger Options**:
  - `DEFERRABLE` / `NOT DEFERRABLE` — whether constraint check can be deferred
  - `INITIALLY IMMEDIATE` (fires immediately, default)
  - `INITIALLY DEFERRED` (fires at transaction end)
  - Only valid with `CONSTRAINT` keyword
- **Transition Relations** (AFTER triggers on plain tables only):
  - `REFERENCING OLD TABLE [ AS ] transition_relation_name` — before-images
  - `REFERENCING NEW TABLE [ AS ] transition_relation_name` — after-images

---

## CREATE EVENT TRIGGER

### Synopsis

```sql
CREATE EVENT TRIGGER name
    ON event
    [ WHEN filter_variable IN (filter_value [, ... ]) [ AND ... ] ]
    EXECUTE { FUNCTION | PROCEDURE } function_name()
```

### Schema-Defining Clauses

- **ON event** — database event triggering the function (e.g., `ddl_command_start`, `ddl_command_end`, `sql_drop`)
- **WHEN filter_variable IN (filter_value [, ...])** — optional tag-based filter
  - Only `TAG` supported as `filter_variable`
  - `filter_value` is command tag list (e.g., `'DROP FUNCTION'`, `'CREATE TABLE'`)
  - Multiple AND conditions allowed
- **EXECUTE { FUNCTION | PROCEDURE } function_name()** — must take no arguments, return `event_trigger`

---

## CREATE RULE

### Synopsis

```sql
CREATE [ OR REPLACE ] RULE name AS ON event
    TO table_name [ WHERE condition ]
    DO [ ALSO | INSTEAD ] { NOTHING | command | ( command ; command ... ) }

where event can be one of:
    SELECT | INSERT | UPDATE | DELETE
```

### Schema-Defining Clauses

- **ON event** — `SELECT`, `INSERT`, `UPDATE`, or `DELETE`
- **TO table_name** — table or view the rule applies to
- **WHERE condition** — optional; limits rule application
- **DO [ALSO | INSTEAD]** — execution mode
  - `ALSO` (default if omitted): rule executes in addition to original command
  - `INSTEAD`: rule replaces original command
- **Action**: `NOTHING`, single command, or multiple commands in `( command ; command ... )`
  - Valid commands: `SELECT`, `INSERT`, `UPDATE`, `DELETE`, `NOTIFY`
  - `NEW` and `OLD` table names available in conditions and actions
- **SELECT Rules** (views only):
  - Must be named `_RETURN`
  - Must be unconditional `INSTEAD` with single `SELECT` command
