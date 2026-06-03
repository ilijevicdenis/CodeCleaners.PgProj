-- ICU collation: case-insensitive, deterministic=false (PG12+; ICU ships with pg18).
CREATE COLLATION afd.case_insensitive (
    PROVIDER      = icu,
    LOCALE        = 'und-u-ks-level2',
    DETERMINISTIC = false
);
