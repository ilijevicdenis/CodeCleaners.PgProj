# PgProj `dotnet new` templates

A `dotnet new` template pack mirroring `pgproj new project` / `pgproj add`, so project + object
creation matches .NET muscle memory.

| `dotnet new` short name | Produces |
|-------------------------|----------|
| `pgproj`          | An empty, buildable PgProj database project (`.pgproj` + folder layout + README) |
| `pgproj-table`    | `Tables/<schema>.<name>.sql` |
| `pgproj-view`     | `Views/<schema>.<name>.sql` |
| `pgproj-function` | `Functions/<schema>.<name>.sql` |
| `pgproj-procedure`| `Procedures/<schema>.<name>.sql` |
| `pgproj-trigger`  | `Triggers/<schema>.<name>.sql` |
| `pgproj-sequence` | `Sequences/<schema>.<name>.sql` |
| `pgproj-type`     | `Types/<schema>.<name>.sql` |
| `pgproj-policy`   | `Policies/<schema>.<name>.sql` |
| `pgproj-schema`   | `Schemas/<name>.sql` |

The object templates render **byte-identical** content to `pgproj add <kind>` (they are generated
from the same `TemplateCatalog`), so both paths stay in sync.

## Install / use (local, from this repo)

```bash
dotnet new install ./templates                 # install the pack from source
dotnet new pgproj -n MyDb --DefaultSchema app  # empty project → ./MyDb/MyDb.pgproj
cd MyDb
dotnet new pgproj-table --Schema app --Name Customer   # → Tables/app.Customer.sql
```

## Pack (NuGet)

```bash
dotnet pack ./templates/PgProj.Templates.csproj -o ./artifacts
# → CodeCleaners.PgProj.Templates.0.1.0.nupkg
```

## Follow-up — NuGet publish is deferred

Publishing this pack to NuGet.org (so it is installable as
`dotnet new install CodeCleaners.PgProj.Templates`) is intentionally **not wired up** here. A project
created **outside this repo** cannot yet resolve the PgProj SDK/parser — that depends on **EP-PKG #13**
(engine/CLI NuGet) and **EP-VS #25** (SDK package). Until those land, the generated `.pgproj` references
`PgProj.Sdk/0.1.0` and builds via the in-repo CLI. The pack is excluded from `PgProj.slnx` so it does not
affect the main build/test loop; `dotnet pack` produces the `.nupkg` on demand.
