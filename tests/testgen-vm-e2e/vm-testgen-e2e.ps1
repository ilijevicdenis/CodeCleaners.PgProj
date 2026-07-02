# EP-TESTGEN end-to-end, run INSIDE the Hyper-V test VM (headless, over ssh). It exercises the REAL feature
# chain with the published pgproj CLI: extract the sample DB -> `pgproj test generate` (emit the standalone
# xUnit project) -> `dotnet test` it. The generated fixture uses the env-var path (PGPROJ_TEST_CONNECTION),
# so it runs against the given PostgreSQL (a throwaway DB is created + dropped) with NO Docker in the VM.
#
# Connection PARTS are passed (not a full string) so nothing with ';' has to cross the ssh command line.
# Windows PowerShell 5.1 compatible (the VM's ssh default shell). Exits non-zero on any failure.
param(
    [Parameter(Mandatory)] [string]$CliDll,
    [Parameter(Mandatory)] [string]$WorkDir,
    [Parameter(Mandatory)] [string]$DbHost,
    [int]$DbPort = 15432,
    [string]$DbUser = "postgres",
    [string]$DbPassword = "pgproj",
    [string]$SampleDb = "sampledb"
)
$ErrorActionPreference = "Stop"
function Fail($msg) { Write-Host "E2E FAIL: $msg"; exit 1 }

$sourceConn = "Host=$DbHost;Port=$DbPort;Username=$DbUser;Password=$DbPassword;Database=$SampleDb"
$testConn   = "Host=$DbHost;Port=$DbPort;Username=$DbUser;Password=$DbPassword;Database=postgres"

if (Test-Path $WorkDir) { Remove-Item $WorkDir -Recurse -Force }
New-Item -ItemType Directory -Force "$WorkDir\Db" | Out-Null

Write-Host "== 1) extract sample DB with the published CLI =="
& dotnet $CliDll extract --connection $sourceConn -o "$WorkDir\Db"
if ($LASTEXITCODE -ne 0) { Fail "extract exited $LASTEXITCODE" }
$pgproj = Get-ChildItem "$WorkDir\Db" -Filter *.pgproj -Recurse | Select-Object -First 1 -ExpandProperty FullName
if (-not $pgproj) { Fail "no .pgproj produced by extract" }

Write-Host "== 2) generate the standalone xUnit project =="
& dotnet $CliDll test generate $pgproj -o "$WorkDir\Tests" --name "Sampledb.Tests"
if ($LASTEXITCODE -ne 0) { Fail "test generate exited $LASTEXITCODE" }

# Assert the emitted shape (project + fixtures + generated tests + regen-safe seed hooks + schema).
foreach ($f in @("Sampledb.Tests.csproj", "PgDatabaseFixture.cs", "PgTestBase.cs", "GlobalUsings.cs", "schema.sql")) {
    if (-not (Test-Path "$WorkDir\Tests\$f")) { Fail "missing generated file: $f" }
}
if (-not (Get-ChildItem "$WorkDir\Tests\Generated" -Filter *.g.cs -ErrorAction SilentlyContinue)) { Fail "no Generated\*.g.cs" }
if (-not (Get-ChildItem "$WorkDir\Tests\Seeds" -Filter *.Seed.cs -ErrorAction SilentlyContinue)) { Fail "no Seeds\*.Seed.cs" }
Write-Host "   generated project shape OK"

Write-Host "== 3) dotnet test (env-var path: throwaway DB on $DbHost, no Docker) =="
$env:PGPROJ_TEST_CONNECTION = $testConn
$log = "$WorkDir\dotnet-test.log"
& dotnet test "$WorkDir\Tests" -v minimal 2>&1 | Tee-Object -FilePath $log | Out-Null
$code = $LASTEXITCODE
Get-Content $log -Tail 6

$summaryMatch = Select-String -Path $log -Pattern "Passed!|Failed!" | Select-Object -Last 1
$summary = ""
if ($summaryMatch) { $summary = $summaryMatch.Line.Trim() }
if ($code -ne 0) { Fail "dotnet test exited $code -- $summary" }

# Guard against a vacuous 'all skipped' pass: require at least one real passing test.
$m = [regex]::Match($summary, "Passed:\s+(\d+)")
if (-not $m.Success -or [int]$m.Groups[1].Value -lt 1) { Fail "no passing tests -- $summary" }

Write-Host ""
Write-Host "E2E PASS -- $summary"
exit 0
