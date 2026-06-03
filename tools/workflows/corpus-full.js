export const meta = {
  name: 'corpus-full',
  description: 'Fan out agents to generate the full PostgreSQL 18 language test corpus (oracle-verified JSONL)',
  phases: [{ title: 'Generate', detail: 'one agent per (category,batch); each writes + oracle-verifies its .jsonl' }],
}

// ---------------------------------------------------------------------------
// Taxonomy of the PostgreSQL 18 "server programming" language surface.
// Each entry: { cat, ref, prefix, target, scope, skip? }
//   cat    - category key (also the .jsonl filename stem)
//   ref    - PG18 doc slug (postgresql.org/docs/18/<ref>.html)
//   prefix - globally-unique 2-5 char id prefix base (batch letter is appended)
//   target - total cases for the category (split into ~PER_BATCH-case files)
//   scope  - the grammar the agent must exhaustively walk
//   skip   - batches already produced by the pilot (start numbering after them)
// ---------------------------------------------------------------------------
const PER_BATCH = 170

const TAXONOMY = [
  // ---- DDL ----------------------------------------------------------------
  { cat: 'create-schema', ref: 'sql-createschema', prefix: 'scm', target: 120,
    scope: 'CREATE SCHEMA name / AUTHORIZATION / IF NOT EXISTS / schema element list (table+view inline), ALTER SCHEMA RENAME/OWNER, DROP SCHEMA CASCADE/RESTRICT/IF EXISTS. Negative: missing name, bad AUTHORIZATION.' },
  { cat: 'create-table-columns', ref: 'sql-createtable', prefix: 'ctcol', target: 450,
    scope: 'column definitions for EVERY built-in type incl modifiers (numeric(p,s), varchar(n), timestamp(p) with/without tz, interval fields, bit(n), arrays t[]/t[][]/t ARRAY[n]), DEFAULT exprs, NOT NULL/NULL, COLLATE, GENERATED ALWAYS AS (expr) STORED, GENERATED {ALWAYS|BY DEFAULT} AS IDENTITY (options), serial/bigserial/smallserial, STORAGE/COMPRESSION, column constraints inline. Negative: bad type modifier, duplicate clauses, trailing comma.' },
  { cat: 'create-table-constraints', ref: 'sql-createtable', prefix: 'ctc', target: 450, skip: 1,
    scope: 'PRIMARY KEY (inline/table/composite/named/INCLUDE), UNIQUE (inline/table/NULLS [NOT] DISTINCT/INCLUDE), FOREIGN KEY (REFERENCES, ON DELETE/UPDATE actions incl SET NULL(cols), MATCH FULL/PARTIAL/SIMPLE, multi-column), CHECK (column + named table-level, NO INHERIT), EXCLUDE USING gist WITH, DEFERRABLE INITIALLY DEFERRED/IMMEDIATE, NOT VALID. Negative: missing parens, bad action keyword, illegal combos.' },
  { cat: 'create-table-advanced', ref: 'sql-createtable', prefix: 'cta', target: 450,
    scope: 'PARTITION BY RANGE/LIST/HASH, PARTITION OF ... FOR VALUES FROM/TO/IN/WITH(modulus,remainder)/DEFAULT, INHERITS, OF composite_type, LIKE source INCLUDING/EXCLUDING (ALL/DEFAULTS/CONSTRAINTS/INDEXES/IDENTITY/GENERATED/STORAGE/COMMENTS), WITH (storage params), WITHOUT OIDS, UNLOGGED, TEMP/TEMPORARY, ON COMMIT, TABLESPACE. Negative: bad partition bound, conflicting clauses.' },
  { cat: 'create-table-as', ref: 'sql-createtableas', prefix: 'ctas', target: 200,
    scope: 'CREATE TABLE AS SELECT, WITH [NO] DATA, column alias list, TEMP/UNLOGGED, WITH storage params, IF NOT EXISTS; SELECT INTO; CREATE TABLE AS EXECUTE prepared. Negative: missing AS, bad data clause.' },
  { cat: 'alter-table', ref: 'sql-altertable', prefix: 'alt', target: 600,
    scope: 'ADD COLUMN [IF NOT EXISTS], DROP COLUMN [IF EXISTS] CASCADE/RESTRICT, ALTER COLUMN TYPE ... USING/COLLATE, SET/DROP DEFAULT, SET/DROP NOT NULL, ADD/DROP/VALIDATE CONSTRAINT, ALTER CONSTRAINT, SET/DROP IDENTITY, ADD GENERATED, SET STATISTICS/STORAGE/COMPRESSION, ENABLE/DISABLE ROW LEVEL SECURITY/FORCE, ENABLE/DISABLE TRIGGER/RULE, CLUSTER ON, SET (storage params)/RESET, INHERIT/NO INHERIT, OF/NOT OF, ATTACH/DETACH PARTITION [CONCURRENTLY], OWNER TO, SET SCHEMA, RENAME COLUMN/CONSTRAINT/TO, SET TABLESPACE, SET LOGGED/UNLOGGED, multiple actions comma-separated, ALTER TABLE ALL IN TABLESPACE, ONLY. Negative: unknown action, missing keyword.' },
  { cat: 'create-index', ref: 'sql-createindex', prefix: 'idx', target: 450,
    scope: 'UNIQUE, methods USING btree/hash/gin/gist/spgist/brin, multi-column, expression index, opclass + opclass params, COLLATE, ASC/DESC, NULLS FIRST/LAST, NULLS [NOT] DISTINCT, INCLUDE (cols), WHERE partial, WITH (storage params incl fillfactor), ONLY, IF NOT EXISTS, named/unnamed, CONCURRENTLY (txn:none), TABLESPACE. Negative: bad method, missing ON, empty column list.' },
  { cat: 'create-view', ref: 'sql-createview', prefix: 'vw', target: 220,
    scope: 'CREATE [OR REPLACE] VIEW, column name list, WITH (security_barrier/security_invoker/check_option), WITH [CASCADED|LOCAL] CHECK OPTION, TEMP, RECURSIVE view (col list + UNION), complex SELECT bodies, ALTER VIEW (SET DEFAULT/OWNER/RENAME/SET schema/SET options). Negative: missing AS, bad option.' },
  { cat: 'create-matview', ref: 'sql-creatematerializedview', prefix: 'mvw', target: 160,
    scope: 'CREATE MATERIALIZED VIEW [IF NOT EXISTS], column list, USING method, WITH (storage), TABLESPACE, WITH [NO] DATA, REFRESH MATERIALIZED VIEW [CONCURRENTLY] (txn:none for CONCURRENTLY), ALTER MATERIALIZED VIEW, DROP. Negative: bad data clause.' },
  { cat: 'create-sequence', ref: 'sql-createsequence', prefix: 'seq', target: 200,
    scope: 'AS type, INCREMENT [BY], MINVALUE/NO MINVALUE, MAXVALUE/NO MAXVALUE, START [WITH], CACHE, [NO] CYCLE, OWNED BY col/NONE, TEMP, IF NOT EXISTS, ALTER SEQUENCE (RESTART [WITH], all options, OWNER, RENAME, SET SCHEMA), nextval/currval/setval calls. Negative: MINVALUE>MAXVALUE syntax-ok but bad keyword, missing value.' },
  { cat: 'create-function', ref: 'sql-createfunction', prefix: 'fn', target: 500,
    scope: 'arg modes IN/OUT/INOUT/VARIADIC, named args, DEFAULT/= defaults, RETURNS scalar/SETOF/TABLE(cols)/void/record/type, LANGUAGE sql/plpgsql/c(skip body)/internal, IMMUTABLE/STABLE/VOLATILE, [NOT] LEAKPROOF, CALLED/RETURNS NULL ON NULL INPUT/STRICT, [EXTERNAL] SECURITY DEFINER/INVOKER, PARALLEL SAFE/RESTRICTED/UNSAFE, COST, ROWS, SUPPORT, SET config FROM CURRENT, TRANSFORM, WINDOW, AS dollar-quoted body, AS obj,link, OR REPLACE, SQL-standard BEGIN ATOMIC ... END body. Negative: missing RETURNS where required, bad option value, unterminated dollar quote.' },
  { cat: 'create-procedure', ref: 'sql-createprocedure', prefix: 'proc', target: 200,
    scope: 'CREATE [OR REPLACE] PROCEDURE, arg modes (IN/INOUT/VARIADIC, no OUT-only return), LANGUAGE sql/plpgsql, SECURITY, SET, AS body, BEGIN ATOMIC, CALL with args incl INOUT, transaction control (COMMIT/ROLLBACK) inside plpgsql body. Negative: RETURNS on procedure, OUT misuse.' },
  { cat: 'create-aggregate', ref: 'sql-createaggregate', prefix: 'agg', target: 200,
    scope: 'SFUNC/STYPE/SSPACE/FINALFUNC/FINALFUNC_EXTRA/FINALFUNC_MODIFY, INITCOND, COMBINEFUNC, SERIALFUNC/DESERIALFUNC, MSFUNC/MINVFUNC/MSTYPE/MSSPACE/MFINALFUNC/MINITCOND (moving-aggregate), SORTOP, PARALLEL, ordered-set aggregate (ORDER BY in args), hypothetical-set (HYPOTHETICAL), variadic, old-syntax. Use existing/builtin functions as sfunc where possible (e.g. int4pl). Negative: missing SFUNC/STYPE.' },
  { cat: 'create-type-enum-composite', ref: 'sql-createtype', prefix: 'tyec', target: 250,
    scope: 'CREATE TYPE name AS ENUM (labels) incl empty enum, CREATE TYPE name AS (attr type [COLLATE], ...), ALTER TYPE ADD VALUE [IF NOT EXISTS] [BEFORE/AFTER] (txn:none for ADD VALUE on used enum), ALTER TYPE RENAME VALUE, ALTER TYPE ADD/DROP/ALTER/RENAME ATTRIBUTE, RENAME TO, SET SCHEMA, OWNER. Negative: duplicate label, bad attribute.' },
  { cat: 'create-type-range-base', ref: 'sql-createtype', prefix: 'tyrb', target: 250,
    scope: 'CREATE TYPE AS RANGE (SUBTYPE, SUBTYPE_OPCLASS, COLLATION, CANONICAL, SUBTYPE_DIFF, MULTIRANGE_TYPE_NAME), CREATE TYPE base type (INPUT, OUTPUT, RECEIVE, SEND, TYPMOD_IN/OUT, ANALYZE, INTERNALLENGTH, PASSEDBYVALUE, ALIGNMENT, STORAGE, LIKE, CATEGORY, PREFERRED, DEFAULT, ELEMENT, DELIMITER, COLLATABLE), shell type (CREATE TYPE name;). Many base-type cases require C functions and will fail to run -> prefer shell type + range types for ok; use base-type forms as expect:error/syntax probes where they reference missing funcs. Negative: missing SUBTYPE.' },
  { cat: 'create-domain', ref: 'sql-createdomain', prefix: 'dom', target: 220,
    scope: 'CREATE DOMAIN AS base [COLLATE], DEFAULT, [NOT] NULL, CHECK (VALUE ...), named CONSTRAINT, multiple constraints, domain over array/composite, ALTER DOMAIN (SET/DROP DEFAULT, SET/DROP NOT NULL, ADD/DROP/VALIDATE/RENAME CONSTRAINT, OWNER, RENAME, SET SCHEMA), DROP DOMAIN. Negative: CHECK without VALUE ref ok? bad base type, missing AS.' },
  { cat: 'create-trigger', ref: 'sql-createtrigger', prefix: 'trg', target: 380,
    scope: 'BEFORE/AFTER/INSTEAD OF, events INSERT/UPDATE [OF cols]/DELETE/TRUNCATE combined with OR, FOR EACH ROW/STATEMENT, WHEN (condition), EXECUTE FUNCTION/PROCEDURE(args), CONSTRAINT trigger (DEFERRABLE INITIALLY..), REFERENCING OLD/NEW TABLE AS, ON table, OR REPLACE. Use fixture s.trg() as the function. Negative: INSTEAD OF on table+FOR EACH STATEMENT illegal combos, missing EXECUTE, bad timing/event combo.' },
  { cat: 'create-event-trigger', ref: 'sql-createeventtrigger', prefix: 'evt', target: 120,
    scope: 'CREATE EVENT TRIGGER ON ddl_command_start/ddl_command_end/sql_drop/table_rewrite, WHEN TAG IN (...), EXECUTE FUNCTION, ALTER EVENT TRIGGER ENABLE/DISABLE/ENABLE REPLICA/ALWAYS/RENAME/OWNER, DROP. event-trigger function must RETURNS event_trigger (create one inline). Negative: bad event name, WHEN on unsupported event.' },
  { cat: 'create-rule', ref: 'sql-createrule', prefix: 'rul', target: 150,
    scope: 'CREATE [OR REPLACE] RULE ON SELECT/INSERT/UPDATE/DELETE TO table [WHERE cond] DO [ALSO|INSTEAD] (NOTHING | command | (multiple commands)), use NEW/OLD, _RETURN rule pattern for views. Negative: missing DO, bad event.' },
  { cat: 'create-policy', ref: 'sql-createpolicy', prefix: 'pol', target: 200,
    scope: 'CREATE POLICY name ON table [AS PERMISSIVE|RESTRICTIVE] [FOR ALL|SELECT|INSERT|UPDATE|DELETE] [TO role/PUBLIC/CURRENT_USER] [USING (expr)] [WITH CHECK (expr)], ALTER POLICY (RENAME, TO roles, USING, WITH CHECK), DROP POLICY. Negative: WITH CHECK on SELECT policy, bad command.' },
  { cat: 'create-cast', ref: 'sql-createcast', prefix: 'cst', target: 130,
    scope: 'CREATE CAST (src AS tgt) WITH FUNCTION fname(args) / WITHOUT FUNCTION / WITH INOUT, AS ASSIGNMENT / AS IMPLICIT, DROP CAST. Use builtin types/functions so it runs (or expect:error for unknown function). Negative: missing AS in (src AS tgt), bad option.' },
  { cat: 'create-operator', ref: 'sql-createoperator', prefix: 'opr', target: 180,
    scope: 'CREATE OPERATOR symbol (FUNCTION/PROCEDURE=, LEFTARG=, RIGHTARG=, COMMUTATOR=, NEGATOR=, RESTRICT=, JOIN=, HASHES, MERGES), unary (prefix) operators, ALTER OPERATOR SET (RESTRICT/JOIN/COMMUTATOR/NEGATOR), DROP OPERATOR name(larg,rarg). Use builtin functions. Negative: missing FUNCTION, no arg types.' },
  { cat: 'create-opclass-family', ref: 'sql-createopclass', prefix: 'opc', target: 200,
    scope: 'CREATE OPERATOR CLASS name [DEFAULT] FOR TYPE t USING method [FAMILY f] AS OPERATOR n op [FOR SEARCH|ORDER BY], FUNCTION n func, STORAGE type; CREATE OPERATOR FAMILY; ALTER OPERATOR FAMILY ADD/DROP OPERATOR/FUNCTION; DROP. Use builtin types/operators. Negative: bad AS member, missing USING.' },
  { cat: 'create-collation', ref: 'sql-createcollation', prefix: 'col', target: 130,
    scope: 'CREATE COLLATION [IF NOT EXISTS] name (LOCALE/LC_COLLATE+LC_CTYPE/PROVIDER libc|icu/DETERMINISTIC/RULES/VERSION), CREATE COLLATION name FROM existing, ALTER COLLATION (REFRESH VERSION/RENAME/OWNER/SET SCHEMA), DROP. Use locale "C" for runnable ok cases. Negative: LOCALE plus LC_COLLATE conflict, missing parens.' },
  { cat: 'create-conversion', ref: 'sql-createconversion', prefix: 'cnv', target: 100,
    scope: 'CREATE [DEFAULT] CONVERSION name FOR src_encoding TO dest_encoding FROM function_name, DROP CONVERSION, ALTER CONVERSION RENAME/OWNER/SET SCHEMA. Use builtin conversion functions (e.g. iso8859_1_to_utf8) for ok. Negative: missing FROM, bad encoding name.' },
  { cat: 'create-textsearch', ref: 'sql-createtsconfig', prefix: 'ts', target: 220,
    scope: 'CREATE TEXT SEARCH CONFIGURATION (PARSER= / COPY=), ALTER ... ADD/ALTER/DROP MAPPING FOR token WITH dict; CREATE TEXT SEARCH DICTIONARY (TEMPLATE=, options); CREATE TEXT SEARCH PARSER (START/GETTOKEN/END/LEXTYPES/HEADLINE); CREATE TEXT SEARCH TEMPLATE (INIT/LEXIZE); DROP each. Use builtin parser "default" / template "simple" for ok. Negative: missing TEMPLATE, bad mapping.' },
  { cat: 'create-extension', ref: 'sql-createextension', prefix: 'ext', target: 120,
    scope: 'CREATE EXTENSION [IF NOT EXISTS] name [WITH] [SCHEMA s] [VERSION v] [CASCADE], ALTER EXTENSION UPDATE [TO v]/SET SCHEMA/ADD member/DROP member, DROP EXTENSION CASCADE. Use a likely-available extension or expect:error for unknown extension (file not found is a clean error). Negative: bad WITH option.' },
  { cat: 'create-language', ref: 'sql-createlanguage', prefix: 'lng', target: 90,
    scope: 'CREATE [OR REPLACE] [TRUSTED] [PROCEDURAL] LANGUAGE name [HANDLER fn [INLINE fn] [VALIDATOR fn]], CREATE LANGUAGE plpgsql (already exists -> IF NOT EXISTS or expect:error), DROP LANGUAGE, ALTER LANGUAGE RENAME/OWNER. Negative: missing HANDLER for new language.' },
  { cat: 'create-transform', ref: 'sql-createtransform', prefix: 'tfm', target: 90,
    scope: 'CREATE [OR REPLACE] TRANSFORM FOR type LANGUAGE lang (FROM SQL WITH FUNCTION f, TO SQL WITH FUNCTION g), DROP TRANSFORM FOR type LANGUAGE lang. Most need real functions -> use expect:error for missing functions, plus syntax-shape positives where feasible. Negative: missing FOR/LANGUAGE.' },
  { cat: 'create-statistics', ref: 'sql-createstatistics', prefix: 'sta', target: 130,
    scope: 'CREATE STATISTICS [IF NOT EXISTS] [name] [ (ndistinct,dependencies,mcv) ] ON col1,col2[,expr] FROM table, expression statistics, ALTER STATISTICS SET STATISTICS/RENAME/OWNER/SET SCHEMA, DROP. Use fixture s.t columns. Negative: single column for multivariate, bad kind.' },
  { cat: 'create-fdw-foreign', ref: 'sql-createforeigntable', prefix: 'fdw', target: 280,
    scope: 'CREATE FOREIGN DATA WRAPPER (HANDLER/NO HANDLER/VALIDATOR/OPTIONS), CREATE SERVER [IF NOT EXISTS] (TYPE/VERSION/FOREIGN DATA WRAPPER/OPTIONS), CREATE USER MAPPING FOR role/PUBLIC/CURRENT_USER SERVER (OPTIONS), CREATE FOREIGN TABLE (cols, SERVER, OPTIONS) incl PARTITION OF, IMPORT FOREIGN SCHEMA, ALTER each, DROP each. Use a self-defined FDW with no handler (CREATE FOREIGN DATA WRAPPER dummy;) so server/foreign-table cases run. Negative: foreign table without SERVER, bad OPTIONS.' },
  { cat: 'create-publication', ref: 'sql-createpublication', prefix: 'pub', target: 180,
    scope: 'CREATE PUBLICATION FOR TABLE t [(cols)] [WHERE (row filter)] / FOR ALL TABLES / FOR TABLES IN SCHEMA s / mixed, WITH (publish=, publish_via_partition_root=), ALTER PUBLICATION ADD/SET/DROP TABLE/ALL TABLES IN SCHEMA/OWNER/RENAME, DROP PUBLICATION; CREATE SUBSCRIPTION name CONNECTION (dsn) PUBLICATION p WITH (connect = false) [must use connect=false so it does not actually connect -> ok], ALTER SUBSCRIPTION, DROP SUBSCRIPTION. Negative: subscription without CONNECTION/PUBLICATION, bad publish value.' },
  { cat: 'comment-on', ref: 'sql-comment', prefix: 'cmt', target: 160,
    scope: 'COMMENT ON {TABLE/COLUMN/VIEW/MATERIALIZED VIEW/INDEX/SEQUENCE/FUNCTION/PROCEDURE/AGGREGATE/TYPE/DOMAIN/SCHEMA/TRIGGER ON/RULE ON/POLICY ON/CONSTRAINT ON/EXTENSION/COLLATION/...} IS \\'text\\' and IS NULL (remove). Use fixture objects. Negative: bad object kind, missing IS.' },
  { cat: 'security-label', ref: 'sql-security-label', prefix: 'sec', target: 80,
    scope: 'SECURITY LABEL [FOR provider] ON object IS \\'label\\' / IS NULL. Without a label provider these error -> most cases expect:error (provider does not exist) which is a clean error; include IS NULL forms and syntax-error negatives. Focus on grammar shapes across object kinds.' },
  { cat: 'drop-statements', ref: 'sql-droptable', prefix: 'drp', target: 400,
    scope: 'DROP {TABLE/VIEW/MATERIALIZED VIEW/INDEX/SEQUENCE/SCHEMA/FUNCTION(argtypes)/PROCEDURE/AGGREGATE(argtypes)/TYPE/DOMAIN/TRIGGER ON/RULE ON/POLICY ON/EXTENSION/...} [IF EXISTS] name[,name] [CASCADE|RESTRICT], DROP FUNCTION with/without arg types for overload resolution, DROP INDEX CONCURRENTLY (txn:none), multiple objects. Use fixture objects so non-IF-EXISTS drops succeed. Negative: missing name, bad combo of CASCADE+RESTRICT.' },
  { cat: 'alter-rename-owner-schema', ref: 'sql-alterobject', prefix: 'aro', target: 280,
    scope: 'ALTER {TABLE/VIEW/SEQUENCE/TYPE/DOMAIN/FUNCTION/AGGREGATE/INDEX/SCHEMA/...} RENAME TO / SET SCHEMA / OWNER TO across object kinds. OWNER TO needs a role -> use CURRENT_USER or session_user (valid). RENAME and SET SCHEMA run cleanly on fixture objects. Negative: missing TO, bad target.' },

  // ---- DML & queries ------------------------------------------------------
  { cat: 'select-basic', ref: 'sql-select', prefix: 'selb', target: 300,
    scope: 'target list with expressions/AS (and no-AS) aliases, t.*, DISTINCT, DISTINCT ON (expr), ALL, FROM single/qualified, WHERE with various predicates, column references, computed columns, SELECT with no FROM, SELECT constant. Wrap as plain SELECT against fixture. Negative: SELECT FROM WHERE, dangling comma, DISTINCT ON without ORDER mismatch is runtime not syntax (skip).' },
  { cat: 'select-joins', ref: 'sql-select', prefix: 'selj', target: 350,
    scope: '[INNER] JOIN ON/USING, LEFT/RIGHT/FULL [OUTER] JOIN, CROSS JOIN, NATURAL joins, multiple joins, self join with aliases, JOIN to subquery, LATERAL join, parenthesized join, qualified column refs. Use s.t/s.t2/s.parent. Negative: JOIN without ON/USING where required, USING with non-list.' },
  { cat: 'select-group-having', ref: 'sql-select', prefix: 'selg', target: 280,
    scope: 'GROUP BY exprs, GROUP BY ALL/DISTINCT, GROUPING SETS, ROLLUP, CUBE, mixed, HAVING, grouping(), aggregates in HAVING, FILTER on aggregates, GROUP BY ordinal. Negative: HAVING without group context is ok actually; aggregate in WHERE -> error.' },
  { cat: 'select-window', ref: 'sql-select', prefix: 'selw', target: 350,
    scope: 'OVER () empty, OVER (PARTITION BY .. ORDER BY ..), named WINDOW w AS (...), reference window, frame ROWS/RANGE/GROUPS BETWEEN ... AND ... (UNBOUNDED PRECEDING/CURRENT ROW/n FOLLOWING), EXCLUDE CURRENT ROW/GROUP/TIES/NO OTHERS, window funcs row_number/rank/dense_rank/percent_rank/cume_dist/ntile/lag/lead/first_value/last_value/nth_value, aggregate as window fn. Negative: frame without ORDER, bad frame bound.' },
  { cat: 'select-cte', ref: 'queries-with', prefix: 'selc', target: 350,
    scope: 'WITH name AS (query), multiple CTEs, WITH RECURSIVE (anchor UNION [ALL] recursive), MATERIALIZED/NOT MATERIALIZED, column alias list, SEARCH BREADTH/DEPTH FIRST BY cols SET col, CYCLE cols SET col [TO/DEFAULT] USING path, data-modifying CTE (WITH x AS (INSERT/UPDATE/DELETE ... RETURNING) SELECT). Negative: recursive without RECURSIVE, missing UNION in recursive.' },
  { cat: 'select-setops', ref: 'sql-select', prefix: 'sels', target: 200,
    scope: 'UNION/UNION ALL, INTERSECT [ALL], EXCEPT [ALL], chaining 3+, parenthesized groupings to control precedence, ORDER BY/LIMIT on the combined result, CORRESPONDING (if supported). Negative: column count mismatch is runtime; bad keyword EXCEPTING.' },
  { cat: 'select-from-advanced', ref: 'sql-select', prefix: 'self', target: 320,
    scope: 'subquery in FROM with alias + column aliases, LATERAL (subquery/function), ROWS FROM (f1(), f2()) [WITH ORDINALITY] AS (...), function call in FROM with column def list, WITH ORDINALITY, TABLESAMPLE SYSTEM/BERNOULLI (pct) REPEATABLE(seed), VALUES (...) AS t(cols), UNNEST(array) [WITH ORDINALITY], generate_series in FROM, ONLY table, table * . Negative: TABLESAMPLE without method, ROWS FROM single bad.' },
  { cat: 'select-lock-fetch', ref: 'sql-select', prefix: 'sellf', target: 250,
    scope: 'FOR UPDATE/FOR NO KEY UPDATE/FOR SHARE/FOR KEY SHARE [OF table] [NOWAIT|SKIP LOCKED], multiple locking clauses, LIMIT n/ALL, OFFSET n [ROW|ROWS], FETCH FIRST/NEXT [n] ROW[S] ONLY / WITH TIES, LIMIT+OFFSET combos. Negative: FETCH without ONLY/WITH TIES, NOWAIT+SKIP LOCKED together.' },
  { cat: 'insert', ref: 'sql-insert', prefix: 'ins', target: 400,
    scope: 'INSERT INTO t [(cols)] VALUES single/multi-row, DEFAULT in values, DEFAULT VALUES, INSERT ... SELECT, INSERT ... query with CTE, OVERRIDING SYSTEM/USER VALUE, ON CONFLICT [(cols)|ON CONSTRAINT name] DO NOTHING / DO UPDATE SET ... [WHERE], excluded.*, RETURNING * / exprs, INSERT with column alias. Use s.t/s.t2. Negative: VALUES count mismatch is runtime; missing INTO, bad ON CONFLICT action.' },
  { cat: 'update', ref: 'sql-update', prefix: 'upd', target: 350,
    scope: 'UPDATE t SET col=expr (multiple), SET (c1,c2)=(v1,v2), SET (c1,c2)=(SELECT ..), SET col=DEFAULT, FROM list, WHERE, WHERE CURRENT OF cursor, RETURNING, sub-selects in SET, ONLY, alias. Use s.t/s.t2. Negative: SET without =, missing SET.' },
  { cat: 'delete', ref: 'sql-delete', prefix: 'del', target: 250,
    scope: 'DELETE FROM t, USING list, WHERE, WHERE CURRENT OF, RETURNING, ONLY, alias, subquery in WHERE, TRUNCATE (TABLE) [ONLY] t [RESTART IDENTITY|CONTINUE IDENTITY] [CASCADE|RESTRICT]. Use s.t/s.t2. Negative: DELETE without FROM, bad USING.' },
  { cat: 'merge', ref: 'sql-merge', prefix: 'mrg', target: 300,
    scope: 'MERGE INTO target [AS t] USING source [AS s] ON join, WHEN MATCHED [AND cond] THEN UPDATE SET../DELETE/DO NOTHING, WHEN NOT MATCHED [BY TARGET] [AND cond] THEN INSERT (cols) VALUES../DEFAULT VALUES/DO NOTHING, WHEN NOT MATCHED BY SOURCE THEN UPDATE/DELETE/DO NOTHING (PG17), multiple WHEN clauses, RETURNING (PG17), USING (subquery)/VALUES. Use s.t/s.t2. Negative: missing ON, WHEN without THEN, INSERT in MATCHED.' },
  { cat: 'values-table-cmd', ref: 'sql-values', prefix: 'val', target: 120,
    scope: 'VALUES (row),(row) standalone, with ORDER BY column1, LIMIT/OFFSET, as a query, typed values, VALUES in INSERT/FROM; TABLE name command, TABLE ONLY. Negative: VALUES with differing row lengths is runtime; bad ORDER BY ref.' },

  // ---- expressions / types / functions ------------------------------------
  { cat: 'literals-quoting', ref: 'sql-syntax-lexical', prefix: 'lit', target: 350,
    scope: "string literals (single quotes, doubled '' escape, adjacent-string concatenation across newline), E'' escape strings (\\n \\t \\uXXXX \\xNN), U&'' unicode with UESCAPE, dollar-quoted $$ $tag$, numeric literals (int, decimal, 1e10, .5, 0x/0o/0b PG16, digit_group 1_000 PG16), boolean true/false, bit-string B'101'/X'1FF', typed literal type 'value' and 'value'::type and CAST, interval literals with fields/precision, ARRAY/row literals, NULL, special chars in identifiers (quoted, U&). Negative: unterminated string/dollar quote, bad escape, malformed number." },
  { cat: 'operators-expr', ref: 'functions-comparison', prefix: 'oex', target: 400,
    scope: 'arithmetic + - * / % ^ unary -, comparison = <> != < <= > >=, logical AND/OR/NOT, IS [NOT] TRUE/FALSE/NULL/UNKNOWN, IS [NOT] DISTINCT FROM, BETWEEN [SYMMETRIC] AND, [NOT] IN (list), string concat ||, bitwise & | # ~ << >>, comparison ANY/ALL/SOME, row comparisons, operator precedence with parens, IS [NOT] DOCUMENT, COLLATE in expr. Wrap as SELECT. Negative: dangling operator, mismatched parens.' },
  { cat: 'case-conditional', ref: 'functions-conditional', prefix: 'cse', target: 200,
    scope: 'CASE WHEN..THEN..ELSE..END (searched), CASE expr WHEN val THEN..END (simple), nested CASE, CASE in various clauses, COALESCE(n args), NULLIF(a,b), GREATEST(...), LEAST(...). Negative: CASE without END, WHEN without THEN.' },
  { cat: 'subquery-expr', ref: 'functions-subquery', prefix: 'sbq', target: 250,
    scope: 'scalar subquery (SELECT ..) in select/where, EXISTS/NOT EXISTS (subq), expr IN (subq), expr NOT IN (subq), op ANY/SOME/ALL (subq), row constructor = (subq), ARRAY(subq), correlated subqueries. Use fixture tables. Negative: subquery returning wrong is runtime; missing parens around subquery.' },
  { cat: 'cast-coercion', ref: 'sql-expressions', prefix: 'cst2', target: 250,
    scope: 'CAST(expr AS type), expr::type, chained ::, casting to array type, to composite, to domain, typmod in cast (::numeric(10,2)), text<->numeric<->bool<->json casts, ARRAY cast, implicit vs explicit. Wrap as SELECT. Negative: :: without type, CAST missing AS.' },
  { cat: 'arrays', ref: 'arrays', prefix: 'arr', target: 350,
    scope: "ARRAY[...] constructor (nested/multidim), array literal '{1,2}'::int[], subscript a[1], slice a[1:2], a[:2], a[2:], multidim a[1][2], array operators @> <@ && || , element = ANY(arr)/ALL(arr), array_append/prepend/cat/length/dims/upper/lower/position/positions/remove/replace/fill, array_agg, unnest, array_to_string/string_to_array, cardinality, type declarations t[]. Wrap as SELECT or against s.t.tags. Negative: bad subscript syntax, unbalanced braces." },
  { cat: 'jsonb-functions-2', ref: 'functions-json', prefix: 'jsn2', target: 350,
    scope: 'jsonb_set/jsonb_set_lax/jsonb_insert/jsonb_strip_nulls/jsonb_pretty, json_populate_record/jsonb_populate_recordset, json_to_record/json_to_recordset, json_each/jsonb_each_text, jsonb_array_elements[_text]/jsonb_object_keys, jsonb_path_exists/query/query_array/query_first/match (@? @@), SQL/JSON PG17: JSON_EXISTS/JSON_VALUE/JSON_QUERY (PASSING, RETURNING, ON ERROR/ON EMPTY, WRAPPER), JSON(), JSON_SCALAR, JSON_SERIALIZE, IS [NOT] JSON [VALUE|OBJECT|ARRAY|SCALAR] [WITH/WITHOUT UNIQUE KEYS]. Wrap as SELECT against s.t.data. Negative: bad jsonpath, wrong RETURNING.' },
  { cat: 'xml', ref: 'functions-xml', prefix: 'xml', target: 200,
    scope: 'xmlelement(NAME .., XMLATTRIBUTES(..), content), xmlforest, xmlconcat, xmlpi, xmlcomment, xmlroot, xmlagg, xmlparse(DOCUMENT|CONTENT ..), xmlserialize(.. AS type), xpath(path, xml [,ns]), xpath_exists, xmltable(.. PASSING .. COLUMNS ..), xml IS DOCUMENT, table_to_xml. Wrap as SELECT. Negative: xmlelement without NAME, bad xmlparse option.' },
  { cat: 'range-ops', ref: 'functions-range', prefix: 'rng', target: 200,
    scope: "range constructors int4range/int8range/numrange/tsrange/tstzrange/daterange with bounds '[)' '[]' '(]' '()' and empty, multirange constructors int4multirange(..), operators @> <@ && << >> -|- + * - &< &> , lower/upper/isempty/lower_inc/upper_inc/lower_inf/upper_inf, range_merge, range_agg, unnest(multirange), @> with element. Wrap as SELECT or against s.t.span. Negative: bad bound chars, malformed range." },
  { cat: 'datetime-ops', ref: 'functions-datetime', prefix: 'dt', target: 350,
    scope: "DATE/TIME/TIMESTAMP[TZ] literals, INTERVAL 'x' [fields] [(p)], INTERVAL with YEAR/MONTH/DAY/HOUR TO ..., AT TIME ZONE, EXTRACT(field FROM ..), date_part, date_trunc('unit', ..[, tz]), date_bin, OVERLAPS, +/- interval arithmetic, age(), make_date/make_time/make_timestamp[tz]/make_interval, to_char/to_date/to_timestamp(format), current_date/time/timestamp/localtime[stamp]/now/clock_timestamp/statement_timestamp, justify_days/hours/interval, isfinite. Wrap as SELECT. Negative: EXTRACT without FROM, bad field." },
  { cat: 'text-pattern', ref: 'functions-matching', prefix: 'txp', target: 350,
    scope: "LIKE/NOT LIKE/ILIKE/NOT ILIKE [ESCAPE c], SIMILAR TO [ESCAPE], POSIX ~ ~* !~ !~*, regexp_match/regexp_matches(..,'g')/regexp_replace(..,flags)/regexp_split_to_table/regexp_split_to_array/regexp_count/regexp_instr/regexp_substr/regexp_like (PG15), substring(s FROM pat FOR esc)/substring(s SIMILAR p ESCAPE e), SUBSTRING with regex, position/strpos, overlay(s PLACING r FROM i FOR n), trim/ltrim/rtrim/btrim, LIKE with _ and %, collation in pattern. Wrap as SELECT. Negative: ESCAPE with multichar, bad regex flag, SIMILAR without TO." },
  { cat: 'string-funcs', ref: 'functions-string', prefix: 'strf', target: 350,
    scope: "length/char_length/character_length/bit_length/octet_length, upper/lower/initcap, substr/substring, concat/concat_ws/format(%s %I %L %1$s), lpad/rpad, left/right, repeat, reverse, replace/translate, split_part, string_to_array/string_to_table, to_ascii/convert/convert_from/convert_to, encode/decode (base64/hex/escape), md5/sha256(bytea), sha224/384/512, ascii/chr, starts_with, btrim, ||, quote_ident/quote_literal/quote_nullable, parse_ident, normalize/unicode_assert, to_hex, gen_random_uuid (nondeterministic ok), unistr. Wrap as SELECT. Negative: wrong arg count for fixed-arity, bad format spec." },
  { cat: 'numeric-funcs', ref: 'functions-math', prefix: 'numf', target: 250,
    scope: 'abs, ceil/ceiling, floor, round(v[,s]), trunc(v[,s]), mod, power/^, sqrt/cbrt, exp, ln, log(b,x)/log10/log, div(y,x), gcd, lcm, sign, min_scale/scale/trim_scale, factorial/!, bit_count, width_bucket(operand,lo,hi,count)/width_bucket(operand,thresholds), random()/random(min,max) PG17/setseed (nondeterministic ok), trig sin/cos/tan/asin/.../atan2/sind/cosd/.../sinh/cosh/tanh, degrees/radians/pi, to_number(text,fmt). Wrap as SELECT. Negative: log without base bad? wrong arity.' },
  { cat: 'aggregate-funcs', ref: 'functions-aggregate', prefix: 'aggf', target: 350,
    scope: 'count(*)/count(expr)/count(DISTINCT), sum/avg/min/max, array_agg([ORDER BY])/string_agg(expr, delim [ORDER BY])/json_agg/jsonb_agg/jsonb_object_agg, bool_and/bool_or/every, bit_and/bit_or/bit_xor, FILTER (WHERE ..), DISTINCT in agg, ordered-set percentile_cont(f)/percentile_disc(f) WITHIN GROUP (ORDER BY)/mode() WITHIN GROUP, hypothetical rank/dense_rank/percent_rank/cume_dist WITHIN GROUP, statistical corr/covar_pop/covar_samp/regr_*/stddev[_pop|_samp]/variance/var_pop/var_samp, range_agg/range_intersect_agg, grouping(). Wrap as SELECT ... [GROUP BY]. Negative: WITHIN GROUP on normal agg, FILTER without WHERE.' },
  { cat: 'type-syntax', ref: 'datatype', prefix: 'tys', target: 300,
    scope: 'every built-in type name as a typename in CAST/column: smallint/int2 integer/int/int4 bigint/int8, decimal/numeric(p,s), real/float4 double precision/float8 float(p), money, char/character(n) varchar/character varying(n) bpchar text, bytea, timestamp(p) [with|without] time zone, date time(p) [with tz] interval [fields] (p), boolean/bool, point line lseg box path polygon circle, cidr inet macaddr macaddr8, bit(n) bit varying(n)/varbit, tsvector tsquery, uuid, xml, json jsonb, int4range/int8range/numrange/tsrange/tstzrange/daterange + multirange, arrays t[] t[3] t ARRAY t ARRAY[3] t[][], oid/regclass/regproc/regtype/etc, pg_lsn, txid_snapshot/pg_snapshot. Use as ::type or column type. Negative: bad modifier (varchar(-1) is runtime; char(0) ), unknown type name, bit varying() empty.' },
  { cat: 'srf-setreturning', ref: 'functions-srf', prefix: 'srf', target: 200,
    scope: 'generate_series(start,stop[,step]) int/numeric/timestamp, generate_subscripts(arr, dim), unnest(arr[, arr2,...]) [WITH ORDINALITY], SRF in target list, SRF in FROM with column list, ROWS FROM, LATERAL SRF, jsonb_array_elements/each in FROM, regexp_split_to_table, set-returning in SELECT with other columns. Wrap as SELECT. Negative: SRF in WHERE, bad generate_series args.' },

  // ---- PL/pgSQL -----------------------------------------------------------
  { cat: 'plpgsql-declare', ref: 'plpgsql-declarations', prefix: 'ppd', target: 300,
    scope: 'DECLARE section: var type [:= default], CONSTANT, NOT NULL, var type DEFAULT expr, %TYPE, table%ROWTYPE, RECORD, ALIAS FOR $1/name, named params, refcursor, row/record vars, scope in nested BEGIN blocks, <<label>> blocks, quoted var names. Wrap in DO $$ DECLARE .. BEGIN .. END $$ or CREATE FUNCTION ... plpgsql. Negative: type missing, := in wrong place, NOT NULL without default error is runtime.' },
  { cat: 'plpgsql-assign', ref: 'plpgsql-statements', prefix: 'ppa', target: 200,
    scope: 'assignment var := expr / var = expr, SELECT .. INTO [STRICT] var[,var], INSERT/UPDATE/DELETE .. RETURNING INTO, var := SELECT scalar, array element/field assignment, GET DIAGNOSTICS var = ROW_COUNT/PG_CONTEXT/RESULT_OID, FOUND, PERFORM expr. Wrap in plpgsql block referencing s.t. Negative: INTO without SELECT, multiple INTO mismatch is runtime, missing :=.' },
  { cat: 'plpgsql-if-case', ref: 'plpgsql-control-structures', prefix: 'ppic', target: 250,
    scope: 'IF cond THEN .. [ELSIF cond THEN ..] [ELSE ..] END IF, nested IF, simple CASE x WHEN .. THEN .. ELSE .. END CASE, searched CASE WHEN cond THEN .. END CASE, CASE with multiple WHEN values. Wrap in plpgsql. Negative: IF without THEN/END IF, CASE without END CASE, ELSIF spelling.' },
  { cat: 'plpgsql-loops', ref: 'plpgsql-control-structures', prefix: 'ppl', target: 350, skip: 1,
    scope: 'LOOP/END LOOP, WHILE cond LOOP, FOR i IN [REVERSE] a..b [BY s] LOOP, FOR rec IN (query) LOOP, FOR rec IN EXECUTE str [USING] LOOP, FOREACH x [SLICE n] IN ARRAY arr LOOP, CONTINUE/EXIT [label] [WHEN cond], <<lbl>> labeled loops, nested loops with labeled exit. Wrap in plpgsql. Negative: missing END LOOP, EXIT outside loop (runtime), bad FOR range form.' },
  { cat: 'plpgsql-exceptions', ref: 'plpgsql-control-structures', prefix: 'ppe', target: 300,
    scope: 'BEGIN .. EXCEPTION WHEN cond THEN .. [WHEN OTHERS THEN ..] END, condition names (unique_violation, division_by_zero, no_data_found, too_many_rows) and SQLSTATE \\'XXXXX\\', multiple conditions with OR, RAISE [level NOTICE/EXCEPTION/WARNING/INFO/LOG/DEBUG] \\'fmt %\\', args USING ERRCODE/MESSAGE/DETAIL/HINT/COLUMN/CONSTRAINT, RAISE re-raise (bare RAISE), RAISE EXCEPTION USING, GET STACKED DIAGNOSTICS var = RETURNED_SQLSTATE/MESSAGE_TEXT/PG_EXCEPTION_DETAIL/HINT/CONTEXT, ASSERT cond [, msg]. Wrap in plpgsql. Negative: EXCEPTION without WHEN, RAISE bad level, ASSERT syntax.' },
  { cat: 'plpgsql-cursors', ref: 'plpgsql-cursors', prefix: 'ppc', target: 300,
    scope: 'DECLARE cur [args] [NO SCROLL|SCROLL] CURSOR [(params)] FOR query, bound vs unbound (refcursor), OPEN cur [(args)] / OPEN cur FOR query / OPEN cur FOR EXECUTE str [USING], FETCH [direction] FROM cur INTO vars, MOVE [direction] cur, CLOSE cur, FOR rec IN cur LOOP, FOR rec IN bound_cursor(args) LOOP, returning refcursor from function, WHERE CURRENT OF cur, directions NEXT/PRIOR/FIRST/LAST/ABSOLUTE n/RELATIVE n/FORWARD/BACKWARD. Wrap in plpgsql. Negative: FETCH without INTO in plpgsql, bad direction.' },
  { cat: 'plpgsql-dynamic', ref: 'plpgsql-statements', prefix: 'ppdy', target: 250,
    scope: 'EXECUTE \\'sql\\' [INTO [STRICT] var] [USING expr,..], format(\\'%I %L %s\\', ..), quote_ident/quote_literal/quote_nullable, EXECUTE with concatenated dynamic identifiers, RETURN QUERY EXECUTE str USING, FOR rec IN EXECUTE str LOOP, dynamic DDL via EXECUTE. Wrap in plpgsql. Negative: EXECUTE USING without placeholders is fine; INTO with non-select; missing quotes.' },
  { cat: 'plpgsql-return', ref: 'plpgsql-statements', prefix: 'ppr', target: 250,
    scope: 'RETURN expr (scalar function), RETURN (no value in void/procedure), RETURN NEXT [expr] (SETOF), RETURN QUERY (query), RETURN QUERY EXECUTE str [USING], RETURNS TABLE(cols) with RETURN NEXT/QUERY, OUT/INOUT params with RETURN, RETURN NULL/NEW/OLD in trigger, RETURN composite/row. Define the function appropriately. Negative: RETURN value in procedure, RETURN NEXT in non-set function (runtime), RETURN missing expr in scalar.' },
  { cat: 'plpgsql-trigger-body', ref: 'plpgsql-trigger', prefix: 'pptb', target: 200,
    scope: 'trigger function bodies using NEW/OLD, TG_OP/TG_NAME/TG_WHEN/TG_LEVEL/TG_RELID/TG_TABLE_NAME/TG_TABLE_SCHEMA/TG_NARGS/TG_ARGV[], RETURN NEW/OLD/NULL, IF TG_OP = \\'INSERT\\'..., modifying NEW fields, RAISE in trigger, statement vs row level, conditional returns, suppressing row. CREATE FUNCTION ... RETURNS trigger LANGUAGE plpgsql (the function alone is the case; it must compile). Negative: RETURN scalar in trigger, referencing NEW in statement-level context is runtime.' },

  // ---- session / procedural commands --------------------------------------
  { cat: 'do-call', ref: 'sql-do', prefix: 'doc', target: 150,
    scope: 'DO $$ .. $$, DO LANGUAGE plpgsql $$..$$, DO with declarations, CALL proc(args) using fixture s.p(int), CALL with INOUT params and constants, CALL with named args. Negative: DO with RETURNS, CALL on a function, missing $$.' },
  { cat: 'transaction-control', ref: 'sql-begin', prefix: 'txc', target: 250,
    scope: 'BEGIN/START TRANSACTION [ISOLATION LEVEL SERIALIZABLE/REPEATABLE READ/READ COMMITTED/READ UNCOMMITTED] [READ WRITE|READ ONLY] [[NOT] DEFERRABLE], COMMIT [AND [NO] CHAIN]/END, ROLLBACK [AND [NO] CHAIN]/ABORT, SAVEPOINT name, RELEASE [SAVEPOINT] name, ROLLBACK TO [SAVEPOINT] name, SET TRANSACTION .., SET TRANSACTION SNAPSHOT, SET CONSTRAINTS ALL/name DEFERRED/IMMEDIATE. Use txn:none for these (they manage txn state). Negative: bad isolation level name, SAVEPOINT without name.' },
  { cat: 'cursors-sql', ref: 'sql-declare', prefix: 'curs', target: 200,
    scope: 'DECLARE name [BINARY] [ASENSITIVE|INSENSITIVE] [[NO] SCROLL] CURSOR [WITH|WITHOUT HOLD] FOR query, FETCH [direction [FROM|IN]] name (NEXT/PRIOR/FIRST/LAST/ABSOLUTE n/RELATIVE n/n/ALL/FORWARD [n|ALL]/BACKWARD [n|ALL]), MOVE .., CLOSE name/CLOSE ALL. These run inside the default BEGIN/ROLLBACK wrapper (a transaction), so DECLARE without HOLD is valid. Negative: FETCH bad direction, DECLARE without FOR.' },
  { cat: 'prepare-execute', ref: 'sql-prepare', prefix: 'prx', target: 150,
    scope: 'PREPARE name [(type,..)] AS stmt (SELECT/INSERT/UPDATE/DELETE/VALUES/MERGE), EXECUTE name [(params)], DEALLOCATE [PREPARE] name/ALL. Use fixture tables. Negative: EXECUTE with wrong param count is runtime; PREPARE without AS, bad type.' },
  { cat: 'listen-notify', ref: 'sql-notify', prefix: 'lsn', target: 100,
    scope: "LISTEN channel, NOTIFY channel [, 'payload'], pg_notify('chan','payload'), UNLISTEN channel, UNLISTEN *, quoted channel names. Negative: NOTIFY with non-string payload, UNLISTEN bad target." },
  { cat: 'set-show-reset', ref: 'sql-set', prefix: 'ssr', target: 200,
    scope: "SET [SESSION|LOCAL] param TO value / = value / TO DEFAULT, SET param TO 'val', list values, SET TIME ZONE 'x'/LOCAL/DEFAULT/INTERVAL, SET search_path TO a,b, SET ROLE name/NONE, SET SESSION AUTHORIZATION, SET CONSTRAINTS (covered elsewhere), SET param FROM CURRENT (in function only), RESET param/ALL, SHOW param/ALL, SET datestyle/intervalstyle/client_encoding. Negative: SET without value, SHOW with assignment." },
  { cat: 'explain', ref: 'sql-explain', prefix: 'exp', target: 150,
    scope: 'EXPLAIN stmt, EXPLAIN ANALYZE stmt, EXPLAIN VERBOSE, EXPLAIN (ANALYZE [true|false], VERBOSE, COSTS, SETTINGS, GENERIC_PLAN, BUFFERS, WAL, TIMING, SUMMARY, MEMORY, FORMAT TEXT|XML|JSON|YAML) stmt, EXPLAIN over SELECT/INSERT/UPDATE/DELETE/MERGE/CREATE TABLE AS/DECLARE/EXECUTE. ANALYZE executes inside the rolled-back txn (fine). Negative: bad option value, FORMAT bad, EXPLAIN with no statement.' },
  { cat: 'lock-cmd', ref: 'sql-lock', prefix: 'lck', target: 100,
    scope: 'LOCK [TABLE] [ONLY] name [, ..] [IN mode MODE] [NOWAIT], modes ACCESS SHARE/ROW SHARE/ROW EXCLUSIVE/SHARE UPDATE EXCLUSIVE/SHARE/SHARE ROW EXCLUSIVE/EXCLUSIVE/ACCESS EXCLUSIVE, default mode. Runs in txn wrapper. Use s.t. Negative: bad lock mode, NOWAIT misplaced.' },
  { cat: 'copy-cmd', ref: 'sql-copy', prefix: 'cpy', target: 200,
    scope: "COPY t [(cols)] TO STDOUT [WITH] (FORMAT text|csv|binary, DELIMITER, NULL, HEADER [MATCH], QUOTE, ESCAPE, FORCE_QUOTE *|(cols), ENCODING), COPY (query) TO STDOUT (..), COPY t FROM STDIN (..) [will need data; expect:error or use program]; FREEZE; ON_ERROR; LOG_VERBOSITY (PG17). Prefer COPY .. TO STDOUT for ok. COPY FROM 'file'/TO 'file' needs server-side perms -> expect:error acceptable. Negative: bad FORMAT, FORCE_QUOTE without csv, missing TO/FROM." },
  { cat: 'grant-revoke', ref: 'sql-grant', prefix: 'grv', target: 300,
    scope: "GRANT priv[,priv]|ALL [PRIVILEGES] ON [TABLE] t / SEQUENCE / DATABASE / SCHEMA / FUNCTION f(args) / ALL TABLES IN SCHEMA / ALL SEQUENCES IN SCHEMA / ALL FUNCTIONS IN SCHEMA / TYPE / DOMAIN / LANGUAGE / FOREIGN DATA WRAPPER / TABLESPACE TO role/PUBLIC/GROUP [WITH GRANT OPTION], column privileges GRANT SELECT(col) ON t, GRANT role TO role [WITH ADMIN OPTION], REVOKE [GRANT OPTION FOR] .. FROM .. [CASCADE|RESTRICT], ALTER DEFAULT PRIVILEGES [FOR ROLE r] [IN SCHEMA s] GRANT/REVOKE. Use PUBLIC and fixture objects; create a throwaway role in-case if a named grantee is needed (CREATE ROLE r; GRANT .. TO r; in same rolled-back txn). Negative: bad privilege name, missing ON/TO." },
]

