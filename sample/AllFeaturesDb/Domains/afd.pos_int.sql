-- Domain with a CHECK constraint and NOT NULL
CREATE DOMAIN afd.pos_int AS integer
    NOT NULL
    CHECK (VALUE > 0);
