-- Rule that turns DELETEs on orders into no-ops (DO INSTEAD NOTHING).
CREATE RULE orders_no_delete AS
    ON DELETE TO afd.orders
    DO INSTEAD NOTHING;
