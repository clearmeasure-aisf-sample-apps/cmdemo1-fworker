# Tier 1 scenarios of the cloud worker runtimes (docs/design/cloud-worker-runtimes.md): the w2 credential, the one-time nonce and the
# git-credential verb (gateway/Auth.cs). Suite 'runtime' (owner mode, messaging test hooks for the nonce seam). Uses the helpers of owner.ps1.

function Rt-Api([string]$Method, [string]$Path, $Body = $null, [string]$Token = 'test-token') {
    $h = @{}; if ($Token) { $h.Authorization = "Bearer $Token" }
    $p = @{ Uri = "http://127.0.0.1:$($script:ApiPort)$Path"; Method = $Method; Headers = $h; SkipHttpErrorCheck = $true; StatusCodeVariable = 'sc' }
    if ($null -ne $Body) { $p.Body = ($Body | ConvertTo-Json -Depth 6 -Compress); $p.ContentType = 'application/json' }
    $r = Invoke-RestMethod @p
    return @{ status = [int]$sc; body = $r }
}
function Rt-Nonce([string]$Name, [double]$TokenSeconds = 3600, [string]$Runtime = 'cae-job') { (Rt-Api POST '/api/v1/_test/runtime-nonce' @{ name = $Name; runtime = $Runtime; tokenSeconds = $TokenSeconds }).body.nonce }
function Rt-Redeem([string]$Nonce) { Rt-Api POST '/api/v1/runtime/redeem' @{ nonce = $Nonce } '' }

# A worker session that holds a w2: the real client (subscribe --own) with that credential, as fwrunner.sh starts it.
function Start-RuntimeClient([string]$Name, [string]$Token) {
    $dir = Join-Path $script:Work "tokens-$Name"; New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $out = Join-Path $script:Work "worker-$Name.out"
    $old = @{ d = $env:FACTORY_TOKEN_DIR; w = $env:FACTORY_WORKER_TOKEN }
    $env:FACTORY_TOKEN_DIR = $dir; $env:FACTORY_WORKER_TOKEN = $Token
    $p = Start-Process pwsh -ArgumentList @('-NoProfile', '-File', (Join-Path $script:Root 'plugin/scripts/factory.ps1'), 'subscribe', '--own', '--name', $Name, '--type', 'claude-cloud') -PassThru -RedirectStandardOutput $out -RedirectStandardError (Join-Path $script:Work "worker-$Name.err")
    $env:FACTORY_TOKEN_DIR = $old.d; $env:FACTORY_WORKER_TOKEN = $old.w
    $script:Procs += $p
    $script:Workers[$Name] = @{ proc = $p; dir = $dir; out = $out; token = $Token }
    [void](Wait-Until -Seconds 20 { @(Get-WorkerEvents $Name | Where-Object { $_.event -eq 'worker.registered' }).Count -gt 0 })
}
# Offers the item to the worker (pinned by label) and accepts it with the worker's item token.
function Rt-Own([string]$Name, [int]$Item) {
    if (-not (Wait-Until -Seconds 40 { (Get-Assigned $Name $Item).Count -gt 0 })) { return @{ exit = -1; code = 'no-offer' } }
    for ($try = 0; $try -lt 5; $try++) {
        $r = Fo $Name @('accept', '--item', $Item, '--assignment', (Get-Assigned $Name $Item)[-1].assignment)
        if ($r.exit -ne 11) { return $r }
        $seen = @(Get-Assigned $Name $Item).Count; [void](Wait-Until -Seconds 30 { @(Get-Assigned $Name $Item).Count -gt $seen })
    }
    return $r
}

