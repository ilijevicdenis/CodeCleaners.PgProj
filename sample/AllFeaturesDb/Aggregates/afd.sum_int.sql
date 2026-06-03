-- User-defined aggregate built on the built-in int4pl transition function.
CREATE AGGREGATE afd.sum_int (integer) (
    SFUNC    = int4pl,
    STYPE    = integer,
    INITCOND = '0'
);
