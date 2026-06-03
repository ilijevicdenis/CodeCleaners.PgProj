-- SQL function: scalar return, IMMUTABLE/STRICT/PARALLEL SAFE, arg default.
CREATE FUNCTION afd.add(a integer, b integer DEFAULT 0)
    RETURNS integer
    LANGUAGE sql
    IMMUTABLE
    STRICT
    PARALLEL SAFE
    RETURN a + b;
