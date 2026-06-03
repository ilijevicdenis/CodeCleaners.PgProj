-- Standalone sequence exercising the full option list.
CREATE SEQUENCE afd.order_no_seq
    AS bigint
    INCREMENT BY 10
    MINVALUE 1000
    MAXVALUE 9999999999
    START WITH 1000
    CACHE 5
    CYCLE;
