-- Equality function backing the custom === operator on afd.mood.
CREATE FUNCTION afd.mood_eq(a afd.mood, b afd.mood)
    RETURNS boolean
    LANGUAGE sql
    IMMUTABLE
    STRICT
AS $$
    SELECT a = b
$$;
