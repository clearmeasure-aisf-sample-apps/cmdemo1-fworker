#!/usr/bin/env pwsh
# Tier 1, all suites in parallel: one pwsh process per suite (run.ps1 -Suite <name>), each with its own ports, HOME and TMPDIR, so none can touch another.
# usage: pwsh tests/scenarios/run-all.ps1 [-Tier fast|full] [-Parallel 4] [-Suites a,b] [-Only <scenario substring>] [-NoBuild] [-KeepWork] [-LogDir <dir>] [-MinAvailableMb 1500] [-MinFreeDiskMb 2048] [-Force]
#   Start guards (guard.ps1): refuses with exit 4 when another run-all.ps1 or run.ps1 is active on this machine, or when the temp filesystem has less than
#   -MinFreeDiskMb free; with less than -MinAvailableMb of memory at the start it runs one suite at a time, and during the run no further suite is started
#   while memory or disk is below its floor. -Force starts anyway.
#   -Tier fast (default)  the curated subset: every scenario not tagged " [full]" in its name; the end of the run says how many scenarios and checks were left to the full tier
#   -Tier full            everything
#   Longest suite first, from suite-times.json (last known seconds per tier; rewritten at the end of every run). One line per suite as it finishes, a combined summary, exit 1 if anything failed.
#   A full run (no -Only) also rewrites scenario-stats.json (seconds and checks per scenario), which is where the "checks skipped" figure of a fast run comes from.
#   -Suites takes suite names and shards of a suite: name@g/n runs group g (from 0) of n (run.ps1 -Groups n -Group g, in the group's own port range
#   10000 + 1000 * slot + 100 * g, its result in name-gG.result.json), name@all/n runs all n groups through one run.ps1 that starts them (as qc runs
#   by itself). A suite is listed once: whole, as @all/n, or as shards of one n. The summary adds the shards of a suite up into its line; only a suite
#   run whole in one process (no shard, no @all) rewrites its time in suite-times.json.
#   The gateways under test run the v2 engine: needs FACTORY_TEST_SQL (a SQL Server connection string without a database; every process makes a database of its own).
#   A suite with scenarios that each need a gateway of their own with a test profile (run.ps1 -V2Profile: the suite 'saga') runs each group of scenarios as a job of its own ($ProfiledSuites below), in the
#   port range 10000 + 1000 * slot + 100 * (9 - index); such a suite cannot be sharded. A job whose scenarios that ran are not the ones listed fails the run.
param([ValidateSet('fast', 'full')][string] $Tier = 'fast', [int] $Parallel = 4, [string[]] $Suites = @(), [string] $Only = '', [switch] $NoBuild, [switch] $KeepWork, [string] $LogDir = '', [int] $MinAvailableMb = 1500, [int] $MinFreeDiskMb = 2048, [switch] $Force)
$Suites = @($Suites | ForEach-Object { $_ -split ',' } | Where-Object { $_ })   # pwsh -File passes "a,b" as one string
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
# No pwsh this starts (the listings, every suite) reads or writes a .NET multicore-JIT startup profile in any HOME (the real one or a suite's): a corrupt one makes every later pwsh exit 139 (issue 375).
# The runtime reads the value as hex (0x65535, 415029), which exceeds any processor count: multicore JIT is off.
if (-not $env:DOTNET_MultiCoreJitMinNumCpus) { $env:DOTNET_MultiCoreJitMinNumCpus = '65535' }
. (Join-Path $here 'guard.ps1')
if (Assert-TestRunMayStart 'run-all.ps1' ([bool]$Force) $MinFreeDiskMb $MinAvailableMb) { $Parallel = 1 }
$env:FACTORY_TEST_RUN_HELD = "$PID"
$root = Split-Path -Parent (Split-Path -Parent $here)
if (-not "$env:FACTORY_TEST_SQL".Trim()) { Write-Host 'the suites need a SQL Server: FACTORY_TEST_SQL (a connection string without a database) is not set'; exit 2 }
$timesFile = Join-Path $here 'suite-times.json'; $statsFile = Join-Path $here 'scenario-stats.json'
$baseTmp = [IO.Path]::GetTempPath()
if (-not $LogDir) { $LogDir = Join-Path $baseTmp "factory-run-all-$(Get-Date -Format 'yyyyMMdd-HHmmss')" }
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$wall = [Diagnostics.Stopwatch]::StartNew()

