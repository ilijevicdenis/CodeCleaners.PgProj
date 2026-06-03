-- Foreign server over the handler-less FDW.
CREATE SERVER dummy_server
    FOREIGN DATA WRAPPER dummy_fdw
    OPTIONS (host 'localhost', dbname 'remote');
