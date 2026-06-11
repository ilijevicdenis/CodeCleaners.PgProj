-- Trigger $fileinputname$. A trigger name is per-table, not schema-qualified:
-- point this at a real table and trigger function in this schema's folder.
CREATE TRIGGER $fileinputname$
    BEFORE INSERT OR UPDATE ON public.table_name
    FOR EACH ROW
    EXECUTE FUNCTION public.trigger_function();