if (-not $NoBuild) {
    foreach ($proj in 'tests/FakeGitHub', 'gateway') {
        Push-Location (Join-Path $root $proj)
        try { $out = & dotnet build -c Release --no-restore -nodeReuse:false -p:UseSharedCompilation=false -v q 2>&1; if ($LASTEXITCODE -ne 0) { $out | Select-Object -Last 20 | ForEach-Object { Write-Host $_ }; throw "build of $proj failed" } } finally { Pop-Location }
        Write-Host "built $proj"
    }
}

$scen = @(& pwsh -NoProfile -File (Join-Path $here 'run.ps1') -ListScenarios | Where-Object { $_ } | ForEach-Object { $f = $_ -split "`t"; [pscustomobject]@{ suite = $f[0]; name = $f[1]; tier = $f[2] } })
$names = @($scen | ForEach-Object { $_.suite } | Select-Object -Unique)
if ($names.Count -eq 0) { throw 'run.ps1 -ListScenarios listed no scenarios' }
$timesAll = @{}; if (Test-Path $timesFile) { $timesAll = Get-Content -Raw $timesFile | ConvertFrom-Json -AsHashtable }
$known = @{}; if ($timesAll.ContainsKey($Tier)) { foreach ($e in $timesAll[$Tier].GetEnumerator()) { $known[$e.Key] = [double]$e.Value } }
# the jobs: one per suite, or one per shard (label = the suite's name, or name-gG for group G); a plain name runs the suite in one process (-Groups 1)
$todo = @()
# Suites whose scenarios each need a gateway of their own with a test profile of run.ps1 (-V2Profile): each entry is a job (run.ps1 -Suite <suite> -Only <regex> -V2Profile <profile>). Together the entries
# must list every scenario of the suite exactly once (checked below), so a scenario added to the suite needs a regular expression here. W9.10d (#552) and W9.10j (#562) say why each runs alone.
$ProfiledSuites = @{
    saga = @(
        @{ only = '^saga: a docs item'; profile = 'saga-docs' }                 # the docs plan: the engine's intake pulls the item, the owner session designs, builds and verifies it, the saga merges it once
        @{ only = '^saga: a code item'; profile = 'saga-feature' }              # the feature plan, the same flow with a code item
        @{ only = '^saga: sweep step: a merged code item'; profile = 'saga-feature' }   # the item steps of the saga (master-green, master-broken, the demotion of a closed PASS, a person's merge)
        @{ only = '^saga: sweep step: a red master'; profile = 'saga-feature' }
        @{ only = '^saga: sweep step: a PASS whose'; profile = 'saga-feature' }
        @{ only = '^saga: sweep step: a merge by a person'; profile = 'saga-feature' }
        @{ only = '^saga: slots: '; profile = 'saga-feature' }                  # the scheduler on (the scenario turns scheduler.featureSlots and the fake Octopus runtime on itself)
        @{ only = '^saga: adoption: a start'; profile = 'saga-feature' }
    )
}
foreach ($spec in $(if ($Suites.Count) { $Suites } else { $names })) {
    $m = [regex]::Match($spec, '^(?<s>[^@]+?)(@(?<g>all|\d+)/(?<n>\d+))?$')
    if (-not $m.Success -or $names -notcontains $m.Groups['s'].Value) { throw "unknown suite in -Suites: '$spec'; known: $($names -join ', ') (a shard of one: name@g/n or name@all/n)" }
    $sn = $m.Groups['s'].Value
    if ($ProfiledSuites.ContainsKey($sn)) {
        if ($m.Groups['n'].Success) { throw "-Suites ${spec}: the scenarios of $sn each run on a profile of their own: they cannot be sharded" }
        $k = 0
        foreach ($e in $ProfiledSuites[$sn]) { $todo += @{ label = "$sn~$k"; suite = $sn; groups = 1; group = -1; only = $e.only; profile = $e.profile; portBase = "@$(9 - $k)"; listed = -1; profiled = $true }; $k++ }
        continue
    }
    if (-not $m.Groups['n'].Success) { $todo += @{ label = $sn; suite = $sn; groups = 1; group = -1; only = ''; profile = 'plain'; portBase = ''; listed = -1 }; continue }
    $gn = [int]$m.Groups['n'].Value
    if ($gn -lt 1 -or $gn -gt 10) { throw "-Suites ${spec}: n is 1 to 10 (run.ps1 -Groups)" }
    if ($m.Groups['g'].Value -eq 'all') { $todo += @{ label = $sn; suite = $sn; groups = $gn; group = -1; only = ''; profile = 'plain'; portBase = ''; listed = -1 }; continue }
    $gg = [int]$m.Groups['g'].Value
    if ($gg -ge $gn) { throw "-Suites ${spec}: g is 0 to n-1 (groups are numbered from 0)" }
    $todo += @{ label = "$sn-g$gg"; suite = $sn; groups = $gn; group = $gg; only = ''; profile = 'plain'; portBase = ''; listed = -1 }
}
foreach ($sn in @($todo | ForEach-Object { $_.suite } | Select-Object -Unique)) {
    $same = @($todo | Where-Object { $_.suite -eq $sn -and -not $_.profiled })
    if ($same.Count -gt 1 -and (@($same | Where-Object { $_.group -lt 0 }).Count -or @($same | ForEach-Object { $_.groups } | Select-Object -Unique).Count -gt 1 -or @($same | ForEach-Object { $_.group } | Select-Object -Unique).Count -ne $same.Count)) {
        throw "-Suites: $sn is listed more than once (list it whole, as $sn@all/n, or as shards $sn@g/n of one n with each g once)"
    }
}
# the port slot of each suite (run.ps1 -ListSuites, in slot order): a shard's port range is 10000 + 1000 * slot + 100 * group, as run.ps1 gives its groups
$slots = @(); if (@($todo | Where-Object { $_.group -ge 0 -or "$($_.portBase)" -like '@*' }).Count) { $slots = @(& pwsh -NoProfile -File (Join-Path $here 'run.ps1') -ListSuites | Where-Object { $_ }) }
# scenarios this run leaves out: the full-tier ones (only when -Tier fast), and the suites that have nothing left to run are not started at all
$todoSuites = @($todo | ForEach-Object { $_.suite } | Select-Object -Unique)
$leftOut = @(); if ($Tier -eq 'fast') { $leftOut = @($scen | Where-Object { $_.tier -eq 'full' -and $todoSuites -contains $_.suite -and (-not $Only -or $_.name -match $Only) }) }
if ($Tier -eq 'fast' -and -not $Only) { $todo = @($todo | Where-Object { $sn = $_.suite; @($scen | Where-Object { $_.suite -eq $sn -and $_.tier -eq 'fast' }).Count -gt 0 }) }
# longest first (a shard as its share of the suite's time); a suite without a known duration goes first (it could be the long one)
$queue = [System.Collections.Generic.List[object]]::new(); foreach ($j in ($todo | Sort-Object { $d = $(if ($_.group -ge 0) { $_.groups } else { 1 }); if ($known.ContainsKey($_.suite)) { -$known[$_.suite] / $d } else { -1e9 } })) { $queue.Add($j) }
$jobOf = @{}; foreach ($j in $todo) { $jobOf[$j.label] = $j }
# The scenarios each job is to run (run.ps1 -ListScenarios, the same selection as the job's own), so that a job that lists none fails the run and a job that ran others is told. -Only does not
# reach a profiled suite's job as such: the job's own regular expression is narrowed by it (a lookahead for the -Only text at the start of the name).
$jobOnly = @{}
foreach ($j in $todo) {
    $jo = "$($j.only)"
    if ($Only) { $jo = $(if ($j.profiled) { '^(?=.*(?:' + $Only + '))' + ($j.only -replace '^\^', '') } else { $Only }) }
    $jobOnly[$j.label] = $jo
}
$empty = @()
foreach ($j in $todo) {
    $la = @('-NoProfile', '-File', (Join-Path $here 'run.ps1'), '-ListScenarios', '-Suite', $j.suite, '-Tier', $Tier)
    if ($jobOnly[$j.label]) { $la += @('-Only', $jobOnly[$j.label]) }; if ($j.groups -gt 1 -and $j.group -ge 0) { $la += @('-Groups', $j.groups, '-Group', $j.group) }
    $j.listed = @(& pwsh @la | Where-Object { $_ -match '^[^\t]+\t[^\t]+\t(fast|full)$' }).Count
    if ($j.listed -eq 0 -and -not ($j.groups -gt 1 -and $j.group -ge 0) -and -not $Only -and $Tier -eq 'full') { $empty += "$($j.suite)$(if ($j.only) { " /$($j.only)/" })" }
}
if ($empty.Count) { throw "the job lists no scenario (a renamed suite or scenario?): $($empty -join '; ')" }
# the jobs of a profiled suite list its scenarios once each: no scenario of the suite is left to a plain profile, none runs twice
foreach ($sn in @($todo | Where-Object { $_.profiled } | ForEach-Object { $_.suite } | Select-Object -Unique)) {
    $la = @('-NoProfile', '-File', (Join-Path $here 'run.ps1'), '-ListScenarios', '-Suite', $sn, '-Tier', $Tier); if ($Only) { $la += @('-Only', $Only) }
    $all = @(& pwsh @la | Where-Object { $_ -match '^[^\t]+\t[^\t]+\t(fast|full)$' }).Count
    $sum = ($todo | Where-Object { $_.suite -eq $sn } | ForEach-Object { $_.listed } | Measure-Object -Sum).Sum
    if ($all -ne $sum) { throw "`$ProfiledSuites of run-all.ps1: the jobs of $sn list $sum scenarios, the suite has $all (a scenario added to the suite needs a regular expression there, or two expressions match one)" }
}

