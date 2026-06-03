-- Foreign table on the dummy server. It cannot be queried (no handler) but the
-- definition is valid DDL and applies cleanly.
CREATE FOREIGN TABLE afd.remote_customers (
    id    bigint NOT NULL,
    email text
)
    SERVER dummy_server
    OPTIONS (schema_name 'public', table_name 'customers');
