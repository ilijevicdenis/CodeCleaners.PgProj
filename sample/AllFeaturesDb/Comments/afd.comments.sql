-- COMMENT ON a spread of object kinds.
COMMENT ON SCHEMA afd IS 'All-features showcase schema';
COMMENT ON TABLE afd.customers IS 'Customer master records (flagship table)';
COMMENT ON COLUMN afd.customers.profile IS 'Arbitrary JSONB customer profile';
COMMENT ON VIEW afd.customer_orders IS 'Per-customer order rollup';
COMMENT ON MATERIALIZED VIEW reporting.customer_mv IS 'Cached customer counts by status';
COMMENT ON FUNCTION afd.add(integer, integer) IS 'Add two integers';
COMMENT ON TYPE afd.mood IS 'Three-valued mood enum';
COMMENT ON DOMAIN afd.email IS 'Validated email address';
COMMENT ON INDEX afd.customers_email_idx IS 'Unique email per tenant';
COMMENT ON SEQUENCE afd.order_no_seq IS 'Human-facing order numbers';
COMMENT ON TRIGGER customers_touch ON afd.customers IS 'Bumps created_at on rename';
