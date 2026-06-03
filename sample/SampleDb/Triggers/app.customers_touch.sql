CREATE TRIGGER customers_touch
    BEFORE UPDATE ON app.customers
    FOR EACH ROW
    EXECUTE FUNCTION app.touch_updated_at();
