# Tier 1 harness: a fake GitHub/Octopus, the real gateway and rule scripts on test ports, and a scripted worker that calls the real client.
Set-StrictMode -Version Latest
$script:Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
# Every port a suite uses derives from ONE base (a private range of 100 ports per suite slot), so any number of suites can run at once:
#   +0 fake GitHub   +10 gateway API   +11 gateway webhooks   +12/+13 a second gateway   +14 the fake's forwarder in front of a gateway
#   +20 a worker-declared QC port (never listened on)   +29 a port nothing listens on
#   +30 a loopback public-URL literal (never listened on)   +40..+89 stub applications of the QC worker scenarios (range 40-59, clash 60-69, flaky 70-79, never 80-89)
# The base comes from TEST_PORT_BASE (run.ps1 sets it from the suite slot); TEST_FAKE_PORT and TEST_API_PORT still override the two main ports.
function Set-TestPorts([int]$Base) {
    $script:PortBase = $Base
    $script:FakePort = $(if ($env:TEST_FAKE_PORT) { [int]$env:TEST_FAKE_PORT } else { $Base }); $script:ApiPort = $(if ($env:TEST_API_PORT) { [int]$env:TEST_API_PORT } else { $Base + 10 }); $script:HookPort = $script:ApiPort + 1
    $script:ReportPort = $Base + 20; $script:DeadPort = $Base + 29; $script:PublicPort = $Base + 30; $script:ProxyPort = $Base + 26   # not 14: qc-pool.ps1 starts a gateway there
}
Set-TestPorts $(if ($env:TEST_PORT_BASE) { [int]$env:TEST_PORT_BASE } else { 6100 })
$script:VerbCalls = 0; $script:VerbSeconds = 0.0; $script:ClientCrashes = 0
$script:Procs = @()
$script:Failures = New-Object System.Collections.ArrayList
$L2 = @('factory worker', 'Auto Merge')
$script:Checks = 0

function Start-TestEnv([hashtable]$Config = @{}) {
    $work = Join-Path ([IO.Path]::GetTempPath()) "factory-worker-test-$([guid]::NewGuid().ToString('N').Substring(0,8))"
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    $script:Work = $work
    $cfg = Get-Content -Raw (Join-Path $script:Root 'config/instance.json') | ConvertFrom-Json -AsHashtable
    $cfg.octopus.url = "http://127.0.0.1:$($script:FakePort)/octopus"
    $t = $cfg.timing
    $t.mergeabilityWaitSeconds = 3; $t.mergeabilityPollSeconds = 1; $t.preMergeChecksWaitSeconds = 12; $t.preMergeChecksPollSeconds = 1
    $t.docsOnlyLegacyBefore = '2020-01-01T00:00:00Z'   # every item and state of a test is new; the legacy scenarios date theirs before it
    $t.updatedHeadWaitSeconds = 10; $t.sweepSeenCount = 1; $t.docsOnlyReleaseGraceSeconds = 4; $t.releaseCandidateTimeoutMinutes = 1; $t.masterGreenTimeoutMinutes = 2
    $t.masterDefectCheckMinutes = 0.05   # (cirule.ps1: a master defect is looked at again every 3 s, not every 5 minutes)
    # one item per session (plant.oneItemPerSession) is on in the source defaults; the suites let one scripted session take many items, so it is off here
    # unless a suite sets it. The scenarios of the rule turn it on for their own gateway (FACTORY_ONE_ITEM_PER_SESSION, owner.ps1).
    $cfg.plant = @{ oneItemPerSession = $false }
    # work intake (issue 104) is off in the source defaults (an environment turns it on); the suites create their items in the columns they need, so it is off here unless a
    # scenario switches it on (FACTORY_INTAKE, owner.ps1)
    $cfg.intake = @{ enabled = $false }
    foreach ($k in $Config.Keys) { $cfg[$k] = $Config[$k] }
    # __FAKE_URL__ in a suite's configuration is the fake's address (the fake ARM of the runtime suite)
    $cfgPath = Join-Path $work 'instance.test.json'; ($cfg | ConvertTo-Json -Depth 12).Replace('__FAKE_URL__', "http://127.0.0.1:$($script:FakePort)") | Set-Content $cfgPath
    $fakeDll = Join-Path $script:Root 'tests/FakeGitHub/bin/Release/net10.0/FakeGitHub.dll'
    $gwDll = Join-Path $script:Root 'gateway/bin/Release/net10.0/FactoryWorker.Gateway.dll'
    foreach ($d in $fakeDll, $gwDll) { if (-not (Test-Path $d)) { throw "build first: $d is missing" } }
    $env:FAKE_PORT = "$($script:FakePort)"; $env:FACTORY_CONFIG = $cfgPath   # (the fake reads docsOnlyPaths from it)
    $env:FAKE_PROXY_PORT = "$($script:ProxyPort)"   # the fake's TCP forwarder in front of a gateway (leader.ps1): idle until a scenario names a backend
    # the golden writes (tests/scenarios/golden/README.md): the fake records every scenario's writes into TEST_WRITES_DIR (CI), else the suite's work directory
    # The gateway passes a request's X-Fake-Cause on to the rule scripts it runs while serving it only with FACTORY_TEST_CAUSE_HEADER=1 (gateway/
    # TestCause.cs); every gateway a suite starts later (Restart-Gateway, a second gateway) inherits it from this process. FACTORY_TEST_CAUSE is
    # never left set here: it goes to one call of F only (Invoke-F), never to a gateway or a stream a scenario starts itself.
    if ((Get-GoldenMode) -eq 'off') { Remove-Item Env:FAKE_RECORD_DIR -ErrorAction SilentlyContinue; Remove-Item Env:FACTORY_TEST_CAUSE_HEADER -ErrorAction SilentlyContinue }
    else { $env:FAKE_RECORD_DIR = $(if ($env:TEST_WRITES_DIR) { $env:TEST_WRITES_DIR } else { Join-Path $work 'writes' }); $env:FACTORY_TEST_CAUSE_HEADER = '1' }
    Remove-Item Env:FACTORY_TEST_CAUSE -ErrorAction SilentlyContinue
    $script:Procs += Start-Process dotnet -ArgumentList $fakeDll -PassThru -RedirectStandardOutput (Join-Path $work 'fake.out') -RedirectStandardError (Join-Path $work 'fake.err')
    $env:GITHUB_API_URL = "http://127.0.0.1:$($script:FakePort)"; $env:GH_TOKEN = 'fake-operator'; $env:OCTOPUS_API_KEY = 'fake'
    $env:FACTORY_CONFIG = $cfgPath; $env:FACTORY_STATE_DIR = Join-Path $work 'state'; $(if ($env:TEST_REAL_APPS) { Remove-Item Env:FACTORY_FAKE_APPS -ErrorAction SilentlyContinue } else { $env:FACTORY_FAKE_APPS = '1' })
    # a stream client does not accept offers by itself in the suites (the scenarios accept, or let an offer lapse); lease.ps1 switches it on
    $env:FACTORY_AUTO_ACCEPT = '0'
    $env:FACTORY_PLUGIN_SCRIPTS = Join-Path $script:Root 'plugin/scripts'; $env:FACTORY_DEV_TOKEN = 'test-token'; $env:FACTORY_WEBHOOK_SECRET = ('x' * 40)
    $env:FACTORY_API_PORT = "$($script:ApiPort)"; $env:FACTORY_WEBHOOK_PORT = "$($script:HookPort)"; $env:FACTORY_SWEEP_SECONDS = $(if ($env:TEST_SWEEP_SECONDS) { $env:TEST_SWEEP_SECONDS } else { '2' })
    $script:Procs += Start-Process dotnet -ArgumentList $gwDll -PassThru -RedirectStandardOutput (Join-Path $work 'gateway.out') -RedirectStandardError (Join-Path $work 'gateway.err')
    $env:FACTORY_URL = "http://127.0.0.1:$($script:ApiPort)"; $env:FACTORY_TOKEN = 'test-token'
    Wait-Until -Seconds 30 -Message 'fake and gateway to answer' { try { (Invoke-RestMethod "http://127.0.0.1:$($script:FakePort)/_fake/state" -TimeoutSec 2) -and (Invoke-RestMethod "http://127.0.0.1:$($script:ApiPort)/ready" -TimeoutSec 2) } catch { $false } } | Out-Null
}

