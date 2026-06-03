CREATE OR REPLACE VIEW app.customer_orders AS
SELECT c.id          AS customer_id,
       c.email,
       count(o.id)   AS order_count,
       coalesce(sum(o.total_cents), 0) AS total_cents
FROM app.customers c
LEFT JOIN app.orders o ON o.customer_id = c.id
GROUP BY c.id, c.email;
