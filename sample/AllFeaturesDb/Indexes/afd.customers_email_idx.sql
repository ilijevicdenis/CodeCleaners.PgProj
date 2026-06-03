-- Plain unique btree index.
CREATE UNIQUE INDEX customers_email_idx ON afd.customers (tenant_id, email);
