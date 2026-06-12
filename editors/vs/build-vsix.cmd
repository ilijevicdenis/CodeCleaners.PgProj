@echo off
setlocal enabledelayedexpansion

rem ============================================================================================
rem  Build the PgProj Visual Studio EXTENSION PACK (and its two child extensions) in dependency
rem  order, producing the single .vsix a user installs.
rem
rem    Usage:   build-vsix.cmd [Configuration]      (Configuration defaults to Debug; pass Release
rem                                                  to produce a release installer)
rem    Output:  PgProj.VisualStudio.ExtensionPack\bin\<Config>\net472\PgProj.VisualStudio.ExtensionPack.vsix
rem
rem  Why three builds and not one solution: the project type must be a classic in-proc (net472)
rem  VSSDK extension and the engine-linked commands/LSP must be a modern OOP (net10) extension -
rem  different toolchains (dotnet vs the VS full MSBuild), so they live outside any .slnx. The pack
rem  embeds the two prebuilt child vsixes, so order matters: children first, pack last.
rem ============================================================================================

set "CONFIG=%~1"
if "%CONFIG%"=="" set "CONFIG=Debug"
set "ROOT=%~dp0"

rem --- locate the VS 2026 full MSBuild (amd64) - a classic VSIX cannot be built with `dotnet` ---
set "MSBUILD="
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if exist "%VSWHERE%" (
  for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -prerelease -products * -requires Microsoft.Component.MSBuild -property installationPath`) do (
    if exist "%%i\MSBuild\Current\Bin\amd64\MSBuild.exe" set "MSBUILD=%%i\MSBuild\Current\Bin\amd64\MSBuild.exe"
  )
)
if not defined MSBUILD if exist "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe" set "MSBUILD=C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe"
if not defined MSBUILD (
  echo ERROR: Could not find the VS 2026 full MSBuild ^(amd64^). Install VS 2026 with the
  echo        "Visual Studio extension development" workload, then re-run.
  exit /b 1
)

echo Using MSBuild : "!MSBUILD!"
echo Configuration : %CONFIG%
echo.

echo [1/3] OOP extension ^(dotnet^)...
dotnet build "%ROOT%PgProj.VisualStudio\PgProj.VisualStudio.csproj" -c %CONFIG% --nologo -v:m
if errorlevel 1 ( echo. & echo ERROR: OOP extension build failed. & exit /b 1 )
echo.

echo [2/3] Project-system extension ^(VS MSBuild^)...
"!MSBUILD!" "%ROOT%PgProj.VisualStudio.ProjectSystem\PgProj.VisualStudio.ProjectSystem.csproj" -t:Rebuild -restore -p:Configuration=%CONFIG% -nologo -v:m
if errorlevel 1 ( echo. & echo ERROR: project-system build failed. & exit /b 1 )
echo.

echo [3/3] Marketplace reference pack ^(VS MSBuild^)...
"!MSBUILD!" "%ROOT%PgProj.VisualStudio.ExtensionPack\PgProj.VisualStudio.ExtensionPack.csproj" -t:Rebuild -restore -p:Configuration=%CONFIG% -nologo -v:m
if errorlevel 1 ( echo. & echo ERROR: extension pack build failed. & exit /b 1 )
echo.

set "OOP=%ROOT%PgProj.VisualStudio\bin\%CONFIG%\net10.0-windows\PgProj.VisualStudio.vsix"
set "PROJSYS=%ROOT%PgProj.VisualStudio.ProjectSystem\bin\%CONFIG%\net472\PgProj.VisualStudio.ProjectSystem.vsix"
set "PACK=%ROOT%PgProj.VisualStudio.ExtensionPack\bin\%CONFIG%\net472\PgProj.VisualStudio.ExtensionPack.vsix"
if not exist "%PACK%" ( echo ERROR: build reported success but the pack vsix is missing: "%PACK%" & exit /b 1 )

echo ============================================================================================
echo  BUILD SUCCEEDED. Artifacts ^(%CONFIG%^):
echo    project type : "%PROJSYS%"
echo    commands/LSP : "%OOP%"
echo    market pack  : "%PACK%"   ^(Marketplace upload only - not a local double-click installer^)
echo.
echo  To install LOCALLY now, run:   install-pgproj.cmd %CONFIG%
echo  ^(the two extensions cannot be merged into one installable vsix - an OOP extension cannot be
echo   embedded in a pack; the Marketplace installs both via the reference pack instead^)
echo ============================================================================================
endlocal
