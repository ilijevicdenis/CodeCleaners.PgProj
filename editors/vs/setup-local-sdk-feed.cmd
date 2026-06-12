@echo off
setlocal enabledelayedexpansion

rem ============================================================================================
rem  Make `PgProj.Sdk` resolvable LOCALLY so a .pgproj that uses `<Project Sdk="PgProj.Sdk/0.1.0">`
rem  opens and builds in Visual Studio and `dotnet` WITHOUT publishing the SDK to nuget.org.
rem
rem  WHY: the New Project template emits `<Project Sdk="PgProj.Sdk/0.1.0">`. MSBuild's NuGet SDK
rem  resolver must find that package or VS reports "the referenced SDK cannot be found" and CPS
rem  refuses to load the project. This packs the SDK into a local folder feed and registers it as a
rem  user NuGet source, so the resolver restores it like any package. (Publishing to nuget.org later
rem  replaces this; same mechanism, public feed.)
rem
rem    Usage:  setup-local-sdk-feed.cmd
rem    Undo:   dotnet nuget remove source pgproj-local
rem ============================================================================================

set "ROOT=%~dp0"
set "FEED=%USERPROFILE%\.pgproj\sdk-feed"
if not exist "%FEED%" mkdir "%FEED%"

rem Clear the cached package + old feed nupkg so a SAME-VERSION repack is actually picked up
rem (NuGet treats a version as immutable and would otherwise reuse the stale cached copy).
if exist "%USERPROFILE%\.nuget\packages\pgproj.sdk" rmdir /s /q "%USERPROFILE%\.nuget\packages\pgproj.sdk"
if exist "%FEED%\PgProj.Sdk.0.1.0.nupkg" del /q "%FEED%\PgProj.Sdk.*.nupkg"

echo Packing PgProj.Sdk into the local feed...
dotnet pack "%ROOT%..\..\src\PgProj.Sdk\PgProj.Sdk.csproj" -c Release -o "%FEED%" --nologo -v:m
if errorlevel 1 ( echo ERROR: pack failed. & exit /b 1 )

rem register the source only if it is not already present
dotnet nuget list source | findstr /i "pgproj-local" >nul
if errorlevel 1 (
  dotnet nuget add source "%FEED%" --name pgproj-local
) else (
  echo NuGet source 'pgproj-local' already registered.
)

echo.
echo ============================================================================================
echo  Done. PgProj.Sdk is resolvable locally.
echo  In Visual Studio: reload the .pgproj (right-click the project -^> Reload Project, or reopen the
echo  solution). New Project -^> "PostgreSQL Database Project" will now load.
echo  Re-run this script after bumping the SDK version so the new package lands in the feed.
echo ============================================================================================
endlocal
