# CREATE COLLATION / CAST / CONVERSION / OPERATOR / OPERATOR CLASS / FAMILY

## CREATE COLLATION

### Synopsis
```
CREATE COLLATION [ IF NOT EXISTS ] name (
    [ LOCALE = locale, ]
    [ LC_COLLATE = lc_collate, ]
    [ LC_CTYPE = lc_ctype, ]
    [ PROVIDER = provider, ]
    [ DETERMINISTIC = boolean, ]
    [ RULES = rules, ]
    [ VERSION = version ]
)
CREATE COLLATION [ IF NOT EXISTS ] name FROM existing_collation
```

### Schema-Defining Clauses
- **LOCALE / LC_COLLATE / LC_CTYPE** — Operating system locale settings; `LOCALE` is a shortcut for both (libc only) and cannot combine with individual LC_* parameters.
- **PROVIDER** — Locale services provider: `libc` (default), `icu`, or `builtin`.
- **DETERMINISTIC** — Comparison behavior (default: true); when false (ICU only), enables case/accent-insensitive comparisons.

---

## CREATE CAST

### Synopsis
```
CREATE CAST (source_type AS target_type)
    WITH FUNCTION function_name [ (argument_type [, ...]) ]
    [ AS ASSIGNMENT | AS IMPLICIT ]

CREATE CAST (source_type AS target_type)
    WITHOUT FUNCTION
    [ AS ASSIGNMENT | AS IMPLICIT ]

CREATE CAST (source_type AS target_type)
    WITH INOUT
    [ AS ASSIGNMENT | AS IMPLICIT ]
```

### Schema-Defining Clauses
- **WITH FUNCTION** — Conversion function that performs the cast; function result type must match target, first argument must match/coerce from source.
- **WITHOUT FUNCTION** — Source and target types are binary-coercible (same internal representation).
- **WITH INOUT** — I/O conversion via source type's output function piped to target type's input function.

---

## CREATE CONVERSION

### Synopsis
```
CREATE [ DEFAULT ] CONVERSION name
    FOR source_encoding TO dest_encoding FROM function_name
```

### Schema-Defining Clauses
- **DEFAULT** — Marks as default for this source-to-destination encoding pair; only one default per pair per schema.
- **FOR source_encoding TO dest_encoding** — Directional encoding transformation; neither can be `SQL_ASCII`.

---

## CREATE OPERATOR

### Synopsis
```
CREATE OPERATOR name (
    {FUNCTION|PROCEDURE} = function_name
    [, LEFTARG = left_type ] [, RIGHTARG = right_type ]
    [, COMMUTATOR = com_op ] [, NEGATOR = neg_op ]
    [, RESTRICT = res_proc ] [, JOIN = join_proc ]
    [, HASHES ] [, MERGES ]
)
```

### Schema-Defining Clauses
- **FUNCTION/PROCEDURE** — Underlying function implementing the operator (must be predefined).
- **LEFTARG & RIGHTARG** — Operand types; both required for binary, only RIGHTARG for prefix.
- **COMMUTATOR & NEGATOR** — Optimization links to commutative/negation pairs; can be defined later via ALTER OPERATOR if circular.

---

## CREATE OPERATOR CLASS

### Synopsis
```
CREATE OPERATOR CLASS name [ DEFAULT ] FOR TYPE data_type
  USING index_method [ FAMILY family_name ] AS
  {  OPERATOR strategy_number operator_name [ ( op_type, op_type ) ] [ FOR SEARCH | FOR ORDER BY sort_family_name ]
   | FUNCTION support_number [ ( op_type [, op_type ] ) ] function_name ( argument_type [, ...] )
   | STORAGE storage_type
  } [, ... ]
```

### Schema-Defining Clauses
- **OPERATOR clause** — Associates operators with strategy numbers; supports `FOR SEARCH` (default) or `FOR ORDER BY` with sort family reference.
- **FUNCTION clause** — Support functions with support numbers; enables cross-data-type comparisons when op_type differs from class type.
- **STORAGE clause** — Index storage type (GiST/GIN/SP-GiST/BRIN only); supports `anyarray`/`anyelement` polymorphism.

---

## CREATE OPERATOR FAMILY

### Synopsis
```
CREATE OPERATOR FAMILY name USING index_method
```

### Schema-Defining Clauses
- **name** — Family identifier, optionally schema-qualified; multiple families can share names across different index methods.
- **index_method** — Target access method (`btree`, `hash`, `gist`, `gin`, `brin`).
- **Initial State** — Created empty; must be populated via `CREATE OPERATOR CLASS` and `ALTER OPERATOR FAMILY`.
