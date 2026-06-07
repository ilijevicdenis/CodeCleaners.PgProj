# Regenerates the parser-performance dashboard SVGs from the data arrays below.
#   pwsh docs/parser-perf-charts.ps1     (emits docs/parser-perf-journey.svg + parser-perf-buckets.svg)
# Numbers are BenchmarkDotNet PipelineBenchmarks allocated MB/op (the gate metric). When a new win lands,
# add its row to $journey / update $buckets, rerun, and commit the SVGs with docs/parser-performance.md.
# (No CI — this is a manual local step, per CLAUDE.md.)

$ErrorActionPreference = 'Stop'
$inv = [Globalization.CultureInfo]::InvariantCulture
function n($x) { return ([math]::Round([double]$x, 1)).ToString($inv) }   # coordinates: 1 dp
function v($x) { return ([double]$x).ToString('0.00', $inv) }             # MB values: 2 dp (matches dashboard)
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

# --- colours (colour-blind-safe Okabe-Ito) ---
$cPrior = '#9AA3AF'; $cRecent = '#0072B2'; $cStable = '#009E73'; $cFloor = '#CC79A7'

# =========================================================================================
# Chart 1 — the full "All"-bucket journey. group: prior (1-10) / recent (11-18) / stable (19)
# =========================================================================================
$journey = @(
  @{ tag='Base';     v=66.28; g='prior'  }
  @{ tag='Lazy';     v=60.41; g='prior'  }
  @{ tag='Views';    v=53.66; g='prior'  }
  @{ tag='Spans';    v=50.93; g='prior'  }
  @{ tag='OpLex';    v=48.44; g='prior'  }
  @{ tag='Intern';   v=40.82; g='prior'  }
  @{ tag='Static';   v=40.22; g='prior'  }
  @{ tag='Capture';  v=39.36; g='prior'  }
  @{ tag='Struct';   v=32.97; g='prior'  }
  @{ tag='ResCap';   v=31.22; g='prior'  }
  @{ tag='NameList'; v=30.48; g='recent' }
  @{ tag='Render';   v=29.39; g='recent' }
  @{ tag='DeadCR';   v=28.48; g='recent' }
  @{ tag='LazySelQ'; v=26.79; g='recent' }
  @{ tag='LazyAST';  v=25.88; g='recent' }
  @{ tag='Pool';     v=19.95; g='recent' }
  @{ tag='Render2';  v=19.59; g='recent' }
  @{ tag='Cursor';   v=18.94; g='recent' }
  @{ tag='Stable';   v=18.70; g='stable' }   # Workstation-GC re-baseline + F4/F5 + alloc guard tests
  @{ tag='Retoken';  v=17.77; g='floor'  }   # pool the raw-identity / table-tail / plpgsql re-tokenizations
  @{ tag='Quote';    v=17.25; g='floor'  }   # ReadQuoted no-escape fast path (no StringBuilder, intern literals)
  @{ tag='NoSet';    v=16.74; g='floor'  }   # drop per-table column-validation HashSets (linear scan)
  @{ tag='Presize';  v=16.16; g='floor'  }   # pre-size the per-file string interner
  @{ tag='LazyCons'; v=16.06; g='floor'  }   # lazy table constraint lists (Unique/FK/Check/Other allocate on first touch) — measured BDN endpoint
)

$N = $journey.Count
$y0 = 476.0; $yTop = 72.0; $yMax = 70.0          # value axis: 0 at y0, 70 at yTop
$px = ($y0 - $yTop) / $yMax
$slot = 64.0; $barW = 46.1; $offset = 9.0; $axisX = 64.0
$chartRight = 73 + $slot * ($N - 1) + $barW + 9
$W = $chartRight + 24; $H = 520

