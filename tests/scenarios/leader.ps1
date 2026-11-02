# One acting gateway across releases (gateway/Leader.cs, issue 94): the lease is the SQL application lock (gateway/Leadership/SqlLease.cs) and the only one since V3.2. A newer process asks for the lock, the holder
# stops acting, writes its final plant to the database, hands its streams over and lets the lock go, and the newer one restores that plant before its first sweep; a graceful stop does the same; the claims that ran
# out while no gateway was up are renewed. The GitHub ref lease, the ref snapshot and the rule scripts that kept them are gone, and so are the scenarios of their compare-and-swap and of the fencing of the scripts.
# Runs in the owner suite, on the engine that has a database (v2): a gateway without one has no lease. The suite runs with the lease and the plant off (run.ps1); these scenarios switch both on for their own
# gateways and give the suite's gateway back in finally. The fake's forwarder (FAKE_PROXY_PORT, lib.ps1) stands in for the platform's ingress.

$script:LeaderEnv = @{ FACTORY_LEADER_LEASE = '1'; FACTORY_PLANT_SNAPSHOT = '1'; FACTORY_TIMING_leaderSqlCheckSeconds = '1'
    FACTORY_TIMING_plantSnapshotDebounceSeconds = '1' }
function Ld-SetEnv([hashtable]$Env) { $old = @{}; foreach ($k in $Env.Keys) { $old[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $Env[$k]) }; return $old }
function Ld-Status([string]$Base) { Invoke-RestMethod "$Base/api/v1/status" -Headers @{ Authorization = 'Bearer test-token' } -TimeoutSec 10 }
# a field of the status by its path (a.b.c), or $null while it is not there yet (strict mode refuses a missing property)
function Ld-Get($Obj, [string]$Path) { $o = $Obj; foreach ($p in $Path.Split('.')) { if ($null -eq $o) { return $null }; $pp = $o.PSObject.Properties[$p]; if (-not $pp) { return $null }; $o = $pp.Value }; return $o }
# A time of the status as a UTC DateTime with its fraction. Invoke-RestMethod has already made a DateTime of the ISO text; formatting that with "$x" keeps whole
# seconds only, so a save 0.6 s after an event of the same second read as older than the event (the owner-8 failure of build 41195566959).
function Ld-Time($V) { if ($V -is [DateTime]) { return $V.ToUniversalTime() }; [DateTime]::Parse("$V", [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AdjustToUniversal) }
function Ld-Restore([string]$Base) { Ld-Get (Ld-Status $Base) 'plantSnapshot.restore' }
function Ld-Log($P) { if (Test-Path $P) { @(Get-Content $P) } else { @() } }
# A gateway process of this suite on another port (same fake GitHub, database, configuration and secrets), with a state directory of its own, as a second replica
# or revision has. Its log is gateway-<tag>.out, which the suite's invariant reads as well.
function Ld-StartGateway([int]$PortOffset, [string]$Tag) {
    $dll = Join-Path $script:Root 'gateway/bin/Release/net10.0/FactoryWorker.Gateway.dll'; $port = $script:PortBase + $PortOffset
    $old = Ld-SetEnv @{ FACTORY_API_PORT = "$port"; FACTORY_WEBHOOK_PORT = "$($port + 1)"; FACTORY_STATE_DIR = (Join-Path $script:Work "state-$Tag") }
    $out = Join-Path $script:Work "gateway-$Tag.out"
    try { $p = Start-Process dotnet -ArgumentList $dll -PassThru -RedirectStandardOutput $out -RedirectStandardError (Join-Path $script:Work "gateway-$Tag.err") }
    finally { [void](Ld-SetEnv $old) }
    $script:Procs += $p
    [void](Wait-Until -Seconds 40 { try { [bool](Invoke-RestMethod "http://127.0.0.1:$port/ready" -TimeoutSec 2) } catch { $false } })
    return @{ proc = $p; base = "http://127.0.0.1:$port"; out = $out; port = $port }
}
# The suite's gateway process, started again on its own port without stopping anything first (the one before was stopped by the scenario).
function Ld-StartSuiteGateway {
    $gwDll = Join-Path $script:Root 'gateway/bin/Release/net10.0/FactoryWorker.Gateway.dll'; $tag = [guid]::NewGuid().ToString('N').Substring(0, 4)
    $out = Join-Path $script:Work "gateway-$tag.out"
    $script:Procs[1] = Start-Process dotnet -ArgumentList $gwDll -PassThru -RedirectStandardOutput $out -RedirectStandardError (Join-Path $script:Work "gateway-restarted.err")
    [void](Wait-Until -Seconds 30 { try { [bool](Invoke-RestMethod "$(Get-GatewayBase)/ready" -TimeoutSec 2) } catch { $false } })
    return $out
}
function Ld-SuiteLog { $f = Get-ChildItem -Path $script:Work -File -Filter 'gateway*.out' | Sort-Object LastWriteTime | Where-Object { $_.Name -notmatch 'gateway-ld' }; if ($f) { "$(@($f)[-1].FullName)" } else { '' } }
# (a gateway without a database has no lease, so the scenarios below say so on the engine that has none and pass)
function Ld-V2Only { if ($script:GoldenEngine -eq 'v2') { return $true }; Check 'v2 only: a gateway without a database has no lease (the SQL application lock is the lease); the engine with one runs this scenario' $true; return $false }

$LeaderScenarios = [ordered]@{}

$LeaderScenarios['leader: two gateways overlap: the newer takes the lease, the older stops acting, writes its final snapshot and hands its streams over; the newer restores it before its first sweep [full]'] = {
    if (-not (Ld-V2Only)) { return }
    $old = Ld-SetEnv ($script:LeaderEnv + @{ FACTORY_ASSIGN_ACK_SECONDS = '90' })
    $oldUrl = $env:FACTORY_URL; $b = $null
    try {
        Restart-Gateway
        $gwA = Get-GatewayBase
        # (the epoch is the term of the SQL lock, one more each time a process takes it in this database: a scenario that ran before has used some)
        Check 'the first gateway takes the lock and acts' (Wait-Until -Seconds 30 { $l = (Ld-Status $gwA).leader; $l.role -eq 'acting' -and $l.epoch -ge 1 -and $l.sql.held }) ((Ld-Status $gwA).leader | ConvertTo-Json -Compress -Depth 4)
        $epochA = [long](Ld-Get (Ld-Status $gwA) 'leader.epoch')
        # the sessions reach the gateways through the forwarder, as through the platform's ingress
        Fake 'proxy' @{ port = $script:ApiPort } | Out-Null
        $env:FACTORY_URL = "http://127.0.0.1:$($script:ProxyPort)"
        Start-OwnerClient 'lena' 'persistent-local' '--capacity 2'
        $ia = New-Item-Fake 'Leader owned' 'x' ($OwnerLabels + 'worker:lena'); $ib = New-Item-Fake 'Leader offered' 'x' ($OwnerLabels + 'worker:lena')
        [void](Wait-Until -Seconds 40 { (Get-Assigned 'lena' $ia).Count -gt 0 -and (Get-Assigned 'lena' $ib).Count -gt 0 })
        $ea = (Get-Assigned 'lena' $ia)[-1]; $eb = (Get-Assigned 'lena' $ib)[-1]
        $logA0 = Ld-SuiteLog; $beforeAccept = [DateTime]::UtcNow
        $r = Fo 'lena' @('accept', '--item', $ia, '--assignment', $ea.assignment); CheckExit 'lena accepts the first item' $r 0
        # When the gateway applied the acceptance: its own log line (verb accept, item), not the clock after the client process has exited. That process takes
        # a second or more to end, and with the snapshot debounce at 1 s the snapshot of the change can be written in that time (owner-8 of build 41195566959,
        # 2026-10-31: accept 11:12:18.17, snapshot 11:12:18.82, the check began after 11:12:18.9 and never saw a later one: nothing else changed).
        $acceptedAt = $beforeAccept
        $m = [regex]::Match((@(Ld-Log $logA0 | Where-Object { $_ -match '"verb":"accept"' -and $_ -match "`"item`":`"$ia`"" }) | Select-Object -Last 1), '"at":"([^"]+)"')
        if ($m.Success) { $acceptedAt = [DateTime]::Parse($m.Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AdjustToUniversal) }
        Check 'the holder writes its plant to the database after the change without being stopped' (Wait-Until -Seconds 20 { $ps = (Ld-Status $gwA).plantSnapshot; (Ld-Get $ps 'saves') -ge 1 -and (Ld-Get $ps 'heldEpoch') -eq $epochA -and (Ld-Time (Ld-Get $ps 'lastSave')) -gt $acceptedAt }) ("accepted at $($acceptedAt.ToString('o')): " + ((Ld-Status $gwA).plantSnapshot | ConvertTo-Json -Compress -Depth 4))
        # a new revision starts while the old one runs; the ingress sends new connections to it
        $b = Ld-StartGateway 12 'ldb'
        Fake 'proxy' @{ port = $b.port } | Out-Null
        Check 'the newer process takes the next term of the lock' (Wait-Until -Seconds 30 { (Ld-Status $b.base).leader.epoch -eq ($epochA + 1) }) ((Ld-Status $b.base).leader | ConvertTo-Json -Compress -Depth 4)
        Check 'the older one finds itself superseded' (Wait-Until -Seconds 20 { (Ld-Status $gwA).leader.role -eq 'superseded' }) ((Ld-Status $gwA).leader | ConvertTo-Json -Compress -Depth 4)
        $supAt = [DateTime]::UtcNow
        $logA = Ld-SuiteLog
        Check 'it writes its final snapshot and ends its streams' (Wait-Until -Seconds 20 { @(Ld-Log $logA | Where-Object { $_ -match '"gateway":"handed-over"' -and $_ -match '"finalSnapshot":true' }).Count -ge 1 }) ((Ld-Log $logA | Where-Object { $_ -match 'handed-over|plant-snapshot' }) -join "`n")
        # the newer process reads the plant the predecessor saved in SQL before it let the lock go (how sql, the epoch of the predecessor's term)
        Check 'the newer process restores that plant: from the database, the term of the older one, the owner and both assignments' (Wait-Until -Seconds 40 { $rs = Ld-Restore $b.base; $rs -and $rs.assignments -ge 2 -and $rs.owners -ge 1 -and $rs.how -eq 'sql' -and $rs.skipped -eq '' -and $rs.snapshotEpoch -eq $epochA }) ((Ld-Status $b.base).plantSnapshot | ConvertTo-Json -Compress -Depth 4)
        $sb = Ld-Status $b.base
        Check 'the owner and the accepted assignment keep their ids on the new process' (@($sb.plant.owners.PSObject.Properties | Where-Object { $_.Name -eq "$ia" -and $_.Value -eq 'lena' }).Count -eq 1 -and @($sb.plant.assignments | Where-Object { $_.item -eq $ia -and $_.id -eq $ea.assignment -and $_.accepted }).Count -eq 1) ($sb.plant | ConvertTo-Json -Compress -Depth 4)
        Check 'the session''s client is told of the hand-over and connects to the new process by itself' (Wait-Until -Seconds 40 { @(Get-WorkerEvents 'lena' | Where-Object { $_.event -eq 'stream.handover' }).Count -ge 1 -and @((Ld-Status $b.base).plant.workers | Where-Object { $_.name -eq 'lena' -and $_.connected }).Count -eq 1 }) (@(Get-WorkerEvents 'lena' | Select-Object -Last 6) | ConvertTo-Json -Compress -Depth 3)
        Check 'its client keeps running (no exit 40)' (-not $script:Workers['lena'].proc.HasExited)
        Check 'the unanswered offer is pushed again by the new process, the same assignment' (Wait-Until -Seconds 30 { (Get-Assigned 'lena' $ib).Count -ge 2 -and (Get-Assigned 'lena' $ib)[-1].assignment -eq $eb.assignment }) ((Get-Assigned 'lena' $ib) | ConvertTo-Json -Compress)
        $r2 = Fo 'lena' @('accept', '--item', $ib, '--assignment', (Get-Assigned 'lena' $ib)[-1].assignment); CheckExit 'and accepted there' $r2 0
        $va = Invoke-WebRequest "$gwA/api/v1/verbs/get-item" -Method POST -Headers @{ Authorization = 'Bearer test-token' } -Body (@{ item = $ia } | ConvertTo-Json) -ContentType 'application/json' -SkipHttpErrorCheck
        $ra = Invoke-WebRequest "$gwA/ready" -SkipHttpErrorCheck
        Check 'the old process answers every call 503 gateway-replaced, and is not ready' ([int]$va.StatusCode -eq 503 -and "$($va.Content)" -match 'gateway-replaced' -and [int]$ra.StatusCode -eq 503) "$([int]$va.StatusCode) $($va.Content) / ready $([int]$ra.StatusCode)"
        Start-Sleep -Seconds 6
        $late = @(Ld-Log $logA | Where-Object { $_ -match '"sweep":\{' } | Where-Object { $m = [regex]::Match($_, '"at":"([^"]+)"'); $m.Success -and [DateTime]::Parse($m.Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AdjustToUniversal) -gt $supAt.AddSeconds(3) })
        Check 'the old process sweeps no more' ($late.Count -eq 0) ($late -join "`n")
        Check 'the new process sweeps' ($null -ne (Ld-Status $b.base).lastSweep)
    } finally {
        Stop-OwnerClient 'lena'; $env:FACTORY_URL = $oldUrl; Fake 'proxy' @{ port = 0 } | Out-Null
        if ($b) { try { Stop-Process -Id $b.proc.Id -Force } catch { } }
        [void](Ld-SetEnv $old); Restart-Gateway
    }
}

$LeaderScenarios['leader: a graceful stop releases the lease; the next start restores at once and renews a claim that ran out while no gateway was up [full]'] = {
    if (-not (Ld-V2Only)) { return }
    # a claim lease of 15 s, so it runs out while no gateway is up
    $old = Ld-SetEnv ($script:LeaderEnv + @{ FACTORY_TIMING_leaseMinutes = '0.25' })
    try {
        Restart-Gateway
        [void](Wait-Until -Seconds 30 { (Ld-Status (Get-GatewayBase)).leader.role -eq 'acting' })
        $epochBefore = [long](Ld-Get (Ld-Status (Get-GatewayBase)) 'leader.epoch')
        Start-OwnerClient 'gina' 'persistent-local' '--capacity 2'
        $n = New-Item-Fake 'Leader gap' 'x' ($OwnerLabels + 'worker:gina')
        [void](Wait-Until -Seconds 40 { (Get-Assigned 'gina' $n).Count -gt 0 })
        $r = Fo 'gina' @('accept', '--item', $n, '--assignment', (Get-Assigned 'gina' $n)[-1].assignment); CheckExit 'gina accepts the item' $r 0
        $leaseUntil = [DateTime]::Parse("$($r.lease_until)", [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AdjustToUniversal)
        # the platform stops the process (SIGTERM) and starts the next one only after the claim's lease has passed
        $logA = Ld-SuiteLog
        $gp = $script:Procs[1]; & kill -s TERM $gp.Id 2>$null
        Check 'the stopped process exits' ($gp.WaitForExit(60000))
        Check 'at its stop it wrote its final plant to the database' (@(Ld-Log $logA | Where-Object { $_ -match '"gateway":"plant-snapshot"' -and $_ -match '"final":true' -and $_ -match '"exit":0' }).Count -ge 1) ((Ld-Log $logA | Where-Object { $_ -match 'plant-snapshot' }) -join "`n")
        $wait = ($leaseUntil.AddSeconds(3) - [DateTime]::UtcNow).TotalSeconds; if ($wait -gt 0) { Start-Sleep -Seconds ([Math]::Ceiling($wait)) }
        Remove-Item Env:FACTORY_TIMING_leaseMinutes -ErrorAction SilentlyContinue
        $t0 = [DateTime]::UtcNow
        [void](Ld-StartSuiteGateway)
        $gw = Get-GatewayBase
        # (the plant the stopped process saved in SQL, and the lock it let go: the next process takes the next term at once)
        Check 'the next start restores at once: the lock was let go and the plant is in the database' (Wait-Until -Seconds 30 { $rs = Ld-Restore $gw; $rs -and $rs.how -eq 'sql' -and $rs.owners -ge 1 -and $rs.gapSince }) ((Ld-Status $gw).plantSnapshot | ConvertTo-Json -Compress -Depth 4)
        Check 'it holds the next term of the lock' ((Wait-Until -Seconds 30 { (Ld-Status $gw).leader.role -eq 'acting' }) -and [long](Ld-Get (Ld-Status $gw) 'leader.epoch') -eq ($epochBefore + 1)) ((Ld-Status $gw).leader | ConvertTo-Json -Compress -Depth 4)
        $rsNow = Ld-Restore $gw
        $rsAt = if ($rsNow) { Ld-Time $rsNow.at } else { [DateTime]::MaxValue }
        Check 'without waiting for a hand-over' (($rsAt - $t0).TotalSeconds -lt 25) "$(($rsAt - $t0).TotalSeconds) s"
        Check 'its first sweep renews the claim that ran out in the gap' (Wait-Until -Seconds 40 { $st = Pm-State $n; $st.ContainsKey('claim') -and $st.claim.ContainsKey('gap_renewed') -and [DateTime]::Parse("$($st.claim.lease_until)", [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AdjustToUniversal) -gt [DateTime]::UtcNow }) ((Pm-State $n).claim | ConvertTo-Json -Compress)
        $j = Get-Journal $n; $gapLines = @((Get-FakeIssue $n).comments | Where-Object { "$($_.body)" -match 'plant event=gap-renewed' })
        Check 'the journal says so, and the item was not offered again' ($gapLines.Count -eq 1 -and $j -notcontains 'reassigned' -and @($j | Where-Object { $_ -eq 'matched' }).Count -eq 1) ($j -join ',')
        Check 'gina still owns it on the new process' (Wait-Until -Seconds 20 { @((Ld-Status $gw).plant.owners.PSObject.Properties | Where-Object { $_.Name -eq "$n" -and $_.Value -eq 'gina' }).Count -eq 1 }) ((Ld-Status $gw).plant.owners | ConvertTo-Json -Compress)
    } finally {
        Stop-OwnerClient 'gina'
        [void](Ld-SetEnv $old); Restart-Gateway
    }
}

foreach ($k in $LeaderScenarios.Keys) { $OwnerScenarios[$k] = $LeaderScenarios[$k] }