// ---------------------------------------------------------------------------
// Build the flat batch list (one agent per batch).
// ---------------------------------------------------------------------------
const LETTERS = 'abcdefghijklmnopqrstuvwxyz'
const batches = []
for (const c of TAXONOMY) {
  const skip = c.skip || 0
  const totalBatches = Math.ceil(c.target / PER_BATCH)
  let remaining = c.target - skip * PER_BATCH
  if (remaining <= 0) continue
  for (let b = skip; b < totalBatches; b++) {
    const n = Math.min(PER_BATCH, remaining)
    remaining -= n
    const fileNo = String(b + 1).padStart(2, '0')
    batches.push({
      category: c.cat,
      ref: c.ref,
      idPrefix: c.prefix + LETTERS[b],   // e.g. ctca, ctcb -> unique across batches
      target: n,
      scope: c.scope,
      file: `tests/corpus/${c.cat}-${fileNo}.jsonl`,
    })
  }
}

const SCHEMA = {
  type: 'object',
  additionalProperties: false,
  required: ['file', 'total', 'oracleMatched', 'oracleTotal', 'clean'],
  properties: {
    file: { type: 'string' },
    category: { type: 'string' },
    total: { type: 'integer' },
    okCount: { type: 'integer' },
    errorCount: { type: 'integer' },
    oracleMatched: { type: 'integer' },
    oracleTotal: { type: 'integer' },
    clean: { type: 'boolean' },
    notes: { type: 'string' },
  },
}

