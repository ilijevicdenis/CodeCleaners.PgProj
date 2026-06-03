-- Partial index (WHERE predicate) with an INCLUDE payload column.
CREATE INDEX customers_active_idx
    ON afd.customers (full_name)
    INCLUDE (email)
    WHERE is_active;
