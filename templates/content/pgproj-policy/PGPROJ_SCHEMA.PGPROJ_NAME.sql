-- Row-level-security policy PGPROJ_NAME (in schema PGPROJ_SCHEMA). Point it at a real table.
ALTER TABLE PGPROJ_SCHEMA.table_name ENABLE ROW LEVEL SECURITY;

CREATE POLICY PGPROJ_NAME ON PGPROJ_SCHEMA.table_name
    AS PERMISSIVE
    FOR ALL
    TO PUBLIC
    USING (true)
    WITH CHECK (true);
