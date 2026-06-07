-- A user procedural language aliasing plpgsql's built-in handler (pure-SQL CREATE LANGUAGE).
CREATE LANGUAGE afd_plpgsql HANDLER plpgsql_call_handler INLINE plpgsql_inline_handler VALIDATOR plpgsql_validator;
