CREATE SCHEMA IF NOT EXISTS sales;

-- Reads common.customer, which lives in the referenced Common project — never emitted by Sales.
CREATE VIEW sales.customer_tiers AS
    SELECT c.id, c.name, c.tier
    FROM common.customer c;
