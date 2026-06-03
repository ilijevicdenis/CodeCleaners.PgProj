-- Shell (placeholder) type declaration. A bare CREATE TYPE with no definition
-- registers a shell type; it would normally be fleshed out into a base type with
-- C-language I/O functions, which this environment cannot compile, so it is left
-- as a shell to exercise the grammar.
CREATE TYPE afd.shell_type;
