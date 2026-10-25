# Tier 1 scenarios of the Octopus runbook runtime (docs/design/octopus-runbook-runtime.md, gateway/Runtimes/OctopusRunbookRuntime.cs) against the
# fake Octopus of tests/FakeGitHub: host selection, no capacity, the overflow rule, the stop, the restart rebuild and the reconcile endpoints.
# Suite 'octopus' (owner mode; the runbook key and the host token are composed at run time in run.ps1). Uses the helpers of owner.ps1 and runtime.ps1.

$OctLabels = @('factory worker', 'Auto Merge', 'runtime:octopus')
function Oct-State { Fake 'octo' }
function Oct-Runs([string]$Runbook, [int]$Item = 0) { , @((Oct-State).runs | Where-Object { $_.runbook -eq $Runbook -and ($Item -eq 0 -or "$($_.values.Item)" -eq "$Item") }) }
function Oct-Status { (Get-Plant).runtimes.octopus }
function Oct-Host([string]$Method, [string]$Path, $Body = $null, [string]$Token = $env:FACTORY_OCTOPUS_HOST_TOKEN) {
    $h = @{}; if ($Token) { $h.Authorization = "Bearer $Token" }
    $p = @{ Uri = "http://127.0.0.1:$($script:ApiPort)$Path"; Method = $Method; Headers = $h; SkipHttpErrorCheck = $true; StatusCodeVariable = 'sc' }
    if ($null -ne $Body) { $p.Body = ($Body | ConvertTo-Json -Depth 6 -Compress); $p.ContentType = 'application/json' }
    $r = Invoke-RestMethod @p
    return @{ status = [int]$sc; body = $r }
}
function Oct-Report([string]$HostName, [object[]]$Containers) { Oct-Host POST '/api/v1/runtime/octopus/report' @{ host = $HostName; containers = @($Containers) } }
# every host reports (no container) once, as Reconcile hosts does: the gateway starts nothing on a host it has not heard from since its start
function Oct-Up { foreach ($h in 'host-a', 'host-b', 'host-c') { [void](Oct-Report $h @()) } }
function Oct-Machines([object[]]$Machines) { [void](Fake 'octo-machines' @{ machines = @($Machines) }) }
$OctDefaultMachines = @(@{ id = 'Machines-1'; name = 'host-a' }, @{ id = 'Machines-2'; name = 'host-b' }, @{ id = 'Machines-3'; name = 'host-sick'; health = 'Unhealthy' }, @{ id = 'Machines-4'; name = 'host-uat'; env = 'Environments-12' }, @{ id = 'Machines-5'; name = 'web-1'; roles = @('web') })
# The scenario's items are closed, both hosts report no container (every detached worker ends, gone), held tasks end, the runtime is healthy again.
function Oct-Cleanup([int[]]$Items, [string[]]$Clients = @()) {
    foreach ($n in $Items) { if ($n) { Rt-Close $n } }
    foreach ($c in $Clients) { if ($c -and $script:Workers.ContainsKey($c)) { Stop-OwnerClient $c } }
    Oct-Machines $OctDefaultMachines
    foreach ($r in @((Oct-State).runs | Where-Object { $_.state -eq 'Executing' })) { [void](Fake 'octo-task' @{ id = $r.task; state = 'Success' }) }
    [void](Wait-Until -Seconds 30 { [void](Oct-Report 'host-a' @()); [void](Oct-Report 'host-b' @()); (Oct-Status).running -eq 0 })
    [void](Invoke-RestMethod "http://127.0.0.1:$($script:ApiPort)/api/v1/runtimes/octopus/reset" -Method POST -Headers @{ Authorization = 'Bearer test-token' })
    # every execution has ended: the next scenario (and a restart's rebuild) starts from an empty project history
    [void](Fake 'octo-reset')
}

# OIDC mode: the gateway restarted without its API key and with auth oidc, serviceAccountId Users-903; the fake takes Bearer tokens only.
function Oct-UseOidc([int]$ExpiresIn = 3600) {
    $cfgPath = Join-Path $script:Work 'instance.test.json'; $orig = Get-Content -Raw $cfgPath
    $cfg = $orig | ConvertFrom-Json -AsHashtable; $cfg.runtimes.octopus.auth = 'oidc'; $cfg.runtimes.octopus.serviceAccountId = 'Users-903'; $cfg | ConvertTo-Json -Depth 12 | Set-Content $cfgPath
    [void](Fake 'octo-oidc' @{ require = $true; serviceAccountId = 'Users-903'; expiresIn = $ExpiresIn })
    $key = $env:OCTOPUS_RUNBOOK_API_KEY; Remove-Item Env:OCTOPUS_RUNBOOK_API_KEY -ErrorAction SilentlyContinue
    Restart-Gateway
    Oct-Machines @(@{ id = 'Machines-1'; name = 'host-a' }, @{ id = 'Machines-2'; name = 'host-b' }, @{ id = 'Machines-6'; name = 'host-c' })   # a free host for each start
    Oct-Up
    return @{ path = $cfgPath; orig = $orig; key = $key }
}
function Oct-UseKey($Saved) {
    [void](Fake 'octo-oidc' @{ require = $false })
    $env:OCTOPUS_RUNBOOK_API_KEY = $Saved.key
    Set-Content -Path $Saved.path -Value $Saved.orig -NoNewline; Restart-Gateway; Oct-Up
}

