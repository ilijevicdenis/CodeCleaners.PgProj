# CREATE TEXT SEARCH CONFIGURATION / DICTIONARY / PARSER / TEMPLATE

## CREATE TEXT SEARCH CONFIGURATION

**Synopsis:**
```sql
CREATE TEXT SEARCH CONFIGURATION name (
    PARSER = parser_name |
    COPY = source_config
)
```

**Schema-defining clauses:**
- **PARSER** — specifies the text search parser; configuration initially has no token-to-dictionary mappings (requires `ALTER TEXT SEARCH CONFIGURATION` to become useful).
- **COPY** — copies an existing configuration's parser and mappings.

---

## CREATE TEXT SEARCH DICTIONARY

**Synopsis:**
```sql
CREATE TEXT SEARCH DICTIONARY name (
    TEMPLATE = template
    [, option = value [, ... ]]
)
```

**Schema-defining clauses:**
- **TEMPLATE** — the template that defines the dictionary's behavior (required).
- **option = value** — template-specific options controlling detailed behavior (any order).

---

## CREATE TEXT SEARCH PARSER

**Synopsis:**
```sql
CREATE TEXT SEARCH PARSER name (
    START = start_function ,
    GETTOKEN = gettoken_function ,
    END = end_function ,
    LEXTYPES = lextypes_function
    [, HEADLINE = headline_function ]
)
```

**Schema-defining functions:**
- **START**, **GETTOKEN**, **END**, **LEXTYPES** — required; define core parsing pipeline and token type reporting.
- **HEADLINE** — optional; summarizes token sets.

---

## CREATE TEXT SEARCH TEMPLATE

**Synopsis:**
```sql
CREATE TEXT SEARCH TEMPLATE name (
    [ INIT = init_function , ]
    LEXIZE = lexize_function
)
```

**Schema-defining functions:**
- **INIT** — optional; initializes dictionary state.
- **LEXIZE** — required; performs tokenization/normalization.