function Assert-NoServerErrors([string]$Suite) {
    # Invariant: the gateway must never log a sweep error or an internal error, whatever the scenario did.
    $bad = @()
    # every gateway log of the suite, including those of a restarted gateway (gateway-xxxx.out, gateway-restarted.err)
    foreach ($f in @(Get-ChildItem -Path $script:Work -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'gateway*.out' -or $_.Name -like 'gateway*.err' })) { $bad += @(Select-String -Path $f.FullName -Pattern 'sweepError|internal error|"sweep":"error"|"sweep":"failed"' | ForEach-Object { $_.Line.Substring(0, [Math]::Min(220, $_.Line.Length)) }) }
    $script:Current = "$Suite invariants"
    Check 'no sweep or internal errors in the gateway log' ($bad.Count -eq 0) ($bad | Select-Object -First 3 | Out-String)
    Assert-GoldenTails $Suite
}
# At the end of a suite's or group's process (Assert-NoServerErrors): after a final settle, the fake's group check normalizes every write the
# process made after each scenario against that scenario and writes <slug>.later.txt next to its record; in compare mode every tail line
# ('~ ') of a scenario's golden file must be in its own record or among those later lines (tests/scenarios/golden/README.md: Tail lines).
# One failing check per scenario with lines missing.
function Assert-GoldenTails([string]$Suite) {
    $mode = Get-GoldenMode
    if ($mode -eq 'off') { return }
    try { Wait-RecordSettled } catch { }
    $dir = $(if ($mode -eq 'compare') { Join-Path $script:Root 'tests/scenarios/golden' } else { '' })
    $script:Current = "$Suite golden tails"
    $res = $null
    try { $res = Fake 'group-check' @{ goldenDir = $dir } }
    catch { Check 'golden tail writes by the end of the group' $false "(the fake did not answer the group check: $($_.Exception.Message))"; return }
    if ((Get-RecField $res 'recording') -eq $false -or $mode -ne 'compare') { return }
    # the fake keeps at most 400,000 writes of a process: beyond that a tail line could be missed for want of room, which must not look like a defect
    $dropped = Get-RecField $res 'droppedProcessWrites'
    if ($null -ne $dropped -and [long]$dropped -gt 0) { Check 'golden tail writes: the fake kept every write of the process' $false "($dropped writes were not kept; the missing tail lines below may be among them)" }
    # every tail line missing is a quarantined miss of its own (Add-GoldenMiss: issue #386)
    $missing = @(Get-RecField $res 'missing')
    foreach ($m in $missing) {
        $suite = "$(Get-RecField $m 'suite')"; $slug = "$(Get-RecField $m 'slug')"
        $golden = Join-Path $dir (Join-Path $suite "$slug.writes.txt")
        foreach ($l in @(Get-RecField $m 'missing')) { Add-GoldenMiss -Section '~' -Scenario "$(Get-RecField $m 'scenario')" -Suite $suite -Slug $slug -Line "$l" -Golden $golden }
    }
    if ($missing.Count -eq 0) { Check "golden tail writes by the end of the group ($(Get-RecField $res 'checked') scenarios)" $true }
}
# Stops the suite's gateway and starts it again with the same environment: a new process, so a new epoch and empty memory. Its logs go to
# gateway-xxxx.out and gateway-restarted.err, which Assert-NoServerErrors reads as well.
function Get-GatewayBase { "http://127.0.0.1:$($script:ApiPort)" }
# -Graceful: SIGTERM, as the platform stops a container (the gateway writes its plant snapshot then), and up to 30 s for the process to end;
# without it the process is killed outright, as an out-of-memory kill would.
function Restart-Gateway([switch]$Graceful) {
    if ($Graceful -and -not $IsWindows) {
        $gp = $script:Procs[1]; & kill -s TERM $gp.Id 2>$null
        if (-not $gp.WaitForExit(30000)) { try { Stop-Process -Id $gp.Id -Force } catch { } }
    }
    else { try { Stop-Process -Id $script:Procs[1].Id -Force } catch { } }
    Start-Sleep -Seconds 1
    $gwDll = Join-Path $script:Root 'gateway/bin/Release/net10.0/FactoryWorker.Gateway.dll'
    $script:Procs[1] = Start-Process dotnet -ArgumentList $gwDll -PassThru -RedirectStandardOutput (Join-Path $script:Work "gateway-$([guid]::NewGuid().ToString('N').Substring(0,4)).out") -RedirectStandardError (Join-Path $script:Work "gateway-restarted.err")
    [void](Wait-Until -Seconds 30 { try { [bool](Invoke-RestMethod "$(Get-GatewayBase)/ready" -TimeoutSec 2) } catch { $false } })
}
function Stop-TestEnv { Stop-ClientHost; foreach ($p in $script:Procs) { try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { } }; $script:Procs = @() }

function Wait-Until([scriptblock]$Cond, [int]$Seconds = 30, [string]$Message = 'condition') {
    $end = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $end) { if (& $Cond) { return $true }; Start-Sleep -Milliseconds 400 }
    return $false
}

