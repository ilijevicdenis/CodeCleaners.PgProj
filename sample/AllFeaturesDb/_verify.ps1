# Applies every object file (in _apply_order.txt) to a fresh `allfeatures` database
# in the pgproj-pg18 container, with ON_ERROR_STOP. Exits non-zero on first failure.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

docker exec -i pgproj-pg18 psql -U postgres -c "DROP DATABASE IF EXISTS allfeatures (FORCE);" -c "CREATE DATABASE allfeatures;" | Out-Null

$order = Get-Content (Join-Path $here '_apply_order.txt') |
    Where-Object { $_.Trim() -and -not $_.Trim().StartsWith('#') }

$fail = 0
foreach ($rel in $order) {
    $path = Join-Path $here $rel.Trim()
    $sql = Get-Content $path -Raw
    $out = $sql | docker exec -i pgproj-pg18 sh -c "psql -U postgres -d allfeatures -v ON_ERROR_STOP=1 -f - 2>&1"
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAIL  $rel" -ForegroundColor Red
        Write-Host $out
        $fail++
        break
    } else {
        Write-Host "ok    $rel"
    }
}
if ($fail -eq 0) { Write-Host "`nALL FILES APPLIED CLEANLY" -ForegroundColor Green }
exit $fail