function Get-StatKey([string]$Name) { $Name -replace '\s\[full\]\s*$', '' }   # the tier tag is not part of a scenario's identity
function Get-MemAvailableMb { try { [int]((Select-String -Path /proc/meminfo -Pattern '^MemAvailable:\s+(\d+)').Matches[0].Groups[1].Value) / 1024 } catch { 99999 } }
function Stop-Tree([int]$ProcId) {
    foreach ($c in @(& ps -o pid= --ppid $ProcId 2>$null)) { if ("$c".Trim()) { Stop-Tree ([int]"$c".Trim()) } }
    try { Stop-Process -Id $ProcId -Force -ErrorAction SilentlyContinue } catch { }
}

$running = @{}      # name -> @{ proc; sw; result; log; work }
$results = [ordered]@{}
$minAvail = Get-MemAvailableMb; $startAvail = $minAvail; $peakRunning = 0
$jobsText = $(if (@($todo | Where-Object { $_.groups -gt 1 }).Count) { "$(@($todo | ForEach-Object { $_.suite } | Select-Object -Unique).Count) suites in $($todo.Count) jobs" } else { "$($todo.Count) suites" })
Write-Host "run-all: tier $Tier, $jobsText, $Parallel at a time, logs in $LogDir, memory available $([int]$startAvail) MB"
try {
    while ($queue.Count -gt 0 -or $running.Count -gt 0) {
        $avail = Get-MemAvailableMb; if ($avail -lt $minAvail) { $minAvail = $avail }
        # start the next suite while there is a free slot (and memory: a suite is ~0.5 GB)
        while ($queue.Count -gt 0 -and $running.Count -lt $Parallel -and ($running.Count -eq 0 -or ((Get-MemAvailableMb) -ge $MinAvailableMb -and (Get-TempFreeDiskMb) -ge $MinFreeDiskMb))) {
            $job = $queue[0]; $queue.RemoveAt(0); $name = $job.label
            $work = Join-Path $LogDir "work-$name"; New-Item -ItemType Directory -Force -Path $work | Out-Null
            $resultFile = Join-Path $LogDir "$name.result.json"; $log = Join-Path $LogDir "$name.log"
            $args2 = @('-NoProfile', '-File', (Join-Path $here 'run.ps1'), '-Suite', $job.suite, '-Tier', $Tier, '-Groups', $job.groups, '-NoBuild', '-ResultFile', $resultFile); if ($job.group -ge 0) { $args2 += @('-Group', $job.group) }; if ($Only -and -not $job.profiled) { $args2 += @('-Only', $Only) }
            $args2 += @('-V2Profile', $job.profile)
            if ($job.profiled) { $jo = $jobOnly[$name]; $args2 += @('-Only', $(if ($jo -match '\s') { '"' + $jo + '"' } else { $jo })) }
            $shardBase = ''; if ($job.group -ge 0) { $slot = $slots.IndexOf($job.suite); if ($slot -lt 0) { throw "run.ps1 -ListSuites does not list $($job.suite)" }; $shardBase = "$(10000 + 1000 * $slot + 100 * $job.group)" }
            if ("$($job.portBase)" -like '@*') { $slot = $slots.IndexOf($job.suite); if ($slot -lt 0) { throw "run.ps1 -ListSuites does not list $($job.suite)" }; $shardBase = "$(10000 + 1000 * $slot + 100 * [int]($job.portBase.Substring(1)))" }
            $oldTmp = $env:TMPDIR; $env:TMPDIR = $work; Remove-Item Env:TEST_PORT_BASE -ErrorAction SilentlyContinue; if ($shardBase) { $env:TEST_PORT_BASE = $shardBase }
            try { $p = Start-Process pwsh -ArgumentList $args2 -PassThru -RedirectStandardOutput $log -RedirectStandardError (Join-Path $LogDir "$name.err") } finally { $env:TMPDIR = $oldTmp; Remove-Item Env:TEST_PORT_BASE -ErrorAction SilentlyContinue }
            $running[$name] = @{ proc = $p; sw = [Diagnostics.Stopwatch]::StartNew(); result = $resultFile; log = $log; work = $work }
            if ($running.Count -gt $peakRunning) { $peakRunning = $running.Count }
        }
        Start-Sleep -Milliseconds 1000
        foreach ($name in @($running.Keys)) {
            $r = $running[$name]
            if (-not $r.proc.HasExited) { continue }
            $secs = [math]::Round($r.sw.Elapsed.TotalSeconds, 1)
            $res = if (Test-Path $r.result) { Get-Content -Raw $r.result | ConvertFrom-Json -AsHashtable } else { $null }
            if (-not $res) { $res = [ordered]@{ suite = $jobOf[$name].suite; checks = 0; failed = 1; failures = @("${name}: the suite process ended without a result (exit $($r.proc.ExitCode)); see $($r.log)"); seconds = $secs; verbCalls = 0; verbSeconds = 0; gatewayVerbLines = 0 } }
            # the scenarios that ran are the ones the job lists (a group that was cut, or an -Only that matched another set, would pass with fewer)
            if ($res.checks -gt 0 -and $jobOf[$name].listed -ge 0 -and @($res.scenarios).Count -ne $jobOf[$name].listed) { $res.failed = [int]$res.failed + 1; $res.failures = @($res.failures) + "${name}: $(@($res.scenarios).Count) scenarios ran, $($jobOf[$name].listed) listed" }
            $results[$name] = $res; $running.Remove($name)
            $perVerb = if ($res.verbCalls -gt 0) { '{0:N2}s/verb' -f ($res.verbSeconds / $res.verbCalls) } else { '-' }
            Write-Host ("  {0,-4} {1,-18} checks {2,4}  failed {3,2}  {4,6:N0}s  verbs {5,4} ({6})" -f $(if ($res.failed -eq 0) { 'done' } else { 'FAIL' }), $name, $res.checks, $res.failed, $res.seconds, $res.verbCalls, $perVerb)
            if ($res.failed -eq 0 -and -not $KeepWork) { Remove-Item -Recurse -Force -Path $r.work -ErrorAction SilentlyContinue }
        }
    }
} finally {
    foreach ($r in $running.Values) { Stop-Tree $r.proc.Id }   # only what this run started
}
$wall.Stop()

