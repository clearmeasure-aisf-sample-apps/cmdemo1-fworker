# Tier 1 scenarios of the environment label (docs/design/environment-test-factories.md 3): two gateways, tdd (the suite's) and uat (a second
# one), share ONE fake board and ONE repository; tdd acts only on items labelled env:tdd. Suite 'envscope': FACTORY_ENVIRONMENT=tdd
# and FACTORY_ENV_SCOPE=1 on the suite's gateway, FACTORY_ENVIRONMENT=uat on the second. engine.v2Environments does not name uat, so the uat
# gateway is inert (V3.3, slice 5): it refuses every verb (code inert) and writes nothing, its own items included.

# The uat gateway: same fake GitHub, configuration and secrets, its own ports and state directory, sweeping every 2 s like the suite's.
function Env-StartUat {
    $dll = Join-Path $script:Root 'gateway/bin/Release/net10.0/FactoryWorker.Gateway.dll'; $port = $script:PortBase + 12
    $all = @{ FACTORY_API_PORT = "$port"; FACTORY_WEBHOOK_PORT = "$($port + 1)"; FACTORY_STATE_DIR = (Join-Path $script:Work 'uat-state'); FACTORY_SWEEP_SECONDS = '2'; FACTORY_ENVIRONMENT = 'uat'; FACTORY_ENV_SCOPE = '1' }
    $saved = @{}; foreach ($k in $all.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $all[$k]) }
    try { $p = Start-Process dotnet -ArgumentList $dll -PassThru -RedirectStandardOutput (Join-Path $script:Work 'gateway-uat.out') -RedirectStandardError (Join-Path $script:Work 'gateway-uat.err') }
    finally { foreach ($k in $all.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) } }
    $script:Procs += $p
    $g = @{ proc = $p; base = "http://127.0.0.1:$port" }
    [void](Wait-Until -Seconds 40 { try { (Invoke-WebRequest "$($g.base)/ready" -SkipHttpErrorCheck -TimeoutSec 2).StatusCode -eq 503 } catch { $false } })
    return $g
}
# One verb over HTTP with the operator token; the parsed answer (ok, code, ...).
function Env-Verb([string]$Base, [string]$Verb, [hashtable]$Body) {
    $r = Invoke-WebRequest -Uri "$Base/api/v1/verbs/$Verb" -Method POST -Body ($Body | ConvertTo-Json -Compress -Depth 10) -ContentType 'application/json' -Headers @{ Authorization = 'Bearer test-token' } -SkipHttpErrorCheck -TimeoutSec 120
    try { return ("$($r.Content)" | ConvertFrom-Json -AsHashtable) } catch { return @{ ok = $false; code = "http-$([int]$r.StatusCode)" } }
}
function Env-NoCardIssue([string]$Title, [string[]]$Labels) { (Fake 'issue' @{ title = $Title; body = 'x'; labels = $Labels }).number }

