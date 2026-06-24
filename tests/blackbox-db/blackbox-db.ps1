# Start / stop / reset the two PostgreSQL 18 servers the BLACKBOX suite runs against, and print
# (and optionally export) the connection strings it expects. PowerShell-only per repo policy.
#
#   .\blackbox-db.ps1              # up + wait both healthy + print connection strings
#   .\blackbox-db.ps1 -Export      # same, but also set the env vars in the CURRENT session
#                                  #   (dot-source it for this to stick: . .\blackbox-db.ps1 -Export)
#   .\blackbox-db.ps1 -Down        # stop and remove both containers (named data kept until -Reset)
#   .\blackbox-db.ps1 -Reset       # remove containers + volumes, then start fresh (re-seeds source)
#
# The SOURCE server publishes 15432 — the same port as tests/sample-db — so this script stops the
# sample-db container first to avoid a port clash (they carry the identical seed; use either).
param(
    [switch]$Down,
    [switch]$Reset,
    [switch]$Export
)
$ErrorActionPreference = "Stop"
$composeDir = $PSScriptRoot
$compose = "$composeDir\docker-compose.yml"

function Wait-Healthy([string]$container) {
    for ($i = 0; $i -lt 60; $i++) {
        $state = docker inspect --format "{{.State.Health.Status}}" $container 2>$null
        if ($state -eq "healthy") { return $true }
        Start-Sleep -Seconds 2
    }
    return $false
}

if ($Down -or $Reset) {
    docker compose -f $compose down --volumes
    if ($Down) { "blackbox source+target stopped."; return }
}

# Free port 15432 if the standing sample-db container is holding it (identical seed; not run together).
$sample = docker ps -q --filter "name=pgproj-sample-db" 2>$null
if ($sample) {
    "stopping pgproj-sample-db (it holds port 15432; blackbox source reuses that port)…"
    docker stop pgproj-sample-db | Out-Null
}

docker compose -f $compose up -d
if (-not (Wait-Healthy "pgproj-bb-source")) { throw "pgproj-bb-source never became healthy - check 'docker logs pgproj-bb-source'." }
if (-not (Wait-Healthy "pgproj-bb-target")) { throw "pgproj-bb-target never became healthy - check 'docker logs pgproj-bb-target'." }

$sourceAdmin = 'Host=localhost;Port=15432;Username=postgres;Password=pgproj;Database=postgres'
$sourceSeed  = 'Host=localhost;Port=15432;Username=postgres;Password=pgproj;Database=sampledb'
$targetAdmin = 'Host=localhost;Port=15433;Username=postgres;Password=pgproj;Database=postgres'

""
"blackbox databases ready (postgres:18):"
"  SOURCE  container pgproj-bb-source  port 15432  (seeded: schemas sales/inventory/audit + data)"
"  TARGET  container pgproj-bb-target  port 15433  (empty; tests create their own throwaway DBs)"
""
"  the blackbox xUnit suite reads these two env vars:"
"    `$env:PGPROJ_SOURCE_CONNECTION = '$sourceAdmin'"
"    `$env:PGPROJ_TARGET_CONNECTION = '$targetAdmin'"
""
"  seeded sample database on the source (extract / compare / pull / data-compare):"
"    $sourceSeed"
""
"  from a Hyper-V test VM, replace localhost with the host's IP on the virtual switch and allow the"
"  two ports through the host firewall once:"
"    New-NetFirewallRule -DisplayName 'pgproj blackbox' -Direction Inbound -Protocol TCP -LocalPort 15432,15433 -Action Allow"

if ($Export) {
    $env:PGPROJ_SOURCE_CONNECTION = $sourceAdmin
    $env:PGPROJ_TARGET_CONNECTION = $targetAdmin
    ""
    "(env vars set in this session — dot-source the script for them to persist: . .\blackbox-db.ps1 -Export)"
}
