-- RETURNS TABLE (named columns) implemented in PL/pgSQL with a RETURN QUERY.
CREATE FUNCTION afd.customer_stats(min_score numeric DEFAULT 0)
    RETURNS TABLE (status afd.mood, customer_count bigint, avg_score numeric)
    LANGUAGE plpgsql
    STABLE
AS $$
BEGIN
    RETURN QUERY
        SELECT c.status, count(*), avg(c.score)
        FROM afd.customers c
        WHERE c.score >= min_score
        GROUP BY c.status;
END;
$$;
