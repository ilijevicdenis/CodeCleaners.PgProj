-- Trigger function (row-level): used by afd.customers_touch.
CREATE FUNCTION afd.touch_updated_at()
    RETURNS trigger
    LANGUAGE plpgsql
AS $$
BEGIN
    NEW.created_at := now();
    RETURN NEW;
END;
$$;