# ---- combined summary ----------------------------------------------------------------------------------------------------
$sumSecs = 0.0; $checks = 0; $failed = 0; $verbs = 0; $verbSecs = 0.0
foreach ($n in $results.Keys) { $x = $results[$n]; $sumSecs += $x.seconds; $checks += $x.checks; $failed += $x.failed; $verbs += $x.verbCalls; $verbSecs += $x.verbSeconds }
Write-Host ""
Write-Host ("{0,-18} {1,5} {2,7} {3,7} {4,8} {5,7} {6,9}" -f 'suite', 'scen.', 'checks', 'failed', 'seconds', 'verbs', 's/verb')
# one line per suite: the jobs of a sharded suite are added up into it (scenarios, checks, failures, seconds of all its shards, verbs)
foreach ($n in $names) {
    $parts = @($results.Keys | Where-Object { $jobOf[$_].suite -eq $n }); if (-not $parts.Count) { continue }
    $x = [ordered]@{ scen = 0; checks = 0; failed = 0; seconds = 0.0; verbCalls = 0; verbSeconds = 0.0 }
    foreach ($p0 in $parts) { $y = $results[$p0]; $x.scen += @($y.scenarios).Count; $x.checks += $y.checks; $x.failed += $y.failed; $x.seconds += $y.seconds; $x.verbCalls += $y.verbCalls; $x.verbSeconds += $y.verbSeconds }
    Write-Host ("{0,-18} {1,5} {2,7} {3,7} {4,8:N0} {5,7} {6,9}" -f $n, $x.scen, $x.checks, $x.failed, $x.seconds, $x.verbCalls, $(if ($x.verbCalls -gt 0) { '{0:N2}' -f ($x.verbSeconds / $x.verbCalls) } else { '-' }))
}
Write-Host ("total: {0} checks, {1} failed; wall-clock {2:N0}s, serial-equivalent (sum of suites) {3:N0}s, speed-up {4:N1}x; {5} verb calls, {6:N2}s per call" -f $checks, $failed, $wall.Elapsed.TotalSeconds, $sumSecs, $(if ($wall.Elapsed.TotalSeconds -gt 0) { $sumSecs / $wall.Elapsed.TotalSeconds } else { 0 }), $verbs, $(if ($verbs) { $verbSecs / $verbs } else { 0 }))
# CPU actually used by this run's processes (waited-for children and their descendants: /proc/self/stat cutime+cstime): the share of the wall-clock that other users of the machine cannot distort
$cpuSec = 0.0; try { $st = (Get-Content -Raw /proc/self/stat); $f = ($st.Substring($st.LastIndexOf(')') + 2)) -split ' '; $cpuSec = ([double]$f[13] + [double]$f[14]) / 100.0 } catch { }
Write-Host ("cpu: {0:N0} CPU-seconds in the run's processes = {1:N0}s wall-clock on 4 idle cores at best; machine load average now {2}" -f $cpuSec, ($cpuSec / 4), ((Get-Content /proc/loadavg) -split ' ')[0])
Write-Host ("memory: available {0:N0} MB at start, lowest {1:N0} MB (peak use by the run {2:N0} MB), at most {3} suites at once" -f $startAvail, $minAvail, ($startAvail - $minAvail), $peakRunning)
$crashes = 0; foreach ($n in $results.Keys) { if ($results[$n].Contains('clientCrashes')) { $crashes += [int]$results[$n].clientCrashes } }
if ($crashes -gt 0) { Write-Host "client crashes: $crashes client calls ended with pwsh exit 139 (segmentation fault): machine load or the runtime, not a product answer; run the failed scenarios again on a quiet machine before reading them as defects" }
$quar = @(); foreach ($n in $results.Keys) { if ($results[$n].Contains('quarantined')) { foreach ($f in @($results[$n].quarantined | Where-Object { $_ })) { $quar += "[$n] $f" } } }
Write-Host "quarantined failures: $($quar.Count) (checks with an open backlog entry that failed; reported, not counted as failed)"; $quar | ForEach-Object { Write-Host "  $_" }
if ($failed -gt 0) { Write-Host ""; Write-Host 'FAILURES:'; foreach ($n in $results.Keys) { foreach ($f in @($results[$n].failures)) { Write-Host "  [$n] $f" } } }

