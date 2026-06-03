-- INHERITS table: adds its own columns on top of afd.base_entity's.
CREATE TABLE afd.document (
    title text NOT NULL,
    body  text
) INHERITS (afd.base_entity);
