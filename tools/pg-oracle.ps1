<#
.SYNOPSIS
  Ground-truth oracle for the PostgreSQL test corpus.

  Runs every case in a JSONL corpus file against a real PostgreSQL 18 server and
  reports whether Postgres' verdict (accepts / rejects the SQL) matches the case's
  declared `expect` ("ok" | "error"). This is the source of truth a corpus author
  must satisfy BEFORE committing cases: a case is only valid once Postgres agrees.

.DESCRIPTION
  Each case is executed against a throwaway database cloned from the `corpus_tmpl`
  template (which has tests/corpus/_fixture.sql preloaded). Normal cases run inside
  BEGIN; <sql> ROLLBACK; so they leave no trace and never collide with other agents'
  clones. Cases flagged "txn":"none" (transaction-control / CONCURRENTLY / VACUUM
  style statements that cannot run inside a transaction block) each get their own
  fresh clone and run unwrapped.

  Verdict rule:
    * Postgres raised an error  -> actual = "error"  (SQLSTATE captured)
    * Postgres ran clean        -> actual = "ok"
  A case PASSES when actual == expect.

.PARAMETER File
  Path to a .jsonl corpus file (one JSON object per line). Mutually exclusive with -Sql.

.PARAMETER Sql
  A single ad-hoc SQL string to check (prints the verdict). For quick probes.

.PARAMETER Expect
  With -Sql: the expected verdict ("ok"|"error") to compare against. Optional.

.PARAMETER Container
  Docker container name running postgres:18. Default: pgproj-pg18.

.PARAMETER Template
  Template database to clone per check. Default: corpus_tmpl.

.PARAMETER Json
  Emit the full result summary as JSON on stdout (for machine consumption).

.OUTPUTS
  Human summary (default) or JSON (-Json). Exit code 0 = all cases matched,
  1 = one or more mismatches, 2 = setup/usage error.

.EXAMPLE
  pwsh tools/pg-oracle.ps1 -File tests/corpus/create-table.jsonl

.EXAMPLE
  pwsh tools/pg-oracle.ps1 -Sql "CREATE TABLE x(a int,)" -Expect error
#>
[CmdletBinding(DefaultParameterSetName = 'File')]
param(
    [Parameter(ParameterSetName = 'File', Mandatory)] [string] $File,
    [Parameter(ParameterSetName = 'Sql',  Mandatory)] [string] $Sql,
    [Parameter(ParameterSetName = 'Sql')]             [string] $Expect,
    [string] $Container = 'pgproj-pg18',
    [string] $Template  = 'corpus_tmpl',
    [switch] $Json
)

$ErrorActionPreference = 'Stop'
$MARK = '@@@CASE::'

# All psql stderr is merged into stdout *inside the container* (sh -c '... 2>&1')
# so Windows PowerShell 5.1 never wraps native stderr lines in ErrorRecords.
function New-Clone {
    # Concurrent agents clone the same template; Postgres may transiently report the template as
    # "being accessed by other users" or serialize CREATE DATABASE. Retry with backoff.
    for ($attempt = 1; $attempt -le 8; $attempt++) {
        $name = 'corpus_chk_' + ([guid]::NewGuid().ToString('N').Substring(0, 12))
        $r = ("CREATE DATABASE $name TEMPLATE $Template;" |
              docker exec -i $Container sh -c "psql -U postgres -d postgres -v ON_ERROR_STOP=1 -f - 2>&1") | Out-String
        if ($r -notmatch 'ERROR:') { return $name }
        if ($attempt -eq 8) { throw "clone failed after 8 attempts: $r" }
        Start-Sleep -Milliseconds (200 * $attempt)
    }
}

function Drop-Clone([string] $name) {
    ("DROP DATABASE IF EXISTS $name (FORCE);" |
     docker exec -i $Container sh -c "psql -U postgres -d postgres -f - 2>&1") | Out-Null
}

# Run a batch of txn-wrappable cases in ONE psql session against $db.
# $cases: array of [pscustomobject]@{ id; sql }. Returns hashtable id -> @{actual; sqlstate; message}
function Invoke-Batch([string] $db, $cases) {
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine('\set VERBOSITY verbose')
    [void]$sb.AppendLine('\set ON_ERROR_STOP off')
    foreach ($c in $cases) {
        [void]$sb.AppendLine("\warn $MARK$($c.id)$MARK")
        [void]$sb.AppendLine('BEGIN;')
        [void]$sb.AppendLine($c.sql)
        [void]$sb.AppendLine(';')      # defensive terminator; harmless empty stmt if already terminated
        [void]$sb.AppendLine('ROLLBACK;')
    }
    [void]$sb.AppendLine("\warn ${MARK}__END__$MARK")
    $script = $sb.ToString()
    $raw = ($script | docker exec -i $Container sh -c "psql -U postgres -d $db -q -f - 2>&1") | Out-String
    return Parse-Output $raw $cases
}

# Run a single unwrapped (transaction-control) case in its own clone.
function Invoke-Solo($case) {
    $db = New-Clone
    try {
        $script = "\set VERBOSITY verbose`n\set ON_ERROR_STOP off`n\warn $MARK$($case.id)$MARK`n$($case.sql)`n\warn ${MARK}__END__$MARK`n"
        $raw = ($script | docker exec -i $Container sh -c "psql -U postgres -d $db -q -f - 2>&1") | Out-String
        return Parse-Output $raw @($case)
    }
    finally { Drop-Clone $db }
}

