-- FK with composite reference, explicit ON DELETE/UPDATE actions, MATCH and DEFERRABLE.
CREATE TABLE afd.orders (
    id            bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id     integer     NOT NULL,
    customer_id   bigint      NOT NULL,
    total_cents   integer     NOT NULL DEFAULT 0 CHECK (total_cents >= 0),
    placed_at     timestamptz NOT NULL DEFAULT now(),

    -- composite FK with full action/match/deferrable surface
    CONSTRAINT orders_customer_fk
        FOREIGN KEY (tenant_id, customer_id)
        REFERENCES afd.customers (tenant_id, id)
        MATCH SIMPLE
        ON DELETE CASCADE
        ON UPDATE RESTRICT
        DEFERRABLE INITIALLY DEFERRED
);
