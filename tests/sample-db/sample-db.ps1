# Start (default), stop, or reset the pgproj sample PostgreSQL container and print the connection
# strings the test layers expect. PowerShell-only per repo policy.
#
#   .\sample-db.ps1            # up + wait healthy + print connection strings
#   .\sample-db.ps1 -Down      # stop and remove the container (data is kept in the container; a
#                              # reset re-runs the seed because -Reset removes it entirely)
#   .\sample-db.ps1 -Reset     # remove container + volume, then start fresh (re-seeds)
param(
    [switch]$Down,
    [switch]$Reset
)
$ErrorActionPreference = "Stop"
$composeDir = $PSScriptRoot

function Wait-Healthy {
    for ($i = 0; $i -lt 60; $i++) {
        $state = docker inspect --format "{{.State.Health.Status}}" pgproj-sample-db 2>$null
        if ($state -eq "healthy") { return $true }
        Start-Sleep -Seconds 2
    }
    return $false
}

if ($Down -or $Reset) {
    docker compose -f "$composeDir\docker-compose.yml" down --volumes
    if ($Down) { "sample db stopped."; return }
}

docker compose -f "$composeDir\docker-compose.yml" up -d
if (-not (Wait-Healthy)) { throw "pgproj-sample-db never became healthy - check 'docker logs pgproj-sample-db'." }

""
"sample db ready (postgres:18, container pgproj-sample-db, port 15432)."
""
"  integration tests (admin server conn; tests create their own throwaway DBs):"
"    `$env:PGPROJ_TEST_CONNECTION = 'Host=localhost;Port=15432;Username=postgres;Password=pgproj;Database=postgres'"
""
"  seeded sample database (extract / compare / VS E2E):"
"    Host=localhost;Port=15432;Username=postgres;Password=pgproj;Database=sampledb"
""
"  from a Hyper-V test VM, replace localhost with the host's IP on the virtual switch"
"  (and allow TCP 15432 through the host firewall once):"
"    New-NetFirewallRule -DisplayName 'pgproj sample db' -Direction Inbound -Protocol TCP -LocalPort 15432 -Action Allow"