function Fake([string]$What, $Body = $null) {
    $u = "http://127.0.0.1:$($script:FakePort)/_fake/$What"
    if ($null -eq $Body) { Invoke-RestMethod $u } else { Invoke-RestMethod $u -Method POST -Body ($Body | ConvertTo-Json -Depth 8) -ContentType 'application/json' }
}
function Get-FakeState { Fake 'state' }
function Get-FakeIssue([int]$n) { (Get-FakeState).issues | Where-Object { $_.number -eq $n } }
function Get-FakePr([int]$n) { (Get-FakeState).prs | Where-Object { $_.number -eq $n } }

# Calls the real client. Returns the parsed JSON with .exit set.
# With $script:UseClientHost (a suite sets it in run.ps1; TEST_CLIENT_HOST=0/1 overrides) the client script runs in ONE long-lived pwsh
# (plugin/scripts/runner-host.ps1: a new runspace per call, this process's environment and directory passed with each call) in place of a new pwsh
# per call. Only F does this, and never for the verbs in $script:RealProcessVerbs: the long-running ones and everything a scenario starts itself
# (subscribe streams, spawned workers, worker-token, calls with their own environment) stay real processes.
$script:UseClientHost = $false; $script:ClientHost = $null; $script:ClientHostCalls = 0
$script:RealProcessVerbs = @('subscribe', 'wait-event', 'plugin-sync', 'worker-token')
# Starts a runner host for one script; Invoke-RunnerHost sends it one call and returns @{ code; out; err }, or $null when it ended or did not answer.
function Start-RunnerHost([string]$Target) {
    $psi = [Diagnostics.ProcessStartInfo]::new('pwsh'); $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.UseShellExecute = $false
    $psi.StandardInputEncoding = [Text.UTF8Encoding]::new($false); $psi.StandardOutputEncoding = [Text.Encoding]::UTF8
    $mark = "fwt-$([guid]::NewGuid().ToString('N'))"
    foreach ($a in '-NoProfile', '-NonInteractive', '-File', (Join-Path $script:Root 'plugin/scripts/runner-host.ps1'), $Target, $mark) { $psi.ArgumentList.Add($a) }
    $h = @{ proc = [Diagnostics.Process]::Start($psi); mark = "$mark " }
    if ($null -eq (Read-RunnerHostFrame $h 60)) { Stop-RunnerHost $h; throw "the runner host for $Target did not start" }
    return $h
}
function Stop-RunnerHost($h) { if ($h) { try { $h.proc.StandardInput.Close() } catch { }; try { if (-not $h.proc.WaitForExit(2000)) { $h.proc.Kill($true) } } catch { } } }
# The next response line of the host (lines without its mark are skipped); $null when it ended or did not answer in time.
function Read-RunnerHostFrame($h, [int]$Seconds) {
    $end = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ($true) {
        $t = $h.proc.StandardOutput.ReadLineAsync()
        if (-not $t.Wait([Math]::Max(1, [int]($end - [DateTime]::UtcNow).TotalMilliseconds))) { return $null }
        $line = $t.Result; if ($null -eq $line) { return $null }
        if ($line.StartsWith($h.mark, [StringComparison]::Ordinal)) { return $line.Substring($h.mark.Length) }
    }
}
function Invoke-RunnerHost($h, [string[]]$CallArgs, [string]$Stdin = '', $CallEnv = $null, $Cwd = $null, [int]$Seconds = 240) {
    $id = [guid]::NewGuid().ToString('N')
    $h.proc.StandardInput.WriteLine((@{ id = $id; args = @($CallArgs); stdin = $Stdin; env = $CallEnv; cwd = $Cwd } | ConvertTo-Json -Compress -Depth 3)); $h.proc.StandardInput.Flush()
    $frame = Read-RunnerHostFrame $h $Seconds
    if ($null -eq $frame) { return $null }
    $r = $frame | ConvertFrom-Json -AsHashtable -DateKind String
    if ("$($r.id)" -cne $id) { return $null }   # not the answer to this call
    return $r
}
function Get-ProcessEnv { $e = @{}; $all = [Environment]::GetEnvironmentVariables(); foreach ($k in $all.Keys) { $e["$k"] = "$($all[$k])" }; return $e }
function Stop-ClientHost { $h = $script:ClientHost; $script:ClientHost = $null; Stop-RunnerHost $h }
function Invoke-ClientHost([string[]]$FactoryArgs, [string]$Cause = '') {
    if (-not $script:ClientHost -or $script:ClientHost.proc.HasExited) { $script:ClientHost = Start-RunnerHost (Join-Path $script:Root 'plugin/scripts/factory.ps1') }
    # (the call's cause id goes into this call's environment only, never into this process's: F)
    $callEnv = Get-ProcessEnv; if ($Cause) { $callEnv['FACTORY_TEST_CAUSE'] = $Cause }
    $r = Invoke-RunnerHost $script:ClientHost $FactoryArgs '' $callEnv (Get-Location).ProviderPath
    if ($null -eq $r) {
        $hc = $(try { if ($script:ClientHost.proc.HasExited) { $script:ClientHost.proc.ExitCode } else { 0 } } catch { 0 })
        Stop-ClientHost
        if ($hc -eq 139) { return @{ code = 139; text = '' } }
        return @{ code = 1; text = 'the client host ended or did not answer' }
    }
    $script:ClientHostCalls++
    return @{ code = [int]$r.code; text = ("$($r.out)$($r.err)" -replace "`r`n", "`n").TrimEnd("`n") }
}
# The cause ids of the golden writes (tests/scenarios/golden/README.md: Caused and background writes): while a scenario's record is open, each
# call of F gets the id "<nonce>.<n>" (the nonce from scenario-begin, n counted per scenario). The client sends it as X-Fake-Cause with every
# request of the call to the gateway (FACTORY_TEST_CAUSE), the gateway hands it to every rule-script run it starts while serving that request
# (FACTORY_TEST_CAUSE_HEADER=1, gateway/TestCause.cs) and the rule scripts send it with every write; the fake counts a write as caused when its
# request carried the scenario's id. Not tagged: the verbs that only wait or stream (they would tag whatever their long requests set off) and
# those that only read (scenarios poll them): a write one of them makes is background.
$script:GoldenOpen = $false; $script:CauseNonce = ''; $script:CauseSeq = 0
$script:CauseSkipVerbs = @('subscribe', 'wait-event', 'wait-for-checks', 'inbox', 'message-status', 'presence', 'get-item', 'peers', 'model-for', 'model-tier', 'questions', 'qc-list', 'pr-checks', 'inspect-facts', 'plant-digest')
# (the role verbs name their action second: `operator status`, `keeper ...`, `supervisor ...`)
$script:CauseRoleVerbs = @('operator', 'keeper', 'supervisor')
$script:CauseSkipRoleActions = @('status', 'metrics', 'act-log', 'release-check')
function F([string]$Holder, [string[]]$FactoryArgs) {
    $skip = $FactoryArgs.Count -gt 0 -and ($FactoryArgs[0] -in $script:CauseSkipVerbs -or ($FactoryArgs[0] -in $script:CauseRoleVerbs -and $FactoryArgs.Count -gt 1 -and $FactoryArgs[1] -in $script:CauseSkipRoleActions))
    $id = ''
    # (the count is raised as a statement of its own: ++$x inside "$( )" outputs nothing, so every id came out as "<nonce>.")
    if ($script:GoldenOpen -and -not $skip -and $script:CauseNonce) { $script:CauseSeq++; $id = "$($script:CauseNonce).$($script:CauseSeq)" }
    return Invoke-F $Holder $FactoryArgs -Cause $id
}
# -Cause: the call's cause id (F), passed to the client as FACTORY_TEST_CAUSE of that call only: in the runner host's call environment, or set
# around the spawned pwsh and removed right after it
function Invoke-F([string]$Holder, [string[]]$FactoryArgs, [string]$Cause = '') {
    $env:FACTORY_HOLDER = $Holder
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $hosted = $script:UseClientHost -and $FactoryArgs.Count -gt 0 -and $FactoryArgs[0] -notin $script:RealProcessVerbs
    if ($env:TEST_CLIENT_HOST -eq '0') { $hosted = $false } elseif ($env:TEST_CLIENT_HOST -eq '1' -and $FactoryArgs.Count -gt 0 -and $FactoryArgs[0] -notin $script:RealProcessVerbs) { $hosted = $true }
    if ($hosted) { $hc = Invoke-ClientHost $FactoryArgs $Cause; $text = $hc.text; $code = $hc.code }
    else {
        if ($Cause) { $env:FACTORY_TEST_CAUSE = $Cause }
        try {
            $out = & pwsh -NoProfile -File (Join-Path $script:Root 'plugin/scripts/factory.ps1') @FactoryArgs 2>&1
            $code = $LASTEXITCODE
        }
        finally { if ($Cause) { Remove-Item Env:FACTORY_TEST_CAUSE -ErrorAction SilentlyContinue } }
        $text = ($out | ForEach-Object { "$_" }) -join "`n"
    }
    $script:VerbCalls++; $script:VerbSeconds += $sw.Elapsed.TotalSeconds
    # pwsh exit 139 is a segmentation fault of the client process itself (seen under load): reported as that, so it is not read as a product answer
    if ($code -eq 139) {
        $script:ClientCrashes++
        Write-Host "    CRASH client call '$($FactoryArgs[0])' ended with pwsh exit 139 (segmentation fault): machine load or the runtime, not a product answer"
        return @{ ok = $false; exit = 139; code = 'client-crashed'; reason = 'PWSH EXIT 139 (SEGMENTATION FAULT) OF THE CLIENT PROCESS: machine load or the runtime, not a product answer; run the scenario again'; raw = $text }
    }
    try { $j = $text | ConvertFrom-Json -AsHashtable; if (-not $j.ContainsKey('exit')) { $j.exit = $code }; return $j } catch { return @{ ok = $false; exit = $code; raw = $text } }
}
function Pv($h, [string]$k) { if ($h -is [System.Collections.IDictionary] -and $h.Contains($k)) { return $h[$k] }; return $null }
# A file that starts like an MP4 (an ftyp box), of the given length.
function New-TestMp4([string]$Name, [int]$Size) { $p = Join-Path $script:Work $Name; $b = [byte[]]::new($Size); [byte[]]$h = [byte[]](0, 0, 0, 24) + [Text.Encoding]::ASCII.GetBytes('ftypisom'); [Array]::Copy($h, $b, $h.Length); [IO.File]::WriteAllBytes($p, $b); return $p }
function TmpFile([string]$Name, [string]$Text) { $p = Join-Path $script:Work $Name; Set-Content -Path $p -Value $Text -NoNewline; return $p }

