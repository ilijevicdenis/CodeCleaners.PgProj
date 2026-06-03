# Postgres Database Project (`pgproj`)

An **SSDT-style database project for PostgreSQL**. You describe your database as a set of
declarative `.sql` files (one object per file), then build, compare, and publish that desired
state against a live server — the same workflow SQL Server developers get from SQL Server Data
Tools (`.sqlproj` / `.dacpac` / Schema Compare), brought to Postgres.

```
your project (.sql files)  ──build──►  model  ──compare──►  plan  ──publish──►  live Postgres
        ▲                                                                            │
        └────────────────────────────── extract ────────────────────────────────────┘
```

## Why

SQL Server has first-class declarative database tooling in Visual Studio. Postgres developers
typically hand-write ordered migration scripts. This project closes that gap: keep the schema as
source, let the tool compute the migration.

## What works today (v0.1)

| Capability | Status |
|-----------|--------|
| Parse `CREATE SCHEMA / TABLE / INDEX / VIEW / SEQUENCE / FUNCTION` | ✅ |
| Columns: types, `NOT NULL`, `DEFAULT`, identity, inline/table PK, UNIQUE, FK (+ ON DELETE/UPDATE) | ✅ |
| Canonical type normalization (`varchar`→`character varying`, `int4`→`integer`, …) | ✅ |
| `.pgproj` MSBuild-style manifest with `**/*.sql` globbing + duplicate detection | ✅ |
| Build project → JSON model artifact (the `.dacpac` analogue) | ✅ |
| Live-server introspection via `pg_catalog` (Npgsql) | ✅ |
| Schema compare (project ↔ live) into an ordered, dependency-safe change set | ✅ |
| Deploy-script generation (transactional, drop-safety guarded) | ✅ |
| `publish` (execute the plan) and `extract` (live DB → buildable project) | ✅ |
| 33 unit tests (parser / comparer / generator / normalizer / loader) | ✅ |

See [`BUGS.md`](./BUGS.md) for the live defect/limitation tracker and the roadmap beyond v0.1
(triggers, types/domains, materialized-view diffing, a Visual Studio project-system/VSIX
front-end, libpg_query-backed parsing).

## Layout

```
PgProj.slnx
├─ src/PgProj.Core/         engine: model, parser, project loader, comparer, generator, introspection
├─ src/PgProj.Cli/          the `pgproj` command-line tool
├─ tests/PgProj.Core.Tests/ xUnit tests
└─ sample/SampleDb/         a worked example project (schema, two tables, index, view, function)
```

## Quick start

```powershell
# Build the toolchain
dotnet build PgProj.slnx

# Build the sample project into a model (no database required)
dotnet run --project src/PgProj.Cli -- build sample/SampleDb/SampleDb.pgproj

# Generate the full create script from the project (no database required)
dotnet run --project src/PgProj.Cli -- script sample/SampleDb/SampleDb.pgproj -o create.sql

# Compare the project against a live server
dotnet run --project src/PgProj.Cli -- compare sample/SampleDb/SampleDb.pgproj `
    --connection "Host=localhost;Database=sample;Username=postgres;Password=postgres"

# Preview the migration the publish would run (dry run)
dotnet run --project src/PgProj.Cli -- publish sample/SampleDb/SampleDb.pgproj `
    --connection "Host=localhost;Database=sample;Username=postgres;Password=postgres" --dry-run

# Apply it (omit --dry-run). Add --allow-drops to permit destructive changes.
dotnet run --project src/PgProj.Cli -- publish sample/SampleDb/SampleDb.pgproj --connection "..."

# Reverse-engineer a live database into a new project
dotnet run --project src/PgProj.Cli -- extract --connection "..." -o ./Extracted
```

The connection string can also be supplied via the `PGPROJ_CONNECTION` environment variable.

## The `.pgproj` file

```xml
<Project Sdk="PgProj.Sdk/0.1.0">
  <PropertyGroup>
    <Name>SampleDb</Name>
    <DefaultSchema>app</DefaultSchema>
    <TargetPostgresVersion>16</TargetPostgresVersion>
  </PropertyGroup>
  <ItemGroup>
    <Build Include="**/*.sql" />
  </ItemGroup>
</Project>
```

It is intentionally MSBuild-shaped so a future Visual Studio project system / build SDK can adopt
it without changing the file format.

## Safety model

Mirrors SSDT's "block on data loss": by default the comparer **never drops** objects that exist
on the server but not in the project. Pass `--allow-drops` to opt in. Deploy scripts are wrapped
in `BEGIN`/`COMMIT` unless you pass `--no-transaction`, so a failed step rolls back cleanly.
