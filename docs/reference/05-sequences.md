# CREATE SEQUENCE

## Synopsis

```sql
CREATE [ { TEMPORARY | TEMP } | UNLOGGED ] SEQUENCE [ IF NOT EXISTS ] name
    [ AS data_type ]
    [ INCREMENT [ BY ] increment ]
    [ MINVALUE minvalue | NO MINVALUE ] [ MAXVALUE maxvalue | NO MAXVALUE ]
    [ [ NO ] CYCLE ]
    [ START [ WITH ] start ]
    [ CACHE cache ]
    [ OWNED BY { table_name.column_name | NONE } ]
```

## Schema-Defining Clauses

- **AS _data_type_** — Specifies sequence data type: `smallint`, `integer`, or `bigint` (default: `bigint`). Determines default min/max values.
- **INCREMENT [ BY ] _increment_** — Value added to current sequence to create new value. Positive = ascending, negative = descending. Default: 1
- **MINVALUE _minvalue_ | NO MINVALUE** — Sets minimum value sequence can generate. Default: 1 for ascending, minimum data type value for descending.
- **MAXVALUE _maxvalue_ | NO MAXVALUE** — Sets maximum value sequence can generate. Default: maximum data type value for ascending, -1 for descending.
- **[ NO ] CYCLE** — Allows sequence to wrap around when limits reached. `NO CYCLE` (default) returns error at limit.
- **START [ WITH ] _start_** — Initial sequence value. Default: `minvalue` for ascending, `maxvalue` for descending.
- **CACHE _cache_** — Number of sequence values to preallocate in memory. Minimum: 1 (default, no caching).
- **OWNED BY _table_name_._column_name_ | NONE** — Associates sequence with table column; drops sequence if column/table dropped. Default: `NONE` (no association).
