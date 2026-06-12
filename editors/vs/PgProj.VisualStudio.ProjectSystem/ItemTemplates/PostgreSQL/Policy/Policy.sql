-- Row-level-security policy $fileinputname$. A policy name is per-table:
-- point this at a real table in this schema's folder.
ALTER TABLE public.table_name ENABLE ROW LEVEL SECURITY;

CREATE POLICY $fileinputname$ ON public.table_name
    AS PERMISSIVE
    FOR ALL
    TO PUBLIC
    USING (true)
    WITH CHECK (true);