$allRun = @(); foreach ($n in $results.Keys) { $allRun += @($results[$n].scenarios) }
if ($Tier -eq 'fast') {
    $stats = @{}; if (Test-Path $statsFile) { $stats = Get-Content -Raw $statsFile | ConvertFrom-Json -AsHashtable }
    $skipChecks = 0; $noStat = 0; foreach ($sc in $leftOut) { if ($stats.ContainsKey($sc.suite) -and $stats[$sc.suite].ContainsKey((Get-StatKey $sc.name))) { $skipChecks += [int]$stats[$sc.suite][(Get-StatKey $sc.name)].checks } else { $noStat++ } }
    Write-Host ("skipped as full-tier: {0} scenarios, {1} checks (from the last full run{2}); run -Tier full for them" -f $leftOut.Count, $skipChecks, $(if ($noStat) { "; $noStat have no recorded count yet" } else { '' }))
}
Write-Host "slowest scenarios:"; foreach ($sc in ($allRun | Sort-Object { -[double]$_.seconds } | Select-Object -First 8)) { Write-Host ("  {0,6:N1}s {1,4} checks  {2}: {3}" -f $sc.seconds, $sc.checks, $sc.suite, $sc.name) }

# ---- remember the durations (clean suites only, a failed or cut-short one says nothing about its length) ----------------------------
if (-not $Only) {
    $mine = @{}; if ($timesAll.ContainsKey($Tier)) { foreach ($e in $timesAll[$Tier].GetEnumerator()) { $mine[$e.Key] = $e.Value } }
    foreach ($n in $results.Keys) { if ($results[$n].failed -eq 0 -and $jobOf[$n].groups -eq 1) { $mine[$n] = [math]::Round($results[$n].seconds, 0) } }   # (a shard or a split run is not the suite's time in one process)
    $ordered = [ordered]@{}; foreach ($n in $names) { if ($mine.ContainsKey($n)) { $ordered[$n] = $mine[$n] } }
    $out = [ordered]@{}; foreach ($t in 'fast', 'full') { if ($t -eq $Tier) { $out[$t] = $ordered } elseif ($timesAll.ContainsKey($t)) { $out[$t] = $timesAll[$t] } }
    $out["wall-$Tier"] = [math]::Round($wall.Elapsed.TotalSeconds, 0)
    foreach ($t in 'fast', 'full') { $k2 = "wall-$t"; if ($t -ne $Tier -and $timesAll.ContainsKey($k2)) { $out[$k2] = $timesAll[$k2] } }
    ($out | ConvertTo-Json -Depth 4) | Set-Content -Path $timesFile
}
if ($Tier -eq 'full' -and -not $Only) {
    $stats = @{}; if (Test-Path $statsFile) { $stats = Get-Content -Raw $statsFile | ConvertFrom-Json -AsHashtable }
    foreach ($sc in $allRun) { if ($sc.failed -eq 0) { if (-not $stats.ContainsKey($sc.suite)) { $stats[$sc.suite] = @{} }; $stats[$sc.suite][(Get-StatKey $sc.name)] = @{ tier = $sc.tier; seconds = $sc.seconds; checks = $sc.checks } } }
    ($stats | ConvertTo-Json -Depth 4) | Set-Content -Path $statsFile
}
exit ([int]($failed -gt 0))