# Parse psql stderr (markers + ERROR lines, same stream, in order) into verdicts.
function Parse-Output([string] $raw, $cases) {
    $result = @{}
    foreach ($c in $cases) { $result[$c.id] = @{ actual = 'ok'; sqlstate = ''; message = '' } }
    $curId = $null
    foreach ($line in ($raw -split "`r?`n")) {
        if ($line -match [regex]::Escape($MARK) + '(.+?)' + [regex]::Escape($MARK)) {
            $curId = $Matches[1]
            continue
        }
        if ($null -ne $curId -and $curId -ne '__END__' -and $result.ContainsKey($curId)) {
            # verbose format: "...ERROR:  <SQLSTATE>: <message>"
            if ($line -match 'ERROR:\s+([0-9A-Z]{5}):\s*(.*)$') {
                if ($result[$curId].actual -eq 'ok') {
                    $result[$curId].actual   = 'error'
                    $result[$curId].sqlstate = $Matches[1]
                    $result[$curId].message  = $Matches[2].Trim()
                }
            }
            elseif ($line -match 'ERROR:\s+(.*)$' -and $result[$curId].actual -eq 'ok') {
                $result[$curId].actual  = 'error'
                $result[$curId].message = $Matches[1].Trim()
            }
        }
    }
    return $result
}

# --- ad-hoc single-SQL mode -----------------------------------------------------
if ($PSCmdlet.ParameterSetName -eq 'Sql') {
    $case = [pscustomobject]@{ id = 'adhoc'; sql = $Sql }
    $db = New-Clone
    try { $v = (Invoke-Batch $db @($case))['adhoc'] } finally { Drop-Clone $db }
    $actual = $v.actual
    $line = "actual=$actual"
    if ($v.sqlstate) { $line += " sqlstate=$($v.sqlstate)" }
    if ($v.message)  { $line += " :: $($v.message)" }
    if ($Expect) {
        $ok = ($actual -eq $Expect)
        Write-Host ("[{0}] expect={1} {2}" -f ($(if ($ok) {'PASS'} else {'FAIL'})), $Expect, $line)
        exit ($(if ($ok) { 0 } else { 1 }))
    }
    Write-Host $line
    exit 0
}

# --- batch file mode ------------------------------------------------------------
if (-not (Test-Path $File)) { Write-Error "corpus file not found: $File"; exit 2 }

$cases = @()
$lineNo = 0
foreach ($raw in Get-Content -LiteralPath $File) {
    $lineNo++
    $t = $raw.Trim()
    if (-not $t -or $t.StartsWith('//') -or $t.StartsWith('#')) { continue }
    try { $obj = $t | ConvertFrom-Json }
    catch { Write-Error "line ${lineNo}: invalid JSON: $t"; exit 2 }
    if (-not $obj.id -or -not $obj.sql -or -not $obj.expect) {
        Write-Error "line ${lineNo}: each case needs id, sql, expect"; exit 2
    }
    if ($obj.expect -notin @('ok', 'error')) {
        Write-Error "line ${lineNo}: expect must be 'ok' or 'error' (got '$($obj.expect)')"; exit 2
    }
    $cases += $obj
}

$ids = $cases | Group-Object id | Where-Object Count -gt 1 | ForEach-Object Name
if ($ids) { Write-Error "duplicate ids: $($ids -join ', ')"; exit 2 }

$wrapped = $cases | Where-Object { $_.txn -ne 'none' }
$solo    = $cases | Where-Object { $_.txn -eq 'none' }

$verdicts = @{}
if ($wrapped) {
    $db = New-Clone
    try {
        # chunk to keep any single psql payload reasonable
        $chunk = 200
        for ($i = 0; $i -lt $wrapped.Count; $i += $chunk) {
            $slice = $wrapped[$i..([Math]::Min($i + $chunk - 1, $wrapped.Count - 1))]
            $batch = Invoke-Batch $db $slice
            foreach ($k in $batch.Keys) { $verdicts[$k] = $batch[$k] }
        }
    }
    finally { Drop-Clone $db }
}
foreach ($c in $solo) {
    $r = Invoke-Solo $c
    $verdicts[$c.id] = $r[$c.id]
}

$mismatches = @()
foreach ($c in $cases) {
    $v = $verdicts[$c.id]
    if ($null -eq $v) { $v = @{ actual = '??'; sqlstate = ''; message = 'no verdict captured' } }
    if ($v.actual -ne $c.expect) {
        $mismatches += [pscustomobject]@{
            id       = $c.id
            expect   = $c.expect
            actual   = $v.actual
            sqlstate = $v.sqlstate
            message  = $v.message
            sql      = $c.sql
        }
    }
}

$summary = [pscustomobject]@{
    file       = $File
    total      = $cases.Count
    matched    = $cases.Count - $mismatches.Count
    mismatched = $mismatches.Count
    mismatches = $mismatches
}

if ($Json) {
    $summary | ConvertTo-Json -Depth 6
}
else {
    Write-Host ("{0}: {1}/{2} matched" -f $File, $summary.matched, $summary.total) -ForegroundColor $(if ($mismatches.Count) {'Yellow'} else {'Green'})
    foreach ($m in $mismatches) {
        Write-Host ("  MISMATCH {0}: expect={1} actual={2} {3} {4}" -f $m.id, $m.expect, $m.actual, $m.sqlstate, $m.message) -ForegroundColor Red
        Write-Host ("    sql: {0}" -f ($m.sql -replace '\s+', ' ')) -ForegroundColor DarkGray
    }
}

exit ($(if ($mismatches.Count) { 1 } else { 0 }))
