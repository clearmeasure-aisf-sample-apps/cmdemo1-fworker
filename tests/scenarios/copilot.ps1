# Tier 1 scenarios of the Copilot cloud agent runtime (docs/design/copilot-cloud-agent.md, gateway/Runtimes/CopilotCloudRuntime.cs) against the
# fake agent tasks API of tests/FakeGitHub. The scenario plays the agent: it takes the one-time code from the task's prompt, exchanges it with
# the bootstrap token for the r1 (directly, and through fworker-mcp, the stdio proxy the gateway serves), and calls /mcp. Suite 'copilot'
# (owner mode). Uses the helpers of owner.ps1 and mcp.ps1.

# the suite's gateway is tdd with env scoping on (as deployed): every item carries env:tdd
$CopLabels = @('factory worker', 'Auto Merge', 'runtime:copilot-cloud', 'env:tdd')
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
# the r1 of a task's session: the exchange is retried until it answers 200 (the task's prompt and code can lag the task's creation on a loaded runner); '' when it never does
function Cop-R1($Task) {
    $got = @{ tok = '' }
    [void](Wait-Until -Seconds 30 { $x = Cop-Exchange (Cop-Code $Task) (Cop-Bootstrap); if ($x.status -eq 200 -and $x.body -and $x.body.token) { $got.tok = "$($x.body.token)"; $true } else { $false } })
    return $got.tok
}
# waits until no task is created for 4 s in a row (earlier scenarios' items have stopped relaunching)
function Cop-Quiet {
    $st = @{ last = -1; since = Get-Date }
    [void](Wait-Until -Seconds 40 { $c = @((Cop-State).tasks).Count; if ($c -ne $st.last) { $st.last = $c; $st.since = Get-Date }; ((Get-Date) - $st.since).TotalSeconds -ge 4 })
}
# every execution end the runtime recorded (status 'last') has run to its last step, the revoke (RevokeOnEnd; a failed one is logged too). The end
# counts an execution out of 'running' at its start, but its claim release and revoke are rule-script calls that the runtime's poll loop awaits:
# until they are done no other execution's end is read, so a scenario that starts meanwhile sees its own session end late (#376)
function Cop-EndsSettled {
    $names = @(@((Cop-Status).last) | Where-Object { $_ } | ForEach-Object { "$($_.worker)" } | Where-Object { $_ })
    if ($names.Count -eq 0) { return $true }
    $lines = @(Get-ChildItem $script:Work -Filter 'gateway*.out' | Select-String -SimpleMatch '"revoke' | ForEach-Object { "$($_.Line)" })
    foreach ($w in $names) { if (-not @($lines | Where-Object { $_.Contains("`"revoke`":`"$w`"") -or ($_.Contains('"runtime":"revoke-failed"') -and $_.Contains("`"worker`":`"$w`"")) }).Count) { return $false } }
    return $true
}
function Cop-Cleanup([int[]]$Items) {
    foreach ($n in $Items) { if ($n) { [void](Invoke-RestMethod "http://127.0.0.1:$($script:FakePort)/repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders/issues/$n" -Method PATCH -Headers @{ Authorization = 'Bearer fake-operator' } -Body (@{ state = 'closed' } | ConvertTo-Json) -ContentType 'application/json') } }
    foreach ($t in @((Cop-State).tasks | Where-Object { $_.state -in 'queued', 'in_progress' })) { [void](Fake 'copilot-task' @{ id = $t.id; state = 'cancelled' }) }
    [void](Wait-Until -Seconds 20 { (Cop-Status).running -eq 0 })
    [void](Wait-Until -Seconds 60 { Cop-EndsSettled })
    [void](Invoke-RestMethod "http://127.0.0.1:$($script:ApiPort)/api/v1/runtimes/copilot-cloud/reset" -Method POST -Headers @{ Authorization = 'Bearer test-token' })
}

# plays one session: exchanges the code of its prompt, waits for the offer and accepts; answers the r1
function Cop-Accept($Entry, [int]$Item) {
    $r1 = (Cop-R1 $Entry)
    $cur = ''
    [void](Wait-Until -Seconds 30 { $e = Mcp-Events $r1 $cur 5; @($e.events | Where-Object { $_.event -eq 'item.assigned' -and $_.item -eq $Item }).Count -gt 0 })
    [void](Mcp-Call 'item_accept' @{ item = $Item } $r1)
    return $r1
}
function Cop-Sessions([int]$Item, [string]$Task, [string]$Phase) { , @((Cop-Tasks $Item) | Where-Object { $_.task -eq $Task -and "$($_.body.prompt)" -match "phase $Phase" }) }
function Cop-NotOffered([int]$Item) { @((Get-FakeIssue $Item).comments | Where-Object { "$($_.body)" -match 'plant event=offered' -and "$($_.body)" -notmatch "cop-$Item-" }).Count -eq 0 }
function Cop-Brief([int]$Item) { ((Get-FakeIssue $Item).comments | ForEach-Object { $_.body.Substring(0, [Math]::Min(140, $_.body.Length)) }) -join ' | ' }

function Cop-Gh { "http://127.0.0.1:$($script:FakePort)/repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders" }
function Cop-Draft([string]$Head, [string]$Base, [string]$Title = '[WIP] Thing', [string]$Body = 'x') { Invoke-RestMethod "$(Cop-Gh)/pulls" -Method POST -Headers @{ Authorization = 'Bearer fake-operator' } -ContentType 'application/json' -Body (@{ title = $Title; head = $Head; base = $Base; body = $Body; draft = $true } | ConvertTo-Json) }
function Cop-OpenPr([int]$Item, [string]$Head) { Invoke-RestMethod "http://127.0.0.1:$($script:ApiPort)/api/v1/verbs/open-pr" -Method POST -Headers @{ Authorization = 'Bearer test-token' } -ContentType 'application/json' -Body (@{ item = $Item; head = $Head; title = 'Thing' } | ConvertTo-Json) -SkipHttpErrorCheck }
function Cop-FakePr([int]$Num) { @((Fake 'state').prs | Where-Object { $_.number -eq $Num })[0] }
function Cop-DevTasks([int]$Item) { , @((Cop-Tasks $Item) | Where-Object { "$($_.body.prompt)" -match 'phase development' }) }
function Cop-Comments([int]$Item, [string]$Like) { , @((Get-FakeIssue $Item).comments | Where-Object { "$($_.body)" -like $Like }) }
# the item's owner in the plant, '' when it has none (a strict-mode read of a missing key throws instead of failing the check)
function Cop-Owner([int]$Item) { $o = (Get-Plant).plant.owners; if ($null -eq $o) { return '' }; $p = $o.PSObject.Properties["$Item"]; if ($p) { "$($p.Value)" } else { '' } }
# the runtime's deadline lines (FACTORY_COPILOT_MAX_MINUTES) logged for a task
function Cop-DeadlineLines([string]$Task) { , @(Get-ChildItem $script:Work -Filter 'gateway*.out' | Select-String -SimpleMatch "`"runtime`":`"copilot-deadline`"" | Where-Object { "$_" -match [regex]::Escape($Task) }) }

$CopilotScenarios = [ordered]@{

'copilot: an item labelled runtime:copilot-cloud creates one agent task with the phase prompt and a one-time code; the code and the bootstrap token give the r1 once; session_end ends the task and revokes it' = {
    $n = 0
    try {
        $n = New-Item-Fake 'Copilot docs item' 'Add a line to docs/copilot.md.' $CopLabels
        Check 'a task is created for the item' (Wait-Until -Seconds 30 { (Cop-Tasks $n).Count -gt 0 })
        $t = (Cop-Tasks $n)[0]; $b = $t.body
        Check 'POST /agents/repos/{owner}/{repo}/tasks with X-GitHub-Api-Version 2026-03-10' ($t.path -eq '/agents/repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders/tasks' -and $t.apiVersion -eq '2026-03-10') ($t | ConvertTo-Json -Compress -Depth 3)
        Check 'the prompt is prompts/bootstrap/copilot.md with the item and phase' ("$($b.prompt)" -match 'You are a factory worker for ONE phase' -and "$($b.prompt)" -match 'fworker_connect' -and "$($b.prompt)" -match "#$n, phase technical-design") "$($b.prompt)"
        Check 'the body: base_ref master, the owner task opens the item''s draft pull request at once, no model (the account default), nothing else' ($b.base_ref -eq 'master' -and $b.create_pull_request -eq $true -and -not $b.PSObject.Properties['model'] -and @($b.PSObject.Properties.Name).Count -eq 3) ($b | ConvertTo-Json -Compress)
        $code = Cop-Code $t
        Check 'the prompt carries a one-time code and no credential' ($code -ne '' -and -not ("$($b.prompt)" -match 'r1\.|b1\.|i1\.|w1\.|w2\.|fake-copilot'))
        Check 'neither the token nor the code is in the gateway log' (@(Get-ChildItem $script:Work -Filter 'gateway*.out' | Select-String -SimpleMatch 'fake-copilot').Count -eq 0 -and @(Get-ChildItem $script:Work -Filter 'gateway*.out' | Select-String -SimpleMatch $code).Count -eq 0)
        [void](Wait-RuntimeExecution { Cop-Status } -Item $n -Execution "$($t.id)")   # (v2 settles the execution when the start's result message is handled; see lib.ps1)
        $st = Cop-Status
        Check 'status: the task runs under the copilot-cloud runtime' ($st.running -eq 1 -and @($st.executions | Where-Object { $_.item -eq $n -and $_.execution -eq $t.id }).Count -eq 1) ($st | ConvertTo-Json -Compress -Depth 5)
        $link = "https://github.com/clearmeasure-aisf-sample-apps/cmdemo1-workorders/tasks/$($t.id)"
        Check 'status: the execution carries the task''s html_url' (@($st.executions | Where-Object { $_.execution -eq $t.id -and $_.url -eq $link -and $_.detail.url -eq $link }).Count -eq 1) ($st.executions | ConvertTo-Json -Compress -Depth 5)
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
        # the end of this task, not the runtime's count: the end re-offers the open item and the runtime launches a second execution for it (below), so running can be 0 and 1 again between two polls
        Check 'session_end ends the task for the runtime' (Wait-Until -Seconds 15 { @((Cop-Status).executions | Where-Object { $_.execution -eq $t.id }).Count -eq 0 })
        Check 'status: last[] carries the url' (Wait-Until -Seconds 15 { @((Cop-Status).last | Where-Object { $_.execution -eq $t.id -and $_.url -eq $link }).Count -eq 1 })
        Check 'the launch comment on the issue names the task and links its Copilot session' (Wait-Until -Seconds 20 { (Cop-Comments $n "*started **cop-$n-* (task *$($t.id)*). Copilot session: $link*").Count -eq 1 }) (((Get-FakeIssue $n).comments | ForEach-Object { $_.body }) -join "`n---`n")
        Check 'the accepted comment links the Copilot session' (Wait-Until -Seconds 20 { (Cop-Comments $n "*cop-$n-* accepted*Copilot session: $link*").Count -eq 1 }) (((Get-FakeIssue $n).comments | ForEach-Object { $_.body }) -join "`n---`n")
        Check 'no comment carries a token' (-not ((((Get-FakeIssue $n).comments | ForEach-Object { $_.body }) -join "`n") -match 'r1\.|b1\.|c1\.|fake-copilot'))
        Check 'and revokes its r1' (Wait-Until -Seconds 30 { (Mcp 'tools/list' @{} $r1).status -eq 401 })
        # (issue #395) the end offers the open item to the next worker, and the runtime launches a second execution for it at the next plan: wait for
        # it, then end it while the item is still open, so the plant still holds its offer when the end is read. That end takes the offer back and
        # releases the claim (claim-item release: a DELETE of the ref the first end already removed, 422). Left to Cop-Cleanup, whether the second
        # execution existed before the close, and whether a sweep dropped the closed item's offer before the end was read, were timing.
        $x2 = @{ e = $null }
        Check 'the ended session''s item is offered again: the runtime launches a second execution' (Wait-Until -Seconds 30 { $x2.e = @((Cop-Status).executions) | Where-Object { $_ -and $_.item -eq $n -and $_.execution -and $_.execution -ne $t.id } | Select-Object -First 1; $null -ne $x2.e }) (Cop-Status | ConvertTo-Json -Compress -Depth 5)
        if ($x2.e) {
            [void](Fake 'copilot-task' @{ id = "$($x2.e.execution)"; state = 'cancelled' })
            Check 'the second execution, stopped while it holds the offer, ends and the runtime has run its end (claim released, revoked)' (Wait-Until -Seconds 60 { @((Cop-Status).last | Where-Object { $_.worker -eq $x2.e.worker }).Count -eq 1 -and (Cop-EndsSettled) }) (Cop-Status | ConvertTo-Json -Compress -Depth 5)
        }
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
        $outFile = Join-Path $script:Work 'fworker-mcp.out'; [IO.File]::WriteAllText($outFile, '')
        # one line at a time, a pause after each, as an agent calls: the connect must be done before the calls after it. The proxy answers each
        # line as it comes (tools/list from what it holds now), so the line after fworker_connect (id 4) is sent once the connect has answered,
        # not after a fixed second: under load the exchange and the upstream tools/list took longer, and tools/list (id 5) still saw the old list.
        $afterConnect = "n=0; until grep -q '`"id`":4[,}]' '$outFile' || [ `$n -ge 600 ]; do sleep 0.1; n=`$((n+1)); done"
        $script_ = (@(for ($k = 0; $k -lt $lines.Count; $k++) { "printf '%s\n' '$($lines[$k].Replace("'", "'\''"))'; $(if ($k -eq 3) { $afterConnect } else { 'sleep 1' })" })) -join '; '
        [IO.File]::WriteAllText($in, "( $script_ ) | node '$js' | tee '$outFile'")
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

'copilot: a task that ends by itself ends its worker and reports its premium requests; a failed create frees the slot' = {
    $n = 0; $m = 0
    try {
        $n = New-Item-Fake 'Copilot self end' 'x' $CopLabels
        Check 'a task is created' (Wait-Until -Seconds 30 { (Cop-Tasks $n).Count -gt 0 })
        $t = (Cop-Tasks $n)[0]
        [void](Fake 'copilot-task' @{ id = $t.id; state = 'completed'; premium = 3 })
        Check 'its end is seen, with the premium requests of its sessions' (Wait-Until -Seconds 20 { $d = @((Cop-Status).last | Where-Object { $_.execution -eq $t.id }); $d.Count -eq 1 -and $d[0].detail.usage.premium_requests -eq 3 }) ((Cop-Status).last | ConvertTo-Json -Compress -Depth 6)
        Check 'its usage is logged' (Wait-Until -Seconds 10 { @(Get-ChildItem $script:Work -Filter 'gateway*.out' | Select-String -SimpleMatch '"runtime":"copilot-usage"').Count -gt 0 })
        # the item is closed first: a relaunch of it would take the injected failure meant for the next item
        Cop-Cleanup @($n); $n = 0
        Cop-Quiet
        [void](Fake 'copilot-launch-failures' @{ count = 1 })
        $m = New-Item-Fake 'Copilot relaunch' 'x' $CopLabels
        Check 'after the failed create a new task is created' (Wait-Until -Seconds 40 { (Cop-Tasks $m).Count -ge 1 })
        # the fake refuses the create with a 500: a transient answer, which the start command tries again (RuntimeCommandService.RetryPauses, 2 s) with the same worker, so nothing ended as start-failed and
        # no failure is counted; a 4xx refusal is a failure at once (V1CommandPort: the suite 'cursor-deadline' has one)
        Check 'status (v2): the refused create is tried again by the start command; it is no failure of the item and the retry is the item''s only task' (@((Cop-Status).failures | Where-Object { $_.item -eq $m }).Count -eq 0 -and (Cop-Tasks $m).Count -eq 1) (Cop-Status | ConvertTo-Json -Compress -Depth 5)
    } finally { Cop-Cleanup @($n, $m) }
}

'copilot: the default runtime takes an unlabelled code item to Development with a pull request by Copilot; pr_open takes over its draft on the copilot/ branch; Functional Testing starts from that branch' = {
    $n = 0
    try {
        $n = New-Item-Fake 'Copilot code item' 'Change src/App/Thing.cs and its unit test.' @('factory worker', 'Auto Merge', 'env:tdd') 'Development'
        Check 'an unlabelled item is routed to copilot-cloud (runtimes.routing *), code and all' (Wait-Until -Seconds 60 { (Cop-Tasks $n).Count -gt 0 })
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
        # the gateway completes Development from the pull request (copilot-settle) and moves the item on when its checks are green
        Check 'the gateway moves the item to Functional Testing' (Wait-Until -Seconds 60 { (Get-FakeIssue $n).column -eq 'Functional Testing' }) "$((Get-FakeIssue $n).column)"
        Check 'no second Development task was started' ((Cop-DevTasks $n).Count -eq 1) (((Cop-Tasks $n) | ForEach-Object { "$($_.id) $($_.state) $($_.body.base_ref)" }) -join '; ')
        $ftFrom = Get-Date
        # (a Development task relaunched before the sweep saw the move is stopped too, inside the wait: on a loaded runner it can start late)
        Check 'Functional Testing is a follow-up session of the same task, on its pull request' (Wait-Until -Seconds 90 { foreach ($tt in @((Cop-Tasks $n) | Where-Object { $_.state -in 'queued', 'in_progress' -and "$($_.body.prompt)" -match 'phase development' })) { [void](Fake 'copilot-task' @{ id = $tt.id; state = 'cancelled' }) }; @((Cop-Tasks $n) | Where-Object { "$($_.body.prompt)" -match 'phase functional-testing' -and $_.task -eq $t.id -and $_.via -eq 'comment' -and $_.pr -eq $draft.number }).Count -eq 1 }) ("from $($ftFrom.ToUniversalTime().ToString('o')) to $((Get-Date).ToUniversalTime().ToString('o')) br=$br " + ((Cop-Tasks $n) | ForEach-Object { "$($_.id) $($_.state) $($_.task) $($_.via) $($_.pr)" }) -join '; ')
        Check 'and its prompt names the current branch and tells a session on a newer branch of the task to use pr_open, not fail' (@((Cop-Tasks $n) | Where-Object { $_.via -eq 'comment' -and "$($_.body.prompt)" -match 'phase functional-testing' -and "$($_.body.prompt)" -match "PR #$($draft.number), branch $br" -and "$($_.body.prompt)" -match 'newer copilot/ branch of this same task' }).Count -ge 1) (((Cop-Tasks $n) | ForEach-Object { "$($_.id) $($_.via) $($_.pr)" }) -join '; ')
        Check 'one task for the item, no second POST' (@((Cop-Tasks $n) | Select-Object -ExpandProperty task -Unique).Count -eq 1)
    } finally { Cop-Cleanup @($n) }
}

'copilot: fix-forward of an item with an open pull request: the task starts from its branch, pr_open makes the task''s draft the item''s pull request, closes the superseded one and its branch, and closes extra drafts; no duplicates (Functional Testing from that branch: the scenario above)' = {
    $n = 0
    try {
        $h = @{ Authorization = 'Bearer fake-operator' }
        $gh = "http://127.0.0.1:$($script:FakePort)/repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders"
        function OpenPr([string]$Head) { Invoke-RestMethod "http://127.0.0.1:$($script:ApiPort)/api/v1/verbs/open-pr" -Method POST -Headers @{ Authorization = 'Bearer test-token' } -ContentType 'application/json' -Body (@{ item = $n; head = $Head; title = 'Thing' } | ConvertTo-Json) -SkipHttpErrorCheck }
        function Draft([string]$Head, [string]$Base, [string]$Body) { Invoke-RestMethod "$gh/pulls" -Method POST -Headers $h -ContentType 'application/json' -Body (@{ title = '[WIP] Thing'; head = $Head; base = $Base; body = $Body; draft = $true } | ConvertTo-Json) }
        function FakePr([int]$Num) { @((Fake 'state').prs | Where-Object { $_.number -eq $Num })[0] }
        $n = New-Item-Fake 'Copilot fix-forward item' 'Change src/App/Thing.cs and its unit test.' @('factory worker', 'Auto Merge', 'env:tdd') 'Development'
        Check 'a Development task is created' (Wait-Until -Seconds 30 { (Cop-Tasks $n).Count -gt 0 })
        $t1 = (Cop-Tasks $n)[0]
        Check 'the first Development task starts from master: the item has no pull request yet' ($t1.body.base_ref -eq 'master' -and -not ("$($t1.body.prompt)" -match "This item's pull request")) ($t1.body | ConvertTo-Json -Compress)
        $a = "copilot/thing-$n"
        # its checks go red: the gateway, waiting for them after the task ended, sends the item back to Development to fix forward
        [void](Fake 'push-branch' @{ branch = $a; files = @('src/App/Thing.cs'); profile = 'red' })
        $d1 = Draft $a 'master' 'x'
        $r1 = OpenPr $a
        Check 'pr_open takes over the first draft' ($r1.ok -and $r1.pullRequest.number -eq $d1.number) ($r1 | ConvertTo-Json -Compress -Depth 4)
        [void](Fake 'copilot-task' @{ id = $t1.id; state = 'completed'; headRef = $a })
        Check 'the task''s end leaves Development done pending checks in the gateway' (Wait-Until -Seconds 30 { (Cop-Comments $n '*Development is done pending checks*').Count -ge 1 }) (((Get-FakeIssue $n).comments | ForEach-Object { $_.body.Substring(0, [Math]::Min(160, $_.body.Length)) }) -join ' | ')
        Check 'the red checks hand it back to Development to fix forward' (Wait-Until -Seconds 60 { (Cop-Comments $n '*Re-dispatching Development to fix forward*').Count -ge 1 })
        Check 'the fix-forward is a follow-up session of the same task' (Wait-Until -Seconds 60 { @((Cop-Tasks $n) | Where-Object { $_.id -ne $t1.id -and "$($_.body.prompt)" -match 'phase development' }).Count -gt 0 }) ((Cop-Tasks $n) | ConvertTo-Json -Compress -Depth 4)
        $t2 = @((Cop-Tasks $n) | Where-Object { $_.id -ne $t1.id })[0]
        Check 'started by a @copilot comment on the item''s pull request, in the owner task' ($t2.task -eq $t1.id -and $t2.via -eq 'comment' -and $t2.pr -eq $d1.number -and @((Cop-Tasks $n) | Select-Object -ExpandProperty task -Unique).Count -eq 1) ($t2 | ConvertTo-Json -Compress -Depth 4)
        Check 'its prompt names the phase, the pull request and what changed (the red checks)' ("$($t2.body.prompt)" -match "phase Development\. Work on PR #$($d1.number), branch $([regex]::Escape($a))" -and "$($t2.body.prompt)" -match 'What changed: .*checks failed') "$($t2.body.prompt)"
        Check 'the prompt asks for the integration tests and the migration tests of a database change' ("$($t2.body.prompt)" -match 'dotnet test\s+src/IntegrationTests' -and "$($t2.body.prompt)" -match 'migration test')
        # Copilot made its own branch from the item's branch and opened its draft against it (as the agent tasks API does), naming the old one
        $b = "copilot/thing-fix-$n"
        [void](Fake 'push-branch' @{ branch = $b; files = @('src/App/Thing.cs', 'src/UnitTests/ThingTests.cs') })
        $d2 = Draft $b $a "Fixes the failing check of #$($d1.number)."
        # the gateway's own resolution (item-pr, as the owner watch, the settle and the sweep run it) adopts the stacked successor before pr_open:
        # the order CI hit now and then (pr_open then takes its same-branch return), made to happen on every run
        function ItemPr { Invoke-RestMethod "http://127.0.0.1:$($script:ApiPort)/api/v1/verbs/item-pr" -Method POST -Headers @{ Authorization = 'Bearer test-token' } -ContentType 'application/json' -Body (@{ item = $n } | ConvertTo-Json) -SkipHttpErrorCheck }
        Check 'the gateway''s resolution adopts the stacked draft before pr_open' (Wait-Until -Seconds 30 { $ip = ItemPr; $null -ne $ip -and $null -ne $ip.PSObject.Properties['number'] -and [int]$ip.number -eq [int]$d2.number }) (ItemPr | ConvertTo-Json -Compress -Depth 4)
        $r2 = OpenPr $b
        Check 'pr_open with the task''s branch makes its draft the item''s pull request' ($r2.ok -and $r2.pullRequest.number -eq $d2.number -and $r2.pullRequest.branch -eq $b) ($r2 | ConvertTo-Json -Compress -Depth 4)
        $p2 = FakePr $d2.number; $p1 = FakePr $d1.number
        Check 'the new pull request is retargeted to master and ready, its title naming the item' ($p2.open -and $p2.base -eq 'master' -and -not $p2.draft -and "$($p2.title)" -match "#$n") ($p2 | ConvertTo-Json -Compress -Depth 3)
        Check 'the superseded pull request is closed with a comment naming the new one' (-not $p1.open -and -not $p1.merged -and @($p1.comments | Where-Object { "$($_.body)" -match "Superseded by #$($d2.number)" }).Count -eq 1) ($p1 | ConvertTo-Json -Compress -Depth 3)
        Check 'and its branch is deleted' (@((Fake 'state').deletedRefs) -contains "heads/$a") ((Fake 'state').deletedRefs | ConvertTo-Json -Compress)
        Check 'the item has exactly one open pull request' (@((Fake 'state').prs | Where-Object { $_.open -and "$($_.ref)" -like "copilot/thing*-$n" }).Count -eq 1)
        # a later task that pushed to the item's branch and also left a draft of its own against it: pr_open closes the extra draft
        $c = "copilot/thing-extra-$n"
        [void](Fake 'push-branch' @{ branch = $c; files = @('src/App/Extra.cs') })
        $d3 = Draft $c $b 'x'
        $r3 = OpenPr $b
        Check 'pr_open with the item''s branch keeps the item''s pull request' ($r3.ok -and $r3.pullRequest.number -eq $d2.number) ($r3 | ConvertTo-Json -Compress -Depth 4)
        Check 'and closes the extra draft the task created, deleting its branch' (-not (FakePr $d3.number).open -and @((Fake 'state').deletedRefs) -contains "heads/$c" -and (FakePr $d2.number).open)
        # a branch that does not hold the item's pull request's commits is refused (a task that started over from master)
        $x = "copilot/thing-over-$n"
        [void](Fake 'push-branch' @{ branch = $x; files = @('src/App/Over.cs') })
        $before = @((Fake 'state').prs).Count
        [void](Fake 'fault' @{ match = "compare/.*\.\.\.$([regex]::Escape($x))$"; status = 200; body = '{"status":"diverged","ahead_by":1,"behind_by":1}' })
        $r4 = OpenPr $x
        Check 'a branch without the item''s pull request''s commits is refused, nothing is opened or closed' ($r4.code -eq 'not-on-item-branch' -and @((Fake 'state').prs).Count -eq $before -and (FakePr $d2.number).open) ($r4 | ConvertTo-Json -Compress)
    } finally { Cop-Cleanup @($n) }
}

# The scenario above makes the gateway's resolution adopt the stacked draft before pr_open. Here pr_open comes first, always: the task's new
# draft targets master, so the resolution (which follows drafts stacked on the owner's branch) cannot find it, and pr_open takes it over.
'copilot: pr_open takes over a task''s new draft on master that names the item''s pull request: the draft is the item''s pull request, titled for the item, and the old one is superseded with the fix [full]' = {
    $n = 0
    try {
        $n = New-Item-Fake 'Copilot pr_open first' 'Change src/App/First.cs.' @('factory worker', 'Auto Merge', 'env:tdd') 'Development'
        Check 'a Development task is created' (Wait-Until -Seconds 30 { (Cop-DevTasks $n).Count -gt 0 })
        $a = "copilot/first-$n"
        [void](Fake 'push-branch' @{ branch = $a; files = @('src/App/First.cs') })
        $d1 = Cop-Draft $a 'master'
        $r1 = Cop-OpenPr $n $a
        Check 'pr_open takes over the first draft' ($r1.ok -and $r1.pullRequest.number -eq $d1.number) ($r1 | ConvertTo-Json -Compress -Depth 4)
        $b = "copilot/first-fix-$n"
        [void](Fake 'push-branch' @{ branch = $b; files = @('src/App/First.cs', 'src/UnitTests/FirstTests.cs') })
        $d2 = Cop-Draft $b 'master' '[WIP] Thing' "Fixes the failing check of #$($d1.number)."
        $r2 = Cop-OpenPr $n $b
        Check 'pr_open with the task''s new branch makes its draft the item''s pull request' ($r2.ok -and $r2.pullRequest.number -eq $d2.number -and $r2.pullRequest.branch -eq $b) ($r2 | ConvertTo-Json -Compress -Depth 4)
        $p2 = Cop-FakePr $d2.number; $p1 = Cop-FakePr $d1.number
        Check 'the new pull request is on master and ready, its title naming the item' ($p2.open -and $p2.base -eq 'master' -and -not $p2.draft -and "$($p2.title)" -match "(^|[^0-9])#$n([^0-9]|$)") ($p2 | ConvertTo-Json -Compress -Depth 3)
        Check 'pr_open supersedes the old pull request: closed with the comment naming the new one and the fix' (-not $p1.open -and -not $p1.merged -and @($p1.comments | Where-Object { "$($_.body)" -match "Superseded by #$($d2.number), the pull request of #$n now: it holds this branch's commits and the fix\." }).Count -eq 1) ($p1 | ConvertTo-Json -Compress -Depth 3)
        Check 'and its branch is deleted' (@((Fake 'state').deletedRefs) -contains "heads/$a") ((Fake 'state').deletedRefs | ConvertTo-Json -Compress)
        Check 'the state records the new pull request' (Wait-Until -Seconds 10 { $sx = Get-ItemState $n; [int]$sx.pr.number -eq [int]$d2.number -and "$($sx.pr.branch)" -eq $b }) ((Get-ItemState $n) | ConvertTo-Json -Compress -Depth 5)
    } finally { Cop-Cleanup @($n) }
}

'copilot: Development tasks that end their session without finishing the phase are launched at most api.copilot.maxLaunchesPerPhase (3) times in a row, then the item is parked, not relaunched' = {
    $n = 0
    try {
        $n = New-Item-Fake 'Copilot relaunch loop' 'Change src/App/Loop.cs.' @('factory worker', 'Auto Merge', 'env:tdd') 'Development'
        foreach ($k in 1..3) {
            Check "launch $k is created" (Wait-Until -Seconds 60 { (Cop-Tasks $n).Count -ge $k }) ((Cop-Tasks $n) | ConvertTo-Json -Compress -Depth 4)
            $tk = (Cop-Tasks $n)[$k - 1]
            # the agent connects, accepts the item and ends its session without finishing the phase (no failure is counted for that)
            $r1 = (Cop-R1 $tk)
            $cur = ''
            Check "launch $k gets its offer" (Wait-Until -Seconds 30 { $e = Mcp-Events $r1 $cur 5; @($e.events | Where-Object { $_.event -eq 'item.assigned' -and $_.item -eq $n }).Count -gt 0 })
            [void](Mcp-Call 'item_accept' @{ item = $n } $r1)
            [void](Mcp-Call 'session_end' @{ reason = 'stopped without finishing' } $r1)
            [void](Fake 'copilot-task' @{ id = $tk.id; state = 'completed' })
            Check "launch $k ends" (Wait-Until -Seconds 20 { @((Cop-Status).last | Where-Object { $_.execution -eq $tk.id }).Count -eq 1 })
        }
        Check 'the item is parked after the third launch' (Wait-Until -Seconds 40 { @(Get-ChildItem $script:Work -Filter 'gateway*.out' | Select-String -SimpleMatch "`"runtime`":`"park`"" | Where-Object { "$_" -match "`"item`":$n," -and "$_" -match 'runtime-failed Development' }).Count -gt 0 })
        Start-Sleep -Seconds 8
        Check 'and no fourth task is created' ((Cop-Tasks $n).Count -eq 3) ((Cop-Tasks $n) | ConvertTo-Json -Compress -Depth 4)
    } finally { Cop-Cleanup @($n) }
}

'copilot: CI release: the gateway re-runs a held (action_required) workflow run of the item''s copilot/ pull request head with its App, once; a head mismatch and another env''s item are skipped; a 403 is surfaced as an owner action' = {
    $n = 0; $m = 0
    try {
        $h = @{ Authorization = 'Bearer fake-operator' }
        $gh = "http://127.0.0.1:$($script:FakePort)/repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders"
        function Held([long]$Id) { @((Fake 'held-runs').runs | Where-Object { $_.id -eq $Id })[0] }
        function CiStatus { (Cop-Status).ciRelease }
        $n = New-Item-Fake 'Copilot CI release item' 'Change src/App/Ci.cs.' $CopLabels 'Development'
        $br = "copilot/ci-$n"
        [void](Fake 'push-branch' @{ branch = $br; files = @('src/App/Ci.cs') })
        $d = Invoke-RestMethod "$gh/pulls" -Method POST -Headers $h -ContentType 'application/json' -Body (@{ title = '[WIP] Ci'; head = $br; base = 'master'; body = 'x'; draft = $true } | ConvertTo-Json)
        $r = Invoke-RestMethod "http://127.0.0.1:$($script:ApiPort)/api/v1/verbs/open-pr" -Method POST -Headers @{ Authorization = 'Bearer test-token' } -ContentType 'application/json' -Body (@{ item = $n; head = $br; title = 'Ci' } | ConvertTo-Json) -SkipHttpErrorCheck
        Check 'pr_open makes the copilot/ draft the item''s pull request' ($r.ok -and $r.pullRequest.number -eq $d.number) ($r | ConvertTo-Json -Compress -Depth 4)
        Check 'status shows the CI release, on' ((CiStatus).enabled -eq $true) ((Cop-Status) | ConvertTo-Json -Compress -Depth 3)
        $verb = Invoke-WebRequest "http://127.0.0.1:$($script:ApiPort)/api/v1/verbs/ci-release" -Method POST -Headers @{ Authorization = 'Bearer test-token' } -ContentType 'application/json' -Body '{}' -SkipHttpErrorCheck
        Check 'the rule verb is the gateway''s own: refused over HTTP' ([int]$verb.StatusCode -eq 400)
        # a head mismatch: a run of an older commit of the branch
        $old = Fake 'held-run' @{ branch = $br; sha = ('ab' * 20) }
        # another environment's item with its own copilot/ pull request
        $m = New-Item-Fake 'uat CI release item' 'x' @('factory worker', 'Auto Merge', 'runtime:copilot-cloud', 'env:uat') 'Development'
        $ub = "copilot/uat-$m"
        [void](Fake 'push-branch' @{ branch = $ub; files = @('src/App/Uat.cs') })
        [void](Invoke-RestMethod "$gh/pulls" -Method POST -Headers $h -ContentType 'application/json' -Body (@{ title = "Fix #$m"; head = $ub; base = 'master'; body = 'x' } | ConvertTo-Json))
        $other = Fake 'held-run' @{ branch = $ub }
        # the item's head, refused once by GitHub (the App lacks Actions: write), then released; sticky: still listed held after the re-run
        [void](Fake 'fault' @{ match = "^POST .*/actions/runs/[0-9]+/rerun$"; status = 403; count = 1; body = '{"message":"Resource not accessible by integration"}' })
        $run = Fake 'held-run' @{ branch = $br; sticky = $true }
        Check 'a 403 is surfaced in status as an owner action' (Wait-Until -Seconds 45 { $c = CiStatus; $c.refused -ge 1 -and $c.permissionMissing -and "$($c.ownerAction)" -match 'Actions: Read & write' -and "$($c.lastError)" -match '403' }) ((CiStatus) | ConvertTo-Json -Compress)
        Check 'and nothing was re-run by another credential' ((Held $run.id).reruns -eq 0)
        Check 'the held run of the item''s head is released after the retry' (Wait-Until -Seconds 120 { (Held $run.id).reruns -eq 1 }) ((Fake 'held-runs') | ConvertTo-Json -Compress -Depth 4)
        Check 'status counts the release and clears the owner action' (Wait-Until -Seconds 10 { $c = CiStatus; $c.released -ge 1 -and -not $c.permissionMissing }) ((CiStatus) | ConvertTo-Json -Compress)
        Check 'a 403 posts one neutral owner-action comment on the item' (Wait-Until -Seconds 30 { @((Get-FakeIssue $n).comments | Where-Object { "$($_.body)" -match "owner action: the gateway could not release workflow run $($run.id)" }).Count -eq 1 })
        # (the state comment is machine-read and holds the release in journal.ciReleased as soon as it is recorded, which the engine v2 does in the call; it is no comment on the item for a person)
        Check 'the successful release posts no issue comment' (@((Get-FakeIssue $n).comments | Where-Object { "$($_.body)" -notmatch '^<!-- factory-state:' -and "$($_.body)" -match 'GitHub held it|released workflow run' }).Count -eq 0) ((Get-FakeIssue $n).comments | ConvertTo-Json -Compress -Depth 3)
        Check 'the release is recorded in status (released, lastRelease)' ((CiStatus).released -ge 1 -and "$((CiStatus).lastRelease)" -ne '') ((CiStatus) | ConvertTo-Json -Compress)
        Start-Sleep -Seconds 25
        Check 'idempotent: the run, still listed held, is not re-run again' ((Held $run.id).reruns -eq 1) ((Held $run.id) | ConvertTo-Json -Compress)
        Check 'a run whose head_sha is not the pull request''s head is not released' ((Held $old.id).reruns -eq 0)
        Check 'a run of another environment''s item is not released' ((Held $other.id).reruns -eq 0)
        Check 'the gateway log names no token' (@(Get-ChildItem $script:Work -Filter 'gateway*.out' | Select-String -SimpleMatch 'fake-copilot').Count -eq 0)
    } finally { Cop-Cleanup @($n, $m) }
}

'copilot: CI release: passes run with no candidate item; an unlabelled item routed to copilot-cloud by runtimes.routing has its held run released' = {
    $n = 0
    try {
        $h = @{ Authorization = 'Bearer fake-operator' }
        $gh = "http://127.0.0.1:$($script:FakePort)/repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders"
        $p0 = (Cop-Status).ciRelease.passes
        Check 'passes run every interval, candidates or not' (Wait-Until -Seconds 40 { (Cop-Status).ciRelease.passes -gt $p0 }) ((Cop-Status).ciRelease | ConvertTo-Json -Compress)
        $n = New-Item-Fake 'Copilot routed CI item' 'Change src/App/Routed.cs.' @('factory worker', 'Auto Merge') 'Development'
        $br = "copilot/routed-$n"
        [void](Fake 'push-branch' @{ branch = $br; files = @('src/App/Routed.cs') })
        $d = Invoke-RestMethod "$gh/pulls" -Method POST -Headers $h -ContentType 'application/json' -Body (@{ title = '[WIP] Routed'; head = $br; base = 'master'; body = 'x'; draft = $true } | ConvertTo-Json)
        $r = Invoke-RestMethod "http://127.0.0.1:$($script:ApiPort)/api/v1/verbs/open-pr" -Method POST -Headers @{ Authorization = 'Bearer test-token' } -ContentType 'application/json' -Body (@{ item = $n; head = $br; title = 'Routed' } | ConvertTo-Json) -SkipHttpErrorCheck
        Check 'pr_open records the copilot/ pull request' ($r.ok -and $r.pullRequest.number -eq $d.number) ($r | ConvertTo-Json -Compress -Depth 4)
        $run = Fake 'held-run' @{ branch = $br }
        Check 'the held run of the routed item is released' (Wait-Until -Seconds 60 { @((Fake 'held-runs').runs | Where-Object { $_.id -eq $run.id -and $_.reruns -eq 1 }).Count -eq 1 }) ((Fake 'held-runs') | ConvertTo-Json -Compress -Depth 4)
        # the routine release is recorded in the item's state, not as a comment: the journal verb holds the item lock only once (#376: it locked
        # the item a second time, waited 45 s for itself, recorded nothing and held the item's lock so long that a later scenario's session end
        # was not seen in time)
        Check 'the release is recorded in the item''s state (journal.ciReleased)' (Wait-Until -Seconds 20 { $s0 = Get-ItemState $n; $js = if ($s0 -and $s0.ContainsKey('journal')) { $s0.journal } else { $null }; $js -and $js.ContainsKey('ciReleased') -and @($js.ciReleased | Where-Object { "$_" -match "released workflow run $($run.id) of PR #$($d.number)" }).Count -eq 1 }) ((Get-ItemState $n).journal | ConvertTo-Json -Compress -Depth 4)
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

'copilot: the gateway owns Development''s end: a task that ends after pushing (no pr_open, no item_complete, even a reported failure) has its draft taken over by the branch of its artifacts; the item waits for its checks in the gateway and moves on when they are green; no second Development task' = {
    $n = 0
    try {
        $n = New-Item-Fake 'Copilot settle item' 'Change src/App/Settle.cs and its unit test.' @('factory worker', 'Auto Merge', 'env:tdd') 'Development'
        Check 'a Development task is created' (Wait-Until -Seconds 30 { (Cop-DevTasks $n).Count -gt 0 })
        $t = (Cop-DevTasks $n)[0]
        $r1 = (Cop-R1 $t)
        $cur = ''
        Check 'the agent gets its offer' (Wait-Until -Seconds 30 { $e = Mcp-Events $r1 $cur 5; @($e.events | Where-Object { $_.event -eq 'item.assigned' -and $_.item -eq $n }).Count -gt 0 })
        [void](Mcp-Call 'item_accept' @{ item = $n } $r1)
        $br = "copilot/settle-$n"
        [void](Fake 'push-branch' @{ branch = $br; files = @('src/App/Settle.cs', 'src/UnitTests/SettleTests.cs'); delaySeconds = 6 })
        $d = Cop-Draft $br 'master' 'Add Settle'
        # as #395: the agent pushed, then reported a failure (a tool refused after its push) and ended its session
        $f = Mcp-Call 'item_complete' @{ item = $n; succeeded = $false; comment = 'checks still pending, cannot finish' } $r1
        Check 'the agent''s failure report parks the item worker-failure' (-not $f.isError -and (Cop-Comments $n '*worker reported **FAILURE***').Count -eq 1) ($f | ConvertTo-Json -Compress -Depth 5)
        [void](Mcp-Call 'session_end' @{ reason = 'done' } $r1)
        [void](Fake 'copilot-task' @{ id = $t.id; state = 'completed'; headRef = $br })
        Check 'the gateway takes over the task''s draft (title naming the item, ready for review)' (Wait-Until -Seconds 60 { $p = Cop-FakePr $d.number; $p.open -and -not $p.draft -and "$($p.title)" -match "#$n" }) (Cop-FakePr $d.number | ConvertTo-Json -Compress -Depth 3)
        Check 'and records Development as done pending checks, over the agent''s failure report' (Wait-Until -Seconds 20 { (Cop-Comments $n '*Development is done pending checks*the Copilot task ended*').Count -eq 1 })
        Check 'green checks move the item to Functional Testing' (Wait-Until -Seconds 60 { (Get-FakeIssue $n).column -eq 'Functional Testing' }) "$((Get-FakeIssue $n).column)"
        Check 'no second Development task was started' ((Cop-DevTasks $n).Count -eq 1) ((Cop-Tasks $n) | ForEach-Object { "$($_.id) $($_.state)" }) -join '; '
        Check 'Functional Testing gets its task, from the pull request''s branch' (Wait-Until -Seconds 60 { @((Cop-Tasks $n) | Where-Object { "$($_.body.prompt)" -match 'phase functional-testing' -and $_.body.base_ref -eq $br }).Count -eq 1 })
    } finally { Cop-Cleanup @($n) }
}

'copilot: the credit guard ends a task over api.copilot.maxCreditsPerTask and parks its item credit-guard; after an unpark, an item whose pull request already carries the change starts no new Development task: it waits for its checks' = {
    $n = 0
    try {
        $n = New-Item-Fake 'Copilot credit item' 'Change src/App/Credit.cs.' @('factory worker', 'Auto Merge', 'env:tdd') 'Development'
        Check 'a Development task is created' (Wait-Until -Seconds 30 { (Cop-DevTasks $n).Count -gt 0 })
        $t = (Cop-DevTasks $n)[0]
        $br = "copilot/credit-$n"
        [void](Fake 'push-branch' @{ branch = $br; files = @('src/App/Credit.cs'); profile = 'stuck' })
        $d = Cop-Draft $br 'master' "Credit (#$n)"
        $r = Cop-OpenPr $n $br
        Check 'pr_open takes over the draft' ($r.ok -and $r.pullRequest.number -eq $d.number) ($r | ConvertTo-Json -Compress -Depth 4)
        [void](Fake 'copilot-task' @{ id = $t.id; credits = 10000000001; headRef = $br })
        Check 'the guard logs the task over the limit' (Wait-Until -Seconds 20 { @(Get-ChildItem $script:Work -Filter 'gateway*.out' | Select-String -SimpleMatch '"runtime":"copilot-credit-guard"' | Where-Object { "$_" -match $t.id }).Count -eq 1 })
        Check 'the item is parked credit-guard' (Wait-Until -Seconds 20 { (Cop-Comments $n '*plant event=parked*').Count -ge 1 -or (Cop-Comments $n '*credit-guard*').Count -ge 1 }) (((Get-FakeIssue $n).comments | ForEach-Object { $_.body.Substring(0, [Math]::Min(160, $_.body.Length)) }) -join ' | ')
        Check 'its end is seen, and the guard''s park is not overridden by a settle' (Wait-Until -Seconds 20 { @((Cop-Status).last | Where-Object { $_.execution -eq $t.id }).Count -eq 1 }) ((Cop-Status).last | ConvertTo-Json -Compress -Depth 6)
        Start-Sleep -Seconds 5
        Check 'no Development task follows while it is parked' ((Cop-DevTasks $n).Count -eq 1 -and (Cop-Comments $n '*Development is done pending checks*').Count -eq 0)
        $u = Invoke-RestMethod "http://127.0.0.1:$($script:ApiPort)/api/v1/items/$n/unpark" -Method POST -Headers @{ Authorization = 'Bearer test-token' } -ContentType 'application/json' -Body (@{ reason = 'credit spike looked at' } | ConvertTo-Json) -SkipHttpErrorCheck
        Check 'the operator unparks it' ($u.ok) ($u | ConvertTo-Json -Compress -Depth 4)
        Check 'the next offer of Development settles instead of launching: the pull request waits for its checks' (Wait-Until -Seconds 60 { (Cop-Comments $n '*Development is done pending checks*already has its pull request*').Count -eq 1 }) (((Get-FakeIssue $n).comments | ForEach-Object { $_.body.Substring(0, [Math]::Min(160, $_.body.Length)) }) -join ' | ')
        Start-Sleep -Seconds 5
        Check 'and no new Development task was created' ((Cop-DevTasks $n).Count -eq 1) ((Cop-Tasks $n) | ForEach-Object { "$($_.id) $($_.state)" }) -join '; '
    } finally { Cop-Cleanup @($n) }
}

'copilot: a fix-forward draft that GitHub will not retarget (part of a stack, 422) is closed and the item''s pull request is opened on its branch, superseding the old one' = {
    $n = 0
    try {
        $n = New-Item-Fake 'Copilot stack item' 'Change src/App/Stack.cs.' @('factory worker', 'Auto Merge', 'env:tdd') 'Development'
        Check 'a Development task is created' (Wait-Until -Seconds 30 { (Cop-DevTasks $n).Count -gt 0 })
        $a = "copilot/stack-$n"
        [void](Fake 'push-branch' @{ branch = $a; files = @('src/App/Stack.cs'); profile = 'stuck' })
        $d1 = Cop-Draft $a 'master'
        Check 'pr_open takes over the first draft' ((Cop-OpenPr $n $a).pullRequest.number -eq $d1.number)
        $b = "copilot/stack-fix-$n"
        [void](Fake 'push-branch' @{ branch = $b; files = @('src/App/Stack.cs', 'src/UnitTests/StackTests.cs'); profile = 'stuck' })
        $d2 = Cop-Draft $b $a "Fix (#$n)"
        [void](Fake 'fault' @{ match = "^PATCH /repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders/pulls/$($d2.number)$"; status = 422; body = '{"message":"Cannot change the base branch because the pull request is part of a stack"}' })
        $r = Cop-OpenPr $n $b
        Check 'pr_open answers a pull request on the task''s branch' ($r.ok -and $r.pullRequest.branch -eq $b -and $r.pullRequest.number -ne $d2.number) ($r | ConvertTo-Json -Compress -Depth 4)
        $np = Cop-FakePr ([int]$r.pullRequest.number)
        Check 'the new pull request targets master' ($np.open -and $np.base -eq 'master') ($np | ConvertTo-Json -Compress -Depth 3)
        Check 'the stacked draft is closed, its branch kept' (-not (Cop-FakePr $d2.number).open -and @((Fake 'state').deletedRefs) -notcontains "heads/$b")
        Check 'the superseded pull request is closed' (-not (Cop-FakePr $d1.number).open -and @((Fake 'state').deletedRefs) -contains "heads/$a")
    } finally { Cop-Cleanup @($n) }
}

'copilot: a Copilot pull request goes from a PASS to the gateway''s merge; GitHub''s refusal of a stacked pull request (403, use the asynchronous endpoint) is merged through merge-async' = {
    $n = 0
    try {
        $n = New-Item-Fake 'Copilot merge item' 'Change src/App/Merge.cs.' @('factory worker', 'Auto Merge') 'Development'
        Check 'a Development task is created' (Wait-Until -Seconds 30 { (Cop-DevTasks $n).Count -gt 0 })
        $t = (Cop-DevTasks $n)[0]
        $br = "copilot/merge-$n"
        [void](Fake 'push-branch' @{ branch = $br; files = @('src/App/Merge.cs') })
        $d = Cop-Draft $br 'master' "Merge (#$n)"
        [void](Fake 'copilot-task' @{ id = $t.id; state = 'completed'; headRef = $br })
        Check 'the item reaches Functional Testing' (Wait-Until -Seconds 60 { (Get-FakeIssue $n).column -eq 'Functional Testing' }) "$((Get-FakeIssue $n).column)"
        Check 'a Functional Testing task is created' (Wait-Until -Seconds 60 { @((Cop-Tasks $n) | Where-Object { "$($_.body.prompt)" -match 'phase functional-testing' }).Count -eq 1 })
        $ft = @((Cop-Tasks $n) | Where-Object { "$($_.body.prompt)" -match 'phase functional-testing' })[0]
        $r1 = (Cop-R1 $ft)
        $cur = ''
        Check 'the FT agent gets its offer' (Wait-Until -Seconds 30 { $e = Mcp-Events $r1 $cur 5; @($e.events | Where-Object { $_.event -eq 'item.assigned' -and $_.item -eq $n }).Count -gt 0 })
        [void](Mcp-Call 'item_accept' @{ item = $n } $r1)
        [void](Fake 'fault' @{ match = "^PUT /repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders/pulls/$($d.number)/merge$"; status = 403; count = 5; body = '{"message":"Merging stacked PRs via this endpoint is not supported. Use the asynchronous merge endpoint instead."}' })
        $rv = Mcp-Call 'item_record_review' @{ item = $n; column = 'Functional Testing'; role = 'reviewer'; text = "Criteria exercised.`nREVIEW: COMPLETE" } $r1
        $v = Mcp-Call 'item_validation_verdict' @{ item = $n; passed = $true; summary = 'The criterion is met.'; pr = $d.number; review_ref = "$($rv.body.review_ref)"; head_sha = "$((Cop-FakePr $d.number).sha)" } $r1
        Check 'the PASS verdict is recorded' (-not $v.isError) ($v | ConvertTo-Json -Compress -Depth 5)
        [void](Mcp-Call 'session_end' @{ reason = 'done' } $r1)
        Check 'the gateway merges the pull request through merge-async' (Wait-Until -Seconds 120 { (Cop-FakePr $d.number).merged }) (Cop-FakePr $d.number | ConvertTo-Json -Compress -Depth 3)
        Check 'and records the merge on the item' (Wait-Until -Seconds 30 { (Cop-Comments $n '*squash-merged PR #*').Count -ge 1 -or (Cop-Comments $n '*was merged as*').Count -ge 1 }) (((Get-FakeIssue $n).comments | ForEach-Object { $_.body.Substring(0, [Math]::Min(160, $_.body.Length)) }) -join ' | ')
    } finally { Cop-Cleanup @($n) }
}

'copilot: one task owns the item end to end: Technical Design opens the draft, Development and Functional Testing are follow-up sessions of the same task on that pull request, no other worker is offered the item, the draft is marked ready, and the owner is done only after the merge, a green master and the production delivery [full]' = {
    $n = 0
    try {
        $n = New-Item-Fake 'Copilot owned item' 'Change src/App/Own.cs and its unit test.' @('factory worker', 'Auto Merge', 'env:tdd')
        Check 'the Technical Design task is created and asks for the item''s draft pull request at once' (Wait-Until -Seconds 30 { (Cop-Tasks $n).Count -gt 0 })
        $t = (Cop-Tasks $n)[0]
        Check 'create_pull_request is set for the first session' ($t.body.create_pull_request -eq $true) ($t.body | ConvertTo-Json -Compress)
        Check 'the item names its owner task, once' (Wait-Until -Seconds 30 { (Cop-Comments $n '*Owner:** Copilot task*').Count -eq 1 }) (Cop-Brief $n)
        Check 'the item state records the owner' ((Get-ItemState $n).copilot_task.id -eq $t.id)
        $br = "copilot/own-$n"
        [void](Fake 'push-branch' @{ branch = $br; files = @('src/App/Own.cs') })
        $d = Cop-Draft $br 'master' '[WIP] Own'
        $r1 = Cop-Accept $t $n
        [void](Mcp-Call 'session_end' @{ reason = 'design done' } $r1)
        [void](Fake 'copilot-task' @{ id = $t.id; state = 'completed'; headRef = $br })
        Check 'the session ends; the item stays owned, no session runs' (Wait-Until -Seconds 30 { (Cop-Status).running -eq 0 -and @((Cop-Status).owned | Where-Object { $_.item -eq $n -and $_.task -eq $t.id }).Count -eq 1 }) ((Cop-Status) | ConvertTo-Json -Compress -Depth 5)
        [void](Fake 'move' @{ number = $n; column = 'Development' })
        Check 'Development is a follow-up session of the same task: a @copilot comment on its draft' (Wait-Until -Seconds 60 { @((Cop-Sessions $n $t.id 'development') | Where-Object { $_.via -eq 'comment' -and $_.pr -eq $d.number }).Count -eq 1 }) ((Cop-Tasks $n) | ConvertTo-Json -Compress -Depth 4)
        $s1 = (Cop-Sessions $n $t.id 'development')[0]
        Check 'the follow-up prompt names the phase, the pull request, the ownership and the one-step rule' ("$($s1.body.prompt)" -match "owns #$n" -and "$($s1.body.prompt)" -match "PR #$($d.number), branch $([regex]::Escape($br))" -and "$($s1.body.prompt)" -match 'never poll or wait for CI') "$($s1.body.prompt)"
        Check 'the follow-up carries a fresh one-time code of its own' ((Cop-Code $s1) -ne '' -and (Cop-Code $s1) -ne (Cop-Code $t))
        $x1 = Cop-Exchange (Cop-Code $s1) (Cop-Bootstrap)
        Check 'which exchanges for the new session''s r1' ($x1.status -eq 200 -and "$($x1.body.name)" -like "cop-$n-*")
        $w1 = "$($x1.body.token)"; $cur1 = ''
        [void](Wait-Until -Seconds 30 { $e = Mcp-Events $w1 $cur1 5; @($e.events | Where-Object { $_.event -eq 'item.assigned' -and $_.item -eq $n }).Count -gt 0 })
        [void](Mcp-Call 'item_accept' @{ item = $n } $w1)
        [void](Fake 'push-branch' @{ branch = $br; files = @('src/App/Own.cs', 'src/UnitTests/OwnTests.cs') })
        $op = Cop-OpenPr $n $br
        Check 'pr_open takes the draft over as the item''s pull request' ($op.ok -and $op.pullRequest.number -eq $d.number) ($op | ConvertTo-Json -Compress -Depth 4)
        Check 'and marks the draft ready for review: no draft is ever left for a person' (-not (Cop-FakePr $d.number).draft) (Cop-FakePr $d.number | ConvertTo-Json -Compress -Depth 3)
        [void](Mcp-Call 'session_end' @{ reason = 'pushed' } $w1)
        [void](Fake 'copilot-task' @{ id = $s1.id; state = 'completed' })
        Check 'the gateway waits for the checks and moves the item to Functional Testing' (Wait-Until -Seconds 90 { (Get-FakeIssue $n).column -eq 'Functional Testing' }) "$((Get-FakeIssue $n).column)"
        Check 'Functional Testing is the next session of the same task' (Wait-Until -Seconds 60 { @((Cop-Sessions $n $t.id 'functional-testing') | Where-Object { $_.via -eq 'comment' -and $_.pr -eq $d.number }).Count -eq 1 }) ((Cop-Tasks $n) | ConvertTo-Json -Compress -Depth 4)
        $s2 = (Cop-Sessions $n $t.id 'functional-testing')[0]
        $w2 = Cop-Accept $s2 $n
        $rv = Mcp-Call 'item_record_review' @{ item = $n; column = 'Functional Testing'; role = 'reviewer'; text = "Criteria exercised.`nREVIEW: COMPLETE" } $w2
        $v = Mcp-Call 'item_validation_verdict' @{ item = $n; passed = $true; summary = 'The criterion is met.'; pr = $d.number; review_ref = "$($rv.body.review_ref)"; head_sha = "$((Cop-FakePr $d.number).sha)" } $w2
        Check 'the PASS verdict is recorded' (-not $v.isError) ($v | ConvertTo-Json -Compress -Depth 5)
        [void](Mcp-Call 'session_end' @{ reason = 'verdict' } $w2)
        [void](Fake 'copilot-task' @{ id = $s2.id; state = 'completed' })
        Check 'the gateway merges the pull request' (Wait-Until -Seconds 120 { (Cop-FakePr $d.number).merged })
        Check 'master goes green for the merge' (Wait-Until -Seconds 120 { (Cop-Comments $n '*master is GREEN*').Count -ge 1 }) (Cop-Brief $n)
        # the Release Queue sees the production deployment of the release that carries the merge: the fake's merge makes that release and its prod
        # deployment succeeds at once (releaseMode auto), so the settle's own path always delivers: a sweep for the candidate, one for the delivery.
        # (No harness write stands in for it: one that raced the gateway's own delivery made the scenario's writes depend on the timing, issue #386.)
        Check 'the Release Queue sees the production delivery of the merge''s release' (Wait-Until -Seconds 90 { (Cop-Comments $n '*Delivered to `*`*Prod`*`**').Count -ge 1 }) (Cop-Brief $n)
        # the owner watch reads the item at most every max(30 s, 3 x ciReleaseSeconds) and closes the owner once the settle recorded done_at
        Check 'delivered to production: the owner is done, with the task''s link' (Wait-Until -Seconds 90 { (Cop-Comments $n '*Owner done:** Copilot task*').Count -eq 1 }) (Cop-Brief $n)
        $cs = @((Get-FakeIssue $n).comments | ForEach-Object { "$($_.body)" })
        $gi = [array]::FindIndex([string[]]$cs, [Predicate[string]]{ param($x) $x -match 'master is GREEN' }); $oi = [array]::FindIndex([string[]]$cs, [Predicate[string]]{ param($x) $x -like '*Owner done:*' })
        $di = [array]::FindIndex([string[]]$cs, [Predicate[string]]{ param($x) $x -match 'Delivered to \*\*Prod\*\*' })
        Check 'the owner is done only after master is green and the production delivery' ($gi -ge 0 -and $oi -gt $gi -and $di -ge 0 -and $oi -gt $di -and "$($cs[$oi])" -match 'delivered to production' -and "$($cs[$oi])" -match 'ca-cmdemo1-prod-ui') "green $gi delivered $di done $oi $($cs[$oi])"
        Check 'the done comment carries the session link' ("$($cs[$oi])" -match "https://github\.com/clearmeasure-aisf-sample-apps/cmdemo1-workorders/tasks/$([regex]::Escape($t.id))")
        Check 'one task, three sessions, never a second task' (@((Cop-Tasks $n) | Select-Object -ExpandProperty task -Unique).Count -eq 1 -and (Cop-Tasks $n).Count -eq 3) ((Cop-Tasks $n) | ForEach-Object { "$($_.id) $($_.via)" }) -join '; '
        Check 'no worker but the task''s sessions was offered the item' (Cop-NotOffered $n) (Cop-Brief $n)
        Check 'status: the item is no longer owned' (Wait-Until -Seconds 20 { @((Cop-Status).owned | Where-Object { $_.item -eq $n }).Count -eq 0 })
    } finally { Cop-Cleanup @($n) }
}

'copilot: an FT FAIL sends the item back to Development as a follow-up session of the owner task, naming the findings [full]' = {
    $n = 0
    try {
        $n = New-Item-Fake 'Copilot FT fail' 'Change src/App/Ft.cs.' @('factory worker', 'Auto Merge', 'env:tdd') 'Development'
        Check 'a Development task is created' (Wait-Until -Seconds 30 { (Cop-DevTasks $n).Count -gt 0 })
        $t = (Cop-DevTasks $n)[0]
        $br = "copilot/ft-$n"
        [void](Fake 'push-branch' @{ branch = $br; files = @('src/App/Ft.cs') })
        $d = Cop-Draft $br 'master' '[WIP] Ft'
        $w = Cop-Accept $t $n
        [void](Cop-OpenPr $n $br)
        [void](Mcp-Call 'session_end' @{ reason = 'pushed' } $w)
        [void](Fake 'copilot-task' @{ id = $t.id; state = 'completed'; headRef = $br })
        Check 'Functional Testing follows as a session of the same task' (Wait-Until -Seconds 120 { (Cop-Sessions $n $t.id 'functional-testing').Count -eq 1 }) ((Cop-Tasks $n) | ConvertTo-Json -Compress -Depth 4)
        $s2 = (Cop-Sessions $n $t.id 'functional-testing')[0]
        $w2 = Cop-Accept $s2 $n
        $rv = Mcp-Call 'item_record_review' @{ item = $n; column = 'Functional Testing'; role = 'reviewer'; text = "The empty title is accepted.`nREVIEW: COMPLETE" } $w2
        $v = Mcp-Call 'item_validation_verdict' @{ item = $n; passed = $false; summary = 'FAIL: the empty title is accepted'; pr = $d.number; review_ref = "$($rv.body.review_ref)"; head_sha = "$((Cop-FakePr $d.number).sha)" } $w2
        Check 'the FAIL verdict is recorded' (-not $v.isError) ($v | ConvertTo-Json -Compress -Depth 5)
        [void](Mcp-Call 'session_end' @{ reason = 'verdict' } $w2)
        [void](Fake 'copilot-task' @{ id = $s2.id; state = 'completed' })
        Check 'the rework is a Development session of the same task on the same pull request' (Wait-Until -Seconds 90 { @((Cop-Sessions $n $t.id 'development') | Where-Object { $_.via -eq 'comment' -and $_.pr -eq $d.number }).Count -eq 1 }) ((Cop-Tasks $n) | ConvertTo-Json -Compress -Depth 4)
        $s3 = @((Cop-Sessions $n $t.id 'development') | Where-Object { $_.via -eq 'comment' })[0]
        Check 'its prompt names what changed: the findings' ("$($s3.body.prompt)" -match 'What changed: .*empty title') "$($s3.body.prompt)"
        Check 'still one task' (@((Cop-Tasks $n) | Select-Object -ExpandProperty task -Unique).Count -eq 1)
        Check 'no other worker was offered the item' (Cop-NotOffered $n) (Cop-Brief $n)
    } finally { Cop-Cleanup @($n) }
}

'copilot: a red master after the merge resumes the owner task once to fix forward; the owner is done only once master is green and the change is in production [full]' = {
    $n = 0
    try {
        $n = New-Item-Fake 'Copilot red master' 'Change src/App/Red.cs.' @('factory worker', 'Auto Merge', 'env:tdd')
        # (#415: 30 s met a loaded runner twice, and the next line then read the first of no task: "Index was outside the bounds of the array" and a golden difference; the wait is longer and the scenario stops here when no task came)
        $created = Wait-Until -Seconds 90 { (Cop-Tasks $n).Count -gt 0 }
        Check 'the owner task is created' $created (Cop-Brief $n)
        if (-not $created) { return }
        $t = (Cop-Tasks $n)[0]
        $br = "copilot/red-$n"
        [void](Fake 'push-branch' @{ branch = $br; files = @('src/App/Red.cs') })
        $d = Cop-Draft $br 'master' "Red (#$n)"
        $w = Cop-Accept $t $n
        [void](Mcp-Call 'session_end' @{ reason = 'done' } $w)
        [void](Fake 'copilot-task' @{ id = $t.id; state = 'completed'; headRef = $br })
        Check 'its session ends' (Wait-Until -Seconds 30 { (Cop-Status).running -eq 0 })
        # The item leaves its worker column at once, as it did before issue 415: the end takes the item back to the plan, and a sweep that still reads
        # it in Technical Design offers it to the next worker, whose launch is a running execution of the item. The owner watch skips an item while
        # one runs, so the master-fix resume (and later the delivery) never came: both runs of the pull request of #415 failed on that offer, one on
        # 'resumed' and one on 'the owner is done' (Brief: "ended ... offered to cop-... ... task-...#N" for Technical Design).
        [void](Fake 'move' @{ number = $n; column = 'Release Queue' })
        # The end counts the execution out of 'running' at its start; its claim release (a rule script that reads the item's state and saves it) and
        # its revoke follow for a few seconds (#376). The state written below must come after them and between two sweeps (issue 415): an edit that a
        # script of the end or a sweep read before and saves after is lost, and then master is never seen red and the task is never resumed.
        Check 'the end of its execution has run to its last step' (Wait-Until -Seconds 60 { Cop-EndsSettled })
        Wait-SweepGap -Fresh 1.0
        # the item was merged (here: written as the settle leaves it) and master went red for the merge commit
        $sha = 'abcdef1' + ('0' * 33)
        Set-ItemState $n { param($st) $st.Remove('claim'); $st.pr = @{ number = $d.number; branch = $br }; $st.merge = @{ sha = $sha; merged_at = (Get-Date).ToUniversalTime().ToString('o'); awaiting = 'master-broken'; rerun_attempted = $true }; $st.park = (New-TestPark 'master-broken' @{ failed = @('Build result'); sha = $sha }) }
        Check 'the owner task is resumed on its pull request to fix master forward' (Wait-Until -Seconds 60 { @((Cop-State).tasks | Where-Object { $_.task -eq $t.id -and $_.via -eq 'comment' -and $_.pr -eq $d.number -and "$($_.body.prompt)" -match 'master is red' }).Count -eq 1 }) ((Cop-State).tasks | ConvertTo-Json -Compress -Depth 4)
        $fx = @((Cop-State).tasks | Where-Object { $_.task -eq $t.id -and  "$($_.body.prompt)" -match 'master is red' })[0]
        Check 'its prompt names the failed check and asks for one pull request against master' ("$($fx.body.prompt)" -match 'Build result' -and "$($fx.body.prompt)" -match 'ONE pull request against master') "$($fx.body.prompt)"
        Start-Sleep -Seconds 25
        Check 'once per merge commit' (@((Cop-State).tasks | Where-Object { $_.task -eq $t.id -and  "$($_.body.prompt)" -match 'master is red' }).Count -eq 1)
        Check 'not done while master is red' ((Cop-Comments $n '*Owner done:*').Count -eq 0 -and @((Cop-Status).owned | Where-Object { $_.item -eq $n }).Count -eq 1)
        Wait-SweepGap -Fresh 1.0   # (the edits in the gap after a sweep: none reads the state before them and saves it after)
        Set-ItemState $n { param($st) $st.merge.green_at = (Get-Date).ToUniversalTime().ToString('o'); $st.merge.awaiting = 'release-candidate'; $st.merge.rc_deadline = (Get-Date).ToUniversalTime().AddHours(1).ToString('o'); $st.Remove('park') }
        Set-ItemState $n { param($st) $st.merge.done_at = (Get-Date).ToUniversalTime().ToString('o'); $st.merge.awaiting = $null }
        Check 'delivered to production: the owner is done' (Wait-Until -Seconds 60 { (Cop-Comments $n '*Owner done:** Copilot task*').Count -eq 1 }) (Cop-Brief $n)
        Check 'and the item is no longer owned' (Wait-Until -Seconds 20 { @((Cop-Status).owned | Where-Object { $_.item -eq $n }).Count -eq 0 })
    } finally { Cop-Cleanup @($n) }
}

'copilot: an item an earlier gateway ran one task per phase for adopts the task whose branch its pull request is on and resumes it there; no new task, no stacked draft [full]' = {
    $n = 0
    try {
        $n = New-Item-Fake 'Copilot migrated item' 'Change src/App/Mig.cs.' @('factory worker', 'Auto Merge', 'env:tdd') 'Release Queue'
        $br = "copilot/mig-$n"
        [void](Fake 'push-branch' @{ branch = $br; files = @('src/App/Mig.cs') })
        $d = Cop-Draft $br 'master' "Mig (#$n)"
        # the earlier gateway's Development task, on the pull request's branch
        $old = Invoke-RestMethod "http://127.0.0.1:$($script:FakePort)/agents/repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders/tasks" -Method POST -Headers @{ Authorization = 'Bearer fake-copilot' } -ContentType 'application/json' -Body (@{ prompt = 'an earlier gateway''s task'; base_ref = 'master'; create_pull_request = $true } | ConvertTo-Json)
        [void](Fake 'copilot-task' @{ id = $old.id; state = 'completed'; headRef = $br })
        $state = "<!-- factory-state:factory-worker v1 -->`nstate`n`n``````json`n" + (@{ item = $n; pr = @{ number = $d.number; branch = $br } } | ConvertTo-Json -Compress) + "`n```````n"
        [void](Invoke-RestMethod "$(Cop-Gh)/issues/$n/comments" -Method POST -Headers @{ Authorization = 'Bearer fake-operator' } -ContentType 'application/json' -Body (@{ body = $state } | ConvertTo-Json))
        $before = @((Cop-State).tasks | Select-Object -ExpandProperty task -Unique).Count
        [void](Fake 'move' @{ number = $n; column = 'Functional Testing' })
        Check 'Functional Testing resumes the earlier task on the item''s pull request' (Wait-Until -Seconds 60 { @((Cop-State).tasks | Where-Object { $_.task -eq $old.id -and $_.via -eq 'comment' -and $_.pr -eq $d.number -and "$($_.body.prompt)" -match 'phase functional-testing' }).Count -eq 1 }) ((Cop-State).tasks | ConvertTo-Json -Compress -Depth 4)
        Check 'no new task was created' (@((Cop-State).tasks | Select-Object -ExpandProperty task -Unique).Count -eq $before)
        Check 'the earlier task is the item''s owner now' (Wait-Until -Seconds 30 { (Cop-Comments $n "*Owner:** Copilot task*$($old.id)*").Count -eq 1 }) (Cop-Brief $n)
        Check 'status shows it' (@((Cop-Status).owned | Where-Object { $_.item -eq $n -and $_.task -eq $old.id }).Count -eq 1)
    } finally { Cop-Cleanup @($n) }
}

'copilot: a running owner session silent past the item check keeps its lease (its runtime''s deadline bounds it); after its session ends unfinished the same task is resumed on its pull request [full]' = {
    $n = 0
    # this scenario alone runs with a short item check (0.1 + 0.05 min): the gateway is restarted with it, and again without it at the end
    # the deadline (counted from the task's creation, before the accept) must end the session after the 12 s quiet window that starts at the accept:
    # 0.5 min leaves about 15 s for the task, the fake calls and the accept; 0.25 min left about 3 s and lost the race on a loaded runner
    $env:FACTORY_TIMING_staleItemMinutes = '0.1'; $env:FACTORY_TIMING_staleConfirmMinutes = '0.05'; $env:FACTORY_COPILOT_MAX_MINUTES = '0.5'; Restart-Gateway
    try {
        $n = New-Item-Fake 'Copilot quiet owner' 'Change src/App/Quiet.cs.' @('factory worker', 'Auto Merge', 'env:tdd') 'Development'
        Check 'a task is created' (Wait-Until -Seconds 30 { (Cop-DevTasks $n).Count -gt 0 })
        $t = (Cop-DevTasks $n)[0]
        $br = "copilot/quiet-$n"
        [void](Fake 'push-branch' @{ branch = $br; files = @('src/App/Quiet.cs') })
        $d = Cop-Draft $br 'master' '[WIP] Quiet'
        [void](Fake 'copilot-task' @{ id = $t.id; state = 'in_progress'; headRef = $br })
        $w = Cop-Accept $t $n
        $own = Cop-Owner $n
        Check 'the session owns the item' ($own -like "cop-$n-*") ((Get-Plant).plant.owners | ConvertTo-Json -Compress)
        # silent (no verb) for longer than staleItemMinutes + staleConfirmMinutes (0.1 + 0.05 min in this suite), its execution running
        Start-Sleep -Seconds 12
        # the precondition of the lease check: the session's execution is still running and its deadline has not ended it yet
        $st = Cop-Status
        Check 'its execution is still running at the item check (the deadline has not ended it)' (@($st.executions | Where-Object { $_.item -eq $n -and $_.execution -eq $t.id }).Count -eq 1 -and (Cop-DeadlineLines $t.id).Count -eq 0) (@{ executions = $st.executions; deadline = @((Cop-DeadlineLines $t.id) | ForEach-Object { "$_" }) } | ConvertTo-Json -Compress -Depth 5)
        $now = Cop-Owner $n
        Check 'it still owns the item: no item check takes a running Copilot session''s lease' ($now -eq $own -and (Cop-Comments $n '*did not answer the check*').Count -eq 0) "owner '$now' (was '$own'); owners $((Get-Plant).plant.owners | ConvertTo-Json -Compress); $(Cop-Brief $n)"
        # its deadline (FACTORY_COPILOT_MAX_MINUTES 0.5 for this scenario) ends the session unfinished: the item is offered again and the SAME task resumed
        Check 'the next session is a follow-up of the same task on its pull request, not a new task' (Wait-Until -Seconds 60 { @((Cop-State).tasks | Where-Object { $_.task -eq $t.id -and $_.via -eq 'comment' -and $_.pr -eq $d.number }).Count -ge 1 }) ((Cop-State).tasks | ConvertTo-Json -Compress -Depth 4)
        Check 'still one task for the item' (@((Cop-Tasks $n) | Select-Object -ExpandProperty task -Unique).Count -eq 1)
    } finally { Cop-Cleanup @($n); $env:FACTORY_TIMING_staleItemMinutes = '600'; Remove-Item Env:FACTORY_TIMING_staleConfirmMinutes -ErrorAction SilentlyContinue; Remove-Item Env:FACTORY_COPILOT_MAX_MINUTES -ErrorAction SilentlyContinue; Restart-Gateway }
}

'copilot: a session refused for the seat''s quota stops starts and resumes without counting against the item, shows the owner action, and the next start after quotaRetryMinutes probes and clears it [full]' = {
    $n = 0
    try {
        $n = New-Item-Fake 'Copilot quota' 'Change src/App/Quota.cs.' @('factory worker', 'Auto Merge', 'env:tdd') 'Development'
        Check 'a task is created' (Wait-Until -Seconds 30 { (Cop-DevTasks $n).Count -gt 0 })
        $t = (Cop-DevTasks $n)[0]
        [void](Fake 'copilot-task' @{ id = $t.id; state = 'failed'; error = 'You have exceeded your monthly quota (Request ID: TEST)' })
        Check 'status: quota exhausted, with the owner action' (Wait-Until -Seconds 20 { $q = (Cop-Status).quota; $q.exhausted -and "$($q.ownerAction)" -like 'raise the Copilot AI-credit budget*' }) ((Cop-Status).quota | ConvertTo-Json -Compress)
        Check 'the refused session is no failure of the item' (Wait-Until -Seconds 20 { @((Cop-Status).last | Where-Object { $_.execution -eq $t.id -and $_.outcome -eq 'blocked' }).Count -eq 1 -and @((Cop-Status).failures | Where-Object { $_.item -eq $n }).Count -eq 0 }) (Cop-Status | ConvertTo-Json -Compress -Depth 5)
        Start-Sleep -Seconds 6
        Check 'nothing is started while the quota is exhausted' ((Cop-Tasks $n).Count -eq 1) ((Cop-Tasks $n) | ForEach-Object { "$($_.id) $($_.state)" }) -join '; '
        Check 'the item stays owned by its task' (@((Cop-Status).owned | Where-Object { $_.item -eq $n -and $_.task -eq $t.id }).Count -eq 1)
        Check 'after quotaRetryMinutes (0.3 here) the next start is the probe' (Wait-Until -Seconds 60 { (Cop-Tasks $n).Count -ge 2 }) ((Cop-Tasks $n) | ForEach-Object { "$($_.id) $($_.state)" }) -join '; '
        $p = (Cop-Tasks $n)[-1]
        [void](Fake 'copilot-task' @{ id = $p.id; state = 'in_progress' })
        Check 'a session that runs clears the quota state' (Wait-Until -Seconds 20 { -not (Cop-Status).quota.exhausted }) ((Cop-Status).quota | ConvertTo-Json -Compress)
    } finally { Cop-Cleanup @($n) }
}

'copilot: a recorded pull request that was closed (a migration, a takeover) is replaced by the open pull request of the owner task''s branch; the wait for checks follows it and the item moves on, not parked stuck [full]' = {
    $n = 0
    try {
        $n = New-Item-Fake 'Copilot closed recorded PR' 'Change src/App/Rec.cs.' @('factory worker', 'Auto Merge', 'env:tdd') 'Release Queue'
        $old = "copilot/old-$n"; $cur = "copilot/cur-$n"
        [void](Fake 'push-branch' @{ branch = $old; files = @('src/App/Rec.cs') })
        $o = Cop-Draft $old 'master' "Old (#$n)"
        [void](Invoke-RestMethod "$(Cop-Gh)/pulls/$($o.number)" -Method PATCH -Headers @{ Authorization = 'Bearer fake-operator' } -ContentType 'application/json' -Body (@{ state = 'closed' } | ConvertTo-Json))
        [void](Fake 'push-branch' @{ branch = $cur; files = @('src/App/Rec.cs', 'src/UnitTests/RecTests.cs') })
        $c = Cop-Draft $cur 'master' "Cur (#$n)"
        $task = Invoke-RestMethod "http://127.0.0.1:$($script:FakePort)/agents/repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders/tasks" -Method POST -Headers @{ Authorization = 'Bearer fake-copilot' } -ContentType 'application/json' -Body (@{ prompt = 'the owner task'; base_ref = 'master'; create_pull_request = $true } | ConvertTo-Json)
        [void](Fake 'copilot-task' @{ id = $task.id; state = 'completed'; headRef = $cur })
        # the item waits for the checks of the closed pull request, as #408, #410 and #412 did
        $now = (Get-Date).ToUniversalTime().ToString('o')
        $state = @{ item = $n; pr = @{ number = $o.number; branch = $old }; copilot_task = @{ id = $task.id; url = ''; branch = $cur; pr = $o.number; phase = 'Development'; since = $now; previous = @() }
            awaiting_checks = @{ pr = $o.number; sha = (Cop-FakePr $o.number).sha; since = $now; column = 'Development'; comment = 'test' }; park = (New-TestPark 'development:awaiting-checks') }
        $body = "<!-- factory-state:factory-worker v1 -->`nstate`n`n``````json`n" + ($state | ConvertTo-Json -Compress -Depth 8) + "`n```````n"
        [void](Invoke-RestMethod "$(Cop-Gh)/issues/$n/comments" -Method POST -Headers @{ Authorization = 'Bearer fake-operator' } -ContentType 'application/json' -Body (@{ body = $body } | ConvertTo-Json))
        [void](Fake 'move' @{ number = $n; column = 'Development' })
        Check 'the item''s pull request is re-resolved to the open one of the owner task''s branch' (Wait-Until -Seconds 90 { (Cop-Comments $n "*pull request is now #$($c.number)*").Count -eq 1 }) (Cop-Brief $n)
        Check 'the state records it' ((Get-ItemState $n).pr.number -eq $c.number -and (Get-ItemState $n).copilot_task.pr -eq $c.number) ((Get-ItemState $n) | ConvertTo-Json -Compress -Depth 5)
        Check 'its green checks move the item on; it is not parked stuck' (Wait-Until -Seconds 90 { (Get-FakeIssue $n).column -eq 'Functional Testing' }) "$((Get-FakeIssue $n).column) $(Cop-Brief $n)"
        Check 'no stuck park' ((Cop-Comments $n '*still not finished*').Count -eq 0)
    } finally { Cop-Cleanup @($n) }
}

'copilot: an owner pull request stacked on another branch with no Build run is consolidated onto master and its CI started; it moves on, not parked stuck [full]' = {
    $n = 0
    try {
        $n = New-Item-Fake 'Copilot stacked PR' 'Change src/App/Stk.cs.' @('factory worker', 'Auto Merge', 'env:tdd') 'Release Queue'
        $bb = "copilot/base-$n"; $sb = "copilot/stk-$n"
        [void](Fake 'push-branch' @{ branch = $bb; files = @('src/App/Stk.cs') })
        $b = Cop-Draft $bb 'master' "Base (#$n)"
        # the owner's branch holds the base's commits; its push started no Build (as #443's)
        [void](Fake 'push-branch' @{ branch = $sb; files = @('src/App/Stk.cs', 'src/UnitTests/StkTests.cs'); profile = 'nobuild' })
        $sp = Cop-Draft $sb $bb "Stacked (#$n)"
        $task = Invoke-RestMethod "http://127.0.0.1:$($script:FakePort)/agents/repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders/tasks" -Method POST -Headers @{ Authorization = 'Bearer fake-copilot' } -ContentType 'application/json' -Body (@{ prompt = 'the owner task'; base_ref = 'master'; create_pull_request = $true } | ConvertTo-Json)
        [void](Fake 'copilot-task' @{ id = $task.id; state = 'completed'; headRef = $sb })
        $now = (Get-Date).ToUniversalTime().ToString('o')
        $state = @{ item = $n; pr = @{ number = $b.number; branch = $bb }; copilot_task = @{ id = $task.id; url = ''; branch = $sb; pr = $b.number; phase = 'Development'; since = $now; previous = @() } }
        $body = "<!-- factory-state:factory-worker v1 -->`nstate`n`n``````json`n" + ($state | ConvertTo-Json -Compress -Depth 8) + "`n```````n"
        [void](Invoke-RestMethod "$(Cop-Gh)/issues/$n/comments" -Method POST -Headers @{ Authorization = 'Bearer fake-operator' } -ContentType 'application/json' -Body (@{ body = $body } | ConvertTo-Json))
        $before = (Cop-Tasks $n).Count
        [void](Fake 'move' @{ number = $n; column = 'Development' })
        Check 'the stacked pull request is retargeted to master' (Wait-Until -Seconds 90 { (Cop-FakePr $sp.number).base -eq 'master' }) ((Cop-FakePr $sp.number) | ConvertTo-Json -Compress -Depth 3)
        Check 'and the base''s open pull request is superseded: one pull request for the item' (Wait-Until -Seconds 30 { -not (Cop-FakePr $b.number).open }) (Cop-Brief $n)
        Check 'the item''s pull request is the owner''s' (Wait-Until -Seconds 30 { (Get-ItemState $n).pr.number -eq $sp.number }) ((Get-ItemState $n) | ConvertTo-Json -Compress -Depth 5)
        Check 'no Build run for its head is no hung CI: the gateway starts the Build on the branch' (Wait-Until -Seconds 120 { @((Fake 'state').dispatches | Where-Object { "$_" -eq "build.yml@$sb" }).Count -ge 1 }) (Cop-Brief $n)
        Check 'the item moves on once the Build is green' (Wait-Until -Seconds 90 { (Get-FakeIssue $n).column -eq 'Functional Testing' }) "$((Get-FakeIssue $n).column) $(Cop-Brief $n)"
        Check 'never parked stuck' ((Cop-Comments $n '*still not finished*').Count -eq 0)
        Check 'no new Development task was started for it' (@((Cop-Tasks $n) | Where-Object { "$($_.body.prompt)" -match 'phase development' }).Count -eq 0) ((Cop-Tasks $n) | ConvertTo-Json -Compress -Depth 3)
    } finally { Cop-Cleanup @($n) }
}

'copilot: a follow-up session that succeeds and pushes without connecting is judged by its run and its push: no failure, the item is settled; its prompt asks to connect first [full]' = {
    $n = 0
    try {
        $n = New-Item-Fake 'Copilot unregistered follow-up' 'Change src/App/Unreg.cs.' @('factory worker', 'Auto Merge', 'env:tdd') 'Development'
        Check 'a Development task is created' (Wait-Until -Seconds 30 { (Cop-DevTasks $n).Count -gt 0 })
        $t = (Cop-DevTasks $n)[0]
        $br = "copilot/unreg-$n"
        [void](Fake 'push-branch' @{ branch = $br; files = @('src/App/Unreg.cs') })
        $d = Cop-Draft $br 'master' '[WIP] Unreg'
        $w = Cop-Accept $t $n
        [void](Cop-OpenPr $n $br)
        [void](Mcp-Call 'session_end' @{ reason = 'pushed' } $w)
        [void](Fake 'copilot-task' @{ id = $t.id; state = 'completed'; headRef = $br })
        Check 'Functional Testing follows as a session of the same task' (Wait-Until -Seconds 120 { (Cop-Sessions $n $t.id 'functional-testing').Count -eq 1 }) ((Cop-Tasks $n) | ConvertTo-Json -Compress -Depth 4)
        $s2 = (Cop-Sessions $n $t.id 'functional-testing')[0]
        Check 'the follow-up prompt asks to connect and accept FIRST, with its code' ("$($s2.body.prompt)" -match 'FIRST, before you read or change anything: call fworker_connect with code c1\.' -and "$($s2.body.prompt)" -match "item_accept for #$n") "$($s2.body.prompt)"
        $w2 = Cop-Accept $s2 $n
        $rv = Mcp-Call 'item_record_review' @{ item = $n; column = 'Functional Testing'; role = 'reviewer'; text = "The empty title is accepted.`nREVIEW: COMPLETE" } $w2
        [void](Mcp-Call 'item_validation_verdict' @{ item = $n; passed = $false; summary = 'FAIL: the empty title is accepted'; pr = $d.number; review_ref = "$($rv.body.review_ref)"; head_sha = "$((Cop-FakePr $d.number).sha)" } $w2)
        [void](Mcp-Call 'session_end' @{ reason = 'verdict' } $w2)
        [void](Fake 'copilot-task' @{ id = $s2.id; state = 'completed' })
        Check 'the rework is a Development follow-up of the same task' (Wait-Until -Seconds 90 { @((Cop-Sessions $n $t.id 'development') | Where-Object { $_.via -eq 'comment' }).Count -eq 1 }) ((Cop-Tasks $n) | ConvertTo-Json -Compress -Depth 4)
        $s3 = @((Cop-Sessions $n $t.id 'development') | Where-Object { $_.via -eq 'comment' })[0]
        $pending = (Cop-Comments $n '*Development is done pending checks*').Count
        # the session never connects; it fixes, pushes to the pull request's branch and its run succeeds
        [void](Fake 'push-branch' @{ branch = $br; files = @('src/App/Unreg.cs', 'src/App/Title.cs') })
        [void](Fake 'copilot-task' @{ id = $s3.id; state = 'completed' })
        Check 'its end is no failure of the item' (Wait-Until -Seconds 60 { @((Cop-Status).last | Where-Object { $_.execution -eq $s3.id -and $_.outcome -eq 'succeeded' }).Count -eq 1 }) ((Cop-Status).last | ConvertTo-Json -Compress -Depth 4)
        Check 'no failure is counted' (@((Cop-Status).failures | Where-Object { $_.item -eq $n }).Count -eq 0) ((Cop-Status).failures | ConvertTo-Json -Compress)
        Check 'the pushed work is settled: the pull request waits for its checks' (Wait-Until -Seconds 60 { (Cop-Comments $n '*Development is done pending checks*').Count -gt $pending }) (Cop-Brief $n)
        Check 'and the item moves on to Functional Testing, not parked' (Wait-Until -Seconds 90 { (Get-FakeIssue $n).column -eq 'Functional Testing' }) "$((Get-FakeIssue $n).column) $(Cop-Brief $n)"
        Check 'never parked runtime-failed' ((Cop-Comments $n '*runtime-failed*').Count -eq 0) (Cop-Brief $n)
    } finally { Cop-Cleanup @($n) }
}

'copilot: a follow-up session lives as long as its Actions run: the slot stays held while the run runs, and a run that succeeds without a push brings one follow-up that is told to push [full]' = {
    $n = 0
    try {
        $n = New-Item-Fake 'Copilot follow-up run' 'Change src/App/Run.cs.' @('factory worker', 'Auto Merge', 'env:tdd') 'Development'
        Check 'a Development task is created' (Wait-Until -Seconds 30 { (Cop-DevTasks $n).Count -gt 0 })
        $t = (Cop-DevTasks $n)[0]
        $br = "copilot/run-$n"
        [void](Fake 'push-branch' @{ branch = $br; files = @('src/App/Run.cs') })
        $d = Cop-Draft $br 'master' '[WIP] Run'
        $w = Cop-Accept $t $n
        [void](Cop-OpenPr $n $br)
        [void](Mcp-Call 'session_end' @{ reason = 'pushed' } $w)
        [void](Fake 'copilot-task' @{ id = $t.id; state = 'completed'; headRef = $br })
        Check 'Functional Testing follows as a follow-up session' (Wait-Until -Seconds 120 { (Cop-Sessions $n $t.id 'functional-testing').Count -eq 1 }) ((Cop-Tasks $n) | ConvertTo-Json -Compress -Depth 4)
        $s2 = (Cop-Sessions $n $t.id 'functional-testing')[0]
        # the tasks API calls the session done; its Actions run still builds and tests
        [void](Fake 'copilot-task' @{ id = $s2.id; state = 'completed'; run = 'in_progress' })
        Start-Sleep -Seconds 8
        Check 'the session still runs: the slot is held while its run runs' (@((Cop-Status).executions | Where-Object { $_.item -eq $n }).Count -eq 1 -and @((Cop-Status).last | Where-Object { $_.execution -eq $s2.id }).Count -eq 0) ((Cop-Status) | ConvertTo-Json -Compress -Depth 4)
        Check 'and the item stays in its phase' ((Get-FakeIssue $n).column -eq 'Functional Testing')
        [void](Fake 'copilot-task' @{ id = $s2.id; run = 'success' })
        Check 'the run''s end ends the session' (Wait-Until -Seconds 30 { @((Cop-Status).last | Where-Object { $_.execution -eq $s2.id }).Count -eq 1 }) ((Cop-Status).last | ConvertTo-Json -Compress -Depth 3)
        Check 'a run that succeeded without a push brings one follow-up told to push first' (Wait-Until -Seconds 60 { @((Cop-Sessions $n $t.id 'functional-testing') | Where-Object { "$($_.body.prompt)" -match 'ended without pushing anything' }).Count -eq 1 }) (((Cop-Tasks $n) | ForEach-Object { "$($_.id) $($_.via)" }) -join '; ')
    } finally { Cop-Cleanup @($n) }
}

'copilot: a follow-up''s own draft stacked on the item''s pull request ("merges into #N") becomes the owner''s current branch: retargeted to master, the old pull request superseded, and the item moves on [full]' = {
    $n = 0
    try {
        $n = New-Item-Fake 'Copilot successor PR' 'Change docs/Succ.md.' @('factory worker', 'Auto Merge', 'env:tdd') 'Release Queue'
        $ab = "copilot/first-$n"; $bb = "copilot/succ-$n"
        [void](Fake 'push-branch' @{ branch = $ab; files = @('src/App/Succ.cs') })
        $a = Cop-Draft $ab 'master' "Succ (#$n)"
        [void](Fake 'push-branch' @{ branch = $bb; files = @('src/App/Succ.cs', 'src/UnitTests/SuccTests.cs') })
        $b = Cop-Draft $bb $ab '[WIP] Succ' "Thanks for the feedback on #$($a.number). I've created this new PR, which merges into #$($a.number)."
        $task = Invoke-RestMethod "http://127.0.0.1:$($script:FakePort)/agents/repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders/tasks" -Method POST -Headers @{ Authorization = 'Bearer fake-copilot' } -ContentType 'application/json' -Body (@{ prompt = 'the owner task'; base_ref = 'master'; create_pull_request = $true } | ConvertTo-Json)
        [void](Fake 'copilot-task' @{ id = $task.id; state = 'completed'; headRef = $ab })
        $now = (Get-Date).ToUniversalTime().ToString('o')
        $state = @{ item = $n; pr = @{ number = $a.number; branch = $ab }; copilot_task = @{ id = $task.id; url = ''; branch = $ab; pr = $a.number; phase = 'Development'; since = $now; previous = @() } }
        $body = "<!-- factory-state:factory-worker v1 -->`nstate`n`n``````json`n" + ($state | ConvertTo-Json -Compress -Depth 8) + "`n```````n"
        [void](Invoke-RestMethod "$(Cop-Gh)/issues/$n/comments" -Method POST -Headers @{ Authorization = 'Bearer fake-operator' } -ContentType 'application/json' -Body (@{ body = $body } | ConvertTo-Json))
        [void](Fake 'move' @{ number = $n; column = 'Development' })
        Check 'the successor is retargeted to master' (Wait-Until -Seconds 90 { (Cop-FakePr $b.number).base -eq 'master' }) ((Cop-FakePr $b.number) | ConvertTo-Json -Compress -Depth 3)
        Check 'the old pull request is superseded' (Wait-Until -Seconds 30 { -not (Cop-FakePr $a.number).open }) (Cop-Brief $n)
        Check 'the owner follows the successor''s branch and pull request' (Wait-Until -Seconds 30 { $sx = Get-ItemState $n; $sx.copilot_task.branch -eq $bb -and $sx.pr.number -eq $b.number }) ((Get-ItemState $n) | ConvertTo-Json -Compress -Depth 5)
        # the resolution writes the title before it records the successor: the CI release (routeAll) finds the item from it
        $bt = "$((Cop-FakePr $b.number).title)"
        Check 'its title names the item, the [WIP] dropped' ($bt -match "(^|[^0-9])#$n([^0-9]|$)" -and $bt -notmatch '^\s*\[WIP\]') $bt
        Check 'the item moves on' (Wait-Until -Seconds 90 { (Get-FakeIssue $n).column -eq 'Functional Testing' }) "$((Get-FakeIssue $n).column) $(Cop-Brief $n)"
    } finally { Cop-Cleanup @($n) }
}

'copilot: pr_open takes a follow-up''s draft that names the item''s pull request ("merges into #N"): a pull request number is no other item [full]' = {
    $n = 0
    try {
        $n = New-Item-Fake 'Copilot names parent PR' 'Change src/App/Par.cs.' @('factory worker', 'Auto Merge', 'env:tdd') 'Development'
        Check 'a Development task is created' (Wait-Until -Seconds 30 { (Cop-DevTasks $n).Count -gt 0 })
        $t = (Cop-DevTasks $n)[0]
        $ab = "copilot/par-$n"; $bb = "copilot/par2-$n"
        [void](Fake 'push-branch' @{ branch = $ab; files = @('src/App/Par.cs') })
        $a = Cop-Draft $ab 'master' '[WIP] Par'
        $w = Cop-Accept $t $n
        $op1 = Cop-OpenPr $n $ab
        Check 'the first pull request is the item''s' ($op1.ok -and $op1.pullRequest.number -eq $a.number) ($op1 | ConvertTo-Json -Compress -Depth 4)
        [void](Fake 'push-branch' @{ branch = $bb; files = @('src/App/Par.cs', 'src/UnitTests/ParTests.cs') })
        $b = Cop-Draft $bb $ab '[WIP] Par again' "I've created this new PR, which merges into #$($a.number), to address your comment."
        $op2 = Cop-OpenPr $n $bb
        Check 'pr_open is not refused as another item''s branch' ($op2.ok -eq $true) ($op2 | ConvertTo-Json -Compress -Depth 4)
        Check 'and the item''s pull request is on the new branch now' ((Get-ItemState $n).pr.branch -eq $bb) ((Get-ItemState $n) | ConvertTo-Json -Compress -Depth 4)
        [void](Mcp-Call 'session_end' @{ reason = 'done' } $w)
    } finally { Cop-Cleanup @($n) }
}

'copilot: the owner''s later draft on its own branch (not stacked, the older pull request closed) is the item''s current pull request: marked ready, never the closed one, and the item moves on [full]' = {
    $n = 0
    try {
        $n = New-Item-Fake 'Copilot later draft' 'Change docs/Later.md.' @('factory worker', 'Auto Merge', 'env:tdd') 'Release Queue'
        $ab = "copilot/old-$n"; $bb = "copilot/later-$n"
        [void](Fake 'push-branch' @{ branch = $ab; files = @('src/App/Later.cs') })
        $a = Cop-Draft $ab 'master' "Later (#$n)"
        [void](Invoke-RestMethod "$(Cop-Gh)/pulls/$($a.number)" -Method PATCH -Headers @{ Authorization = 'Bearer fake-operator' } -ContentType 'application/json' -Body (@{ state = 'closed' } | ConvertTo-Json))
        [void](Fake 'push-branch' @{ branch = $bb; files = @('src/App/Later.cs', 'src/UnitTests/LaterTests.cs') })
        $b = Cop-Draft $bb 'master' "Later again (#$n)"
        $task = Invoke-RestMethod "http://127.0.0.1:$($script:FakePort)/agents/repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders/tasks" -Method POST -Headers @{ Authorization = 'Bearer fake-copilot' } -ContentType 'application/json' -Body (@{ prompt = 'the owner task'; base_ref = 'master'; create_pull_request = $true } | ConvertTo-Json)
        [void](Fake 'copilot-task' @{ id = $task.id; state = 'completed'; headRef = $ab })
        $now = (Get-Date).ToUniversalTime().ToString('o')
        $state = @{ item = $n; pr = @{ number = $b.number; branch = $bb }; copilot_task = @{ id = $task.id; url = ''; branch = $ab; pr = $a.number; phase = 'Development'; since = $now; previous = @() } }
        $body = "<!-- factory-state:factory-worker v1 -->`nstate`n`n``````json`n" + ($state | ConvertTo-Json -Compress -Depth 8) + "`n```````n"
        [void](Invoke-RestMethod "$(Cop-Gh)/issues/$n/comments" -Method POST -Headers @{ Authorization = 'Bearer fake-operator' } -ContentType 'application/json' -Body (@{ body = $body } | ConvertTo-Json))
        [void](Fake 'move' @{ number = $n; column = 'Development' })
        Check 'the owner follows the later draft, not the closed pull request' (Wait-Until -Seconds 60 { $sx = Get-ItemState $n; $sx.copilot_task.branch -eq $bb -and $sx.copilot_task.pr -eq $b.number }) ((Get-ItemState $n) | ConvertTo-Json -Compress -Depth 5)
        Check 'the later draft is marked ready for review' (Wait-Until -Seconds 60 { -not (Cop-FakePr $b.number).draft }) ((Cop-FakePr $b.number) | ConvertTo-Json -Compress -Depth 3)
        Check 'the item moves on to Functional Testing' (Wait-Until -Seconds 90 { (Get-FakeIssue $n).column -eq 'Functional Testing' }) "$((Get-FakeIssue $n).column) $(Cop-Brief $n)"
    } finally { Cop-Cleanup @($n) }
}
}

# The deadline has its own suite ('copilot-deadline', api.copilot.maxMinutes 0.25): in 'copilot' it would end any task of a scenario that runs
# over 15 s on a loaded runner (an item moved on by the gateway, an extra session), so that suite keeps a generous maxMinutes.
$CopilotDeadlineScenarios = [ordered]@{

'copilot: a task past api.copilot.maxMinutes fails and its code dies' = {
    $n = 0
    try {
        $n = New-Item-Fake 'Copilot deadline' 'x' $CopLabels
        Check 'a task is created' (Wait-Until -Seconds 30 { (Cop-Tasks $n).Count -gt 0 })
        $t2 = (Cop-Tasks $n)[0]
        # the deadline is counted from the gateway's own launch of the task; the wait is generous for a loaded runner
        Check 'the deadline (0.25 min in this suite) fails it' (Wait-Until -Seconds 120 { @(Get-ChildItem $script:Work -Filter 'gateway*.out' | Select-String -SimpleMatch "`"runtime`":`"copilot-deadline`"" | Where-Object { "$_" -match $t2.id }).Count -gt 0 })
        Check 'and its unspent code no longer exchanges' (Wait-Until -Seconds 20 { (Cop-Exchange (Cop-Code $t2) (Cop-Bootstrap)).status -eq 401 })
    } finally { Cop-Cleanup @($n) }
}
}
