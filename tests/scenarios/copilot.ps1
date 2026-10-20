# Tier 1 scenarios of the Copilot cloud agent runtime (docs/design/copilot-cloud-agent.md, gateway/Runtimes/CopilotCloudRuntime.cs) against the
# fake agent tasks API of tests/FakeGitHub. The scenario plays the agent: it takes the one-time code from the task's prompt, exchanges it with
# the bootstrap token for the r1 (directly, and through fworker-mcp, the stdio proxy the gateway serves), and calls /mcp. Suite 'copilot'
# (owner mode). Uses the helpers of owner.ps1 and mcp.ps1.

$CopLabels = @('factory worker', 'Auto Merge', 'runtime:copilot-cloud')
function Cop-State { Fake 'copilot' }
function Cop-Tasks([int]$Item) { , @((Cop-State).tasks | Where-Object { "$($_.body.prompt)" -match "work item: #$Item," }) }
function Cop-Code($Task) { if ("$($Task.body.prompt)" -match '(c1\.[0-9a-f]{48})') { $Matches[1] } else { '' } }
function Cop-Status { (Get-Plant).runtimes.'copilot-cloud' }
function Cop-Bootstrap { (Invoke-RestMethod "http://127.0.0.1:$($script:ApiPort)/api/v1/copilot/bootstrap-token" -Method POST -Headers @{ Authorization = 'Bearer test-token' }).token }
function Cop-Exchange([string]$Code, [string]$Bootstrap) {
    $h = @{}; if ($Bootstrap) { $h.Authorization = "Bearer $Bootstrap" }
    $r = Invoke-WebRequest "http://127.0.0.1:$($script:ApiPort)/api/v1/copilot/exchange" -Method POST -Headers $h -Body (@{ code = $Code } | ConvertTo-Json -Compress) -ContentType 'application/json' -SkipHttpErrorCheck
    $b = $null; try { $b = "$($r.Content)" | ConvertFrom-Json } catch { }
    return @{ status = [int]$r.StatusCode; body = $b }
}
function Cop-Cleanup([int[]]$Items) {
    foreach ($n in $Items) { if ($n) { [void](Invoke-RestMethod "http://127.0.0.1:$($script:FakePort)/repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders/issues/$n" -Method PATCH -Headers @{ Authorization = 'Bearer fake-operator' } -Body (@{ state = 'closed' } | ConvertTo-Json) -ContentType 'application/json') } }
    foreach ($t in @((Cop-State).tasks | Where-Object { $_.state -in 'queued', 'in_progress' })) { [void](Fake 'copilot-task' @{ id = $t.id; state = 'cancelled' }) }
    [void](Wait-Until -Seconds 20 { (Cop-Status).running -eq 0 })
    [void](Invoke-RestMethod "http://127.0.0.1:$($script:ApiPort)/api/v1/runtimes/copilot-cloud/reset" -Method POST -Headers @{ Authorization = 'Bearer test-token' })
}