function Check([string]$Name, [bool]$Ok, $Detail = '') {
    $script:Checks++
    if ($Ok) { Write-Host "    ok    $Name" } else { Write-Host "    FAIL  $Name  $Detail"; [void]$script:Failures.Add("$($script:Current): $Name $Detail") }
}
# A check that is known to fail now and then for a cause not yet found (docs/HANDOFF.md names it under -Issue). It runs and reports like any
# check; when it fails it prints QUARANTINED (failed) with the detail and is counted in a total of its own (the suite summary and run-all show it),
# and it does not fail the suite. When it passes it prints as any check. Use it only for a check with an open backlog entry, and remove it with the cause.
$script:Quarantined = New-Object System.Collections.ArrayList
function CheckQuarantined([string]$Name, [bool]$Ok, $Detail = '', [Parameter(Mandatory)][string]$Issue) {
    $script:Checks++
    if ($Ok) { Write-Host "    ok    $Name" } else { Write-Host "    QUARANTINED (failed)  $Name  $Detail  [$Issue]"; [void]$script:Quarantined.Add("$($script:Current): $Name $Detail [$Issue]") }
}
function CheckExit($Name, $r, [int]$Exit, [string]$Code = '') {
    $got = if ($r.ContainsKey('code')) { "$($r.code)" } else { '' }; $why = if ($r.ContainsKey('reason')) { "$($r.reason)" } else { '' }
    $ok = ($r.exit -eq $Exit) -and ((-not $Code) -or $got -eq $Code)
    Check $Name $ok "(wanted exit $Exit $Code, got exit $($r.exit) $got $why)"
}

# a suite whose definition names DefaultEnvLabel (copilot: its gateway is env-scoped): an item given no env: label gets that one
function New-Item-Fake([string]$Title, [string]$Body, [string[]]$Labels, [string]$Column = 'Technical Design') { if ($script:DefaultEnvLabel -and -not @($Labels | Where-Object { "$_" -like 'env:*' }).Count) { $Labels = @($Labels) + $script:DefaultEnvLabel }; (Fake 'issue' @{ title = $Title; body = $Body; labels = $Labels; column = $Column }).number }

