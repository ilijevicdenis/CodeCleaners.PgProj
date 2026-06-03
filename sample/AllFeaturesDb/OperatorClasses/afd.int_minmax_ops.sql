-- Operator class for integer under btree built from existing operators/support fn.
CREATE OPERATOR CLASS afd.int_minmax_ops
    FOR TYPE integer USING btree AS
        OPERATOR 1 <,
        OPERATOR 2 <=,
        OPERATOR 3 =,
        OPERATOR 4 >=,
        OPERATOR 5 >,
        FUNCTION 1 btint4cmp(integer, integer);
