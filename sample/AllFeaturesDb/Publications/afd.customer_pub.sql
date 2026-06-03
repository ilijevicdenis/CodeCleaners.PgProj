-- Logical-replication publication over two tables with a publish parameter.
CREATE PUBLICATION customer_pub
    FOR TABLE afd.customers, afd.orders
    WITH (publish = 'insert, update');