# The scripted worker: runs one column the way the skill does, with canned judgement.
function Do-TechnicalDesign([int]$n, [string]$Holder = 'w1', [bool]$DocsOnly = $true) {
    $r = F $Holder @('claim-item', '--item', $n, '--column', 'Technical Design'); CheckExit "TD $n claim" $r 0
    $sec = "## Technical Design`n`n$(if ($DocsOnly) { '<!-- aisf:docs-only -->' })`n- change as described in the item.`n"
    $r = F $Holder @('append-body', '--item', $n, '--column', 'Technical Design', '--content-file', (TmpFile "td$n.md" $sec)); CheckExit "TD $n append" $r 0
    $r = F $Holder @('record-review', '--item', $n, '--column', 'Technical Design', '--role', 'reviewer', '--text-file', (TmpFile "rv-td$n.md" "Section present and complete.`nREVIEW: COMPLETE")); CheckExit "TD $n record-review" $r 0
    $ref = $r.review_ref
    $r = F $Holder @('complete', '--item', $n, '--column', 'Technical Design', '--review-ref', $ref, '--comment', 'REVIEW: COMPLETE - design done'); CheckExit "TD $n complete" $r 0
    if ($r.exit -ne 0) { $iss = Get-FakeIssue $n; Write-Host "    DEBUG item $n comments:"; foreach ($c in $iss.comments) { Write-Host ("      #{0}: {1}" -f $c.id, (($c.body -replace '\s+', ' ')).Substring(0, [Math]::Min(2600, ($c.body -replace "\s+", " ").Length))) } }
}
function Do-Development([int]$n, $Files = @('docs/trial.md'), [string]$Holder = 'w1', [string]$Profile = 'green', [double]$Delay = 0) {
    $r = F $Holder @('claim-item', '--item', $n, '--column', 'Development'); CheckExit "DEV $n claim" $r 0
    Fake 'push-branch' @{ branch = "factory-worker/$n-development"; files = $Files; profile = $Profile; delaySeconds = $Delay } | Out-Null
    $r = F $Holder @('open-pr', '--item', $n, '--head', "factory-worker/$n-development", '--title', "Trial $n", '--body-file', (TmpFile "pr$n.md" 'change')); CheckExit "DEV $n open-pr" $r 0
    return $r.pullRequest.number
}
function Do-DevComplete([int]$n, [string]$Holder = 'w1') {
    $r = F $Holder @('record-review', '--item', $n, '--column', 'Development', '--role', 'reviewer', '--text-file', (TmpFile "rv-dev$n.md" "Diff matches the plan.`nREVIEW: COMPLETE"))
    return F $Holder @('complete', '--item', $n, '--column', 'Development', '--review-ref', $r.review_ref, '--comment', 'PR open, CI green. REVIEW: COMPLETE')
}
function Do-Verdict([int]$n, [int]$pr, [string]$Holder = 'w1', [bool]$Passed = $true) {
    $r = F $Holder @('claim-item', '--item', $n, '--column', 'Functional Testing'); CheckExit "FT $n claim" $r 0
    $rv = F $Holder @('record-review', '--item', $n, '--column', 'Functional Testing', '--role', 'reviewer', '--text-file', (TmpFile "rv-ft$n.md" "Criteria exercised.`nREVIEW: COMPLETE"))
    $args2 = @('validation-verdict', '--item', $n, '--passed', "$($Passed.ToString().ToLower())", '--summary-file', (TmpFile "vs$n.md" 'Both criteria met.'), '--pr', $pr)
    if ($Passed) { $args2 += @('--review-ref', $rv.review_ref) }
    return F $Holder $args2
}

# Tier: a scenario whose name ends in " [full]" belongs to the full tier only (it waits in real time, or repeats a rule another scenario already proves, or has a long setup);
# every other scenario is in the fast tier (the smoke check of its area). run.ps1 -Tier fast skips the [full] ones and counts them; the tag is part of the name, so -Only still matches.
function Get-ScenarioTier([string]$Name) { if ($Name -match '\s\[full\]\s*$') { 'full' } else { 'fast' } }
$script:ScenarioStats = New-Object System.Collections.ArrayList
$script:SuiteName = ''

