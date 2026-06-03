-- LIKE clause: copy column definitions, defaults and constraints from afd.customers.
CREATE TABLE afd.customers_archive (
    archived_at timestamptz NOT NULL DEFAULT now(),
    LIKE afd.customers INCLUDING DEFAULTS INCLUDING CONSTRAINTS INCLUDING COMMENTS
);