$CopilotScenarios = [ordered]@{

'copilot: an item labelled runtime:copilot-cloud creates one agent task with the phase prompt and a one-time code; the code and the bootstrap token give the r1 once; session_end ends the task and revokes it' = {
    $n = 0
    try {
        $n = New-Item-Fake 'Copilot docs item' 'Add a line to docs/copilot.md.' $CopLabels
        Check 'a task is created for the item' (Wait-Until -Seconds 30 { (Cop-Tasks $n).Count -gt 0 })
        $t = (Cop-Tasks $n)[0]; $b = $t.body
        Check 'POST /agents/repos/{owner}/{repo}/tasks with X-GitHub-Api-Version 2026-03-10' ($t.path -eq '/agents/repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders/tasks' -and $t.apiVersion -eq '2026-03-10') ($t | ConvertTo-Json -Compress -Depth 3)
        Check 'the prompt is prompts/bootstrap/copilot.md with the item and phase' ("$($b.prompt)" -match 'You are a factory worker for ONE phase' -and "$($b.prompt)" -match 'fworker_connect' -and "$($b.prompt)" -match "#$n, phase technical-design") "$($b.prompt)"
        Check 'the body: base_ref master, no pull request by Copilot, no model (the account default), nothing else' ($b.base_ref -eq 'master' -and $b.create_pull_request -eq $false -and -not $b.PSObject.Properties['model'] -and @($b.PSObject.Properties.Name).Count -eq 3) ($b | ConvertTo-Json -Compress)
        $code = Cop-Code $t
        Check 'the prompt carries a one-time code and no credential' ($code -ne '' -and -not ("$($b.prompt)" -match 'r1\.|b1\.|i1\.|w1\.|w2\.|fake-copilot'))
        Check 'neither the token nor the code is in the gateway log' (@(Get-ChildItem $script:Work -Filter 'gateway*.out' | Select-String -SimpleMatch 'fake-copilot').Count -eq 0 -and @(Get-ChildItem $script:Work -Filter 'gateway*.out' | Select-String -SimpleMatch $code).Count -eq 0)
        $st = Cop-Status
        Check 'status: the task runs under the copilot-cloud runtime' ($st.running -eq 1 -and @($st.executions | Where-Object { $_.item -eq $n -and $_.execution -eq $t.id }).Count -eq 1) ($st | ConvertTo-Json -Compress -Depth 5)
        $boot = Cop-Bootstrap
        Check 'the bootstrap token is a b1 and authenticates no other endpoint' ($boot -like 'b1.1.*' -and (Mcp 'tools/list' @{} $boot).status -eq 401)
        Check 'the exchange without the bootstrap token is refused' ((Cop-Exchange $code '').status -eq 401)
        Check 'the exchange with a forged bootstrap token is refused' ((Cop-Exchange $code ('b1.1.' + ('0' * 64))).status -eq 401)
        $x = Cop-Exchange $code $boot
        Check 'the code and the bootstrap token give the worker''s r1' ($x.status -eq 200 -and "$($x.body.token)" -like 'r1.*' -and "$($x.body.name)" -like "cop-$n-*") ($x | ConvertTo-Json -Compress)
        Check 'a code works once' ((Cop-Exchange $code $boot).status -eq 401)
        $r1 = "$($x.body.token)"
        Check 'the r1 is a worker of the api mode: the worker tools' ((Mcp-ToolNames $r1) -contains 'get_prompt')
        $cur = ''
        Check 'the offer arrives by next_event' (Wait-Until -Seconds 30 { $e = Mcp-Events $r1 $cur 5; @($e.events | Where-Object { $_.event -eq 'item.assigned' -and $_.item -eq $n }).Count -gt 0 })
        $ac = Mcp-Call 'item_accept' @{ item = $n } $r1
        Check 'the agent accepts its item' (-not $ac.isError) ($ac | ConvertTo-Json -Compress -Depth 4)
        $row = @((Get-Plant).plant.workers | Where-Object { $_.name -like "cop-$n-*" })
        Check 'its worker shows type copilot-cloud, mode api' ($row.Count -eq 1 -and $row[0].mode -eq 'api' -and $row[0].type -eq 'copilot-cloud' -and $row[0].execution -eq $t.id) ($row | ConvertTo-Json -Compress -Depth 4)
        [void](Mcp-Call 'session_end' @{ reason = 'scenario done' } $r1)
        Check 'session_end ends the task for the runtime' (Wait-Until -Seconds 15 { (Cop-Status).running -eq 0 })
        Check 'and revokes its r1' (Wait-Until -Seconds 30 { (Mcp 'tools/list' @{} $r1).status -eq 401 })
    } finally { Cop-Cleanup @($n) }
}

'copilot: fworker-mcp, the stdio proxy the gateway serves, connects with the code and the bootstrap token and proxies the phase''s tools' = {
    $n = 0
    try {
        $n = New-Item-Fake 'Copilot proxy item' 'x' $CopLabels
        Check 'a task is created' (Wait-Until -Seconds 30 { (Cop-Tasks $n).Count -gt 0 })
        $code = Cop-Code (Cop-Tasks $n)[0]
        $js = Join-Path $script:Work 'fworker-mcp.mjs'
        $resp = Invoke-WebRequest "http://127.0.0.1:$($script:ApiPort)/fworker-mcp.mjs" -SkipHttpErrorCheck
        Check 'the gateway serves fworker-mcp.mjs anonymously' ([int]$resp.StatusCode -eq 200 -and "$($resp.Content)" -match 'fworker_connect')
        [IO.File]::WriteAllText($js, "$($resp.Content)")
        $lines = @(
            @{ jsonrpc = '2.0'; id = 1; method = 'initialize'; params = @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'scenario'; version = '1' } } }
            @{ jsonrpc = '2.0'; id = 2; method = 'tools/list'; params = @{} }
            @{ jsonrpc = '2.0'; id = 3; method = 'tools/call'; params = @{ name = 'get_prompt'; arguments = @{} } }
            @{ jsonrpc = '2.0'; id = 4; method = 'tools/call'; params = @{ name = 'fworker_connect'; arguments = @{ code = $code } } }
            @{ jsonrpc = '2.0'; id = 5; method = 'tools/list'; params = @{} }
            @{ jsonrpc = '2.0'; id = 6; method = 'tools/call'; params = @{ name = 'get_prompt'; arguments = @{} } }
            @{ jsonrpc = '2.0'; id = 7; method = 'tools/call'; params = @{ name = 'fworker_call'; arguments = @{ name = 'get_prompt'; arguments = @{} } } }
        ) | ForEach-Object { $_ | ConvertTo-Json -Depth 8 -Compress }
        $in = Join-Path $script:Work 'fworker-mcp.in'
        # one line at a time, a pause after each, as an agent calls: the connect must be done before the calls after it
        $script_ = ($lines | ForEach-Object { "printf '%s\n' '$($_.Replace("'", "'\''"))'; sleep 1" }) -join '; '
        [IO.File]::WriteAllText($in, "( $script_ ) | node '$js'")
        $oldG = $env:FWORKER_GATEWAY_URL; $oldB = $env:FWORKER_BOOTSTRAP_TOKEN
        try {
            $env:FWORKER_GATEWAY_URL = "http://127.0.0.1:$($script:ApiPort)"; $env:FWORKER_BOOTSTRAP_TOKEN = Cop-Bootstrap
            $out = @(& bash $in 2>$null)
        } finally { $env:FWORKER_GATEWAY_URL = $oldG; $env:FWORKER_BOOTSTRAP_TOKEN = $oldB }
        $msgs = @($out | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json -Depth 30 })
        function ById($i) { @($msgs | Where-Object { $_.PSObject.Properties['id'] -and $_.id -eq $i })[0] }
        Check 'initialize answers fworker-mcp with tools that change' ((ById 1).result.serverInfo.name -eq 'fworker-mcp' -and (ById 1).result.capabilities.tools.listChanged -eq $true) ($out -join "`n")
        Check 'before the connect only fworker_connect and fworker_call are listed' ((@((ById 2).result.tools | ForEach-Object { $_.name }) -join ',') -eq 'fworker_connect,fworker_call')
        Check 'a gateway tool before the connect is refused not-connected' ((ById 3).result.isError -eq $true -and "$((ById 3).result.content[0].text)" -match 'not-connected')
        Check 'fworker_connect exchanges the code' ((ById 4).result.isError -eq $false -and "$((ById 4).result.content[0].text)" -match '"ok":true') "$((ById 4) | ConvertTo-Json -Compress -Depth 6)"
        Check 'the tool list change is announced' (@($msgs | Where-Object { $_.PSObject.Properties['method'] -and $_.method -eq 'notifications/tools/list_changed' }).Count -eq 1)
        Check 'afterwards the technical-design tools are listed' (@((ById 5).result.tools | ForEach-Object { $_.name }) -contains 'get_prompt' -and @((ById 5).result.tools | ForEach-Object { $_.name }) -notcontains 'pr_open')
        Check 'a gateway tool is proxied: get_prompt gives the technical-design prompt' (-not (ById 6).result.isError -and "$((ById 6).result.content[0].text)" -match 'worker/technical-design')
        Check 'and so is fworker_call' (-not (ById 7).result.isError -and "$((ById 7).result.content[0].text)" -match 'worker/technical-design')
        Check 'nothing of a token reached stdout' (-not (($out -join "`n") -match 'r1\.|b1\.'))
    } finally { Cop-Cleanup @($n) }
}

