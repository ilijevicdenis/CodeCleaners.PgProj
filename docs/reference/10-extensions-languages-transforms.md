# CREATE EXTENSION / LANGUAGE / TRANSFORM

PostgreSQL schema-defining language extensions, procedural languages, and type transformations.

---

## CREATE EXTENSION

**Synopsis:**
```sql
CREATE EXTENSION [ IF NOT EXISTS ] extension_name
    [ WITH ] [ SCHEMA schema_name ]
             [ VERSION version ]
             [ CASCADE ]
```

**Schema-defining clauses:**

- **IF NOT EXISTS** — Do not throw error if extension already exists; issue notice instead.
- **SCHEMA** `schema_name` — Install extension's objects into specified schema (must exist); overridden by control file's `schema` parameter unless `CASCADE` is also given.
- **VERSION** `version` — Install specific extension version (identifier or string literal); default is latest.
- **CASCADE** — Automatically install extension dependencies recursively; applies `SCHEMA` to all cascaded extensions.

---

## CREATE LANGUAGE

**Synopsis:**
```sql
CREATE [ OR REPLACE ] [ TRUSTED ] [ PROCEDURAL ] LANGUAGE name
    HANDLER call_handler [ INLINE inline_handler ] [ VALIDATOR valfunction ]
```

**Schema-defining clauses:**

- **TRUSTED** — Language does not grant access beyond user's existing privileges; omitted restricts use to superusers only.
- **HANDLER** `call_handler` — *Required*. C function (version 1 convention, no arguments, returns `language_handler`) that executes the language's functions.
- **INLINE** `inline_handler` — *Optional*. Function to execute anonymous code blocks (`DO` command); takes one `internal` argument, returns `void`.
- **VALIDATOR** `valfunction` — *Optional*. Function to validate newly created language functions; takes one `oid` argument (function OID), returns `void`.

---

## CREATE TRANSFORM

**Synopsis:**
```sql
CREATE [ OR REPLACE ] TRANSFORM FOR type_name LANGUAGE lang_name (
    FROM SQL WITH FUNCTION from_sql_function_name [ (argument_type [, ...]) ],
    TO SQL WITH FUNCTION to_sql_function_name [ (argument_type [, ...]) ]
);
```

**Schema-defining clauses:**

- **FOR** `type_name LANGUAGE` `lang_name` — Specifies data type and procedural language for which transform applies.
- **FROM SQL WITH FUNCTION** `from_sql_function_name` — Converts type from SQL → language environment; function signature `(internal) → internal`; optional.
- **TO SQL WITH FUNCTION** `to_sql_function_name` — Converts type from language → SQL environment; function signature `(internal) → target_type`; optional.
- **OR REPLACE** — Update existing transform without manual deletion.
