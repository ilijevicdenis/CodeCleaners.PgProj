-- Expression extended statistics (stxexprs set) — exercises full pg_get_statisticsobjdef reconstruction.
CREATE STATISTICS afd.customers_expr_stats ON (lower(full_name)), tenant_id FROM afd.customers;
