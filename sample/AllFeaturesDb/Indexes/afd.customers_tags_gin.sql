-- GIN index over an array column with a storage parameter.
CREATE INDEX customers_tags_gin
    ON afd.customers USING gin (tags) WITH (fastupdate = on);
