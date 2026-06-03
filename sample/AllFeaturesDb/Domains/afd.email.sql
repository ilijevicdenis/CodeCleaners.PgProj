-- Domain over text with a named CHECK constraint and a DEFAULT
CREATE DOMAIN afd.email AS text
    DEFAULT 'unknown@example.com'
    CONSTRAINT email_format CHECK (VALUE ~ '^[^@]+@[^@]+\.[^@]+$');