$RuntimeScenarios = [ordered]@{

'runtime: a nonce is redeemed once for a w2 bound to its name and runtime; a second redemption and an unknown nonce are refused' = {
    $nonce = Rt-Nonce 'cae-1-a1b2c3'
    Check 'the seam mints a nonce' ("$nonce" -like 'n1.*')
    $r = Rt-Redeem $nonce
    Check 'the first redemption answers a w2 for the name and runtime' ($r.status -eq 200 -and $r.body.name -eq 'cae-1-a1b2c3' -and $r.body.runtime -eq 'cae-job' -and "$($r.body.token)" -like 'w2.cae-1-a1b2c3.cae-job.*') ($r | ConvertTo-Json -Compress)
    $w2 = "$($r.body.token)"
    $again = Rt-Redeem $nonce
    Check 'a second redemption of the same nonce is refused' ($again.status -eq 401 -and $again.body.code -eq 'unauthorized') ($again | ConvertTo-Json -Compress)
    $unknown = Rt-Redeem ('n1.' + ('0' * 64))
    Check 'an unknown nonce is refused alike' ($unknown.status -eq 401)
    Check 'the log tells a spent nonce from an unknown one' (Wait-Until -Seconds 5 { @(Get-ChildItem $script:Work -Filter 'gateway*.out' | Select-String -Pattern '"runtime":"redeem-refused","code":"nonce-spent"').Count -gt 0 })
    $mi = Rt-Api POST '/api/v1/verbs/my-items' @{} $w2
    Check 'the w2 is a worker credential: my-items answers' ($mi.status -eq 200) ($mi | ConvertTo-Json -Compress -Depth 5)
    $forged = $w2.Substring(0, $w2.LastIndexOf('.')) + '.' + ('0' * 64)
    Check 'a w2 with a forged MAC is refused' ((Rt-Api POST '/api/v1/verbs/my-items' @{} $forged).status -eq 401)
    $parts = $w2.Split('.'); $longer = "w2.$($parts[1]).$($parts[2]).$([long]$parts[3] + 86400).$($parts[4])"
    Check 'a w2 whose expiry was moved is refused' ((Rt-Api POST '/api/v1/verbs/my-items' @{} $longer).status -eq 401)
    $other = "w2.$($parts[1]).local.$($parts[3]).$($parts[4])"
    Check 'a w2 whose runtime was changed is refused' ((Rt-Api POST '/api/v1/verbs/my-items' @{} $other).status -eq 401)
    $vi = Rt-Api POST '/api/v1/verbs/get-item' @{ item = 1 } $w2
    Check 'a w2 cannot act on an item' ($vi.status -eq 403)
}

'runtime: a w2 expires with the execution' = {
    $r = Rt-Redeem (Rt-Nonce 'cae-2-d4e5f6' 3)
    Check 'redeemed' ($r.status -eq 200)
    Check 'the w2 answers before its expiry' ((Rt-Api POST '/api/v1/verbs/my-items' @{} "$($r.body.token)").status -eq 200)
    Start-Sleep -Seconds 5
    Check 'and is refused after it' ((Rt-Api POST '/api/v1/verbs/my-items' @{} "$($r.body.token)").status -eq 401)
    $late = Rt-Redeem (Rt-Nonce 'cae-2-d4e5f7' -5)
    Check 'a nonce whose token would already be expired is refused at redemption' ($late.status -eq 401)
}

'runtime: the runtime of a registration comes from the credential: a w1 cannot claim one, a w2 registers with its own' = {
    $tok = New-WorkerToken 'rtlocal1'
    $h = @{ Authorization = "Bearer $tok" }
    $resp = Invoke-WebRequest "http://127.0.0.1:$($script:ApiPort)/api/v1/events?name=rtlocal1&runtime=cae-job" -Headers $h -SkipHttpErrorCheck -TimeoutSec 10
    Check 'a w1 that claims runtime cae-job is refused' ([int]$resp.StatusCode -eq 403 -and "$($resp.Content)" -match 'runtime-not-trusted') "$([int]$resp.StatusCode) $($resp.Content)"
    $w2 = "$((Rt-Redeem (Rt-Nonce 'cae-3-0a0b0c')).body.token)"
    Start-RuntimeClient 'cae-3-0a0b0c' $w2
    try {
        $row = @((Get-Plant).plant.workers | Where-Object { $_.name -eq 'cae-3-0a0b0c' })
        Check 'the w2 worker is registered with its runtime' ($row.Count -eq 1 -and $row[0].runtime -eq 'cae-job' -and $row[0].connected) ($row | ConvertTo-Json -Compress)
        Start-OwnerClient 'rtlocal2'
        $row2 = @((Get-Plant).plant.workers | Where-Object { $_.name -eq 'rtlocal2' })
        Check 'a w1 worker is local' ($row2.Count -eq 1 -and $row2[0].runtime -eq 'local')
    } finally { Stop-OwnerClient 'cae-3-0a0b0c'; Stop-OwnerClient 'rtlocal2' }
}

'runtime: git-credential mints a one-repository contents-write token for the owner of a runtime worker, and for nobody else' = {
    $name = 'cae-4-1a2b3c'
    $w2 = "$((Rt-Redeem (Rt-Nonce $name)).body.token)"
    Start-RuntimeClient $name $w2
    Start-OwnerClient 'rtlocal4'
    try {
        $n = New-Item-Fake 'Runtime git credential' 'x' ($OwnerLabels + "worker:$name")
        $r = Rt-Own $name $n; CheckExit 'the runtime worker owns the item' $r 0
        $before = @((Get-FakeState).scopedMints).Count
        $g = Fo $name @('git-credential', '--item', $n)
        CheckExit 'git-credential answers' $g 0
        Check 'with a token, its expiry and the repository' ("$($g.token)" -ne '' -and "$($g.expires_at)" -ne '' -and $g.repository -eq 'clearmeasure-aisf-sample-apps/cmdemo1-workorders' -and $g.username -eq 'x-access-token') ($g | ConvertTo-Json -Compress)
        $mints = @((Get-FakeState).scopedMints)
        $req = $(if ($mints.Count -gt $before) { $mints[-1] | ConvertFrom-Json -AsHashtable } else { @{} })
        Check 'GitHub was asked for this repository only, contents write only' ($mints.Count -eq $before + 1 -and @($req.repositories).Count -eq 1 -and $req.repositories[0] -eq 'cmdemo1-workorders' -and @($req.permissions.Keys).Count -eq 1 -and $req.permissions.contents -eq 'write') ($req | ConvertTo-Json -Compress)
        Check 'the token is not written to the gateway log' (@(Get-ChildItem $script:Work -Filter 'gateway*.out' | Select-String -SimpleMatch "$($g.token)").Count -eq 0)
        $m = New-Item-Fake 'Local git credential' 'x' ($OwnerLabels + 'worker:rtlocal4')
        $r = Rt-Own 'rtlocal4' $m; CheckExit 'the local worker owns its item' $r 0
        $l = Fo 'rtlocal4' @('git-credential', '--item', $m)
        Check 'the owner token of a local worker is refused' ($l.exit -eq 30 -and $l.code -eq 'forbidden') ($l | ConvertTo-Json -Compress)
        $o = Fo $name @('git-credential', '--item', $m)
        Check 'a runtime owner cannot ask for another item' ($o.exit -ne 0) ($o | ConvertTo-Json -Compress)
        $wk = Rt-Api POST '/api/v1/verbs/git-credential' @{ item = $n } $w2
        Check 'the w2 itself (no item token) is refused' ($wk.status -eq 403)
        $r = Fo $name @('release', '--item', $n, '--reason', 'scenario: the item leaves the runtime worker'); CheckExit 'the runtime worker releases its item' $r 0
        $rel = Fo $name @('git-credential', '--item', $n)
        Check 'after the release its item token gets no git credential' ($rel.exit -eq 10 -and $rel.code -eq 'not-owner') ($rel | ConvertTo-Json -Compress)
        $k = New-Item-Fake 'Offered, not accepted' 'x' ($OwnerLabels + "worker:$name")
        Check 'a second item is offered to the runtime worker' (Wait-Until -Seconds 40 { (Get-Assigned $name $k).Count -gt 0 })
        $na = Fo $name @('git-credential', '--item', $k)
        Check 'the item token of an offer not accepted is refused' ($na.exit -eq 10 -and $na.code -eq 'not-owner') ($na | ConvertTo-Json -Compress)
        Check 'no further token was minted for the refused calls' (@((Get-FakeState).scopedMints).Count -eq $before + 1)
    } finally { Stop-OwnerClient $name; Stop-OwnerClient 'rtlocal4' }
}
}
