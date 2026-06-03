-- Updatable view with WITH CASCADED CHECK OPTION: inserts/updates that violate
-- the WHERE predicate are rejected.
CREATE VIEW afd.active_customers_v AS
SELECT tenant_id, id, email, full_name, is_active, status
FROM afd.customers
WHERE is_active
WITH CASCADED CHECK OPTION;
