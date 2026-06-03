-- Aggregating view joining customers and orders.
CREATE VIEW afd.customer_orders AS
SELECT c.tenant_id,
       c.id                              AS customer_id,
       c.email,
       count(o.id)                       AS order_count,
       coalesce(sum(o.total_cents), 0)   AS total_cents
FROM afd.customers c
LEFT JOIN afd.orders o
       ON o.tenant_id = c.tenant_id AND o.customer_id = c.id
GROUP BY c.tenant_id, c.id, c.email;
