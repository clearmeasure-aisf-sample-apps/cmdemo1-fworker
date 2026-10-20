# Tier 1 scenarios of the environment label (docs/design/environment-test-factories.md 3): two gateways, tdd (the suite's) and uat (a second
# one), share ONE fake board and ONE repository; each acts only on items labelled env:<its environment>. Suite 'envscope': FACTORY_ENVIRONMENT=tdd
# and FACTORY_ENV_SCOPE=1 on the suite's gateway, FACTORY_ENVIRONMENT=uat on the second.

# The uat gateway: same fake GitHub, configuration and secrets, its own ports and state directory, sweeping every 2 s like the suite's.
function Env-StartUat {
    $dll = Join-Path $script:Root 'gateway/bin/Release/net10.0/FactoryWorker.Gateway.dll'; $port = $script:PortBase + 12
    $all = @{ FACTORY_API_PORT = "$port"; FACTORY_WEBHOOK_PORT = "$($port + 1)"; FACTORY_STATE_DIR = (Join-Path $script:Work 'uat-state'); FACTORY_SWEEP_SECONDS = '2'; FACTORY_ENVIRONMENT = 'uat'; FACTORY_ENV_SCOPE = '1' }
    $saved = @{}; foreach ($k in $all.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $all[$k]) }
    try { $p = Start-Process dotnet -ArgumentList $dll -PassThru -RedirectStandardOutput (Join-Path $script:Work 'gateway-uat.out') -RedirectStandardError (Join-Path $script:Work 'gateway-uat.err') }
    finally { foreach ($k in $all.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) } }
    $script:Procs += $p
    $g = @{ proc = $p; base = "http://127.0.0.1:$port" }
    [void](Wait-Until -Seconds 40 { try { "$((Invoke-RestMethod "$($g.base)/api/v1/status" -Headers @{ Authorization = 'Bearer test-token' } -TimeoutSec 2).lastSweep)" -ne '' } catch { $false } })
    return $g
}
# One verb over HTTP with the operator token; the parsed answer (ok, code, ...).
function Env-Verb([string]$Base, [string]$Verb, [hashtable]$Body) {
    $r = Invoke-WebRequest -Uri "$Base/api/v1/verbs/$Verb" -Method POST -Body ($Body | ConvertTo-Json -Compress -Depth 10) -ContentType 'application/json' -Headers @{ Authorization = 'Bearer test-token' } -SkipHttpErrorCheck -TimeoutSec 120
    try { return ("$($r.Content)" | ConvertFrom-Json -AsHashtable) } catch { return @{ ok = $false; code = "http-$([int]$r.StatusCode)" } }
}
function Env-NoCardIssue([string]$Title, [string[]]$Labels) { (Fake 'issue' @{ title = $Title; body = 'x'; labels = $Labels }).number }
function Env-Mentions($Answer, [int]$n) { (($Answer | ConvertTo-Json -Depth 12 -Compress) -match "`"item`":$n[,}]") }

$EnvScopeScenarios = [ordered]@{

'env label: two gateways on one board each act only on items of their own env: label; unlabelled and doubly labelled items are nobody''s' = {
    $tdd = Get-GatewayBase
    $uat = Env-StartUat
    try {
        $wl = @('factory worker', 'Auto Merge')
        $a = New-Item-Fake 'tdd item' 'x' ($wl + 'env:tdd')
        $u = New-Item-Fake 'uat item' 'x' ($wl + 'env:uat')
        $x = New-Item-Fake 'both envs' 'x' ($wl + 'env:tdd' + 'env:uat')
        $z = New-Item-Fake 'no env label' 'x' $wl
        # the board guard: an issue without a card is placed only by the gateway of its environment
        $ga = Env-NoCardIssue 'tdd intake' ($wl + 'env:tdd'); $gu = Env-NoCardIssue 'uat intake' ($wl + 'env:uat'); $gz = Env-NoCardIssue 'nobody''s intake' $wl
        Check 'the tdd gateway places the env:tdd issue on the board' (Wait-Until -Seconds 30 { (Get-FakeIssue $ga).column -eq 'Technical Design' })
        Check 'the uat gateway places the env:uat issue on the board' (Wait-Until -Seconds 30 { (Get-FakeIssue $gu).column -eq 'Technical Design' })
        Start-Sleep -Seconds 5
        Check 'nobody places an issue without an env: label' ($null -eq (Get-FakeIssue $gz).column)

        # the sweep plans only its own items
        $st = Env-Verb $tdd 'sync-board' @{ dry = $true }; $su = Env-Verb $uat.base 'sync-board' @{ dry = $true }
        Check 'the tdd sweep never names the uat, doubly labelled or unlabelled items' (-not (Env-Mentions $st $u) -and -not (Env-Mentions $st $x) -and -not (Env-Mentions $st $z)) ($st | ConvertTo-Json -Compress -Depth 6)
        Check 'the uat sweep never names the tdd, doubly labelled or unlabelled items' (-not (Env-Mentions $su $a) -and -not (Env-Mentions $su $x) -and -not (Env-Mentions $su $z)) ($su | ConvertTo-Json -Compress -Depth 6)

        # assignment: a claim through the other environment's gateway is refused before anything is written
        $c1 = Env-Verb $tdd 'claim-item' @{ item = $u; column = 'Technical Design'; holder = 'w-tdd' }
        Check 'tdd refuses to claim the env:uat item: out-of-scope' (-not $c1.ok -and $c1.code -eq 'out-of-scope') ($c1 | ConvertTo-Json -Compress)
        $c2 = Env-Verb $uat.base 'claim-item' @{ item = $a; column = 'Technical Design'; holder = 'w-uat' }
        Check 'uat refuses to claim the env:tdd item: out-of-scope' (-not $c2.ok -and $c2.code -eq 'out-of-scope') ($c2 | ConvertTo-Json -Compress)
        foreach ($b in $tdd, $uat.base) {
            foreach ($n in $x, $z) { $c = Env-Verb $b 'claim-item' @{ item = $n; column = 'Technical Design'; holder = 'w' }; Check "item #$n (doubly or not labelled) is refused by $b" ($c.code -eq 'out-of-scope') ($c | ConvertTo-Json -Compress) }
        }
        $own = Env-Verb $tdd 'claim-item' @{ item = $a; column = 'Technical Design'; holder = 'w-tdd' }
        Check 'tdd claims its own item' ($own.ok) ($own | ConvertTo-Json -Compress)
        $ownU = Env-Verb $uat.base 'claim-item' @{ item = $u; column = 'Technical Design'; holder = 'w-uat' }
        Check 'uat claims its own item' ($ownU.ok) ($ownU | ConvertTo-Json -Compress)

        # merge: the other environment's item is refused before any other check
        $m = Env-Verb $tdd 'merge' @{ item = $u; pr = 1 }
        Check 'tdd refuses to merge for the env:uat item: out-of-scope' ($m.code -eq 'out-of-scope') ($m | ConvertTo-Json -Compress)
        $m2 = Env-Verb $uat.base 'merge' @{ item = $a; pr = 1 }
        Check 'uat refuses to merge for the env:tdd item: out-of-scope' ($m2.code -eq 'out-of-scope') ($m2 | ConvertTo-Json -Compress)

        # webhooks: an event about the other environment's item makes no event
        $payload = @{ action = 'labeled'; label = @{ name = 'factory worker' }; issue = @{ number = $u; labels = @(@{ name = 'factory worker' }, @{ name = 'env:uat' }) }; repository = @{ full_name = 'clearmeasure-aisf-sample-apps/cmdemo1-workorders' } }
        $ev = Env-Verb $tdd 'ingest-event' @{ event = 'issues'; delivery = 'd1'; payload = $payload }
        Check 'tdd makes no event of a uat item''s webhook' ($ev.ok -and @($ev.events).Count -eq 0) ($ev | ConvertTo-Json -Compress -Depth 6)
        $evU = Env-Verb $uat.base 'ingest-event' @{ event = 'issues'; delivery = 'd2'; payload = $payload }
        Check 'uat makes its item.labeled event of the same webhook' (@($evU.events | Where-Object { $_.event -eq 'item.labeled' }).Count -eq 1) ($evU | ConvertTo-Json -Compress -Depth 6)

        # after several sweeps of both: neither gateway wrote on the other's items (state comments, journal lines, claim refs)
        Start-Sleep -Seconds 6
        $fs = Get-FakeState
        Check 'no comment of any gateway on the unlabelled or doubly labelled items' (@(@($fs.issues | Where-Object { $_.number -in $x, $z }) | ForEach-Object { @($_.comments) } | Where-Object { $_ }).Count -eq 0)
        $claimU = (Get-FakeIssue $u).comments | Where-Object { "$($_.body)" -match 'w-tdd' }
        Check 'the uat item carries nothing of the tdd holder' (@($claimU).Count -eq 0)
        $claimA = (Get-FakeIssue $a).comments | Where-Object { "$($_.body)" -match 'w-uat' }
        Check 'the tdd item carries nothing of the uat holder' (@($claimA).Count -eq 0)
        $uerr = @(Select-String -Path (Join-Path $script:Work 'gateway-uat.out'), (Join-Path $script:Work 'gateway-uat.err') -Pattern 'sweepError|internal error|"sweep":"error"|"sweep":"failed"' -ErrorAction SilentlyContinue)
        Check 'the uat gateway swept without errors beside tdd''s claims' ($uerr.Count -eq 0) ($uerr | Select-Object -First 2 | Out-String)
    } finally {
        try { Stop-Process -Id $uat.proc.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
}
}
