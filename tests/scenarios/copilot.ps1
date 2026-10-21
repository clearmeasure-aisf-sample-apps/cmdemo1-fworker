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
function Cop-Cleanup([int[]]$Items) {
    foreach ($n in $Items) { if ($n) { [void](Invoke-RestMethod "http://127.0.0.1:$($script:FakePort)/repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders/issues/$n" -Method PATCH -Headers @{ Authorization = 'Bearer fake-operator' } -Body (@{ state = 'closed' } | ConvertTo-Json) -ContentType 'application/json') } }
    foreach ($t in @((Cop-State).tasks | Where-Object { $_.state -in 'queued', 'in_progress' })) { [void](Fake 'copilot-task' @{ id = $t.id; state = 'cancelled' }) }
    [void](Wait-Until -Seconds 20 { (Cop-Status).running -eq 0 })
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
        Check 'status: the failure was counted against the item' (Wait-Until -Seconds 20 { @((Cop-Status).failures | Where-Object { $_.item -eq $m -and $_.count -ge 1 }).Count -eq 1 }) (Cop-Status | ConvertTo-Json -Compress -Depth 5)
    } finally { Cop-Cleanup @($n, $m) }
}

'copilot: the default runtime takes an unlabelled code item to Development with a pull request by Copilot; pr_open takes over its draft on the copilot/ branch; Functional Testing starts from that branch' = {
    $n = 0
    try {
        $n = New-Item-Fake 'Copilot code item' 'Change src/App/Thing.cs and its unit test.' @('factory worker', 'Auto Merge', 'env:tdd') 'Development'
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
        # the gateway completes Development from the pull request (copilot-settle) and moves the item on when its checks are green
        Check 'the gateway moves the item to Functional Testing' (Wait-Until -Seconds 60 { (Get-FakeIssue $n).column -eq 'Functional Testing' }) "$((Get-FakeIssue $n).column)"
        Check 'no second Development task was started' ((Cop-DevTasks $n).Count -eq 1) (((Cop-Tasks $n) | ForEach-Object { "$($_.id) $($_.state) $($_.body.base_ref)" }) -join '; ')
        $ftFrom = Get-Date
        # (a Development task relaunched before the sweep saw the move is stopped too, inside the wait: on a loaded runner it can start late)
        Check 'Functional Testing is a follow-up session of the same task, on its pull request' (Wait-Until -Seconds 90 { foreach ($tt in @((Cop-Tasks $n) | Where-Object { $_.state -in 'queued', 'in_progress' -and "$($_.body.prompt)" -match 'phase development' })) { [void](Fake 'copilot-task' @{ id = $tt.id; state = 'cancelled' }) }; @((Cop-Tasks $n) | Where-Object { "$($_.body.prompt)" -match 'phase functional-testing' -and $_.task -eq $t.id -and $_.via -eq 'comment' -and $_.pr -eq $draft.number }).Count -eq 1 }) ("from $($ftFrom.ToUniversalTime().ToString('o')) to $((Get-Date).ToUniversalTime().ToString('o')) br=$br " + ((Cop-Tasks $n) | ForEach-Object { "$($_.id) $($_.state) $($_.task) $($_.via) $($_.pr)" }) -join '; ')
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
        Check 'one journal line on the item names the run' (Wait-Until -Seconds 30 { @((Get-FakeIssue $n).comments | Where-Object { "$($_.body)" -match "released workflow run $($run.id) of PR #$($d.number)" }).Count -eq 1 })
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

'copilot: one task owns the item end to end: Technical Design opens the draft, Development and Functional Testing are follow-up sessions of the same task on that pull request, no other worker is offered the item, and the owner is done only after the merge and a green master [full]' = {
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
        Check 'master green: the owner is done, with the task''s link' (Wait-Until -Seconds 120 { (Cop-Comments $n '*Owner done:** Copilot task*').Count -eq 1 }) (Cop-Brief $n)
        $cs = @((Get-FakeIssue $n).comments | ForEach-Object { "$($_.body)" })
        $gi = [array]::FindIndex([string[]]$cs, [Predicate[string]]{ param($x) $x -match 'master is GREEN' }); $oi = [array]::FindIndex([string[]]$cs, [Predicate[string]]{ param($x) $x -like '*Owner done:*' })
        Check 'the owner is done only after master is green' ($gi -ge 0 -and $oi -gt $gi) "green $gi done $oi"
        Check 'the done comment carries the session link' ("$($cs[$oi])" -match "https://github\.com/copilot/tasks/$([regex]::Escape($t.id))")
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

'copilot: a red master after the merge resumes the owner task once to fix forward; the owner is done only once master is green [full]' = {
    $n = 0
    try {
        $n = New-Item-Fake 'Copilot red master' 'Change src/App/Red.cs.' @('factory worker', 'Auto Merge', 'env:tdd')
        Check 'the owner task is created' (Wait-Until -Seconds 30 { (Cop-Tasks $n).Count -gt 0 })
        $t = (Cop-Tasks $n)[0]
        $br = "copilot/red-$n"
        [void](Fake 'push-branch' @{ branch = $br; files = @('src/App/Red.cs') })
        $d = Cop-Draft $br 'master' "Red (#$n)"
        $w = Cop-Accept $t $n
        [void](Mcp-Call 'session_end' @{ reason = 'done' } $w)
        [void](Fake 'copilot-task' @{ id = $t.id; state = 'completed'; headRef = $br })
        Check 'its session ends' (Wait-Until -Seconds 30 { (Cop-Status).running -eq 0 })
        # the item was merged (here: written as the settle leaves it) and master went red for the merge commit
        [void](Fake 'move' @{ number = $n; column = 'Release Queue' })
        $sha = 'abcdef1' + ('0' * 33)
        Set-ItemState $n { param($st) $st.Remove('claim'); $st.pr = @{ number = $d.number; branch = $br }; $st.merge = @{ sha = $sha; merged_at = (Get-Date).ToUniversalTime().ToString('o'); awaiting = 'master-broken'; rerun_attempted = $true }; $st.park = (New-TestPark 'master-broken' @{ failed = @('Build result'); sha = $sha }) }
        Check 'the owner task is resumed on its pull request to fix master forward' (Wait-Until -Seconds 60 { @((Cop-State).tasks | Where-Object { $_.task -eq $t.id -and $_.via -eq 'comment' -and $_.pr -eq $d.number -and "$($_.body.prompt)" -match 'master is red' }).Count -eq 1 }) ((Cop-State).tasks | ConvertTo-Json -Compress -Depth 4)
        $fx = @((Cop-State).tasks | Where-Object { $_.task -eq $t.id -and  "$($_.body.prompt)" -match 'master is red' })[0]
        Check 'its prompt names the failed check and asks for one pull request against master' ("$($fx.body.prompt)" -match 'Build result' -and "$($fx.body.prompt)" -match 'ONE pull request against master') "$($fx.body.prompt)"
        Start-Sleep -Seconds 25
        Check 'once per merge commit' (@((Cop-State).tasks | Where-Object { $_.task -eq $t.id -and  "$($_.body.prompt)" -match 'master is red' }).Count -eq 1)
        Check 'not done while master is red' ((Cop-Comments $n '*Owner done:*').Count -eq 0 -and @((Cop-Status).owned | Where-Object { $_.item -eq $n }).Count -eq 1)
        Set-ItemState $n { param($st) $st.merge.green_at = (Get-Date).ToUniversalTime().ToString('o'); $st.merge.awaiting = 'release-candidate'; $st.merge.rc_deadline = (Get-Date).ToUniversalTime().AddHours(1).ToString('o'); $st.Remove('park') }
        Check 'master green: the owner is done' (Wait-Until -Seconds 60 { (Cop-Comments $n '*Owner done:** Copilot task*').Count -eq 1 }) (Cop-Brief $n)
        Check 'and the item is no longer owned' (Wait-Until -Seconds 20 { @((Cop-Status).owned | Where-Object { $_.item -eq $n }).Count -eq 0 })
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
