# Like vm-run-tests.ps1 but runs a FILTERED subset (dotnet test --filter) in the VM's interactive
# desktop session. Same scheduled-task + session self-heal mechanics; added for the EP-TESTGEN
# command-registration scenario so we don't run the whole 100+ scenario suite to validate one command.
param(
    [string]$PayloadDir = "C:\pgproj\payload",
    [string]$Filter = "ToolingOperationScenarios",
    [string]$DbConnection = "",
    [int]$TimeoutMinutes = 20
)
$ErrorActionPreference = "Stop"

# Reattach a disconnected desktop so UIA reads work (closing vmconnect disconnects the session).
$disc = (quser 2>$null | Select-String "Disc")
if ($disc) {
    $sessionId = ($disc.Line -split "\s+" | Where-Object { $_ -match "^\d+$" } | Select-Object -First 1)
    if ($sessionId) { tscon $sessionId /dest:console; Start-Sleep -Seconds 3; "reattached session $sessionId to console" }
}

$outFile = "C:\pgproj\uitest-filtered-output.txt"
New-Item -ItemType Directory -Force "C:\pgproj" | Out-Null
Remove-Item $outFile -ErrorAction SilentlyContinue

$inner = @"
`$env:PGPROJ_UITEST_DB = '$DbConnection'
dotnet test '$PayloadDir\UiTests' -v minimal --filter '$Filter' *> '$outFile'
Add-Content '$outFile' ("EXITCODE: " + `$LASTEXITCODE)
"@
Set-Content "C:\pgproj\run-inner-filtered.ps1" $inner -Encoding ascii

Unregister-ScheduledTask -TaskName pgproj-uitest-f -Confirm:$false -ErrorAction SilentlyContinue
$action = New-ScheduledTaskAction -Execute "powershell.exe" `
    -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File C:\pgproj\run-inner-filtered.ps1"
$principal = New-ScheduledTaskPrincipal -UserId ([System.Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit (New-TimeSpan -Minutes ($TimeoutMinutes + 10))
Register-ScheduledTask -TaskName pgproj-uitest-f -Action $action -Principal $principal -Settings $settings | Out-Null
Start-ScheduledTask -TaskName pgproj-uitest-f

$deadline = [DateTime]::UtcNow.AddMinutes($TimeoutMinutes)
while ([DateTime]::UtcNow -lt $deadline) {
    Start-Sleep -Seconds 5
    if ((Test-Path $outFile) -and (Select-String -Path $outFile -Pattern "EXITCODE:" -Quiet -ErrorAction SilentlyContinue)) { break }
}
Unregister-ScheduledTask -TaskName pgproj-uitest-f -Confirm:$false -ErrorAction SilentlyContinue
if (Test-Path $outFile) { Get-Content $outFile } else { "NO OUTPUT - task never produced $outFile" }
