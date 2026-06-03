-- Event-trigger function (RETURNS event_trigger): used by the audit_ddl event trigger.
CREATE FUNCTION afd.log_ddl()
    RETURNS event_trigger
    LANGUAGE plpgsql
AS $$
BEGIN
    RAISE NOTICE 'DDL command % executed', tg_tag;
END;
$$;
