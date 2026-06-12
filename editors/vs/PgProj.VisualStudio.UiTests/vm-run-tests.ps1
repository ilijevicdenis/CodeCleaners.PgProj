# Runs the PgProj UI tests in the VM's INTERACTIVE desktop session and streams the result back.
# An ssh session lives outside the interactive desktop, so launching devenv/UIA from it directly
# would not see the UI — instead this registers a one-shot scheduled task bound to the logged-on
# user's interactive session, runs it, waits, and prints the captured output.
#
# Implementation notes (each survived a real failure):
#   * the task body lives in an inner .ps1 FILE — a schtasks-style /tr command line is capped at
#     261 chars and an -EncodedCommand blows straight through that;
#   * ScheduledTasks cmdlets, not schtasks.exe — native stderr under ErrorActionPreference=Stop
#     in PS 5.1 turns into a terminating NativeCommandError over ssh.
param(
    [string]$PayloadDir = $PSScriptRoot,
    [string]$DbConnection = "",  # optional: PGPROJ_UITEST_DB for real-database mode
    [switch]$NoWait,             # start the task and return; poll C:\pgproj\uitest-output.txt yourself
    [int]$TimeoutMinutes = 35    # the 100+ scenario suite runs ~12-15 min; leave headroom
)
$ErrorActionPreference = "Stop"

# Self-heal a disconnected desktop: closing the Hyper-V vmconnect window (enhanced session = RDP)
# DISCONNECTS the session — the screen goes black, and every UIA read (Error List, popups, colors,
# screenshots) silently returns nothing while DTE keeps working. tscon reattaches the session to
# the VM's virtual console so the suite runs unattended. (Cost a full day of phantom failures.)
$disc = (quser 2>$null | Select-String "Disc")
if ($disc) {
    $sessionId = ($disc.Line -split "\s+" | Where-Object { $_ -match "^\d+$" } | Select-Object -First 1)
    if ($sessionId) { tscon $sessionId /dest:console; Start-Sleep -Seconds 3; "reattached session $sessionId to console" }
}

$outFile = "C:\pgproj\uitest-output.txt"
New-Item -ItemType Directory -Force "C:\pgproj" | Out-Null
Remove-Item $outFile -ErrorAction SilentlyContinue

$inner = @"
`$env:PGPROJ_UITEST_DB = '$DbConnection'
dotnet test '$PayloadDir\UiTests' -v minimal *> '$outFile'
Add-Content '$outFile' ("EXITCODE: " + `$LASTEXITCODE)
"@
Set-Content "C:\pgproj\run-inner.ps1" $inner -Encoding ascii

Unregister-ScheduledTask -TaskName pgproj-uitest -Confirm:$false -ErrorAction SilentlyContinue
# -WindowStyle Hidden is LOAD-BEARING: the task's console window otherwise opens on the
# interactive desktop and STEALS FOREGROUND from VS — typed test input (trigger chars, ESC)
# then lands in the console, completion popups never dismiss, and VS's COM message filter
# rejects DTE calls for minutes (the 2x120s busy-retry cascade).
$action = New-ScheduledTaskAction -Execute "powershell.exe" `
    -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File C:\pgproj\run-inner.ps1"
# Not $env:USERDOMAIN\$env:USERNAME: an sshd session may have no USERDOMAIN, yielding an
# unmappable "\user" (0x80070534). WindowsIdentity carries the real machine\user name.
$principal = New-ScheduledTaskPrincipal -UserId ([System.Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit (New-TimeSpan -Minutes ($TimeoutMinutes + 10))
Register-ScheduledTask -TaskName pgproj-uitest -Action $action -Principal $principal -Settings $settings | Out-Null
Start-ScheduledTask -TaskName pgproj-uitest

if ($NoWait) { "started (NoWait) - poll $outFile for 'EXITCODE:'"; return }

# Wait for the run to finish (VS launch + nuget restore + the whole scenario suite).
$deadline = [DateTime]::UtcNow.AddMinutes($TimeoutMinutes)
while ([DateTime]::UtcNow -lt $deadline) {
    Start-Sleep -Seconds 5
    if ((Test-Path $outFile) -and (Select-String -Path $outFile -Pattern "EXITCODE:" -Quiet -ErrorAction SilentlyContinue)) { break }
}
Unregister-ScheduledTask -TaskName pgproj-uitest -Confirm:$false -ErrorAction SilentlyContinue

if (Test-Path $outFile) { Get-Content $outFile } else { "NO OUTPUT - task never produced $outFile (is a user logged on to the desktop?)" }
