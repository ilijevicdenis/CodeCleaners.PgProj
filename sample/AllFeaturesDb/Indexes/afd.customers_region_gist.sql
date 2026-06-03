-- GiST index over a geometric column.
CREATE INDEX customers_region_gist
    ON afd.customers USING gist (region);