$OctopusScenarios = [ordered]@{

'octopus: auth apiKey: without a configured auth and with OCTOPUS_RUNBOOK_API_KEY set the key is sent, no token exchange is made' = {
    Oct-Up   # every host has reported, as after a reconcile: the gateway starts nothing on a host it has not heard from
    [void](Fake 'octo-oidc' @{ require = $false }); Oct-Up
    $st = Oct-Status
    Check 'status: auth apiKey' ($st.auth -eq 'apiKey') ($st | ConvertTo-Json -Compress -Depth 4)
    $x = Rt-Api POST '/api/v1/_test/octopus-provision' @{ item = 9101; phase = 'Technical Design'; role = '' }
    Check 'a Start worker run is made with the key' ((Oct-Runs 'Start worker' 9101).Count -eq 1) ($x | ConvertTo-Json -Compress)
    Check 'no exchange, no refusal' ((Oct-State).exchanges -eq 0 -and (Oct-State).unauthorized -eq 0)
    Oct-Cleanup @()
}

'octopus: auth oidc: the Entra token is exchanged at /token/v1 for the service account; the access token is cached; a 401 renews it once' = {
    Oct-Up   # every host has reported, as after a reconcile: the gateway starts nothing on a host it has not heard from
    $saved = $null
    try {
        $saved = Oct-UseOidc
        $st = Oct-Status
        Check 'status: auth oidc, the service account, no key needed' ($st.enabled -eq $true -and $st.auth -eq 'oidc' -and $st.serviceAccountId -eq 'Users-903') ($st | ConvertTo-Json -Compress -Depth 4)
        [void](Rt-Api POST '/api/v1/_test/octopus-provision' @{ item = 9111; phase = 'Technical Design'; role = '' })
        Check 'a Start worker run is made with a Bearer access token' ((Oct-Runs 'Start worker' 9111).Count -eq 1) ((Oct-State) | ConvertTo-Json -Compress -Depth 3)
        Check 'one exchange, none refused, no 401' ((Oct-State).exchanges -eq 1 -and (Oct-State).exchangeRefusals -eq 0 -and (Oct-State).unauthorized -eq 0) ((Oct-State) | ConvertTo-Json -Compress -Depth 2)
        [void](Rt-Api POST '/api/v1/_test/octopus-provision' @{ item = 9112; phase = 'Technical Design'; role = '' })
        Start-Sleep -Seconds 3
        Check 'the access token is cached: still one exchange after more calls' ((Oct-Runs 'Start worker' 9112).Count -eq 1 -and (Oct-State).exchanges -eq 1)
        [void](Fake 'octo-oidc-revoke')
        [void](Rt-Api POST '/api/v1/_test/octopus-provision' @{ item = 9113; phase = 'Technical Design'; role = '' })
        Check 'a 401 renews the token once and the call succeeds' ((Oct-Runs 'Start worker' 9113).Count -eq 1 -and (Oct-State).exchanges -eq 2 -and (Oct-State).unauthorized -ge 1) ((Oct-State) | ConvertTo-Json -Compress -Depth 2)
        Check 'neither the Entra token nor an access token is in the gateway log' (@(Get-ChildItem $script:Work -Filter 'gateway*.out' | Select-String -SimpleMatch "$env:FACTORY_TEST_OCTOPUS_ENTRA_TOKEN", 'oat-').Count -eq 0)
        Check 'status: no token in the runtime block' (-not (((Oct-Status) | ConvertTo-Json -Compress -Depth 8) -match 'oat-|eyJ'))
    } finally { Oct-Cleanup @(); if ($saved) { Oct-UseKey $saved } }
}

'octopus: auth oidc: a short-lived access token is exchanged again before it expires' = {
    Oct-Up   # every host has reported, as after a reconcile: the gateway starts nothing on a host it has not heard from
    $saved = $null
    try {
        $saved = Oct-UseOidc -ExpiresIn 2
        [void](Rt-Api POST '/api/v1/_test/octopus-provision' @{ item = 9121; phase = 'Technical Design'; role = '' })
        Start-Sleep -Seconds 2
        [void](Rt-Api POST '/api/v1/_test/octopus-provision' @{ item = 9122; phase = 'Technical Design'; role = '' })
        Check 'both runs made, a new exchange for the second, no 401' ((Oct-Runs 'Start worker' 9122).Count -eq 1 -and (Oct-State).exchanges -ge 2 -and (Oct-State).unauthorized -eq 0) ((Oct-State) | ConvertTo-Json -Compress -Depth 2)
    } finally { Oct-Cleanup @(); if ($saved) { Oct-UseKey $saved } }
}

'octopus: provision picks a healthy fworker-host with capacity and runs Start worker there with the prompted variables; the worker redeems and is offered; no capacity left refuses and the item waits' = {
    Oct-Up   # every host has reported, as after a reconcile: the gateway starts nothing on a host it has not heard from
    $a = 0; $b = 0; $c = 0; $na = ''
    Oct-Up
    try {
        $a = New-Item-Fake 'Octopus host a' 'x' $OctLabels
        Check 'a Start worker run is made for the item' (Wait-Until -Seconds 30 { (Oct-Runs 'Start worker' $a).Count -eq 1 })
        $ra = (Oct-Runs 'Start worker' $a)[0]; $na = "$($ra.values.Name)"
        Check 'its worker name is oct-<item>-<6 hex>' ($na -match "^oct-$a-[0-9a-f]{6}$") $na
        Check 'on one machine: the first healthy host by name (host-a), never the unhealthy, other-environment or untagged ones' (@($ra.machines).Count -eq 1 -and $ra.machines[0] -eq 'Machines-1') ($ra | ConvertTo-Json -Compress -Depth 5)
        Check 'the prompted variables: item, phase, agent, mode detached, model profile and a nonce' ("$($ra.values.Item)" -eq "$a" -and $ra.values.Phase -eq 'Technical Design' -and $ra.values.Agent -eq 'codex' -and $ra.values.Mode -eq 'detached' -and "$($ra.values.Nonce)" -like 'n1.*' -and $null -ne $ra.values.ModelProfile) ($ra.values | ConvertTo-Json -Compress)
        Check 'neither the nonce nor the runbook key is in the gateway log' (@(Get-ChildItem $script:Work -Filter 'gateway*.out' | Select-String -SimpleMatch "$($ra.values.Nonce)", "$env:OCTOPUS_RUNBOOK_API_KEY").Count -eq 0)
        $st = Oct-Status
        Check 'status: the execution is the server task, with its machine' ($st.enabled -eq $true -and @($st.executions | Where-Object { $_.worker -eq $na -and $_.execution -eq $ra.task -and $_.detail.machine -eq 'Machines-1' }).Count -eq 1) ($st | ConvertTo-Json -Compress -Depth 6)
        Check 'status: no credential in the runtime block' (-not (($st | ConvertTo-Json -Compress -Depth 8) -match 'n1\.|API-'))
        $r = Rt-Redeem "$($ra.values.Nonce)"
        Check 'the container redeems the nonce for its w2' ($r.status -eq 200 -and $r.body.name -eq $na -and $r.body.runtime -eq 'octopus') ($r | ConvertTo-Json -Compress)
        Start-RuntimeClient $na "$($r.body.token)"
        Check 'the worker is offered the item' (Wait-Until -Seconds 20 { (Get-Assigned $na $a).Count -gt 0 })
        # every comment the worker's verbs write ends with the link to its session: the gateway's page of the transcript; the gateway's own lines do not
        [void](Rt-Api GET '/api/v1/status')   # the gateway learns its own address from the operator's call
        $own = Rt-Own $na $a; CheckExit 'the runtime worker owns the item' $own 0
        $cr = Fo $na @('comment', '--item', $a, '--body', 'octopus worker note'); CheckExit 'the worker comments' $cr 0
        $foot = "AI session: [$na]($(Get-GatewayBase)/sessions/$na)"
        $wc = @((Get-FakeIssue $a).comments | Where-Object { "$($_.body)" -match 'octopus worker note' })
        Check 'the worker''s comment ends with one AI session footer that links the gateway''s session page' ($wc.Count -eq 1 -and "$($wc[0].body)" -match ("octopus worker note`n`n" + [regex]::Escape($foot) + "`n`n<!-- factory-journal:") -and ([regex]::Matches("$($wc[0].body)", 'AI session: ')).Count -eq 1) "$($wc | ForEach-Object { $_.body })"
        $oc = Rt-Api POST '/api/v1/verbs/comment' @{ item = $a; body = 'operator note' }
        $oc2 = @((Get-FakeIssue $a).comments | Where-Object { "$($_.body)" -match 'operator note' })
        Check 'a comment of the operator token carries no AI session footer' ($oc.status -eq 200 -and $oc2.Count -eq 1 -and "$($oc2[0].body)" -notmatch 'AI session: ') ($oc | ConvertTo-Json -Compress)
        Check 'the plant manager''s own lines carry no AI session footer' (@((Get-FakeIssue $a).comments | Where-Object { "$($_.body)" -match 'plant event=' -and "$($_.body)" -match 'AI session: ' }).Count -eq 0)
        [void](Oct-Report 'host-a' @(@{ name = "fw-$na"; item = $a; state = 'running'; startedAt = (Get-Date).ToUniversalTime().ToString('o') }))
        $b = New-Item-Fake 'Octopus host b' 'x' $OctLabels
        Check 'the second item goes to the other host (host-a is full at perHostCapacity 1)' (Wait-Until -Seconds 30 { $x = Oct-Runs 'Start worker' $b; $x.Count -eq 1 -and $x[0].machines[0] -eq 'Machines-2' }) ((Oct-State).runs | ConvertTo-Json -Compress -Depth 5)
        $c = New-Item-Fake 'Octopus no capacity' 'x' $OctLabels
        Check 'the third item waits: no host has capacity and its role may not overflow' (Wait-Until -Seconds 40 { @((Get-FakeIssue $c).comments | Where-Object { $_.body -match 'plant event=waiting ' -and $_.body -match 'runtime-no-capacity' }).Count -ge 1 })
        Check 'no run was made for it, not even on the overflow pool' ((Oct-Runs 'Start worker' $c).Count -eq 0 -and (Oct-Runs 'Start worker (overflow)').Count -eq 0) ((Oct-State).runs | ConvertTo-Json -Compress -Depth 5)
        Check 'a refusal for capacity is no failure: the runtime stays healthy and the item has no failure' ((Oct-Status).healthy -eq $true -and @((Oct-Status).failures | Where-Object { $_.item -eq $c }).Count -eq 0) (Oct-Status | ConvertTo-Json -Compress -Depth 5)
    } finally { Oct-Cleanup @($a, $b, $c) @($na) }
}

'octopus: the reconcile endpoints take the host token only; desired names the host''s workers; an exited container ends its execution and the item is planned again, a container gone from the host is Stopped' = {
    Oct-Up   # every host has reported, as after a reconcile: the gateway starts nothing on a host it has not heard from
    $n = 0; $name = ''
    try {
        Check 'desired with a wrong host token answers 401' ((Oct-Host GET '/api/v1/runtime/octopus/desired?host=host-a' $null ('h' * 40)).status -eq 401)
        Check 'report with a wrong host token answers 401' ((Oct-Host POST '/api/v1/runtime/octopus/report' @{ host = 'host-a'; containers = @() } ('h' * 40)).status -eq 401)
        Check 'report with the host token answers 200' ((Oct-Report 'host-a' @()).status -eq 200)
        Check 'the operator token is not a host token' ((Oct-Host GET '/api/v1/runtime/octopus/desired?host=host-a' $null 'test-token').status -eq 401)
        Check 'no token answers 401' ((Oct-Host GET '/api/v1/runtime/octopus/desired?host=host-a' $null '').status -eq 401)
        $n = New-Item-Fake 'Octopus reconcile' 'x' $OctLabels
        Check 'a run is made' (Wait-Until -Seconds 60 { (Oct-Runs 'Start worker' $n).Count -eq 1 })
        $run = (Oct-Runs 'Start worker' $n)[0]; $name = "$($run.values.Name)"
        $d = Oct-Host GET '/api/v1/runtime/octopus/desired?host=host-a'
        Check 'desired for host-a names the worker' ($d.status -eq 200 -and @($d.body.workers) -contains $name) ($d | ConvertTo-Json -Compress -Depth 4)
        Check 'desired by machine id alike' (@((Oct-Host GET '/api/v1/runtime/octopus/desired?host=Machines-1').body.workers) -contains $name)
        Check 'desired for host-b does not' (-not (@((Oct-Host GET '/api/v1/runtime/octopus/desired?host=host-b').body.workers) -contains $name))
        $rep = Oct-Report 'host-a' @(@{ name = "fw-$name"; item = $n; state = 'running' }, @{ name = 'fw-oct-999-abcdef'; item = 999; state = 'running' })
        # a stray: no record, no startedAt, and host-a has reported since the start (Oct-Up), so it is past the adoption window: stopped, not adopted
        Check 'a report answers the containers that should not run' ($rep.status -eq 200 -and @($rep.body.stop) -contains 'oct-999-abcdef' -and -not (@($rep.body.stop) -contains $name)) ($rep | ConvertTo-Json -Compress -Depth 4)
        Start-Sleep -Seconds 3
        Check 'a running container keeps its execution running' (@((Oct-Status).executions | Where-Object { $_.worker -eq $name -and $_.status -eq 'Running' }).Count -eq 1) (Oct-Status | ConvertTo-Json -Compress -Depth 5)
        [void](Oct-Report 'host-a' @(@{ name = "fw-$name"; item = $n; state = 'exited' }))
        Check 'an exited container ends the execution' (Wait-Until -Seconds 20 { @((Oct-Status).last | Where-Object { $_.worker -eq $name }).Count -eq 1 }) ((Oct-Status).last | ConvertTo-Json -Compress -Depth 4)
        Check 'and the plant plans the item again: a new Start worker run' (Wait-Until -Seconds 30 { @((Oct-Runs 'Start worker' $n) | Where-Object { $_.values.Name -ne $name }).Count -ge 1 })
        $name2 = "$((Oct-Runs 'Start worker' $n)[-1].values.Name)"
        $host2 = "$((Oct-Runs 'Start worker' $n)[-1].machines[0])"
        [void](Oct-Report $host2 @(@{ name = "fw-$name2"; item = $n; state = 'running' }))
        Start-Sleep -Seconds 3
        [void](Oct-Report $host2 @())
        Check 'a container gone from its host is Stopped' (Wait-Until -Seconds 20 { [void](Oct-Report $host2 @()); @((Oct-Status).last | Where-Object { $_.worker -eq $name2 -and $_.outcome -eq 'stopped' }).Count -eq 1 }) ((Oct-Status).last | ConvertTo-Json -Compress -Depth 4)
    } finally { Oct-Cleanup @($n) }
}

'octopus: the stop of a worker runs Stop worker on the machine it was started on; desired no longer names it' = {
    Oct-Up   # every host has reported, as after a reconcile: the gateway starts nothing on a host it has not heard from
    $n = 0; $name = ''
    try {
        $n = New-Item-Fake 'Octopus stop' 'x' $OctLabels
        Check 'a run is made' (Wait-Until -Seconds 60 { (Oct-Runs 'Start worker' $n).Count -eq 1 })
        $run = (Oct-Runs 'Start worker' $n)[0]; $name = "$($run.values.Name)"
        [void](Oct-Report "$($run.machines[0])" @(@{ name = "fw-$name"; item = $n; state = 'running' }))
        $rv = Invoke-RestMethod "http://127.0.0.1:$($script:ApiPort)/api/v1/workers/$name/revoke" -Method POST -Headers @{ Authorization = 'Bearer test-token' } -SkipHttpErrorCheck
        Check 'the operator revokes the worker' ($rv.ok -eq $true) ($rv | ConvertTo-Json -Compress)
        Check 'a Stop worker run is made on the start machine, for that worker' (Wait-Until -Seconds 20 { @((Oct-State).runs | Where-Object { $_.runbook -eq 'Stop worker' -and $_.values.Name -eq $name -and $_.machines[0] -eq $run.machines[0] }).Count -eq 1 }) ((Oct-State).runs | ConvertTo-Json -Compress -Depth 5)
        Check 'with no nonce' ("$((@((Oct-State).runs | Where-Object { $_.runbook -eq 'Stop worker' -and $_.values.Name -eq $name }))[0].values.Nonce)" -eq '')
        Check 'desired no longer names it' (-not (@((Oct-Host GET "/api/v1/runtime/octopus/desired?host=$($run.machines[0])").body.workers) -contains $name))
        [void](Oct-Report "$($run.machines[0])" @())
        Check 'the stopped container ends the execution as stopped by the revoke' (Wait-Until -Seconds 20 { [void](Oct-Report "$($run.machines[0])" @()); @((Oct-Status).last | Where-Object { $_.worker -eq $name -and $_.outcome -eq 'stopped:revoked' }).Count -eq 1 }) ((Oct-Status).last | ConvertTo-Json -Compress -Depth 4)
        Check 'one Stop worker run only' (@((Oct-State).runs | Where-Object { $_.runbook -eq 'Stop worker' -and $_.values.Name -eq $name }).Count -eq 1)
    } finally { Oct-Cleanup @($n) }
}

# The plant starts own-item workers only (no role): a short role is started through the test seam, as a manager start would pass it.
'octopus: overflow runs a held Start worker (overflow) only for an explicit allowed role, capped at overflowMax; an own-item worker never overflows; its stop cancels the task' = {
    Oct-Up   # every host has reported, as after a reconcile: the gateway starts nothing on a host it has not heard from
    $nx = ''
    try {
        Oct-Machines @(@{ id = 'Machines-1'; name = 'host-a'; health = 'Unavailable' }, @{ id = 'Machines-2'; name = 'host-b'; disabled = $true })
        $own = Rt-Api POST '/api/v1/_test/octopus-provision' @{ item = 9001; phase = 'Technical Design'; role = '' }
        Check 'an own-item worker (no role) does not overflow' ($own.status -eq 200 -and (Oct-Runs 'Start worker (overflow)').Count -eq 0 -and "$($own.body.execution)" -eq '') ($own | ConvertTo-Json -Compress)
        $no = Rt-Api POST '/api/v1/_test/octopus-provision' @{ item = 9002; phase = 'Technical Design'; role = 'supervisor' }
        Check 'a role not in overflowRoles does not overflow' ((Oct-Runs 'Start worker (overflow)').Count -eq 0)
        $x = Rt-Api POST '/api/v1/_test/octopus-provision' @{ item = 9003; phase = 'Technical Design'; role = 'qc' }
        $nx = "$($x.body.name)"
        Check 'an allowed role (qc) runs Start worker (overflow)' ((Oct-Runs 'Start worker (overflow)' 9003).Count -eq 1) ((Oct-State).runs | ConvertTo-Json -Compress -Depth 5)
        $rx = (Oct-Runs 'Start worker (overflow)' 9003)[0]
        Check 'held, on no specific machine, the execution is its task' ($rx.values.Mode -eq 'held' -and @($rx.machines).Count -eq 0 -and $rx.state -eq 'Executing' -and $x.body.execution -eq $rx.task) ($rx | ConvertTo-Json -Compress -Depth 5)
        Check 'status: the overflow slot is in use' ((Oct-Status).overflowInUse -eq 1)
        [void](Rt-Api POST '/api/v1/_test/octopus-provision' @{ item = 9004; phase = 'Technical Design'; role = 'qc' })
        Check 'a second one is refused: overflowMax is 1' ((Oct-Runs 'Start worker (overflow)' 9004).Count -eq 0)
        $cancels = (Oct-State).cancels
        [void](Invoke-RestMethod "http://127.0.0.1:$($script:ApiPort)/api/v1/workers/$nx/revoke" -Method POST -Headers @{ Authorization = 'Bearer test-token' })
        Check 'the stop of a held worker cancels its server task' (Wait-Until -Seconds 20 { (Oct-State).cancels -eq $cancels + 1 -and @((Oct-State).runs | Where-Object { $_.task -eq $rx.task -and $_.state -eq 'Canceled' }).Count -eq 1 })
        Check 'no Stop worker run for a held worker' (@((Oct-State).runs | Where-Object { $_.runbook -eq 'Stop worker' -and $_.values.Name -eq $nx }).Count -eq 0)
        Check 'the slot is free again' (Wait-Until -Seconds 20 { (Oct-Status).overflowInUse -eq 0 })
        Start-Sleep -Seconds 16   # past the no-capacity pause of the earlier refusals
        [void](Rt-Api POST '/api/v1/_test/octopus-provision' @{ item = 9005; phase = 'Technical Design'; role = 'qc' })
        Check 'and the next allowed role gets it' ((Oct-Runs 'Start worker (overflow)' 9005).Count -eq 1)
    } finally { Oct-Cleanup @() }
}

'octopus: a coding role never overflows, whatever overflowRoles says' = {
    Oct-Up   # every host has reported, as after a reconcile: the gateway starts nothing on a host it has not heard from
    $cfgPath = Join-Path $script:Work 'instance.test.json'; $orig = Get-Content -Raw $cfgPath
    try {
        $cfg = $orig | ConvertFrom-Json -AsHashtable; $cfg.runtimes.octopus.overflowRoles = @('development', 'worker', 'qc'); $cfg | ConvertTo-Json -Depth 12 | Set-Content $cfgPath
        Restart-Gateway; Oct-Up
        Oct-Machines @(@{ id = 'Machines-1'; name = 'host-a'; health = 'Unhealthy' })
        [void](Rt-Api POST '/api/v1/_test/octopus-provision' @{ item = 9011; phase = 'Development'; role = 'development' })
        [void](Rt-Api POST '/api/v1/_test/octopus-provision' @{ item = 9012; phase = 'Development'; role = 'worker' })
        Check 'development and worker never run on the overflow pool' ((Oct-Runs 'Start worker (overflow)').Count -eq 0)
    } finally {
        Oct-Cleanup @()
        Set-Content -Path $cfgPath -Value $orig -NoNewline; Restart-Gateway; Oct-Up
    }
}

'octopus: desired and report answer 404 for a host that is not a target' = {
    Oct-Up   # every host has reported, as after a reconcile: the gateway starts nothing on a host it has not heard from
    Check 'desired for an unknown host' ((Oct-Host GET '/api/v1/runtime/octopus/desired?host=nobody').status -eq 404)
    Check 'report for an unknown host' ((Oct-Report 'nobody' @()).status -eq 404)
    Check 'report for a target of another tag' ((Oct-Report 'web-1' @()).status -eq 404)
}

'octopus: a gateway restart rebuilds the running workers from the project''s tasks; no second run for their items; desired still names them' = {
    Oct-Up   # every host has reported, as after a reconcile: the gateway starts nothing on a host it has not heard from
    $n = 0
    try {
        [void](Fake 'octo-v1-empty-form' @{ empty = $false })   # the legacy path: runs that keep their FormValues (Octopus keeps {} for a v1 run)
        $n = New-Item-Fake 'Octopus restart' 'x' $OctLabels
        Check 'a run is made' (Wait-Until -Seconds 60 { (Oct-Runs 'Start worker' $n).Count -eq 1 })
        $run = (Oct-Runs 'Start worker' $n)[0]; $name = "$($run.values.Name)"
        Restart-Gateway
        Check 'the new gateway adopts the worker with its task' (Wait-Until -Seconds 20 { $s = Oct-Status; $s.relisted -and @($s.executions | Where-Object { $_.worker -eq $name -and $_.item -eq $n -and $_.execution -eq $run.task }).Count -eq 1 }) (Oct-Status | ConvertTo-Json -Compress -Depth 5)
        $d0 = Oct-Host GET "/api/v1/runtime/octopus/desired?host=$($run.machines[0])"
        Check 'before the host reports after the restart, desired has no workers list (the reconcile removes nothing)' ($d0.status -eq 200 -and "$($d0.body.waitingFor)" -eq 'report' -and -not $d0.body.PSObject.Properties['workers']) ($d0.body | ConvertTo-Json -Compress)
        [void](Oct-Report "$($run.machines[0])" @(@{ name = "fw-$name"; item = $n; state = 'running' }))
        Check 'after its report, desired still names it on its host' (@((Oct-Host GET "/api/v1/runtime/octopus/desired?host=$($run.machines[0])").body.workers) -contains $name)
        Start-Sleep -Seconds 5
        Check 'no second run for the item' ((Oct-Runs 'Start worker' $n).Count -eq 1)
        $rv = Invoke-RestMethod "http://127.0.0.1:$($script:ApiPort)/api/v1/workers/$name/revoke" -Method POST -Headers @{ Authorization = 'Bearer test-token' } -SkipHttpErrorCheck
        Check 'the adopted worker is stopped on its start machine' (Wait-Until -Seconds 20 { @((Oct-State).runs | Where-Object { $_.runbook -eq 'Stop worker' -and $_.values.Name -eq $name -and $_.machines[0] -eq $run.machines[0] }).Count -eq 1 })
        Restart-Gateway
        Start-Sleep -Seconds 3
        Check 'a worker whose Stop run is newer than its Start is not adopted' (@((Oct-Status).executions | Where-Object { $_.worker -eq $name }).Count -eq 0) (Oct-Status | ConvertTo-Json -Compress -Depth 5)
    } finally { Oct-Cleanup @($n) }
}

# Runs started through runbook-runs/create/v1 keep FormValues {} (the fake's default, as Octopus): the rebuild reads nothing back, the host
# reports are the source of truth. perHostCapacity is 1, but every running container is adopted (capacity limits new starts only): two on host-a.
'octopus: after a restart the running containers the hosts report are all adopted, also beyond perHostCapacity; desired names them, the reconcile keeps them, List shows them' = {
    Oct-Up   # every host has reported, as after a reconcile: the gateway starts nothing on a host it has not heard from
    try {
        $t0 = (Get-Date).ToUniversalTime()
        $a1 = 'oct-9201-aaaaaa'; $b1 = 'oct-9202-bbbbbb'; $a2 = 'oct-9203-cccccc'
        Restart-Gateway
        Check 'nothing is known before the reports' (@((Oct-Status).executions | Where-Object { $_.worker -in @($a1, $b1, $a2) }).Count -eq 0) (Oct-Status | ConvertTo-Json -Compress -Depth 5)
        $ra = Oct-Report 'host-a' @(@{ name = "fw-$a1"; item = 9201; state = 'running'; startedAt = $t0.AddMinutes(-10).ToString('o') }, @{ name = "fw-$a2"; item = 9203; state = 'running'; startedAt = $t0.AddMinutes(-5).ToString('o') })
        $rb = Oct-Report 'host-b' @(@{ name = "fw-$b1"; item = 9202; state = 'running'; startedAt = $t0.AddMinutes(-8).ToString('o') })
        Check 'the reconcile does not stop the adopted containers' ($ra.status -eq 200 -and $rb.status -eq 200 -and -not (@($ra.body.stop) -contains $a1) -and @($rb.body.stop).Count -eq 0) (@($ra, $rb) | ConvertTo-Json -Compress -Depth 4)
        Check 'the second container on host-a is adopted too: beyond perHostCapacity, the reconcile still keeps it' (-not (@($ra.body.stop) -contains $a2)) ($ra | ConvertTo-Json -Compress -Depth 4)
        $da = Oct-Host GET '/api/v1/runtime/octopus/desired?host=host-a'; $db = Oct-Host GET '/api/v1/runtime/octopus/desired?host=host-b'
        Check 'desired names each adopted worker on its host, both on host-a' (@($da.body.workers) -contains $a1 -and @($da.body.workers) -contains $a2 -and @($db.body.workers) -contains $b1) (@($da, $db) | ConvertTo-Json -Compress -Depth 4)
        $ra2 = Oct-Report 'host-a' @(@{ name = "fw-$a1"; item = 9201; state = 'running' }, @{ name = "fw-$a2"; item = 9203; state = 'running' })
        Check 'a second report adopts nothing twice and stops neither' (@($ra2.body.stop).Count -eq 0) ($ra2 | ConvertTo-Json -Compress -Depth 4)
        Check 'List reports the adopted workers: the runtime manager has their executions, running, with their items' (Wait-Until -Seconds 15 { $s = Oct-Status; @($s.executions | Where-Object { ($_.worker -eq $a1 -and $_.item -eq 9201) -or ($_.worker -eq $a2 -and $_.item -eq 9203) -or ($_.worker -eq $b1 -and $_.item -eq 9202) }).Count -eq 3 }) (Oct-Status | ConvertTo-Json -Compress -Depth 5)
        Check 'no start run was made for them' ((Oct-State).runs.Count -eq 0) ((Oct-State).runs | ConvertTo-Json -Compress -Depth 5)
    } finally { Oct-Cleanup @() }
}

'octopus: session transcript: the container posts its lines with its w2, the start comment links the gateway''s session page and the Octopus task, the page needs the operator token, the transcript survives a gateway restart' = {
    $n = 0; $na = ''
    try {
        [void](Rt-Api GET '/api/v1/status')   # the gateway learns its own address from the operator's call (no FACTORY_PUBLIC_URL in the suite)
        $n = New-Item-Fake 'Octopus transcript' 'x' $OctLabels
        Check 'a Start worker run is made for the item' (Wait-Until -Seconds 30 { (Oct-Runs 'Start worker' $n).Count -eq 1 })
        $ra = (Oct-Runs 'Start worker' $n)[0]; $na = "$($ra.values.Name)"
        $page = "$(Get-GatewayBase)/sessions/$na"
        Check 'the launched comment carries [session](<gateway>/sessions/<name>) and the Octopus task link' (Wait-Until -Seconds 30 { @((Get-FakeIssue $n).comments | Where-Object { $_.body -match 'plant event=launched ' -and $_.body.Contains("[session]($page)") -and $_.body -match '\(\[Octopus task\]\(http[^)]+/tasks/' }).Count -eq 1 }) (@((Get-FakeIssue $n).comments | ForEach-Object { $_.body }) -join ' | ')
        $r = Rt-Redeem "$($ra.values.Nonce)"; $w2 = "$($r.body.token)"
        Check 'the container redeems its nonce' ($r.status -eq 200 -and $w2 -like 'w2.*')
        $x = Rt-Api POST '/api/v1/runtime/transcript' @{ lines = @('[system init] session starts', 'the token is w2.***', 'line three') } $w2
        Check 'a batch posted with the w2 is taken' ($x.status -eq 200 -and $x.body.taken -eq 3 -and $x.body.total -eq 3) ($x | ConvertTo-Json -Compress)
        Check 'no credential posts nothing' ((Rt-Api POST '/api/v1/runtime/transcript' @{ lines = @('x') } '').status -eq 401)
        Check 'the operator token is no session credential' ((Rt-Api POST '/api/v1/runtime/transcript' @{ lines = @('x') }).status -eq 403)
        Check 'more than 500 lines a batch is refused' ((Rt-Api POST '/api/v1/runtime/transcript' @{ lines = @(1..501 | ForEach-Object { "l$_" }) } $w2).status -eq 413)
        $s = Rt-Api GET "/api/v1/sessions/$na"
        Check 'GET /api/v1/sessions/<name> with the operator token: the lines, the item, live' ($s.status -eq 200 -and @($s.body.lines).Count -eq 3 -and $s.body.lines[2] -eq 'line three' -and $s.body.item -eq $n -and $s.body.live -eq $true -and $s.body.runtime -eq 'octopus') ($s | ConvertTo-Json -Compress -Depth 4)
        Check 'after=2 answers the lines from number 2 on' (@((Rt-Api GET "/api/v1/sessions/$($na)?after=2").body.lines).Count -eq 1)
        Check 'the session''s own w2 does not read the transcript' ((Rt-Api GET "/api/v1/sessions/$na" $null $w2).status -eq 403)
        Check 'no token: 401' ((Rt-Api GET "/api/v1/sessions/$na" $null '').status -eq 401)
        $html = Invoke-WebRequest -Uri $page -SkipHttpErrorCheck
        Check 'the page is served without data: it reads the API with the operator token (#token= or the dashboard''s tab)' ($html.StatusCode -eq 200 -and $html.Content -match '/api/v1/sessions/' -and $html.Content -match 'token=' -and $html.Content -notmatch 'line three')
        $ref = "repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders/git/matching-refs/factory/"
        Check 'the transcript is stored in GitHub beside the plant snapshot, encrypted' (Wait-Until -Seconds 45 { @((Invoke-RestMethod "http://127.0.0.1:$($script:FakePort)/$ref") | Where-Object { "$($_.ref)" -match "/transcripts/\d{8}/$([regex]::Escape($na))$" }).Count -eq 1 })
        $stored = @((Invoke-RestMethod "http://127.0.0.1:$($script:FakePort)/$ref") | Where-Object { "$($_.ref)" -match "/transcripts/\d{8}/$([regex]::Escape($na))$" })[0].ref
        $sha = (Invoke-RestMethod "http://127.0.0.1:$($script:FakePort)/repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders/git/ref/$($stored.Substring(5))").object.sha
        $msg = "$((Invoke-RestMethod "http://127.0.0.1:$($script:FakePort)/repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders/git/commits/$sha").message)"
        Check 'the stored message is ciphertext: no line of the transcript in it' ($msg -like 'fwt1:*' -and $msg -notmatch 'line three|session starts')
        Restart-Gateway
        $s2 = Rt-Api GET "/api/v1/sessions/$na"
        Check 'after a restart (killed, no stop) the transcript is read back from GitHub' ($s2.status -eq 200 -and @($s2.body.lines).Count -eq 3 -and $s2.body.lines[0] -eq '[system init] session starts' -and $s2.body.item -eq $n) ($s2 | ConvertTo-Json -Compress -Depth 4)
        $x2 = Rt-Api POST '/api/v1/runtime/transcript' @{ lines = @('after the restart') } $w2
        Check 'the container goes on posting with the same w2; the lines append to what was stored' ($x2.status -eq 200 -and $x2.body.total -eq 4 -and (Rt-Api GET "/api/v1/sessions/$na").body.lines[3] -eq 'after the restart') ($x2 | ConvertTo-Json -Compress)
        Check 'an unknown session answers 404' ((Rt-Api GET '/api/v1/sessions/oct-1-ffffff').status -eq 404)
    } finally { Oct-Cleanup @($n) }
}

'octopus: session transcript: the start comment of a new session is posted even when the item''s journal is full' = {
    $n = 0
    try {
        [void](Rt-Api GET '/api/v1/status')
        $n = New-Item-Fake 'Octopus transcript journal full' 'x' $OctLabels
        Check 'a Start worker run is made' (Wait-Until -Seconds 30 { (Oct-Runs 'Start worker' $n).Count -eq 1 })
        Check 'its launched line is posted' (Wait-Until -Seconds 30 { @((Get-FakeIssue $n).comments | Where-Object { $_.body -match 'plant event=launched ' }).Count -eq 1 })
        # the journal is past its cap (30 lines and the "journal full" note): a further line of an ordinary kind would not be posted
        Set-ItemState $n { param($st) $st.journal.count = 40 }
        $before = @((Get-FakeIssue $n).comments).Count
        Oct-Machines @(@{ id = 'Machines-1'; name = 'host-a' }, @{ id = 'Machines-2'; name = 'host-b' }, @{ id = 'Machines-6'; name = 'host-c' })   # a free host for the next start
        $x = Rt-Api POST '/api/v1/_test/octopus-provision' @{ item = $n; phase = 'Technical Design'; role = '' }
        $na2 = "$($x.body.name)"
        Check 'the start of a new session is still posted, with its session link' (Wait-Until -Seconds 30 { @((Get-FakeIssue $n).comments | Where-Object { $_.body -match 'plant event=launched ' -and $_.body.Contains("/sessions/$na2)") }).Count -eq 1 }) "$na2 $(@((Get-FakeIssue $n).comments | Select-Object -Last 2 | ForEach-Object { $_.body }) -join ' | ')"
        Check 'and no "journal full" note instead of it' (@((Get-FakeIssue $n).comments | Select-Object -Skip $before | Where-Object { $_.body -match 'journal full' }).Count -eq 0)
    } finally { Oct-Cleanup @($n) }
}
}
