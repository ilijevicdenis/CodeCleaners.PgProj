# PgProj.Sdk — MSBuild SDK for `.pgproj`

Lets a PostgreSQL **database project** build and publish with the ordinary toolchain gesture —
`dotnet build`, a solution build, or the Visual Studio **Build** / **Publish** commands — by routing
MSBuild's Restore/Build/Clean/Rebuild/Publish verbs to the `pgproj` CLI.

This is **Route A** of EP-VS #25: make `.pgproj` a clean `dotnet build` citizen so Visual Studio's
built-in generic-project support can open, build, clean, and publish it without a custom project
system. (The full VSIX project system is **Route B**, scaffolded under [`editors/vs/`](../../editors/vs/README.md).)

## What the SDK does

| MSBuild verb | What it runs | Output |
|---|---|---|
| `Build` (default) | `pgproj build` | `bin/<Name>.model.json` (the `.dacpac` analogue) **and** `bin/<Name>.pgpkg` (portable package) |
| `Clean` | deletes the model + package | — |
| `Rebuild` | `Clean` then `Build` | as Build |
| `Publish` | `pgproj publish` / `pgproj script` | deploys to a server, or writes a deploy script |
| `Restore` | no-op (a DB project has nothing to restore) | — |

### Publish (right-click → Publish in Visual Studio)

`dotnet build -t:Publish` (or VS's Publish verb) runs the deploy engine. Configure it with properties
in the `.pgproj` or on the command line:

| Property | Meaning |
|---|---|
| `PgProjPublishConnection` | Npgsql connection string of the target server (required for a real publish) |
| `PgProjPublishProfile` | a `.pgpublish.json` profile (options + SQLCMD variables) |
| `PgProjPublishAllowDrops` | `true` → allow destructive changes |
| `PgProjPublishDryRun` | `true` → do not execute; write the deploy script instead |
| `PgProjPublishExtraArgs` | extra `pgproj publish` flags |

Three shapes:

```powershell
# Real publish: diff the project vs the live server and apply.
dotnet build MyDb.pgproj -t:Publish -p:PgProjPublishConnection="Host=localhost;Database=app;Username=postgres;Password=..."

# Diff dry-run against a server: write the incremental deploy script, no mutation.
dotnet build MyDb.pgproj -t:Publish -p:PgProjPublishConnection="..." -p:PgProjPublishDryRun=true

# Offline dry-run: no connection — write the full create script (preview, no server needed).
dotnet build MyDb.pgproj -t:Publish -p:PgProjPublishDryRun=true
```

Never hard-code a connection string in a committed `.pgproj`; pass it at publish time (or via the
`PGPROJ_CONNECTION` environment variable, which `pgproj` also honors).

## Two consumption modes

### 1. Packaged (the shipping experience): `Sdk="PgProj.Sdk/x.y.z"`

`dotnet pack src/PgProj.Sdk` produces `PgProj.Sdk.<version>.nupkg`. The package follows the canonical
MSBuild SDK layout (`Sdk/Sdk.props` + `Sdk/Sdk.targets` at the root) **and** carries a
framework-dependent publish of the `pgproj` CLI under `tools/`, so a consumer needs only the .NET
runtime — not this repo's source. A `.pgproj` then uses the terse top-level form:

```xml
<Project Sdk="PgProj.Sdk/0.1.0" DefaultTargets="Build">
  <PropertyGroup>
    <Name>MyDb</Name>
    <DefaultSchema>app</DefaultSchema>
    <TargetPostgresVersion>16</TargetPostgresVersion>
  </PropertyGroup>
</Project>
```

MSBuild's SDK resolver locates the package (from the configured NuGet feeds), imports the props/targets,
and the targets invoke `dotnet tools/PgProj.Cli.dll`. `DefaultTargets="Build"` makes a plain
`dotnet build` (and VS's Build button) run the `Build` target rather than no-opping — the scaffolder
and templates emit it for you.

> The package is **not** auto-published to nuget.org. Publishing is a deliberate, user-gated step
> (see the repo's CI/CD hard rule). To consume it without a public feed, point a `nuget.config` at a
> local folder feed containing the `.nupkg`.

### 2. From source (this repo): explicit `<Import>`

When working inside this repo you can import the SDK by relative path; the targets then auto-detect the
CLI **source** (`../../PgProj.Cli/PgProj.Cli.csproj`) and run it with `dotnet run`:

```xml
<Project DefaultTargets="Build">
  <Import Project="..\..\src\PgProj.Sdk\Sdk\Sdk.props" />
  <PropertyGroup>
    <Name>SampleDb</Name>
    <DefaultSchema>app</DefaultSchema>
  </PropertyGroup>
  <Import Project="..\..\src\PgProj.Sdk\Sdk\Sdk.targets" />
</Project>
```

This is how the `sample/*` projects build. No `<Build Include>` is needed — the SDK auto-includes
every `**/*.sql` (opt out with `<EnableDefaultSqlItems>false</EnableDefaultSqlItems>`).

## CLI resolution (override points)

The targets resolve the CLI command line in this order; set any property to override:

1. `PgProjCliExe` — a `pgproj`/`pgproj.exe` on PATH or a full path (invoked directly).
2. `PgProjCliDll` — a published `PgProj.Cli.dll` (invoked as `dotnet <dll>`). Auto-set from `tools/` in a packaged SDK.
3. `PgProjCliProject` — the CLI `.csproj` (invoked as `dotnet run`). Auto-detected from source.

## Validation

The model artifact a `.pgproj` build emits is **byte-identical** to a direct `pgproj build -o` of the
same project. The `.pgpkg` is byte-identical once the build timestamp is pinned
(`PGPROJ_BUILD_STAMP`) — the only non-deterministic field is the manifest's `createdUtc`.
