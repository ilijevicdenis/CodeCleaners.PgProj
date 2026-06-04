# PgProjProject

A PostgreSQL declarative database project (CodeCleaners.PgProj). Describe your database as
one-object-per-file `.sql` files, then build / compare / publish / validate against a live server.

## Layout (matches `pgproj extract` / `pgproj add`)

```
Schemas/    Tables/      Views/       Functions/   Procedures/
Triggers/   Sequences/   Types/       Policies/
```

## Common commands

```bash
pgproj add table app.Customer        # scaffold Tables/app.Customer.sql from a template
pgproj build  PgProjProject.pgproj   # parse every .sql into a model (offline)
pgproj validate PgProjProject.pgproj --connection <conn>   # apply to a throwaway DB, rolled back
```

Files whose name starts with `_` are treated as non-source.
