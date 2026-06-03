-- Event trigger on ddl_command_end, filtered to a set of command tags.
CREATE EVENT TRIGGER audit_ddl
    ON ddl_command_end
    WHEN TAG IN ('CREATE TABLE', 'ALTER TABLE', 'DROP TABLE')
    EXECUTE FUNCTION afd.log_ddl();