$first = $journey[0].v; $last = $journey[$N-1].v
$cutPct = n((($first - $last) / $first) * -100)
$parts = @()
$parts += "<svg xmlns='http://www.w3.org/2000/svg' width='$(n $W)' height='$H' viewBox='0 0 $(n $W) $H' font-family='Segoe UI,Helvetica,Arial,sans-serif'>"
$parts += "<rect width='$(n $W)' height='$H' fill='#ffffff'/>"
$parts += "<text x='64' y='30' font-size='17' font-weight='700' fill='#111827'>ParseAndBuild allocation, All corpus &#8212; $(v $first) to $(v $last) MB/op ($cutPct%)</text>"
$parts += "<text x='64' y='50' font-size='12' fill='#6b7280'>MB allocated per parse+build (lower is better) &#183; $N optimizations, in order &#183; Workstation GC</text>"
foreach ($g in 0,10,20,30,40,50,60,70) {
  $gy = n($y0 - $g * $px)
  $parts += "<line x1='64' y1='$gy' x2='$(n $chartRight)' y2='$gy' stroke='#eef2f7'/><text x='56' y='$(n ([double]$gy+4))' text-anchor='end' font-size='11' fill='#9ca3af'>$g</text>"
}
for ($i = 0; $i -lt $N; $i++) {
  $b = $journey[$i]
  $x = 73 + $slot * $i
  $h = $b.v * $px; $by = $y0 - $h
  $col = if ($b.g -eq 'prior') { $cPrior } elseif ($b.g -eq 'recent') { $cRecent } elseif ($b.g -eq 'stable') { $cStable } else { $cFloor }
  $cx = n($x + $barW / 2)
  $parts += "<rect x='$(n $x)' y='$(n $by)' width='$(n $barW)' height='$(n $h)' rx='2' fill='$col'/>"
  $parts += "<text x='$cx' y='468' transform='rotate(-90 $cx 468)' font-size='10' font-weight='600' fill='#ffffff'>$($b.tag)</text>"
  $parts += "<text x='$cx' y='$(n ([double]$by-5))' text-anchor='middle' font-size='9' font-weight='600' fill='#1f2937'>$(v $b.v)</text>"
}
# legend (4 groups)
$l1 = $chartRight - 690; $l2 = $chartRight - 500; $l3 = $chartRight - 320; $l4 = $chartRight - 150
$parts += "<rect x='$(n $l1)' y='66' width='14' height='14' fill='$cPrior'/><text x='$(n ([double]$l1+20))' y='78' font-size='12' fill='#374151'>prior effort (1-10)</text>"
$parts += "<rect x='$(n $l2)' y='66' width='14' height='14' fill='$cRecent'/><text x='$(n ([double]$l2+20))' y='78' font-size='12' fill='#374151'>recent (11-18)</text>"
$parts += "<rect x='$(n $l3)' y='66' width='14' height='14' fill='$cStable'/><text x='$(n ([double]$l3+20))' y='78' font-size='12' fill='#374151'>stability (19)</text>"
$parts += "<rect x='$(n $l4)' y='66' width='14' height='14' fill='$cFloor'/><text x='$(n ([double]$l4+20))' y='78' font-size='12' fill='#374151'>alloc floor (20-24)</text>"
$parts += "</svg>"
[IO.File]::WriteAllText((Join-Path $here 'parser-perf-journey.svg'), ($parts -join ''), [Text.UTF8Encoding]::new($false))

