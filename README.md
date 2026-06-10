# CodeCleaners.PgProj

**SSDT-style declarative database projects for PostgreSQL.** Describe your database as a set of
declarative `.sql` files (one object per file), then **build, compare, deploy, validate, and
reverse-sync** that desired state against a live server — the workflow SQL Server developers get
from SQL Server Data Tools (`.sqlproj` / `.dacpac` / Schema Compare), brought to Postgres.

> A product of **[CodeCleaners d.o.o.](https://code-cleaners.com/)** — free to use, see [License](#license).

```
 your project (.sql files) ──build──► model ──compare──► plan ──publish──► live PostgreSQL
            ▲                                                                     │
            └───────────────── pull / drift / extract ◄───────────────────────────┘
                              (capture changes made on the server back into source)
```

## Why

SQL Server has first-class declarative database tooling in Visual Studio; Postgres developers
usually hand-write ordered migration scripts. This closes the gap: **keep the schema as source,
let the tool compute the migration** — and when someone hotfixes production directly, pull that
change back into the project instead of copy-pasting it.

## What it does

| Capability | Notes |
|-----------|-------|
| **Build** a project into a model | Parses every `.sql` file; offline, no database needed |
| **Compare** project ↔ live server | Ordered, dependency-safe change set |
| **Publish** (deploy) | Transactional by default; **never drops** unless you pass `--allow-drops`; optional phased parallel apply |
| **Validate** against real PostgreSQL | Applies the project to a throwaway temp database inside a transaction, then **rolls back and drops** it — a zero-risk preflight |
| **Extract** a live DB into a project | Reverse-engineer an existing database into buildable `.sql` files |
| **Drift / Pull** (reverse-sync) | Detect how the live DB differs from the project and rewrite the project files **from** the database — capture a production hotfix into source |
| **Analyze** | Static safety rules over the parsed AST (function volatility, `SELECT *` in views, …) |
| Incremental deploys | Only the changed objects are scripted |

### Supported PostgreSQL surface

A hand-written recursive-descent parser + semantic analyzer covering the modern PostgreSQL
(16/17/18) language, including:

- **DDL** — schemas, tables (incl. **partitioning** `RANGE`/`LIST`/`HASH`, `PARTITION OF … DEFAULT`),
  columns with **identity** (`GENERATED ALWAYS/BY DEFAULT`), **generated** (`… STORED`) and serial
  columns, `CHECK` / `UNIQUE` / `PRIMARY KEY` / **`EXCLUDE`** / foreign keys with `ON DELETE/UPDATE`.
- **Objects** — views & **materialized views**, sequences, functions/procedures/aggregates, enum /
  composite / **range** / shell **types**, **domains**, **collations**, **casts**, **operators** &
  **operator classes/families**, **conversions**, triggers (incl. `CONSTRAINT TRIGGER`) & event
  triggers, rules, **row-level-security policies**, **foreign data wrappers / servers / foreign
  tables**, **text-search dictionaries & configurations**, **extended statistics**, **publications**,
  comments.
- **Queries** — CTEs incl. `WITH RECURSIVE`, set ops (`UNION`/`INTERSECT`/`EXCEPT [ALL]`), `LATERAL`,
  `DISTINCT ON`, correlated & scalar subqueries, `GROUPING SETS`, window functions with frames and
  `FILTER`, `MERGE`, `JSON_TABLE` and the JSON path/predicate surface, `percentile_… WITHIN GROUP`,
  `FETCH FIRST … WITH TIES`.
- **PL/pgSQL** — `DECLARE` / control flow / loops / cursors / `EXCEPTION` / `RAISE … USING` /
  `RETURN QUERY`, dollar-quoting with custom tags.
- **Indexes** — B-tree / GIN / GiST / BRIN, partial (`WHERE`), expression, `INCLUDE`, operator classes.

**Coverage is proven, not claimed.** The parser is measured against a ground-truth corpus of
**21,743 statements across 173 categories**, every one verified against a real **PostgreSQL 18**
server. The toolchain reaches **100% accept/reject parity with PostgreSQL** on that corpus: the
static engine resolves the vast majority instantly and offline (with **zero false positives** — it
never rejects valid SQL), and the remaining runtime-only cases (errors that only surface on
execution) are verified by executing them against PostgreSQL.

## Performance

The parser/model engine is allocation-tuned: parsing + building the corpus now allocates **≈76% less**
than the pre-optimization baseline (66.28 → 16.06 MB/op, BenchmarkDotNet `PipelineBenchmarks.ParseAndBuild`),
across 24 merged optimizations — `Token[]` array pooling, lazy AST collections, lazy table-constraint lists,
and more. See the progress dashboard with per-stage and per-bucket charts: **[docs/parser-performance.md](docs/parser-performance.md)**.

## Install / build

Requires the **.NET 10 SDK**. (A live **PostgreSQL** server — or Docker `postgres:18` — is only
needed for the database-touching commands.)

```bash
git clone https://github.com/ilijevicdenis/CodeCleaners.PgProj.git
cd CodeCleaners.PgProj
dotnet build PgProj.slnx
```

The CLI is `pgproj` (run via `dotnet run --project src/PgProj.Cli -- <command>`).

## Build & install the tools locally

Four pieces ship from this repo: the **CLI/engine**, the **MSBuild SDK** (build `.pgproj` like any
project), the **VS Code extension**, and a **Visual Studio** integration. None of this needs a database
except the DB-touching commands.

### 1. The CLI (`pgproj`)

```bash
dotnet build PgProj.slnx -c Release          # builds the engine + CLI
dotnet run --project src/PgProj.Cli -- build myschema/MySchema.pgproj   # run from source
```

To get a `pgproj` you can call from anywhere, publish a self-contained/framework-dependent build and put
it on your `PATH`:

```bash
dotnet publish src/PgProj.Cli -c Release -o "$HOME/.pgproj/bin"
# then add ~/.pgproj/bin to PATH  (Windows: setx PATH "%USERPROFILE%\.pgproj\bin;%PATH%")
pgproj --help
```

### 2. The MSBuild SDK (`PgProj.Sdk`) — build a `.pgproj` like a normal project

Pack the SDK and consume it from a **local NuGet feed**, so a `.pgproj` can just say
`Sdk="PgProj.Sdk/<version>"` and `dotnet build` produces the model + `.pgpkg`:

```bash
dotnet pack src/PgProj.Sdk -c Release -o ./artifacts/sdk            # → PgProj.Sdk.<ver>.nupkg
dotnet nuget add source "$(pwd)/artifacts/sdk" -n pgproj-local      # register the local feed (once)
# in your project's nuget.config, or globally; then:
dotnet build path/to/MyDb.pgproj -c Release                        # emits bin/MyDb.model.json + MyDb.pgpkg
dotnet build path/to/MyDb.pgproj -t:Publish --p:Connection="Host=...;Database=..."   # deploy via the SDK
```

A minimal project file:

```xml
<Project Sdk="PgProj.Sdk/0.1.0" DefaultTargets="Build">
  <PropertyGroup><Name>MyDb</Name><DefaultSchema>public</DefaultSchema><TargetPostgresVersion>18</TargetPostgresVersion></PropertyGroup>
</Project>
```

### 3. The VS Code extension (`editors/vscode`)

Requires **Node 18+** (and the .NET SDK for the engine it drives). Build, package, and install the `.vsix`:

```bash
cd editors/vscode
npm install
npm run compile          # tsc + esbuild → dist/extension.js  (the runtime entry)
npm run package          # → pgproj-vscode-<ver>.vsix
code --install-extension pgproj-vscode-0.1.0.vsix
```

Or press **F5** in VS Code with `editors/vscode` open to launch an Extension Development Host. Point it at
your engine with the `pgproj.cliPath` setting (the built `PgProj.Cli.dll`) if `pgproj` isn't on `PATH`.
The extension's live features (squiggles/hover/go-to-definition/completion) are powered by `pgproj serve`
(see [`docs/LSP_LANGUAGE_SERVER.md`](docs/LSP_LANGUAGE_SERVER.md)).

Run its tests:

```bash
npm run test:unit        # vitest, no VS Code host — the fast gate
npm run test:e2e         # @vscode/test-electron: downloads VS Code and runs a real host
```

> **E2E note:** `test:e2e` needs a desktop session (on headless Linux wrap it: `xvfb-run -a npm run
> test:e2e`). `runTest.ts` automatically mirrors the build to a space-free temp dir if your checkout path
> contains a space (a `@vscode/test-electron` limitation), so it works from any path. Always let it run
> `npm run compile` first (it does, via `pretest:e2e`) so the `dist/` bundle is current.

### 4. Visual Studio (`editors/vs`)

- **Route A (works today):** the `PgProj.Sdk` above makes a `.pgproj` build/clean/publish inside Visual
  Studio via generic project handling — open the project and Build/Publish from Solution Explorer.
- **Route B (scaffold):** a full VSIX project system lives under `editors/vs/` in its own solution; it
  requires **Visual Studio + the "Visual Studio extension development" workload** to build. See
  [`docs/VISUAL_STUDIO.md`](docs/VISUAL_STUDIO.md).

## Test your own SQL scripts

You don't need a full project to check your scripts — point a one-line `.pgproj` at them.

1. Put your `.sql` file(s) in a folder with a tiny manifest:

   ```xml
   <!-- myschema/MySchema.pgproj -->
   <Project>
     <PropertyGroup><Name>MySchema</Name><DefaultSchema>public</DefaultSchema></PropertyGroup>
   </Project>
   ```

   All `*.sql` files in the folder are included automatically — no `<Build Include>` needed.

2. **Parse / lint — offline, no database:**

   ```bash
   dotnet run --project src/PgProj.Cli -- build   myschema/MySchema.pgproj   # parse; reports file:line on errors
   dotnet run --project src/PgProj.Cli -- analyze myschema/MySchema.pgproj   # static safety findings
   ```

3. **Validate against real PostgreSQL — applied to a throwaway DB, then rolled back:**

   ```bash
   dotnet run --project src/PgProj.Cli -- validate myschema/MySchema.pgproj \
       --connection "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=postgres"
   ```

   `validate` creates a uniquely-named scratch database, applies your scripts inside a transaction,
   **rolls back, and drops the scratch DB** — so it catches everything PostgreSQL would (missing
   types, bad references, ordering, runtime errors) without ever touching your real data.

The connection string can also be supplied via the `PGPROJ_CONNECTION` environment variable.

## Commands

```
pgproj new project <name> [-o <dir>] [--default-schema public] [--target-version 18]   # scaffold an empty buildable project
pgproj add <kind> <schema.name> [-p <project|dir>] [--force]   # scaffold an object .sql from a template (kinds: table, view, function, procedure, trigger, sequence, type, schema, policy)
pgproj build    <project.pgproj> [-o model.json] [--strict] [--no-analyze]
pgproj script   <project.pgproj> [-o create.sql] [--no-transaction]
pgproj compare  <project.pgproj> --connection <conn> [--allow-drops]
pgproj publish  <project.pgproj> --connection <conn> [--dry-run] [--allow-drops] [--no-transaction] [--parallel]
pgproj validate <project.pgproj> --connection <conn>          # apply to a throwaway temp DB, rolled back
pgproj extract  --connection <conn> -o <outDir>               # live DB → buildable project
pgproj drift    <project.pgproj> --connection <conn>          # preview project files that differ from the DB
pgproj pull     <project.pgproj> --connection <conn> [--dry-run] [--allow-deletes]   # rewrite project files FROM the DB
pgproj analyze  <project.pgproj> [--strict]                   # static safety analysis
```

Worked examples live in [`sample/SampleDb`](./sample/SampleDb) (minimal) and
[`sample/AllFeaturesDb`](./sample/AllFeaturesDb) (a single schema exercising nearly every supported
feature).

## The `.pgproj` file

```xml
<Project Sdk="PgProj.Sdk/0.1.0" DefaultTargets="Build">
  <PropertyGroup>
    <Name>SampleDb</Name>
    <DefaultSchema>app</DefaultSchema>
    <TargetPostgresVersion>17</TargetPostgresVersion>
  </PropertyGroup>
</Project>
```

The SDK auto-includes every `**/*.sql` file in the project folder — no `<Build Include>` is
needed.  To opt out and control item inclusion yourself, add
`<EnableDefaultSqlItems>false</EnableDefaultSqlItems>` to the `<PropertyGroup>` and declare your
own `<Build Include="..." />` items.

It is intentionally MSBuild-shaped so Visual Studio's built-in SDK-style project support can open,
build, and **publish** it — see the [Visual Studio experience](./docs/VISUAL_STUDIO.md) (Route A: the
`PgProj.Sdk` MSBuild SDK, packable to NuGet; Route B: a scaffolded VSIX project system under
[`editors/vs/`](./editors/vs/README.md)). Files whose name starts with `_` are treated as non-source.

## Safety model

- **No accidental data loss.** The comparer never drops objects that exist on the server but not in
  the project; pass `--allow-drops` to opt in. `pull` never deletes project files unless you pass
  `--allow-deletes`.
- **Transactional deploys.** Scripts are wrapped in `BEGIN`/`COMMIT` (disable with `--no-transaction`),
  so a failed step rolls back cleanly.
- **Analysis gate.** `build` and `publish` run the static analyzer first — errors block; `--strict`
  makes warnings block too; `--no-analyze` skips it.
- **Preview everything.** `compare`, `drift`, and `publish --dry-run` show the plan before anything runs.

## Reporting bugs & requesting features

Found a statement the parser rejects that PostgreSQL accepts (or vice-versa), a wrong deploy plan,
or anything else? Please **[open an issue](https://github.com/ilijevicdenis/CodeCleaners.PgProj/issues)**.

A great bug report includes:

1. The **exact SQL** (minimal repro) and the **PostgreSQL version** you target.
2. What you **expected** vs. what `pgproj` did (the command you ran + its output — `build`/`validate`
   output includes the `file:line`).
3. Whether real PostgreSQL accepts/rejects it (e.g. the error and `SQLSTATE`), if known.

Parser/grammar gaps are especially welcome — each becomes a corpus case so it never regresses.

## Roadmap & progress

Where the project is heading and what has shipped — the master delivery tracker, updated on every
delivered milestone: **[docs/reference/PROGRESS.md](docs/reference/PROGRESS.md)**
(milestones mirror the [GitHub milestones](https://github.com/ilijevicdenis/CodeCleaners.PgProj/milestones)).

Known deferred items (tracked there with the technical reason): base-type introspection (needs C
I/O functions in the server), transform introspection (needs C internal-typed functions), NuGet
restore of `<PackageReference>` `.pgpkg`s (use `<ArtifactReference>` meanwhile), and the editable
table designer.

## License

© 2026 **CodeCleaners d.o.o.** — https://code-cleaners.com/. All rights reserved.

This software is **free to use** but **may not be modified, copied, redistributed, or otherwise
used beyond running it without the direct written approval of CodeCleaners d.o.o.** See
[`LICENSE`](./LICENSE) for the full terms.
