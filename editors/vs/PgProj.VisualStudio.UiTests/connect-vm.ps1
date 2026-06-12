# Connects the host to the Hyper-V test VM for the PgProj UI-test workflow. Run **elevated** on
# the HOST (PowerShell Direct and the firewall rule both need admin). It:
#   1. opens a PowerShell Direct session into the VM (no network needed - goes via the hypervisor),
#   2. enables OpenSSH Server in the VM and authorizes the agent's public key,
#   3. opens the host firewall so the VM can reach the sample database (TCP 15432),
#   4. prints the VM's IP - hand that to the agent and it takes over via ssh.
#
#   .\connect-vm.ps1                  # picks the only running VM, prompts for VM credentials
#   .\connect-vm.ps1 -VMName MyVm     # explicit VM name
#Requires -RunAsAdministrator
param(
    [string]$VMName,
    [string]$PublicKeyPath = "$env:USERPROFILE\.ssh\pgproj-vm.pub"
)
$ErrorActionPreference = "Stop"

# ---- pick the VM ---------------------------------------------------------------------------
$running = Get-VM | Where-Object State -eq "Running"
if (-not $VMName) {
    if (@($running).Count -eq 1) { $VMName = $running[0].Name }
    else {
        "Running VMs:"; $running | ForEach-Object { "  - $($_.Name)" }
        $VMName = Read-Host "VM name"
    }
}
"Connecting to VM '$VMName' (PowerShell Direct)..."
$cred = Get-Credential -Message "Credentials of the account logged on INSIDE the VM"

# ---- bootstrap inside the VM ----------------------------------------------------------------
if (-not (Test-Path $PublicKeyPath)) { throw "Public key not found: $PublicKeyPath (the agent generates it as ~\.ssh\pgproj-vm)" }
$publicKey = (Get-Content $PublicKeyPath -Raw).Trim()

$result = Invoke-Command -VMName $VMName -Credential $cred -ArgumentList $publicKey -ScriptBlock {
    param([string]$key)
    $out = @()

    # OpenSSH Server (capability download needs Windows Update reachable; Tiny11 keeps WU)
    $cap = Get-WindowsCapability -Online -Name "OpenSSH.Server~~~~0.0.1.0"
    if ($cap.State -ne "Installed") {
        Add-WindowsCapability -Online -Name "OpenSSH.Server~~~~0.0.1.0" | Out-Null
        $out += "OpenSSH Server installed"
    } else { $out += "OpenSSH Server already present" }

    Set-Service sshd -StartupType Automatic
    Start-Service sshd

    # PowerShell as the ssh default shell (the agent drives the VM with PowerShell commands)
    New-Item -Path "HKLM:\SOFTWARE\OpenSSH" -Force | Out-Null
    New-ItemProperty -Path "HKLM:\SOFTWARE\OpenSSH" -Name DefaultShell `
        -Value "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe" -PropertyType String -Force | Out-Null

    # authorize the agent's key (admin accounts read administrators_authorized_keys)
    $f = "$env:ProgramData\ssh\administrators_authorized_keys"
    Set-Content $f $key -Encoding ascii
    icacls $f /inheritance:r /grant "Administrators:F" /grant "SYSTEM:F" | Out-Null
    $out += "agent key authorized"

    # firewall: the capability normally adds the sshd rule; make sure
    if (-not (Get-NetFirewallRule -Name "OpenSSH-Server-In-TCP" -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -Name "OpenSSH-Server-In-TCP" -DisplayName "OpenSSH Server (sshd)" `
            -Enabled True -Direction Inbound -Protocol TCP -Action Allow -LocalPort 22 | Out-Null
    }

    $ip = (Get-NetIPAddress -AddressFamily IPv4 |
        Where-Object { $_.IPAddress -like "172.*" -or $_.IPAddress -like "192.168.*" } |
        Select-Object -First 1).IPAddress
    $out += "VM user: $env:USERNAME"
    $out += "VM IP:   $ip"
    $out -join "`n"
}
$result

# ---- host side: let the VM reach the sample database ----------------------------------------
if (-not (Get-NetFirewallRule -DisplayName "pgproj sample db" -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName "pgproj sample db" -Direction Inbound -Protocol TCP -LocalPort 15432 -Action Allow | Out-Null
    "host firewall: TCP 15432 opened (sample db reachable from the VM)"
} else { "host firewall: rule already present" }

""
"Done. Tell the agent the VM IP above - it connects with:  ssh -i `$env:USERPROFILE\.ssh\pgproj-vm <user>@<ip>"
