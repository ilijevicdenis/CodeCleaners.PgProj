-- GIN index over a jsonb column with an explicit opclass.
CREATE INDEX customers_profile_gin
    ON afd.customers USING gin (profile jsonb_path_ops);
