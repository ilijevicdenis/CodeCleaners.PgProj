-- SQL function with OUT parameters (implicit composite return).
CREATE FUNCTION afd.split_name(
    IN  full_name text,
    OUT first_name text,
    OUT last_name  text
)
    LANGUAGE sql
    IMMUTABLE
AS $$
    SELECT split_part(full_name, ' ', 1),
           split_part(full_name, ' ', 2)
$$;
