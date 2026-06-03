CREATE OR REPLACE FUNCTION app.count_active_customers()
RETURNS bigint
LANGUAGE sql
STABLE
AS $$
    SELECT count(*) FROM app.customers WHERE status = 'active';
$$;
