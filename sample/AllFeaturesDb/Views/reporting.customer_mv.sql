-- Materialized view in the reporting schema (built WITH DATA).
CREATE MATERIALIZED VIEW reporting.customer_mv AS
SELECT status, count(*) AS n, avg(score) AS avg_score
FROM afd.customers
GROUP BY status
WITH DATA;