function buildPrompt(t) {
  return `You are generating a batch of PostgreSQL 18 test cases for a parser test corpus. Output is DATA (a .jsonl file), not code. Work in the repo at "C:\\repos\\Code cleaners\\Postgres-database-project".

STEP 1 - read the contract and fixture (REQUIRED):
- Read tests/corpus/CORPUS.md  (the authoring contract - obey it exactly)
- Read tests/corpus/_fixture.sql  (the ONLY pre-existing objects you may reference: schema s; tables s.t, s.t2, s.parent, s.child, s.events(+s.events_2024); types s.mood(enum), s.addr(composite), s.pos_int(domain); s.seq, s.v(view), s.mv(matview); functions s.f(int), s.g(int,int), s.rows_f(), procedure s.p(int), trigger fn s.trg(); index t_name_idx)

STEP 2 - generate cases for category "${t.category}" (PG18 doc slug: ${t.ref}).
Target: ${t.target} cases. Cover the grammar EXHAUSTIVELY and NON-repetitively - walk every clause / option / keyword / ordering / combination of this feature. Include BOTH valid forms (expect:ok) and characteristic malformed forms (expect:error - PREFER true syntax errors). Aim ~65% ok / ~35% error.
COVERAGE SCOPE: ${t.scope}
Rules:
- Each case is ONE JSON object on its own line: {"id","category","sql","expect","ref","note"}. Add "txn":"none" ONLY for statements that cannot run inside a transaction block (VACUUM, REINDEX, CREATE/DROP INDEX CONCURRENTLY, CREATE DATABASE, explicit BEGIN/COMMIT/ROLLBACK/SAVEPOINT, ALTER TYPE ADD VALUE on an in-use enum, etc.).
- category MUST be exactly "${t.category}". ref SHOULD be "${t.ref}" (or a more specific PG18 slug).
- ids MUST be globally unique: prefix "${t.idPrefix}" + zero-padded counter from 1, e.g. ${t.idPrefix}0001, ${t.idPrefix}0002, ...
- ok cases MUST be self-contained and deterministic: reference only fixture objects or objects the case itself creates earlier in the same sql; no reliance on wall-clock/random VALUES that would error.
- SQL must be valid JSON-escaped (\\n, \\"). Multiple ;-separated statements allowed within one case.
You MAY consult https://www.postgresql.org/docs/18/${t.ref}.html via WebFetch and docs/reference/*.md for exact grammar; if WebFetch is blocked, rely on your PostgreSQL 18 knowledge.

STEP 3 - write all cases to ${t.file} (one JSON object per line; no blank lines; no trailing commas).

STEP 4 - VERIFY with the ground-truth oracle (REQUIRED; iterate to ZERO mismatches):
Run:  powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/pg-oracle.ps1 -File ${t.file}
It executes every case against a real postgres:18 and prints "N/N matched" or lists each MISMATCH (your declared expect disagreeing with PostgreSQL). For every mismatch, FIX it (correct expect, or fix/replace the sql) and re-run. Repeat until mismatched=0. If a case is too hard to make valid, REPLACE it with a different grammar point - never leave a wrong case. Do not finish while any mismatch remains.

STEP 5 - return the structured summary: file, category, total (lines written), okCount, errorCount, oracleMatched, oracleTotal, clean (true iff mismatched=0), notes.

SUCCESS CRITERION: the oracle reports ZERO mismatches for ${t.file}, with ~${t.target} cases covering the scope above.`
}

phase('Generate')
log(`fanning out ${batches.length} agents across ${TAXONOMY.length} categories (~${batches.reduce((s, b) => s + b.target, 0)} cases target)`)

const results = await parallel(batches.map(t => () =>
  agent(buildPrompt(t), { label: `${t.category}-${t.idPrefix}`, phase: 'Generate', schema: SCHEMA, model: 'sonnet' })
))

const ok = results.filter(Boolean)
const clean = ok.filter(r => r.clean)
const totalCases = ok.reduce((s, r) => s + (r.total || 0), 0)
log(`done: ${ok.length}/${batches.length} agents returned, ${clean.length} oracle-clean, ~${totalCases} cases written`)

return {
  agents: batches.length,
  returned: ok.length,
  oracleClean: clean.length,
  totalCases,
  notClean: ok.filter(r => !r.clean).map(r => ({ file: r.file, matched: r.oracleMatched, total: r.oracleTotal })),
  files: ok.map(r => ({ file: r.file, total: r.total, clean: r.clean })),
}