$EnvScopeScenarios = [ordered]@{

'env label: tdd acts only on items of its env: label beside an inert uat gateway on the same board; unlabelled and doubly labelled items are nobody''s' = {
    $tdd = Get-GatewayBase
    $uat = Env-StartUat
    try {
        $ready = Invoke-WebRequest "$($uat.base)/ready" -SkipHttpErrorCheck -TimeoutSec 5
        $rj = try { "$($ready.Content)" | ConvertFrom-Json -AsHashtable } catch { @{} }
        Check 'the uat gateway is inert: /ready 503 waitingFor engine' ($ready.StatusCode -eq 503 -and $rj.waitingFor -eq 'engine') "$($ready.StatusCode) $($ready.Content)"
        $wl = @('factory worker', 'Auto Merge')
        $a = New-Item-Fake 'tdd item' 'x' ($wl + 'env:tdd')
        $u = New-Item-Fake 'uat item' 'x' ($wl + 'env:uat')
        $x = New-Item-Fake 'both envs' 'x' ($wl + 'env:tdd' + 'env:uat')
        $z = New-Item-Fake 'no env label' 'x' $wl
        # the board guard: an issue without a card is placed only by the gateway of its environment
        $ga = Env-NoCardIssue 'tdd intake' ($wl + 'env:tdd'); $gu = Env-NoCardIssue 'uat intake' ($wl + 'env:uat'); $gz = Env-NoCardIssue 'nobody''s intake' $wl
        Check 'the tdd gateway places the env:tdd issue on the board' (Wait-Until -Seconds 30 { (Get-FakeIssue $ga).column -eq 'Technical Design' })
        # (V3.2: the uat gateway of this scenario has no database, so no engine acts there and it places nothing; PlantStepsTests cover the board guard's env: rule)
        Start-Sleep -Seconds 5
        Check 'nobody places an issue without an env: label' ($null -eq (Get-FakeIssue $gz).column)

        # (V3.2: what each environment's intake and admission take is IntakeRule's and the admission's envScope rule: IntakeCapacityTests, AdmissionTests)

        # assignment: a claim through the other environment's gateway is refused before anything is written
        $c1 = Env-Verb $tdd 'claim-item' @{ item = $u; column = 'Technical Design'; holder = 'w-tdd' }
        Check 'tdd refuses to claim the env:uat item: out-of-scope' (-not $c1.ok -and $c1.code -eq 'out-of-scope') ($c1 | ConvertTo-Json -Compress)
        $c2 = Env-Verb $uat.base 'claim-item' @{ item = $a; column = 'Technical Design'; holder = 'w-uat' }
        Check 'uat (inert) refuses to claim the env:tdd item' (-not $c2.ok -and $c2.code -eq 'inert' -and $c2.exit -eq 20) ($c2 | ConvertTo-Json -Compress)
        foreach ($n in $x, $z) {
            $c = Env-Verb $tdd 'claim-item' @{ item = $n; column = 'Technical Design'; holder = 'w' }; Check "item #$n (doubly or not labelled) is refused by tdd" ($c.code -eq 'out-of-scope') ($c | ConvertTo-Json -Compress)
            $c = Env-Verb $uat.base 'claim-item' @{ item = $n; column = 'Technical Design'; holder = 'w' }; Check "item #$n (doubly or not labelled) is refused by uat (inert)" ($c.code -eq 'inert') ($c | ConvertTo-Json -Compress)
        }
        $own = Env-Verb $tdd 'claim-item' @{ item = $a; column = 'Technical Design'; holder = 'w-tdd' }
        Check 'tdd claims its own item' ($own.ok) ($own | ConvertTo-Json -Compress)
        $ownU = Env-Verb $uat.base 'claim-item' @{ item = $u; column = 'Technical Design'; holder = 'w-uat' }
        Check 'uat (inert) refuses even its own item' (-not $ownU.ok -and $ownU.code -eq 'inert') ($ownU | ConvertTo-Json -Compress)

        # merge: the other environment's item is refused before any other check
        $m = Env-Verb $tdd 'merge' @{ item = $u; pr = 1 }
        Check 'tdd refuses to merge for the env:uat item: out-of-scope' ($m.code -eq 'out-of-scope') ($m | ConvertTo-Json -Compress)
        $m2 = Env-Verb $uat.base 'merge' @{ item = $a; pr = 1 }
        Check 'uat (inert) refuses to merge' ($m2.code -eq 'inert') ($m2 | ConvertTo-Json -Compress)

        # webhooks: an event about the other environment's item makes no event
        $payload = @{ action = 'labeled'; label = @{ name = 'factory worker' }; issue = @{ number = $u; labels = @(@{ name = 'factory worker' }, @{ name = 'env:uat' }) }; repository = @{ full_name = 'clearmeasure-aisf-sample-apps/cmdemo1-workorders' } }
        $ev = Env-Verb $tdd 'ingest-event' @{ event = 'issues'; delivery = 'd1'; payload = $payload }
        Check 'tdd makes no event of a uat item''s webhook' ($ev.ok -and @($ev.events).Count -eq 0) ($ev | ConvertTo-Json -Compress -Depth 6)
        $evU = Env-Verb $uat.base 'ingest-event' @{ event = 'issues'; delivery = 'd2'; payload = $payload }
        Check 'uat (inert) makes no event of its own item''s webhook' ($evU.code -eq 'inert' -and -not $evU.ContainsKey('events')) ($evU | ConvertTo-Json -Compress -Depth 6)

        # after several sweeps of both: neither gateway wrote on the other's items (state comments, journal lines, claim refs)
        Start-Sleep -Seconds 6
        $fs = Get-FakeState
        Check 'no comment of any gateway on the unlabelled or doubly labelled items' (@(@($fs.issues | Where-Object { $_.number -in $x, $z }) | ForEach-Object { @($_.comments) } | Where-Object { $_ }).Count -eq 0)
        $claimU = (Get-FakeIssue $u).comments | Where-Object { "$($_.body)" -match 'w-tdd' }
        Check 'the uat item carries nothing of the tdd holder' (@($claimU).Count -eq 0)
        Check 'the uat item carries nothing of the inert uat gateway' (@((Get-FakeIssue $u).comments | Where-Object { "$($_.body)" -match 'w-uat' }).Count -eq 0)
        $claimA = (Get-FakeIssue $a).comments | Where-Object { "$($_.body)" -match 'w-uat' }
        Check 'the tdd item carries nothing of the uat holder' (@($claimA).Count -eq 0)
        $uerr = @(Select-String -Path (Join-Path $script:Work 'gateway-uat.out'), (Join-Path $script:Work 'gateway-uat.err') -Pattern 'sweepError|internal error|"sweep":"error"|"sweep":"failed"' -ErrorAction SilentlyContinue)
        Check 'the inert uat gateway logged no errors beside tdd''s claims' ($uerr.Count -eq 0) ($uerr | Select-Object -First 2 | Out-String)
    } finally {
        try { Stop-Process -Id $uat.proc.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
}
}