'copilot: a task that ends by itself ends its worker and reports its premium requests; a failed create frees the slot; a task past api.copilot.maxMinutes fails and its code dies' = {
    $n = 0; $m = 0
    try {
        $n = New-Item-Fake 'Copilot self end' 'x' $CopLabels
        Check 'a task is created' (Wait-Until -Seconds 30 { (Cop-Tasks $n).Count -gt 0 })
        $t = (Cop-Tasks $n)[0]
        [void](Fake 'copilot-task' @{ id = $t.id; state = 'completed'; premium = 3 })
        Check 'its end is seen, with the premium requests of its sessions' (Wait-Until -Seconds 20 { $d = @((Cop-Status).last | Where-Object { $_.execution -eq $t.id }); $d.Count -eq 1 -and $d[0].detail.usage.premium_requests -eq 3 }) ((Cop-Status).last | ConvertTo-Json -Compress -Depth 6)
        Check 'its usage is logged' (Wait-Until -Seconds 10 { @(Get-ChildItem $script:Work -Filter 'gateway*.out' | Select-String -SimpleMatch '"runtime":"copilot-usage"').Count -gt 0 })
        [void](Fake 'copilot-launch-failures' @{ count = 1 })
        $m = New-Item-Fake 'Copilot relaunch' 'x' $CopLabels
        Check 'after the failed create a new task is created' (Wait-Until -Seconds 40 { (Cop-Tasks $m).Count -ge 1 })
        Check 'status: the failure was counted against the item' (@((Cop-Status).failures | Where-Object { $_.item -eq $m -and $_.count -ge 1 }).Count -eq 1) (Cop-Status | ConvertTo-Json -Compress -Depth 5)
        $t2 = (Cop-Tasks $m)[-1]
        Check 'the deadline (0.25 min in this suite) fails it' (Wait-Until -Seconds 60 { @((Get-ChildItem $script:Work -Filter 'gateway*.out' | Select-String -SimpleMatch "`"runtime`":`"copilot-deadline`"") | Where-Object { "$_" -match $t2.id }).Count -gt 0 })
        Check 'and its unspent code no longer exchanges' ((Cop-Exchange (Cop-Code $t2) (Cop-Bootstrap)).status -eq 401)
    } finally { Cop-Cleanup @($n, $m) }
}

'copilot: the default runtime takes an unlabelled code item to Development with a pull request by Copilot; pr_open takes over its draft on the copilot/ branch; Functional Testing starts from that branch' = {
    $n = 0
    try {
        $n = New-Item-Fake 'Copilot code item' 'Change src/App/Thing.cs and its unit test.' @('factory worker', 'Auto Merge') 'Development'
        Check 'an unlabelled item is routed to copilot-cloud (runtimes.routing *), code and all' (Wait-Until -Seconds 30 { (Cop-Tasks $n).Count -gt 0 })
        $t = (Cop-Tasks $n)[0]
        Check 'the Development task asks Copilot for its pull request' ($t.body.create_pull_request -eq $true -and "$($t.body.prompt)" -match "#$n, phase development") ($t.body | ConvertTo-Json -Compress)
        Check 'the prompt names the gateway for fworker_connect' ("$($t.body.prompt)" -match 'Your gateway URL for fworker_connect: http://gw\.test')
        $br = "copilot/thing-$n"
        [void](Fake 'push-branch' @{ branch = $br; files = @('src/App/Thing.cs', 'src/UnitTests/ThingTests.cs') })
        $h = @{ Authorization = 'Bearer fake-operator' }
        $draft = Invoke-RestMethod "http://127.0.0.1:$($script:FakePort)/repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders/pulls" -Method POST -Headers $h -ContentType 'application/json' -Body (@{ title = '[WIP] Thing'; head = $br; base = 'master'; body = 'x'; draft = $true } | ConvertTo-Json)
        $r = Invoke-RestMethod "http://127.0.0.1:$($script:ApiPort)/api/v1/verbs/open-pr" -Method POST -Headers @{ Authorization = 'Bearer test-token' } -ContentType 'application/json' -Body (@{ item = $n; head = $br; title = 'Thing' } | ConvertTo-Json) -SkipHttpErrorCheck
        Check 'pr_open with the copilot/ branch takes over the draft' ($r.ok -and $r.pullRequest.number -eq $draft.number -and $r.created -eq $false) ($r | ConvertTo-Json -Compress -Depth 4)
        $p = Invoke-RestMethod "http://127.0.0.1:$($script:FakePort)/repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders/pulls/$($draft.number)" -Headers $h
        Check 'and marks it ready, its title naming the item' (-not $p.draft -and "$($p.title)" -match "#$n") ($p | ConvertTo-Json -Compress -Depth 3)
        $bad = Invoke-RestMethod "http://127.0.0.1:$($script:ApiPort)/api/v1/verbs/open-pr" -Method POST -Headers @{ Authorization = 'Bearer test-token' } -ContentType 'application/json' -Body (@{ item = $n; head = 'feature/other'; title = 'x' } | ConvertTo-Json) -SkipHttpErrorCheck
        Check 'another branch name is still refused' ($bad.code -eq 'bad-branch-name') ($bad | ConvertTo-Json -Compress)
        [void](Fake 'push-branch' @{ branch = "copilot/other-$n"; files = @('src/App/Other.cs') })
        [void](Invoke-RestMethod "http://127.0.0.1:$($script:FakePort)/repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders/pulls" -Method POST -Headers $h -ContentType 'application/json' -Body (@{ title = 'Fix #99999'; head = "copilot/other-$n"; base = 'master'; body = 'x'; draft = $true } | ConvertTo-Json))
        $oth = Invoke-RestMethod "http://127.0.0.1:$($script:ApiPort)/api/v1/verbs/open-pr" -Method POST -Headers @{ Authorization = 'Bearer test-token' } -ContentType 'application/json' -Body (@{ item = $n; head = "copilot/other-$n"; title = 'x' } | ConvertTo-Json) -SkipHttpErrorCheck
        Check 'a draft that names another item is not taken over' ($oth.code -eq 'branch-of-other-item') ($oth | ConvertTo-Json -Compress)
        [void](Fake 'copilot-task' @{ id = $t.id; state = 'completed'; headRef = $br })
        Check 'the task''s end is seen' (Wait-Until -Seconds 20 { @((Cop-Status).last | Where-Object { $_.execution -eq $t.id }).Count -eq 1 })
        [void](Fake 'move' @{ number = $n; column = 'Functional Testing' })
        Check 'Functional Testing gets its own task, from the branch Development reported' (Wait-Until -Seconds 40 { @(Cop-Tasks $n | Where-Object { "$($_.body.prompt)" -match 'phase functional-testing' -and $_.body.base_ref -eq $br -and $_.body.create_pull_request -eq $false }).Count -eq 1 }) ((Cop-Tasks $n) | ConvertTo-Json -Compress -Depth 4)
    } finally { Cop-Cleanup @($n) }
}

'copilot: fworker-mcp accepts only the three factory gateways (https, no path) or its configured URL, each with its own bootstrap token' = {
    $js = Join-Path $script:Work 'fworker-mcp-check.mjs'
    [IO.File]::WriteAllText($js, "$((Invoke-WebRequest "http://127.0.0.1:$($script:ApiPort)/fworker-mcp.mjs").Content)")
    function Pick([string]$u) { "$(& node $js --check-gateway $u)".Trim() }
    foreach ($ok in 'https://ca-cmdemo1-tdd-fworker.graybush-19a6e13b.southcentralus.azurecontainerapps.io', 'https://ca-cmdemo1-uat-fworker-fccv.graybush-19a6e13b.southcentralus.azurecontainerapps.io/', 'https://ca-cmdemo1-prod-fworker-bhj5.happyisland-465ac2bf.southcentralus.azurecontainerapps.io') {
        Check "allowed: $ok" ((Pick $ok) -eq ('"' + $ok.TrimEnd('/') + '"'))
    }
    foreach ($no in 'https://evil.example.com', 'http://ca-cmdemo1-tdd-fworker.graybush-19a6e13b.southcentralus.azurecontainerapps.io', 'https://ca-cmdemo1-tdd-fworker.graybush-19a6e13b.southcentralus.azurecontainerapps.io.evil.com', 'https://ca-cmdemo1-tdd-fworker.graybush-19a6e13b.southcentralus.azurecontainerapps.io:8443', 'https://ca-cmdemo1-tdd-fworker.graybush-19a6e13b.southcentralus.azurecontainerapps.io/x', 'https://u@ca-cmdemo1-tdd-fworker.graybush-19a6e13b.southcentralus.azurecontainerapps.io') {
        Check "refused: $no" ((Pick $no) -eq 'null')
    }
}
}
