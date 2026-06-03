# Language-feature coverage matrix

Tracks every **in-scope** PostgreSQL DDL command (statements that define persistent schema).
Administration is **out of scope** (roles/users, GRANT/REVOKE, DATABASE, TABLESPACE,
replication/pub-sub, config, VACUUM/ANALYZE/CLUSTER/REINDEX and other runtime).

Grammar source: `docs/reference/01..13-*.md` (fetched from postgresql.org/docs/current).

Columns: **Parse** (project `.sql` → model) · **Script** (model → DDL, used by `build`/`script`/
`compare`/`publish`) · **Introspect** (live server → model, used by `compare`/`publish`/`extract`).

Legend: ✅ full · ◑ partial · ⬜ not yet · `raw` = handled by the generic raw-object mechanism
(captured verbatim with a stable identity, diffed by normalized body, recreated on change).

| Command | Parse | Script | Introspect | Notes |
|---|---|---|---|---|
| CREATE SCHEMA | ✅ | ✅ | ✅ | |
| CREATE TABLE — columns/types/DEFAULT/identity | ✅ | ✅ | ✅ | |
| CREATE TABLE — PK / UNIQUE / FK (+actions) | ✅ | ✅ | ✅ | |
| CREATE TABLE — serial / bigserial | ✅ | ✅ | ✅ | first-class column flag (LIM-004) |
| CREATE TABLE — CHECK constraint | ✅ | ✅ | ✅ | column + named table-level (LIM-103) |
| CREATE TABLE — GENERATED … STORED | ✅ | ✅ | ✅ | expression retained (LIM-104) |
| CREATE TABLE — identity ALWAYS/BY DEFAULT | ✅ | ✅ | ✅ | LIM-009 |
| CREATE TABLE — EXCLUDE constraint | ✅ | ✅ | ⬜ | captured + scripted verbatim; introspection pending |
| CREATE TABLE — INHERITS / PARTITION BY / WITH | ◑ | ◑ | ⬜ | trailing clauses round-trip (LIM-101) |
| CREATE TABLE — PARTITION OF / OF type | `raw` | `raw` | ⬜ | captured verbatim as raw Table object |
| ALTER TABLE (as deploy output) | n/a | ✅ | n/a | ADD/ALTER/DROP COLUMN (+USING), PK, FK, CHECK |
| CREATE INDEX (+ partial/INCLUDE/opclass) | ✅ | ✅ | ◑ | expression/opclass compare is textual (LIM-005) |
| CREATE STATISTICS | `raw` | `raw` | ◑ | existence-only introspection |
| CREATE SEQUENCE (+ options) | ✅ | ✅ | ✅ | AS/INCREMENT/MIN/MAX/START/CACHE/CYCLE (LIM-102) |
| CREATE VIEW | ✅ | ✅ | ✅ | body compared normalized (LIM-003) |
| CREATE MATERIALIZED VIEW | ✅ | ✅ | ◑ | introspected as view; matview flag on extract pending |
| CREATE FUNCTION / PROCEDURE | ✅ | ✅ | ✅ | overloads disambiguated by arg types (LIM-002) |
| CREATE AGGREGATE | `raw` | `raw` | ⬜ | |
| CREATE TYPE — enum/composite/range/base | `raw` | `raw` | ◑ | enum + composite introspected; range/base pending |
| CREATE DOMAIN | `raw` | `raw` | ✅ | introspected (base type + constraints) |
| CREATE TRIGGER | `raw` | `raw` | ✅ | via pg_get_triggerdef |
| CREATE EVENT TRIGGER | `raw` | `raw` | ◑ | reconstructed (tags omitted) |
| CREATE RULE | `raw` | `raw` | ✅ | via pg_get_ruledef |
| CREATE POLICY (RLS) | `raw` | `raw` | ◑ | reconstructed (TO roles omitted) |
| CREATE EXTENSION | `raw` | `raw` | ✅ | |
| CREATE LANGUAGE | `raw` | `raw` | ⬜ | procedural-language (xplang) |
| CREATE TRANSFORM | `raw` | `raw` | ⬜ | |
| CREATE COLLATION | `raw` | `raw` | ◑ | existence-only introspection |
| CREATE CAST | `raw` | `raw` | ⬜ | |
| CREATE CONVERSION | `raw` | `raw` | ◑ | existence-only introspection |
| CREATE OPERATOR / CLASS / FAMILY | `raw` | `raw` | ⬜ | |
| CREATE TEXT SEARCH CONFIG/DICT | `raw` | `raw` | ◑ | existence-only introspection |
| CREATE TEXT SEARCH PARSER/TEMPLATE | `raw` | `raw` | ⬜ | |
| CREATE FOREIGN TABLE | `raw` | `raw` | ◑ | existence-only introspection |
| CREATE FOREIGN DATA WRAPPER / SERVER | `raw` | `raw` | ◑ | existence-only introspection |
| CREATE USER MAPPING | `raw` | `raw` | ⬜ | references a role but defines schema |
| COMMENT ON … | `raw` | `raw` | ⬜ | comment text is schema metadata |

## Raw-object mechanism

For every kind marked `raw`, the parser captures the full `CREATE` statement verbatim with a
**stable identity** (independent of body) so it can be matched across project vs server:

- schema-scoped: `schema.name` (type, domain, collation, conversion, statistics, foreign table, text search)
- table-scoped: `name ON schema.table` (trigger, rule, policy)
- global: `name` (extension, language, server, foreign data wrapper, event trigger)
- signature: verbatim target text (aggregate, operator, cast, operator class/family, transform, user mapping)

Diff rule: missing on target → emit body; body changed → `DROP … IF EXISTS` + re-emit body
(destructive recreations — type/domain/foreign table — require `--allow-drops`; in-place ones
— trigger/rule/policy/etc. — do not). Comments never drop; a change just re-emits.

The introspection column is where the remaining work concentrates: project-side build/script/
project-vs-project compare already cover everything; live-server compare/publish/extract cover the
✅/◑ rows and will be filled in kind-by-kind (see BUGS.md LIM-1xx).
