# PgProj.Sdk — MSBuild SDK for `.pgproj`

Lets a PostgreSQL **database project** build with the ordinary toolchain gesture — `dotnet build`,
a solution build, or the Visual Studio **Build** command — by routing MSBuild's Restore/Build/
Clean/Rebuild verbs to the `pgproj` CLI.

## Using it from source (this repo)

A `.pgproj` imports the SDK by relative path and declares its sources:

```xml
<Project DefaultTargets="Build">
  <Import Project="..\..\src\PgProj.Sdk\Sdk\Sdk.props" />
  <PropertyGroup>
    <Name>SampleDb</Name>
    <DefaultSchema>app</DefaultSchema>
  </PropertyGroup>
  <ItemGroup>
    <Build Include="**/*.sql" />
  </ItemGroup>
  <Import Project="..\..\src\PgProj.Sdk\Sdk\Sdk.targets" />
</Project>
```

Then:

```powershell
dotnet build sample/SampleDb/SampleDb.pgproj
```

The build parses every `.sql` file, validates the model, and writes the model artifact to
`bin/<Name>.model.json` — the equivalent of an SSDT `.sqlproj` producing a `.dacpac`.

## Publishing as a real SDK (roadmap)

The `Sdk/` folder follows the canonical MSBuild SDK layout (`Sdk.props` + `Sdk.targets`). Packaged
as a NuGet package named `PgProj.Sdk`, a project could then use the terse top-level form

```xml
<Project Sdk="PgProj.Sdk/0.1.0">
```

and MSBuild's SDK resolver would locate it — no relative `Import` needed. That packaging step (and
a Visual Studio project-system/VSIX for Server Explorer + Schema Compare parity) is tracked in
`BUGS.md` (LIM-008 / LIM-107).