# Golden writes (tests/scenarios/golden/README.md): the fake records every scenario's writes (FAKE_RECORD_DIR) and Run-Scenario compares them
# with tests/scenarios/golden/<suite>/<slug>.writes.txt where that file exists. TEST_GOLDEN: compare (the default), record (never compare), off.
function Get-GoldenMode {
    $m = "$env:TEST_GOLDEN".Trim().ToLowerInvariant()
    if ($m -eq '') { return 'compare' }
    if ($m -in 'compare', 'record', 'off') { return $m }
    Write-Host "    (TEST_GOLDEN='$env:TEST_GOLDEN' is not compare, record or off: compare)"
    return 'compare'
}
# The file name of a scenario's record: the name without its " [full]" tag (a change of tier keeps the golden), lowercase, each run of other
# characters one '-', at most 80 characters, then '-' and the first 8 hex digits of the SHA-256 of that name (two names never share a file).
function Get-ScenarioSlug([string]$Name) {
    $base = $Name -replace '\s\[full\]\s*$', ''
    $s = ($base.ToLowerInvariant() -replace '[^a-z0-9]+', '-').Trim('-')
    if ($s.Length -gt 80) { $s = $s.Substring(0, 80).TrimEnd('-') }
    if ($s -eq '') { $s = 'scenario' }
    $h = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($base))).Substring(0, 8).ToLowerInvariant()
    return "$s-$h"
}
function Get-RecField($Object, [string]$Key) { if ($null -eq $Object) { return $null }; $p = $Object.PSObject.Properties[$Key]; if ($p) { return $p.Value }; return $null }
# The write lines of a record or a golden file: every line but the empty ones and the '# ' header lines.
function Read-WritesLines([string]$Path) { @([IO.File]::ReadAllLines($Path, [Text.UTF8Encoding]::new($false)) | Where-Object { $_ -ne '' -and -not $_.StartsWith('# ') }) }
# The sections of a record or a golden file: cause (the '= ' lines, in their order), background (the '+ ' lines: settled, a set), tail (the
# '~ ' lines, a set) and other (any line with no prefix: a file of an older format, which can be compared with nothing). With -Later, the
# '~ ' lines of the record's <slug>.later.txt (the fake's group check) are read as later.
function Read-WritesSections([string]$Path, [switch]$Later) {
    $cause = [System.Collections.Generic.List[string]]::new()
    $background = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $tail = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $laterLines = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $other = [System.Collections.Generic.List[string]]::new()
    foreach ($l in Read-WritesLines $Path) {
        if ($l.StartsWith('= ')) { $cause.Add($l.Substring(2)) } elseif ($l.StartsWith('+ ')) { [void]$background.Add($l.Substring(2)) } elseif ($l.StartsWith('~ ')) { [void]$tail.Add($l.Substring(2)) } else { $other.Add($l) }
    }
    if ($Later -and $Path.EndsWith('.writes.txt')) {
        $lp = $Path.Substring(0, $Path.Length - '.writes.txt'.Length) + '.later.txt'
        if (Test-Path -LiteralPath $lp) { foreach ($l in Read-WritesLines $lp) { if ($l.StartsWith('~ ')) { [void]$laterLines.Add($l.Substring(2)) } } }
    }
    return @{ cause = $cause; background = $background; tail = $tail; later = $laterLines; other = $other }
}
# The object of a caused write: the first item or pull request number of its line ({#k} or {#prk}), '*' for a line that names none. The caused
# writes are compared as one sequence per object: the writes on one item keep their order, while how the writes on two items interleave (a verb
# on #1 and the gateway's answer on #2 can race) is not compared.
function Get-WriteObject([string]$Line) { $m = [regex]::Match($Line, '\{#(?:pr\d+|\d+)\}'); if ($m.Success) { return $m.Value }; return '*' }
function Group-CauseLines($Lines) {
    $g = @{}
    foreach ($l in @($Lines)) { $k = Get-WriteObject $l; if (-not $g.ContainsKey($k)) { $g[$k] = [System.Collections.Generic.List[string]]::new() }; $g[$k].Add($l) }
    return $g
}
# The first difference of two cause sequences ($null when they are equal): the object, the 1-based position in that object's writes, the line
# of each side ($null where that side has no more), up to 2 lines before it, and how many objects differ.
function Compare-CauseLines($Expected, $Actual) {
    $ge = Group-CauseLines $Expected; $ga = Group-CauseLines $Actual
    $objs = @(@($ge.Keys) + @($ga.Keys) | Sort-Object -Unique -CaseSensitive)
    $first = $null; $count = 0
    foreach ($o in $objs) {
        $a = @(if ($ge.ContainsKey($o)) { $ge[$o] }); $b = @(if ($ga.ContainsKey($o)) { $ga[$o] })
        $n = [Math]::Max($a.Count, $b.Count)
        for ($i = 0; $i -lt $n; $i++) {
            $x = $(if ($i -lt $a.Count) { $a[$i] } else { $null }); $y = $(if ($i -lt $b.Count) { $b[$i] } else { $null })
            if ($null -eq $x -or $null -eq $y -or $x -cne $y) {
                $count++
                if ($null -eq $first) { $first = @{ object = $o; index = $i + 1; expected = $x; actual = $y; expectedCount = $a.Count; actualCount = $b.Count; before = @(if ($i -gt 0) { $a[[Math]::Max(0, $i - 2)..($i - 1)] }) } }
                break
            }
        }
    }
    if ($null -eq $first) { return $null }
    $first.objects = $count
    return $first
}
# A record against a golden file: (a) the caused writes ('= ') must be the golden's exactly, in the golden's order per object (Get-WriteObject);
# (b) every settled background line of the golden ('+ ') must be among the record's background lines, settled or tail ('+ ' or '~ '). The
# golden's tail lines ('~ ') are checked at the end of the group (Assert-GoldenTails), not here. Background lines the golden does not name
# are not a difference: what else the gateway's loops wrote before the scenario ended is timing.
function Compare-WritesFiles([string]$Golden, [string]$Record) {
    $g = Read-WritesSections $Golden; $r = Read-WritesSections $Record
    $cause = Compare-CauseLines $g.cause $r.cause
    $missing = @($g.background | Where-Object { -not $r.background.Contains($_) -and -not $r.tail.Contains($_) } | Sort-Object -CaseSensitive)
    $format = @($g.other) + @($r.other)
    return @{ ok = ($null -eq $cause -and $missing.Count -eq 0 -and $format.Count -eq 0); cause = $cause; missing = $missing; format = $format }
}
# After a scenario: the fake's answer to scenario-end ($Rec: ok, file, raw, writes, leftover), checked against the golden file when one exists.
function Compare-GoldenWrites($Rec, [string]$Name, [string]$Mode = (Get-GoldenMode)) {
    if ((Get-RecField $Rec 'recording') -eq $false) { return }   # a fake started without FAKE_RECORD_DIR records nothing
    $file = "$(Get-RecField $Rec 'file')"
    if (-not (Get-RecField $Rec 'ok') -or $file -eq '') { Check 'golden writes recorded' $false "(the fake wrote no record: $(Get-RecField $Rec 'reason'))"; return }
    if ($Mode -ne 'compare') { return }
    # the golden file has the record's own suite directory and file name, so the fake's spelling of both is the only one
    $golden = Join-Path $script:Root (Join-Path 'tests/scenarios/golden' (Join-Path (Split-Path -Leaf (Split-Path -Parent $file)) (Split-Path -Leaf $file)))
    if (-not (Test-Path -LiteralPath $golden)) { return }   # no golden file: this scenario is recorded only
    $r = Compare-WritesFiles $golden $file
    $detail = ''
    if (-not $r.ok) {
        $shown = @(); $why = @()
        if ($r.format.Count -gt 0) { $why += "$($r.format.Count) lines without '= ', '+ ' or '~ '"; $shown += @($r.format | Select-Object -First 2 | ForEach-Object { "? no section  $_" }) }
        if ($null -ne $r.cause) {
            $c = $r.cause
            $why += "the caused writes differ on $($c.objects) object(s)"
            $shown += "= caused writes of $($c.object): write $($c.index) differs (golden has $($c.expectedCount), the record $($c.actualCount))"
            foreach ($b in @($c.before)) { $shown += "    same      $b" }
            $shown += "    golden    $(if ($null -eq $c.expected) { '(no more writes)' } else { $c.expected })"
            $shown += "    record    $(if ($null -eq $c.actual) { '(no more writes)' } else { $c.actual })"
        }
        $shown += "golden: $golden"; $shown += "record: $file"; $shown += "raw:    $(Get-RecField $Rec 'raw')"
        $detail = "(the writes differ from the golden file: $($why -join '; '))`n        " + ($shown -join "`n        ")
    }
    # caused writes and the format block; a missing settled background line is a quarantined miss of its own (Add-GoldenMiss)
    Check 'golden writes unchanged' ($null -eq $r.cause -and $r.format.Count -eq 0) $detail
    foreach ($l in $r.missing) { Add-GoldenMiss -Section '+' -Scenario $Name -Suite (Split-Path -Leaf (Split-Path -Parent $file)) -Slug ((Split-Path -Leaf $file) -replace '\.writes\.txt$', '') -Line $l -Golden $golden }
}
# A required background line of a golden file that a run did not write: a settled line ('+ ') missing from the scenario's record, or a tail line
# ('~ ') written by no write of the group. Whether a background line lands in time is the gateway loops' timing, so a miss is reported as a
# quarantined check (issue #386: printed with its scenario and normalized line, counted, the suite does not fail), while a difference of the
# caused writes stays a failure. The repeat rule (README: Comparison): a stage C pull request with a miss re-runs that shard once, and the same
# line missing again in the same scenario is a regression that blocks the pull request. To compare two attempts, every miss is also appended
# as "<section> <suite>/<slug> <line>" to golden-misses.txt in the records' directory (TEST_WRITES_DIR, uploaded as writes-<job>-<attempt>).
function Add-GoldenMiss([string]$Section, [string]$Scenario, [string]$Suite, [string]$Slug, [string]$Line, [string]$Golden) {
    $what = $(if ($Section -eq '+') { 'settled background line missing from the record' } else { 'tail line written by no write of the group' })
    CheckQuarantined "golden $what`: $Scenario" $false "`n        $Section $Line`n        golden: $Golden" -Issue '#386'
    if ($env:FAKE_RECORD_DIR) {
        try { [IO.File]::AppendAllText((Join-Path $env:FAKE_RECORD_DIR 'golden-misses.txt'), "$Section $Suite/$Slug $Line`n", [Text.UTF8Encoding]::new($false)) } catch { Write-Host "    (golden-misses.txt not written: $($_.Exception.Message))" }
    }
}
# Makes golden files from the records of several runs of the same code (each -From directory is one run: its *.writes.txt files, searched
# recursively, are keyed by their directory, the suite, and their name). A scenario's caused writes must be the same in every record of it
# (Compare-CauseLines): then the golden file gets them in the order of the first run's record; else the scenario is reported unstable, gets no
# golden file, and a golden file it had in -Out is removed (it is recorded only, until it is made stable). Its settled lines ('+ ') are those
# settled in every record (the intersection); its tail lines ('~ ') those that every record's group set holds (its '+ ' and '~ ' lines and
# the '~ ' lines of its <slug>.later.txt) and that are neither '+ ' nor '= ' lines. A scenario missing from a run is reported and left out.
# The merge itself is the fake's command (tests/FakeGitHub/GoldenMerge.cs, build it first), so it runs without PowerShell as
#   dotnet tests/FakeGitHub/bin/Release/net10.0/FakeGitHub.dll merge-golden --from a,b --out tests/scenarios/golden
# and this function only calls it (relative directories are of the current location, which a native command starts in).
function Merge-GoldenWrites([string[]]$From, [string]$Out = (Join-Path $script:Root 'tests/scenarios/golden')) {
    $dirs = @($From | ForEach-Object { "$_" -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })
    if ($dirs.Count -eq 0) { throw 'Merge-GoldenWrites: name at least one -From directory' }
    $dll = Join-Path $script:Root 'tests/FakeGitHub/bin/Release/net10.0/FakeGitHub.dll'
    if (-not (Test-Path -LiteralPath $dll)) { throw "Merge-GoldenWrites: build first: $dll is missing" }
    & dotnet $dll merge-golden --from ($dirs -join ',') --out $Out
    if ($LASTEXITCODE -ne 0) { throw "Merge-GoldenWrites: merge-golden ended with exit $LASTEXITCODE" }
}
# Before a scenario's record is closed (and before the group check): the gateway's background work the scenario set off (the sweep's journal
# lines, plant offers and reassignments, runtime ends, promotion sign-offs) is let finish, so whether such a tail write lands in the record
# does not depend on how fast the runner was. The record closes when both hold: a sweep has finished since the body ended (the gateway's
# lastSweep moved; not waited for when the gateway is down or never swept) and the fake has seen no compared write for 2.2 s (a sweep only
# starts in an interval; its rule script writes later). At most 5 s after the body in all: a gateway that writes on every sweep is not waited out.
function Wait-RecordSettled {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $base = try { Get-GatewayBase } catch { $null }
    $read = { if (-not $base) { return $null }; try { $v = (Invoke-RestMethod "$base/api/v1/status" -Headers @{ Authorization = 'Bearer test-token' } -TimeoutSec 2).lastSweep; if ($v -is [datetime]) { "$($v.Ticks)" } else { "$v" } } catch { $null } }
    $first = & $read
    $swept = ($null -eq $first -or $first -eq '')   # no sweep to wait for
    while ($sw.ElapsedMilliseconds -lt 5000) {
        if (-not $swept) { $now = & $read; if ($null -eq $now -or $now -ne $first) { $swept = $true } }
        $q = $null
        try { $q = Fake 'quiet' @{} } catch { return }
        $since = Get-RecField $q 'sinceLastWriteMs'
        if ($null -eq $since) { return }
        if ($swept -and [double]$since -ge 2200) { return }
        $left = 5000 - $sw.ElapsedMilliseconds
        if ($left -le 0) { return }
        $wait = $(if ($swept) { [math]::Min(2250 - [double]$since, 1000) } else { 250 })
        Start-Sleep -Milliseconds ([int][math]::Max(50, [math]::Min($wait, $left)))
    }
}
# Before a scenario's /_fake/reset: the reset must not land while the gateway's sweep is reading the items it removes. A sweep that is settling an
# item the scenario before merged (its release candidate, its delivery) then finds the card or the Octopus task gone and logs a sweep error, which
# the suite's invariant counts (issue #386: seen on item 25 of the auto suite). After every sweep the loop waits 2 s before the next one
# (gateway/Sweeper.cs), so the reset goes in that gap: at once when the last full sweep ended less than 1.5 s ago, else after the sweep that runs
# now has ended (lastSweep moves), at most 5 s. No wait without a gateway, or when its last sweep is more than 8 s old (a suite whose sweep
# interval is long: no sweep is due), or when it never swept. A scenario that edits an item's state which the sweep also saves (Set-ItemState)
# makes the edit in the same gap, so no sweep reads the state before the edit and saves it after (-MaxAge: how recent the last sweep must be).
function Wait-SweepGap([double]$MaxAge = 1.5) {
    $base = try { Get-GatewayBase } catch { $null }
    if (-not $base) { return }
    $read = { try { $v = (Invoke-RestMethod "$base/api/v1/status" -Headers @{ Authorization = 'Bearer test-token' } -TimeoutSec 2).lastSweep; if ($v -is [datetime]) { $v.ToUniversalTime() } elseif ("$v") { [datetime]::Parse("$v").ToUniversalTime() } else { $null } } catch { $null } }
    $first = & $read
    if ($null -eq $first) { return }
    $age = ([datetime]::UtcNow - $first).TotalSeconds
    if ($age -lt $MaxAge -or $age -gt 8) { return }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt 5000) {
        Start-Sleep -Milliseconds 100
        $now = & $read
        if ($null -eq $now -or $now -ne $first) { return }
    }
}
function Run-Scenario([string]$Name, [scriptblock]$Body) {
    $script:Current = $Name; Write-Host "  $Name"
    $tmStart = [Diagnostics.Stopwatch]::StartNew(); $tmChecks = $script:Checks; $tmFailed = $script:Failures.Count
    $goldenMode = Get-GoldenMode
    try {
        try { Wait-SweepGap } catch { }
        Fake 'reset' @{} | Out-Null
        # the record of this scenario's writes starts after the reset (tests/scenarios/golden/README.md)
        if ($goldenMode -ne 'off') {
            try {
                $begun = Fake 'scenario-begin' @{ suite = $script:SuiteName; scenario = $Name; scenarioSlug = (Get-ScenarioSlug $Name) }
                # the scenario's cause nonce: F tags each call with "<nonce>.<n>" ('' when the fake records nothing: no call is tagged)
                $script:CauseNonce = "$(Get-RecField $begun 'cause')"; $script:CauseSeq = 0; $script:GoldenOpen = $true
            }
            catch { Check 'golden writes recorded' $false "(the fake did not begin the record: $($_.Exception.Message))"; $goldenMode = 'off' }
        }
        & $Body
    }
    catch { Check 'no unexpected exception' $false $_.Exception.Message }
    finally {
        $script:GoldenOpen = $false; $script:CauseNonce = ''; $script:CauseSeq = 0   # (no call is tagged after the body)
        if ($goldenMode -ne 'off') {
            $goldenRec = $null
            try { Wait-RecordSettled } catch { }
            try { $goldenRec = Fake 'scenario-end' @{} } catch { Check 'golden writes recorded' $false "(the fake did not end the record: $($_.Exception.Message))" }
            if ($null -ne $goldenRec) { try { Compare-GoldenWrites $goldenRec $Name $goldenMode } catch { Check 'golden writes recorded' $false "(the record could not be compared: $($_.Exception.Message))" } }
        }
    }
    $tmCount = $script:Checks - $tmChecks
    Write-Host ("    scenario took {0:N1}s, {1} checks" -f $tmStart.Elapsed.TotalSeconds, $tmCount)
    [void]$script:ScenarioStats.Add([ordered]@{ suite = $script:SuiteName; name = $Name; tier = (Get-ScenarioTier $Name); seconds = [math]::Round($tmStart.Elapsed.TotalSeconds, 1); checks = $tmCount; failed = ($script:Failures.Count - $tmFailed) })
}

