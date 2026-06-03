-- SETOF return over a table type.
CREATE FUNCTION afd.active_customers()
    RETURNS SETOF afd.customers
    LANGUAGE sql
    STABLE
    SECURITY DEFINER
AS $$
    SELECT * FROM afd.customers WHERE is_active
$$;
