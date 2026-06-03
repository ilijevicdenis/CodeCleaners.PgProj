# CREATE POLICY / COMMENT ON

## CREATE POLICY

### Synopsis

```sql
CREATE POLICY name ON table_name
    [ AS { PERMISSIVE | RESTRICTIVE } ]
    [ FOR { ALL | SELECT | INSERT | UPDATE | DELETE } ]
    [ TO { role_name | PUBLIC | CURRENT_ROLE | CURRENT_USER | SESSION_USER } [, ...] ]
    [ USING ( using_expression ) ]
    [ WITH CHECK ( check_expression ) ]
```

### Schema-Defining Clauses

- **AS**: Policy type; default `PERMISSIVE`. `RESTRICTIVE` policies are combined with AND (reductive), `PERMISSIVE` with OR (additive).
- **FOR**: Applicable command; default `ALL`. Values: `SELECT`, `INSERT`, `UPDATE`, `DELETE`.
- **TO**: Target roles; default `PUBLIC`. Comma-separated: `role_name`, `PUBLIC`, `CURRENT_ROLE`, `CURRENT_USER`, `SESSION_USER`. Role is admin-side but policy object is schema.
- **USING**: Expression filtering existing rows (SELECT, UPDATE, DELETE). Boolean SQL; no aggregates/window functions.
- **WITH CHECK**: Expression validating new/modified rows (INSERT, UPDATE). Boolean SQL; no aggregates/window functions. For `ALL` and `UPDATE`, if omitted, `USING` applies to both.

---

## COMMENT ON

### Synopsis

```sql
COMMENT ON
{
  ACCESS METHOD object_name |
  AGGREGATE aggregate_name ( aggregate_signature ) |
  CAST (source_type AS target_type) |
  COLLATION object_name |
  COLUMN relation_name.column_name |
  CONSTRAINT constraint_name ON table_name |
  CONSTRAINT constraint_name ON DOMAIN domain_name |
  CONVERSION object_name |
  DATABASE object_name |
  DOMAIN object_name |
  EXTENSION object_name |
  EVENT TRIGGER object_name |
  FOREIGN DATA WRAPPER object_name |
  FOREIGN TABLE object_name |
  FUNCTION function_name [ ( [ [ argmode ] [ argname ] argtype [, ...] ] ) ] |
  INDEX object_name |
  LARGE OBJECT large_object_oid |
  MATERIALIZED VIEW object_name |
  OPERATOR operator_name (left_type, right_type) |
  OPERATOR CLASS object_name USING index_method |
  OPERATOR FAMILY object_name USING index_method |
  POLICY policy_name ON table_name |
  [ PROCEDURAL ] LANGUAGE object_name |
  PROCEDURE procedure_name [ ( [ [ argmode ] [ argname ] argtype [, ...] ] ) ] |
  PUBLICATION object_name |
  ROLE object_name |
  ROUTINE routine_name [ ( [ [ argmode ] [ argname ] argtype [, ...] ] ) ] |
  RULE rule_name ON table_name |
  SCHEMA object_name |
  SEQUENCE object_name |
  SERVER object_name |
  STATISTICS object_name |
  SUBSCRIPTION object_name |
  TABLE object_name |
  TABLESPACE object_name |
  TEXT SEARCH CONFIGURATION object_name |
  TEXT SEARCH DICTIONARY object_name |
  TEXT SEARCH PARSER object_name |
  TEXT SEARCH TEMPLATE object_name |
  TRANSFORM FOR type_name LANGUAGE lang_name |
  TRIGGER trigger_name ON table_name |
  TYPE object_name |
  VIEW object_name
} IS { string_literal | NULL }
```

### Object Kinds (48 total)

Access Method, Aggregate, Cast, Collation, Column, Constraint (table), Constraint (domain), Conversion, Database, Domain, Extension, Event Trigger, Foreign Data Wrapper, Foreign Table, Function, Index, Large Object, Materialized View, Operator, Operator Class, Operator Family, Policy, Language, Procedure, Publication, Role, Routine, Rule, Schema, Sequence, Server, Statistics, Subscription, Table, Tablespace, Text Search Configuration, Text Search Dictionary, Text Search Parser, Text Search Template, Transform, Trigger, Type, View.

### Key Points

- **Schema metadata**: comment text is stored in the database as object documentation, retrievable via `obj_description()`, `col_description()`, `shobj_description()`, or psql `\d` commands.
- **Single per object**: new COMMENT statements replace existing ones; use `NULL` or `''` to remove.
- **No security**: all connected users can view comments.
