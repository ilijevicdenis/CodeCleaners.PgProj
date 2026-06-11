#requires -Version 5.1
<#
  sign-oop.ps1 — sign the PgProj OOP (VisualStudio.Extensibility) extension vsix with a local
  self-signed code-signing certificate and trust it, so Visual Studio 2026 will install it locally.

  WHY THIS IS NEEDED — and its LIMIT
    VS refuses to install an UNSIGNED VisualStudio.Extensibility (out-of-process) extension
    ("not installable as it is not marked Experimental" / SignatureState=Unsigned). Signing with a
    trusted cert clears that gate. BUT signing alone does NOT give you a command-line install:
    `VSIXInstaller.exe` cannot install an OOP extension at all ("must unzip and call the finalizer
    instead") — only the VS IDE or the Marketplace runs that finalizer. So after running this script
    the signed+trusted vsix can be installed via **VS -> Extensions -> Manage Extensions ->
    Install from disk** (the IDE runs the finalizer). For just trying the features, **F5** the slnx
    into the experimental instance (no signing needed). For distribution, the **Marketplace** signs
    and finalizes for you. The classic project-system extension does NOT need any of this.

  RUNTIME NOTE (.NET version)
    The signer (OpenVsixSignTool) ships compiled against .NET Core 2.1, which is end-of-life and is
    NOT installed on this machine. We do NOT install 2.1. Instead we roll the tool forward to the
    LATEST installed .NET runtime via DOTNET_ROLL_FORWARD=LatestMajor (here that is .NET 10). So the
    tool always runs on the newest runtime present, never on 2.1.

  USAGE
    powershell -File editors/vs/sign-oop.ps1 [-Configuration Debug]
      -Configuration  Debug (default) or Release — which built vsix to sign.
    Run it in an interactive console: trusting the cert shows a one-time Windows Security dialog
    (click Yes). Then install the signed vsix from inside VS (Manage Extensions -> Install from disk).

  REMOVE the dev cert when done:
    Get-ChildItem Cert:\CurrentUser\My,Cert:\CurrentUser\Root,Cert:\CurrentUser\TrustedPublisher |
      Where-Object { $_.Subject -eq 'CN=PgProj Local Dev (CodeCleaners)' } | Remove-Item
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$subject = 'CN=PgProj Local Dev (CodeCleaners)'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$oop = Join-Path $root "PgProj.VisualStudio\bin\$Configuration\net10.0-windows\PgProj.VisualStudio.vsix"
if (-not (Test-Path $oop)) {
    throw "OOP vsix not found: $oop`nRun build-vsix.cmd $Configuration first."
}

# 1) self-signed code-signing cert in CurrentUser\My (reuse if it already exists).
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $subject } | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject -KeyUsage DigitalSignature `
        -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(5) `
        -FriendlyName 'PgProj Local Dev Signing'
    Write-Host "Created signing certificate $($cert.Thumbprint)"
}
else {
    Write-Host "Reusing signing certificate $($cert.Thumbprint)"
}

# 2) ensure the signer is installed (a .NET global tool).
if (-not (Get-Command OpenVsixSignTool -ErrorAction SilentlyContinue)) {
    Write-Host 'Installing OpenVsixSignTool (global tool)...'
    dotnet tool install --global OpenVsixSignTool | Out-Null
}

# 3) sign. Use the cert straight from the store by thumbprint (-s) — no PFX export.
#    DOTNET_ROLL_FORWARD=LatestMajor runs the 2.1-targeted tool on the latest installed runtime.
$env:DOTNET_ROLL_FORWARD = 'LatestMajor'
OpenVsixSignTool sign -s $cert.Thumbprint -fd sha256 -f $oop
if ($LASTEXITCODE -ne 0) { throw "OpenVsixSignTool failed with exit code $LASTEXITCODE." }

# 4) trust the cert so the signature validates:
#      TrustedPublisher — silent.
#      Root            — self-signed cert is its own root; VS needs a trusted chain. Importing to the
#                        CurrentUser Root store shows a one-time Windows Security confirm dialog.
$cer = Join-Path $env:TEMP 'pgproj-dev.cer'
Export-Certificate -Cert $cert -FilePath $cer | Out-Null
if (-not (Get-ChildItem Cert:\CurrentUser\TrustedPublisher | Where-Object { $_.Thumbprint -eq $cert.Thumbprint })) {
    Import-Certificate -FilePath $cer -CertStoreLocation Cert:\CurrentUser\TrustedPublisher | Out-Null
    Write-Host 'Added cert to CurrentUser\TrustedPublisher.'
}
if (-not (Get-ChildItem Cert:\CurrentUser\Root | Where-Object { $_.Thumbprint -eq $cert.Thumbprint })) {
    Write-Host 'Approve the Windows Security dialog to trust the signing certificate...'
    Import-Certificate -FilePath $cer -CertStoreLocation Cert:\CurrentUser\Root | Out-Null
    Write-Host 'Added cert to CurrentUser\Root.'
}
Remove-Item $cer -Force -ErrorAction SilentlyContinue
Write-Host ''
Write-Host "Signed and trusted: $oop"
Write-Host ''
Write-Host 'Next (VSIXInstaller.exe canNOT install an OOP extension - it needs the IDE finalizer):'
Write-Host '  In Visual Studio 2026:  Extensions -> Manage Extensions -> Install from disk -> pick the vsix above.'
Write-Host '  Or just F5 editors\vs\PgProj.VisualStudio.slnx for the experimental instance (no signing needed).'
