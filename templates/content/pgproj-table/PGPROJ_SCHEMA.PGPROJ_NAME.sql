-- Table PGPROJ_SCHEMA.PGPROJ_NAME. Replace the placeholder columns with your own.
CREATE TABLE PGPROJ_SCHEMA.PGPROJ_NAME (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    created_at  timestamptz NOT NULL DEFAULT now()
);
