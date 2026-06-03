-- Row-level BEFORE UPDATE trigger with a WHEN condition.
CREATE TRIGGER customers_touch
    BEFORE UPDATE ON afd.customers
    FOR EACH ROW
    WHEN (OLD.full_name IS DISTINCT FROM NEW.full_name)
    EXECUTE FUNCTION afd.touch_updated_at();
