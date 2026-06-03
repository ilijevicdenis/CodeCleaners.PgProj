-- Handler-less foreign data wrapper so it applies without a contrib .so.
CREATE FOREIGN DATA WRAPPER dummy_fdw NO HANDLER NO VALIDATOR OPTIONS (debug 'true');
