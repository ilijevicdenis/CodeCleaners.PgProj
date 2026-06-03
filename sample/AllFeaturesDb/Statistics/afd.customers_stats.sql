-- Extended statistics object over correlated columns.
CREATE STATISTICS afd.customers_stats (ndistinct, dependencies, mcv)
    ON tenant_id, status, is_active
    FROM afd.customers;
