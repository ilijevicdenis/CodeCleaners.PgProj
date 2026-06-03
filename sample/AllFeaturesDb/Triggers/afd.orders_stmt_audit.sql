-- Statement-level AFTER trigger covering multiple events.
CREATE TRIGGER orders_stmt_audit
    AFTER INSERT OR UPDATE OR DELETE ON afd.orders
    FOR EACH STATEMENT
    EXECUTE FUNCTION afd.touch_updated_at();
