-- Trigger PGPROJ_NAME (in schema PGPROJ_SCHEMA). Point it at a real table and function.
CREATE TRIGGER PGPROJ_NAME
    BEFORE INSERT OR UPDATE ON PGPROJ_SCHEMA.table_name
    FOR EACH ROW
    EXECUTE FUNCTION PGPROJ_SCHEMA.trigger_function();