function Insp-Text([string]$Verdict, [string]$Notes = 'Read the diff against the item.') { "$Notes`nINSPECTOR: $Verdict" }
function Gh-As([string]$Token, [string]$Path, $Body) {
    Invoke-RestMethod "http://127.0.0.1:$($script:FakePort)/repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders/$Path" -Method POST -Headers @{ Authorization = "Bearer $Token" } -Body ($Body | ConvertTo-Json -Depth 5) -ContentType 'application/json'
}
function Get-Head([int]$pr) { (Invoke-RestMethod "http://127.0.0.1:$($script:FakePort)/repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders/pulls/$pr").head.sha }
function To-Verdict([int]$n, [int]$Pr, $Files = @('docs/a.md'), [bool]$DocsOnly = $true, [string]$Body = 'x', [switch]$HoldMerge) {
    # An item taken to a recorded PASS verdict, ready for the approval step. Under merge policy auto the gateway merges that PASS at once:
    # -HoldMerge lets the checks of the head run again for an hour first, so the PASS stays recorded and unmerged for the scenario.
    $n2 = New-Item-Fake 'Approval trial' $Body $L2
    Do-TechnicalDesign $n2 'w1' $DocsOnly
    $pr2 = Do-Development $n2 $Files
    Do-DevComplete $n2 | Out-Null
    if ($HoldMerge) { Fake 'pr' @{ number = $pr2; delaySeconds = 3600 } | Out-Null }
    $v = Do-Verdict $n2 $pr2
    return @{ n = $n2; pr = $pr2; verdict = $v }
}

