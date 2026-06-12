# PgProj VM bootstrap — runs INSIDE the Hyper-V test VM (over ssh, elevated not required beyond
# the logged-on user). Registers the local PgProj.Sdk feed, installs the classic VSIX into the
# main VS 2026 instance, purges the MEF cache, and verifies the toolchain. The UI test run itself
# is started separately through a scheduled task so it executes in the INTERACTIVE session.
param([string]$PayloadDir = $PSScriptRoot)
$ErrorActionPreference = "Stop"
$log = @()

# 1) .NET 10 SDK present?
$sdks = (& dotnet --list-sdks) 2>$null
if (-not ($sdks -match "^10\.")) { throw ".NET 10 SDK missing in VM - install it first (winget install Microsoft.DotNet.SDK.10)." }
$log += "dotnet SDK ok: " + (($sdks -match "^10\.") | Select-Object -First 1)

# 2) local PgProj.Sdk feed (VS resolves Sdk="PgProj.Sdk/0.1.0" through the NuGet SDK resolver)
$feed = "C:\pgproj\feed"
New-Item -ItemType Directory -Force $feed | Out-Null
Copy-Item "$PayloadDir\feed\*.nupkg" $feed -Force
$existing = (& dotnet nuget list source) -join "`n"
if ($existing -notmatch "pgproj-local") { & dotnet nuget add source $feed --name pgproj-local | Out-Null }
# purge any cached copy so the freshly packed SDK wins
$cached = "$env:USERPROFILE\.nuget\packages\pgproj.sdk"
if (Test-Path $cached) { Remove-Item $cached -Recurse -Force }
$log += "feed registered: $feed"

# 3) close VS if running (test VM - nothing to lose), then install the VSIX
Get-Process devenv -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsPath = & $vswhere -latest -prerelease -products * -property installationPath
$vsixInstaller = Join-Path $vsPath "Common7\IDE\VSIXInstaller.exe"
& $vsixInstaller /quiet /uninstall:PgProj.VisualStudio.b0000000-0025 2>$null | Out-Null
Start-Process -FilePath $vsixInstaller -ArgumentList "/quiet", "`"$PayloadDir\PgProj.VisualStudio.ProjectSystem.vsix`"" -Wait
$log += "vsix installed"

# 4) rebuild config caches + purge the MEF ComponentModelCache (the VS 2026 per-user-extension trap)
Start-Process -FilePath (Join-Path $vsPath "Common7\IDE\devenv.exe") -ArgumentList "/updateconfiguration" -Wait
Get-ChildItem "$env:LOCALAPPDATA\Microsoft\VisualStudio" -Directory -Filter "18.0_*" | ForEach-Object {
    $cm = Join-Path $_.FullName "ComponentModelCache"
    if (Test-Path $cm) { Remove-Item $cm -Recurse -Force; $log += "cleared: $cm" }
}

# 5) verify install landed
$ext = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\VisualStudio\18.0_*\Extensions" -Recurse -Filter "PgProj.VisualStudio.ProjectSystem.dll" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $ext) { throw "extension dll not found under per-user Extensions after install" }
$log += "installed at: $($ext.DirectoryName)"

$log -join "`n"
"VM SETUP OK"
