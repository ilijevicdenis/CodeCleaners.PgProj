# CREATE TYPE / CREATE DOMAIN

## CREATE TYPE

### Synopsis

```sql
-- Composite Type
CREATE TYPE name AS
    ( [ attribute_name data_type [ COLLATE collation ] [, ... ] ] )

-- Enumerated Type
CREATE TYPE name AS ENUM
    ( [ 'label' [, ... ] ] )

-- Range Type
CREATE TYPE name AS RANGE (
    SUBTYPE = subtype
    [ , SUBTYPE_OPCLASS = subtype_operator_class ]
    [ , COLLATION = collation ]
    [ , CANONICAL = canonical_function ]
    [ , SUBTYPE_DIFF = subtype_diff_function ]
    [ , MULTIRANGE_TYPE_NAME = multirange_type_name ]
)

-- Base Type (Scalar)
CREATE TYPE name (
    INPUT = input_function,
    OUTPUT = output_function
    [ , RECEIVE = receive_function ]
    [ , SEND = send_function ]
    [ , TYPMOD_IN = type_modifier_input_function ]
    [ , TYPMOD_OUT = type_modifier_output_function ]
    [ , ANALYZE = analyze_function ]
    [ , SUBSCRIPT = subscript_function ]
    [ , INTERNALLENGTH = { internallength | VARIABLE } ]
    [ , PASSEDBYVALUE ]
    [ , ALIGNMENT = alignment ]
    [ , STORAGE = storage ]
    [ , LIKE = like_type ]
    [ , CATEGORY = category ]
    [ , PREFERRED = preferred ]
    [ , DEFAULT = default ]
    [ , ELEMENT = element ]
    [ , DELIMITER = delimiter ]
    [ , COLLATABLE = collatable ]
)

-- Shell Type (placeholder)
CREATE TYPE name
```

### Schema-Defining Clauses

#### Composite Type
- **Attributes:** `attribute_name data_type` (quoted identifier for reserved words)
- **Collation:** Optional per-attribute `COLLATE collation`
- **Example:** `CREATE TYPE compfoo AS (f1 int, f2 text, f3 timestamp)`

#### Enumerated Type (ENUM)
- **Labels:** Quoted string values (< 64 bytes each, ordered as declared)
- **Example:** `CREATE TYPE bug_status AS ENUM ('new', 'open', 'closed')`

#### Range Type
- **SUBTYPE** (required): Element type with b-tree operator class
- **SUBTYPE_OPCLASS:** Non-default b-tree ordering
- **COLLATION:** For collatable subtypes
- **CANONICAL:** Function to convert ranges to canonical form
- **SUBTYPE_DIFF:** Difference function for range optimization
- **MULTIRANGE_TYPE_NAME:** Custom multirange type name (auto-generated if omitted)
- **Example:** `CREATE TYPE float8_range AS RANGE (subtype = float8, subtype_diff = float8mi)`

#### Base Type (Scalar)
- **INPUT** (required): Function converting external text to internal form
- **OUTPUT** (required): Function converting internal form to external text
- **RECEIVE:** Optional binary input function
- **SEND:** Optional binary output function
- **TYPMOD_IN/TYPMOD_OUT:** Type modifier functions (e.g., `varchar(n)`)
- **ANALYZE:** Custom statistics function
- **SUBSCRIPT:** Array subscript handler function
- **INTERNALLENGTH:** Byte size or `VARIABLE` (required for non-`PASSEDBYVALUE` types)
- **PASSEDBYVALUE:** Type fits in single `Datum` (usually for small fixed-size types)
- **ALIGNMENT:** `char`, `int2`, `int4`, or `double` (default: `int4`)
- **STORAGE:** `plain`, `external`, `extended`, or `main` (default: `plain`)
- **LIKE:** Copy properties from existing type
- **CATEGORY:** Single ASCII character (default: `'U'`)
- **PREFERRED:** Boolean; mark as preferred in category
- **DEFAULT:** Default value for type
- **ELEMENT:** Element type for pseudo-arrays
- **DELIMITER:** Multi-element delimiter (for arrays)
- **COLLATABLE:** Boolean; type supports collation

#### Shell Type
- **Purpose:** Placeholder for forward references; allows defining I/O functions before type creation
- **Use:** Required for recursive types or base types with function dependencies
- **Example:** `CREATE TYPE box;` then define functions, then `CREATE TYPE box (...)`

---

## CREATE DOMAIN

### Synopsis

```sql
CREATE DOMAIN name [ AS ] data_type
    [ COLLATE collation ]
    [ DEFAULT expression ]
    [ domain_constraint [ ... ] ]

where domain_constraint is:

[ CONSTRAINT constraint_name ]
{ NOT NULL | NULL | CHECK (expression) }
```

### Schema-Defining Clauses

| Clause | Purpose |
|--------|---------|
| **Base Type** (`data_type`) | Underlying type (required); can include array specifiers |
| **COLLATE** | Optional collation; inherits from base type if omitted |
| **DEFAULT** (`expression`) | Default value (variable-free); overrides underlying type default; overridden by column-level defaults |
| **NOT NULL** | Reject null values (PostgreSQL extension; best practice: use column-level NOT NULL) |
| **NULL** | Allow null values (default) |
| **CHECK** (`expression`) | Boolean expression; uses `VALUE` keyword for tested value; no subqueries; constraints checked in alphabetical order by name |

---

## Notes

- **CREATE TYPE ... CASCADE:** No `OR REPLACE` or `IF NOT EXISTS` for composite/enum/range types. To evolve a composite type, drop dependent functions first, then `DROP TYPE ... CASCADE`, then recreate.
- **Domain constraints:** Checked when values are cast to the domain type.
- **CHECK expressions:** Assumed immutable; cannot reference other columns or contain subqueries.
- **Composite type attributes:** Must list all fields; reordering or removal requires dropping the type and dependents.
- **ENUM labels:** Immutable once created; new values added with `ALTER TYPE ... ADD VALUE`.
