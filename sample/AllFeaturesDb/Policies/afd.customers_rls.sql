-- Row-level security: enable RLS, then a permissive per-tenant policy.
ALTER TABLE afd.customers ENABLE ROW LEVEL SECURITY;

CREATE POLICY customers_tenant_isolation ON afd.customers
    AS PERMISSIVE
    FOR ALL
    TO PUBLIC
    USING (tenant_id = current_setting('afd.tenant_id', true)::integer)
    WITH CHECK (tenant_id = current_setting('afd.tenant_id', true)::integer);
