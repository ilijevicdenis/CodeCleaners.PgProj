# Host-side driver for the EP-TESTGEN Hyper-V E2E. Publishes the pgproj CLI, ships it + the VM script to the
# test VM, and runs the end-to-end (extract -> `test generate` -> `dotnet test`) over ssh against the HOST's
# PostgreSQL. Prints the VM's PASS/FAIL summary and exits with the VM script's exit code.
#
# Prereqs: the test VM is reachable over ssh with the pgproj key, has the .NET 10 SDK, and can reach the
# host PostgreSQL (the seeded sample DB) at $HostDbIp:$DbPort. See tests/testgen-vm-e2e/README.md.
param(
    [string]$VmUser = "denis",
    [string]$VmIp = "192.168.127.177",
    [string]$SshKey = "$env:USERPROFILE\.ssh\pgproj-vm",
    [string]$HostDbIp = "192.168.112.1",   # host IP on the VM's Hyper-V (Default Switch) subnet
    [int]$DbPort = 15432,
    [string]$DbUser = "postgres",
    [string]$DbPassword = "pgproj",
    [string]$SampleDb = "sampledb",
    [string]$VmDir = "C:\pgproj\testgen-e2e"
)
$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$vm = "$VmUser@$VmIp"
$sshKeyArg = @("-i", $SshKey, "-o", "StrictHostKeyChecking=no")

Write-Host "== publish pgproj CLI (framework-dependent net10) =="
$stageParent = Join-Path $env:TEMP "pgproj-testgen-e2e"
$cliDir = Join-Path $stageParent "cli"
if (Test-Path $stageParent) { Remove-Item $stageParent -Recurse -Force }
& dotnet publish (Join-Path $repo "src\PgProj.Cli\PgProj.Cli.csproj") -c Release -f net10.0 -o $cliDir --nologo -v:m --self-contained false
if ($LASTEXITCODE -ne 0) { Write-Host "publish failed"; exit 1 }

Write-Host "== ship CLI + VM script to $vm ($VmDir) =="
ssh @sshKeyArg $vm "if (Test-Path '$VmDir') { Remove-Item '$VmDir' -Recurse -Force }; New-Item -ItemType Directory -Force '$VmDir' | Out-Null"
scp @sshKeyArg -r $cliDir "${vm}:$VmDir/" | Out-Null          # lands as $VmDir\cli
scp @sshKeyArg (Join-Path $PSScriptRoot "vm-testgen-e2e.ps1") "${vm}:$VmDir/vm-testgen-e2e.ps1" | Out-Null

Write-Host "== run E2E in the VM =="
$remote = "powershell -ExecutionPolicy Bypass -NoProfile -File $VmDir\vm-testgen-e2e.ps1 " +
          "-CliDll $VmDir\cli\PgProj.Cli.dll -WorkDir $VmDir\work " +
          "-DbHost $HostDbIp -DbPort $DbPort -DbUser $DbUser -DbPassword $DbPassword -SampleDb $SampleDb"
ssh @sshKeyArg $vm $remote
$code = $LASTEXITCODE

Write-Host ""
if ($code -eq 0) { Write-Host "VM E2E: PASS" } else { Write-Host "VM E2E: FAIL (exit $code)" }
exit $code
