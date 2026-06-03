-- VARIADIC argument + INOUT, exercised via a plpgsql body.
CREATE FUNCTION afd.accumulate(INOUT running bigint, VARIADIC vals integer[])
    LANGUAGE plpgsql
    IMMUTABLE
AS $$
DECLARE
    v integer;
BEGIN
    FOREACH v IN ARRAY vals LOOP
        running := running + v;
    END LOOP;
END;
$$;
