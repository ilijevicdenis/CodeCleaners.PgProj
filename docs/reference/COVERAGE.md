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
| CREATE TABLE — EXCLUDE constraint | ✅ | ✅ | ✅ | introspected via pg_get_constraintdef into OtherConstraints (#98) |
| CREATE TABLE — INHERITS / PARTITION BY / WITH | ◑ | ◑ | ✅ | PARTITION BY + INHERITS introspected to TrailingOptions (#99); WITH storage params still off by default |
| CREATE TABLE — PARTITION OF / OF type | `raw` | `raw` | ✅ | `OF type` (`ReadTypedTablesAsync`) + `PARTITION OF` (`ReadPartitionChildrenAsync`) (#99) |
| ALTER TABLE (as deploy output) | n/a | ✅ | n/a | ADD/ALTER/DROP COLUMN (+USING), PK, FK, CHECK |
| CREATE INDEX (+ partial/INCLUDE/opclass) | ✅ | ✅ | ✅ | opclass/expression introspected; redundant ASC/NULLS defaults folded so they round-trip (#101) |
| CREATE STATISTICS | `raw` | `raw` | ✅ | column-based (`ReadStatisticsAsync`) + expression stats via pg_get_statisticsobjdef (`ReadExpressionStatisticsAsync`, #110) |
| CREATE SEQUENCE (+ options) | ✅ | ✅ | ✅ | AS/INCREMENT/MIN/MAX/START/CACHE/CYCLE (LIM-102) |
| CREATE VIEW | ✅ | ✅ | ✅ | body compared normalized (LIM-003) |
| CREATE MATERIALIZED VIEW | ✅ | ✅ | ✅ | matview flag introspected via relkind (`ReadViewsAsync`) |
| CREATE FUNCTION / PROCEDURE | ✅ | ✅ | ✅ | overloads disambiguated by arg types (LIM-002) |
| CREATE AGGREGATE | `raw` | `raw` | ✅ | introspected (`ReadAggregatesAsync`: SFUNC/STYPE/FINALFUNC/…) |
| CREATE TYPE — enum/composite/range/base | `raw` | `raw` | ◑ | enum + composite + range introspected; base type pending (#102) |
| CREATE DOMAIN | `raw` | `raw` | ✅ | introspected (base type + constraints) |
| CREATE TRIGGER | `raw` | `raw` | ✅ | via pg_get_triggerdef |
| CREATE EVENT TRIGGER | `raw` | `raw` | ✅ | reconstructed incl. WHEN TAG IN; body-comparable (#104) |
| CREATE RULE | `raw` | `raw` | ✅ | via pg_get_ruledef |
| CREATE POLICY (RLS) | `raw` | `raw` | ✅ | reconstructed incl. TO roles; identity-only compare (#103) |
| CREATE EXTENSION | `raw` | `raw` | ✅ | |
| CREATE PUBLICATION | `raw` | `raw` | ✅ | introspected (`ReadPublicationsAsync`) |
| CREATE LANGUAGE | `raw` | `raw` | ⬜ | procedural-language (xplang) (#108) |
| CREATE TRANSFORM | `raw` | `raw` | ⬜ | (#108) |
| CREATE COLLATION | `raw` | `raw` | ✅ | introspected (`ReadCollationsAsync`: provider/locale/deterministic) |
| CREATE CAST | `raw` | `raw` | ✅ | introspected (`ReadCastsAsync`, user casts) |
| CREATE CONVERSION | `raw` | `raw` | ✅ | introspected (`ReadConversionsAsync`) |
| CREATE OPERATOR / CLASS / FAMILY | `raw` | `raw` | ✅ | full DDL reconstruction (`ReadOperators/OperatorClasses/OperatorFamiliesAsync`) |
| CREATE TEXT SEARCH CONFIG/DICT | `raw` | `raw` | ✅ | introspected (`ReadTextSearchConfigurations/DictionariesAsync`) |
| CREATE TEXT SEARCH PARSER/TEMPLATE | `raw` | `raw` | ⬜ | (#109) |
| CREATE FOREIGN TABLE | `raw` | `raw` | ✅ | introspected (`ReadForeignTablesAsync`: columns/server/options) |
| CREATE FOREIGN DATA WRAPPER / SERVER | `raw` | `raw` | ✅ | full DDL reconstruction (`ReadForeignDataWrappers/ServersAsync`) |
| CREATE USER MAPPING | `raw` | `raw` | ✅ | parser fix + `ReadUserMappingsAsync` (FOR user SERVER server +OPTIONS) (#108) |
| COMMENT ON … | `raw` | `raw` | ✅ | introspected across all object classes (`ReadCommentsAsync`) |

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
project-vs-project compare already cover everything; live-server compare/publish/extract already cover
all ✅ rows. The remaining ⬜/◑ are tracked under **EP-COVERAGE (#72)** on milestone M7. **Done:** EXCLUDE
constraints (#98), EVENT TRIGGER tags (#104), POLICY `TO` roles (#103), USER MAPPING (#108),
PARTITION/INHERITS (#99), index opclass/ordering (#101), expression statistics (#110). **Open (all need
C-backed objects a pure-SQL sample can't exercise):** base types (#102), LANGUAGE/TRANSFORM (#108 tail),
TEXT SEARCH PARSER/TEMPLATE (#109). See also BUGS.md LIM-1xx.
