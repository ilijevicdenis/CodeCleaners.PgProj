@echo off
setlocal enabledelayedexpansion

rem ============================================================================================
rem  Install (or reinstall) the PgProj Visual Studio experience LOCALLY from the built vsixes.
rem
rem  It uninstalls any previous versions first, then installs the two extensions DIRECTLY via
rem  VSIXInstaller (each through its own correct install flow). This is the offline/private path:
rem  the two cannot be merged into one installable vsix because a VisualStudio.Extensibility (OOP)
rem  extension cannot be embedded/nested in a pack. The Marketplace handles "one click" instead
rem  (see PgProj.VisualStudio.ExtensionPack, reference form).
rem
rem    Usage:  install-pgproj.cmd [Configuration]     (Configuration defaults to Debug)
rem    Note:   CLOSE all Visual Studio instances first - VSIXInstaller cannot modify a running IDE.
rem            Run build-vsix.cmd [Configuration] beforehand to produce the vsixes.
rem ============================================================================================

set "CONFIG=%~1"
if "%CONFIG%"=="" set "CONFIG=Debug"
set "ROOT=%~dp0"

rem --- locate VSIXInstaller.exe (ships in the VS install) ---
set "VSIXI="
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if exist "%VSWHERE%" (
  for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -prerelease -products * -property installationPath`) do (
    if exist "%%i\Common7\IDE\VSIXInstaller.exe" set "VSIXI=%%i\Common7\IDE\VSIXInstaller.exe"
  )
)
if not defined VSIXI (
  echo ERROR: VSIXInstaller.exe not found. Install Visual Studio 2026.
  exit /b 1
)

set "OOP=%ROOT%PgProj.VisualStudio\bin\%CONFIG%\net10.0-windows\PgProj.VisualStudio.vsix"
set "PROJSYS=%ROOT%PgProj.VisualStudio.ProjectSystem\bin\%CONFIG%\net472\PgProj.VisualStudio.ProjectSystem.vsix"
if not exist "%OOP%"     ( echo ERROR: not found: "%OOP%"     - run build-vsix.cmd %CONFIG% first. & exit /b 1 )
if not exist "%PROJSYS%" ( echo ERROR: not found: "%PROJSYS%" - run build-vsix.cmd %CONFIG% first. & exit /b 1 )

echo Using VSIXInstaller : "!VSIXI!"
echo Configuration       : %CONFIG%
echo.
echo IMPORTANT: close every Visual Studio window before continuing, or the install will be deferred.
echo.

rem --- 1) uninstall any previous versions (quiet; ignore "not installed" failures) ---
echo Removing any previously installed PgProj extensions...
"!VSIXI!" /quiet /uninstall:PgProj.VisualStudio.Pack.b0000000-0026
"!VSIXI!" /quiet /uninstall:PgProj.VisualStudio.b0000000-0000-0000-0000-000000000025
"!VSIXI!" /quiet /uninstall:PgProj.VisualStudio.b0000000-0025
echo.

rem --- 2) install the CLASSIC project-system extension (this is the one VSIXInstaller CAN install) ---
echo Installing the project-system extension (.pgproj project type + templates)...
"!VSIXI!" "%PROJSYS%"
if errorlevel 1 ( echo ERROR: project-system install failed/cancelled. & exit /b 1 )

rem --- 3) force VS to rebuild its merged command-table / pkgdef caches. Without this, a reinstall
rem        that keeps the same extension version can leave the cached menu table stale and newly
rem        added context-menu buttons (e.g. Import Database) silently never appear. ---
set "DEVENV=!VSIXI:VSIXInstaller.exe=devenv.exe!"
if exist "!DEVENV!" (
  echo Refreshing the VS configuration caches - takes a moment...
  "!DEVENV!" /updateconfiguration
) else (
  echo WARNING: devenv.exe not found next to VSIXInstaller; run "devenv /updateconfiguration" manually.
)

echo.
echo ============================================================================================
echo  Project-system extension installed. Launch Visual Studio 2026 to use:
echo    - New Project -^> "PostgreSQL Database Project", Add New Item per object (grouped by schema),
echo      Build/Publish from Solution Explorer.
echo.
echo  The OOP extension (Publish / Schema Compare / .sql IntelliSense) is NOT installed here:
echo  VSIXInstaller cannot install a VisualStudio.Extensibility extension ("must unzip and call the
echo  finalizer"). Use ONE of these instead:
echo    - F5: open editors\vs\PgProj.VisualStudio.slnx in VS 2026 and press F5 (experimental instance).
echo    - VS Marketplace build (it runs the finalizer + signs); see editors\vs\README.md.
echo    - It is already signed at:
echo        "%OOP%"
echo      you can try VS -^> Extensions -^> Manage Extensions -^> Install from disk on that file.
echo ============================================================================================
endlocal