# =========================================================================================
# Chart 2 — recent effort across the 4 buckets: start (#10) vs after the recent wins (#19).
# =========================================================================================
$buckets = @(
  @{ name='All';    start=31.22; end=16.06; col='#0072B2' }
  @{ name='Raw';    start=19.25; end=9.53;  col='#D55E00' }
  @{ name='Select'; start=11.09; end=4.84;  col='#009E73' }
  @{ name='Table';  start=3.20;  end=1.66;  col='#E69F00' }
)
$by0 = 402.0; $byTop = 78.0; $bspan = $by0 - $byTop      # 0% at by0, 100% at byTop
$bW = 78.0; $gap = 16.0; $pitch = 273.0; $g1 = 114.5
$BW2 = 1180; $BH2 = 480
$p2 = @()
$p2 += "<svg xmlns='http://www.w3.org/2000/svg' width='$BW2' height='$BH2' viewBox='0 0 $BW2 $BH2' font-family='Segoe UI,Helvetica,Arial,sans-serif'>"
$p2 += "<rect width='$BW2' height='$BH2' fill='#ffffff'/>"
$p2 += "<text x='64' y='30' font-size='17' font-weight='700' fill='#111827'>Recent + floor wins (11-23) &#8212; allocation remaining per corpus bucket (lower is better)</text>"
$p2 += "<text x='64' y='50' font-size='12' fill='#6b7280'>before (start #10, grey) vs after the latest wins (coloured), normalized to each bucket's start = 100%</text>"
foreach ($g in 0,20,40,60,80,100) {
  $gy = n($by0 - ($g/100.0) * $bspan)
  $p2 += "<line x1='64' y1='$gy' x2='1156' y2='$gy' stroke='#eef2f7'/><text x='56' y='$(n ([double]$gy+4))' text-anchor='end' font-size='11' fill='#9ca3af'>$g%</text>"
}
for ($i = 0; $i -lt $buckets.Count; $i++) {
  $bk = $buckets[$i]
  $sx = $g1 + $pitch * $i
  $ax = $sx + $bW + $gap
  $pct = $bk.end / $bk.start
  $delta = n((($pct) - 1) * 100)
  $sh = $bspan; $sy = $byTop
  $ah = $pct * $bspan; $ay = $by0 - $ah
  $acx = n($ax + $bW / 2); $scx = n($sx + $bW / 2)
  $grpcx = n($sx + $bW / 2 - 8 + ($pitch - $bW - $gap) / 2 * 0)   # group label under start bar (matches original)
  # start (grey)
  $p2 += "<rect x='$(n $sx)' y='$(n $sy)' width='$(n $bW)' height='$(n $sh)' rx='2' fill='#9AA3AF'/><text x='$scx' y='72' text-anchor='middle' font-size='10' fill='#6b7280'>100%</text>"
  # after (coloured)
  $p2 += "<rect x='$(n $ax)' y='$(n $ay)' width='$(n $bW)' height='$(n $ah)' rx='2' fill='$($bk.col)'/>"
  $p2 += "<text x='$acx' y='394' transform='rotate(-90 $acx 394)' font-size='13' font-weight='700' fill='#ffffff'>$($bk.name)</text>"
  $p2 += "<text x='$acx' y='$(n ([double]$ay-6))' text-anchor='middle' font-size='11' font-weight='700' fill='$($bk.col)'>$(n ($pct*100))%</text>"
  # group label + delta under start bar
  $lblx = n($sx + $bW / 2 - 8)
  $p2 += "<text x='$lblx' y='422' text-anchor='middle' font-size='11.5' font-weight='600' fill='#374151'>$($bk.name)</text>"
  $p2 += "<text x='$lblx' y='439' text-anchor='middle' font-size='10.5' fill='#6b7280'>$(v $bk.start)&#8594;$(v $bk.end) MB &#183; $delta%</text>"
}
$p2 += "<rect x='856' y='66' width='14' height='14' fill='#9AA3AF'/><text x='876' y='78' font-size='12' fill='#374151'>start (100%)</text>"
$p2 += "<rect x='996' y='66' width='14' height='14' fill='#0072B2'/><text x='1016' y='78' font-size='12' fill='#374151'>after the latest wins</text>"
$p2 += "</svg>"
[IO.File]::WriteAllText((Join-Path $here 'parser-perf-buckets.svg'), ($p2 -join ''), [Text.UTF8Encoding]::new($false))

Write-Host "journey: $N bars, $(n $first)->$(n $last) MB ($cutPct%), width $(n $W)"
foreach ($bk in $buckets) { Write-Host ("  {0,-7} {1,6} -> {2,6} MB  ({3}%)" -f $bk.name, (n $bk.start), (n $bk.end), (n ((($bk.end/$bk.start)-1)*100))) }
