-- Conversion function backing the afd.mood -> integer cast.
CREATE FUNCTION afd.mood_to_int(m afd.mood)
    RETURNS integer
    LANGUAGE sql
    IMMUTABLE
    STRICT
AS $$
    SELECT CASE m WHEN 'sad' THEN -1 WHEN 'ok' THEN 0 WHEN 'happy' THEN 1 END
$$;
