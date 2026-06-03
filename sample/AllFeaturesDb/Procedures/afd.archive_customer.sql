-- Stored procedure with IN/INOUT args and a transaction-free body. Procedures may
-- COMMIT; here we just move a row and report the archived id back via INOUT.
CREATE PROCEDURE afd.archive_customer(IN p_tenant integer, IN p_id bigint, INOUT moved boolean DEFAULT false)
    LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO afd.customers_archive
    SELECT now(), c.*
    FROM afd.customers c
    WHERE c.tenant_id = p_tenant AND c.id = p_id;

    DELETE FROM afd.customers c
    WHERE c.tenant_id = p_tenant AND c.id = p_id;

    moved := FOUND;
END;
$$;
