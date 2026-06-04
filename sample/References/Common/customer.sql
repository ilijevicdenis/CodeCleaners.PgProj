CREATE SCHEMA IF NOT EXISTS common;

CREATE TABLE common.customer (
    id   int  PRIMARY KEY,
    name text NOT NULL,
    tier text NOT NULL DEFAULT 'standard'
);