# Edits the item's state comment the way a lease end would (the fake keeps it as a comment): $Edit gets the state hashtable.
function Set-ItemState([int]$Item, [scriptblock]$Edit) {
    $sc = @((Get-FakeIssue $Item).comments | Where-Object { $_.body -like '<!-- factory-state:*' })[0]
    $mm = [regex]::Match($sc.body, '(?s)```json\s*(.*?)\s*```'); $st = $mm.Groups[1].Value | ConvertFrom-Json -AsHashtable
    & $Edit $st
    $body = $sc.body.Substring(0, $mm.Groups[1].Index) + ($st | ConvertTo-Json -Depth 12 -Compress) + $sc.body.Substring($mm.Groups[1].Index + $mm.Groups[1].Length)
    [void](Invoke-RestMethod "http://127.0.0.1:$($script:FakePort)/repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders/issues/comments/$($sc.id)" -Method PATCH -Body (@{ body = $body } | ConvertTo-Json) -ContentType 'application/json')
}
function Get-ItemState([int]$Item) {
    $sc = @((Get-FakeIssue $Item).comments | Where-Object { $_.body -like '<!-- factory-state:*' })[0]
    [regex]::Match($sc.body, '(?s)```json\s*(.*?)\s*```').Groups[1].Value | ConvertFrom-Json -AsHashtable
}
function Expire-Claim([int]$Item, [string]$TakenBack = '') {
    Set-ItemState $Item { param($st) $st.claim.lease_until = (Get-Date).ToUniversalTime().AddMinutes(-1).ToString('o'); if ($TakenBack) { $st.claim.taken_back = $TakenBack } }
}

# Waits until -Count full sweeps of the gateway have started after the call and their sync-board has returned: its lastSweep (/api/v1/status) moves
# -Count + 1 times (the first move may end a sweep that was already running when the call came). The gateway sets lastSweep when sync-board returns,
# before it applies that sweep's plan (offers, drops), so the wait goes on 1 s after the last move it saw for that step to finish. It waits at least
# -MinSeconds in all as well, so a fixed sleep it replaces is never made shorter. When the moves do not come within -Seconds more it throws: the
# scenario fails with the message, it never goes on as if they ran. A gateway that restarts meanwhile (lastSweep empty again) starts the count over.
function Wait-Sweeps([int]$Count = 1, [double]$MinSeconds = 0, [int]$Seconds = 120, [string]$Base = '') {
    if (-not $Base) { $Base = Get-GatewayBase }
    $read = { try { $v = (Invoke-RestMethod "$Base/api/v1/status" -Headers @{ Authorization = 'Bearer test-token' } -TimeoutSec 5).lastSweep; if ($v -is [datetime]) { "$($v.Ticks)" } else { "$v" } } catch { $null } }
    $sw = [Diagnostics.Stopwatch]::StartNew(); $last = & $read; $moves = 0
    while ($moves -le $Count) {
        if ($sw.Elapsed.TotalSeconds -gt $MinSeconds + $Seconds) { throw "Wait-Sweeps: lastSweep of the gateway at $Base moved $moves of $($Count + 1) times in $([int]$sw.Elapsed.TotalSeconds) s (wanted $Count sweeps after the call)" }
        Start-Sleep -Milliseconds 250
        $now = & $read
        if ($null -eq $now) { continue }
        if ($null -eq $last -or $now -eq '') { $last = $now; if ($now -eq '') { $moves = 0 }; continue }
        if ($now -ne $last) { $moves++; $last = $now }
    }
    Start-Sleep -Milliseconds 1000   # (the plan of the last sweep is applied after its lastSweep moved)
    $rest = $MinSeconds - $sw.Elapsed.TotalSeconds; if ($rest -gt 0) { Start-Sleep -Milliseconds ([int][math]::Ceiling($rest * 1000)) }
}
