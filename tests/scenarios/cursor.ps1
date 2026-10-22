# Tier 1 scenarios of the api launch mode, v1 Cursor Cloud Agents (docs/design/agents-roles-launch.md 10, gateway/Runtimes/CursorCloudRuntime.cs),
# against the fake Cursor API of tests/FakeGitHub. The scenario plays the Cursor agent: it takes the r1 from the launch body's MCP server entry
# and calls the gateway's /mcp with it. Suite 'cursor' (owner mode). Uses the helpers of owner.ps1 and mcp.ps1.

$CurLabels = @('factory worker', 'Auto Merge', 'runtime:cursor-cloud')
function Cur-State { Fake 'cursor' }
function Cur-Agents([int]$Item) { , @((Cur-State).agents | Where-Object { $_.body.PSObject.Properties['prompt'] -and "$($_.body.prompt.text)" -match "work item: #$Item," }) }
function Cur-R1($Agent) { "$($Agent.body.mcpServers[0].headers.Authorization)".Substring(7) }
# the dashboard's link helpers, run in node against sample rows: item and PR numbers link to GitHub, an execution id to its session url, in a new tab
function Test-DashLinks {
    $html = (Invoke-WebRequest "http://127.0.0.1:$($script:ApiPort)/dashboard" -SkipHttpErrorCheck).Content
    $defs = @(foreach ($k in 'const esc=', 'const REPO_DEF=', 'const ghLink=', 'const sessionLink=', 'const itemLink=') { $i = $html.IndexOf($k); if ($i -lt 0) { return $false }; $html.Substring($i, $html.IndexOf("`n", $i) - $i) })
    $js = "let repoUrl='';" + ($defs -join "`n") + "`nconst o=[itemLink(503),prLink(12),sessionLink({execution:'bc-1',url:'https://cursor.com/agents/bc-1'})].join('|');console.log(o)"
    $out = $js | node - 2>&1
    return ("$out" -match '<a href="https://github.com/clearmeasure-aisf-sample-apps/cmdemo1-workorders/issues/503" target="_blank" rel="noopener">#503</a>' -and "$out" -match '/pull/12" target="_blank"' -and "$out" -match '<a href="https://cursor.com/agents/bc-1" target="_blank" rel="noopener">bc-1</a>')
}
function Cur-FollowUps([string]$Id) { , @(@((Cur-State).agents | Where-Object { $_.id -eq $Id })[0].followUps | Where-Object { $_ }) }
function Cur-FuR1($FollowUp) { "$($FollowUp.body.mcpServers[0].headers.Authorization)".Substring(7) }
function Cur-Accept([string]$R1, [int]$Item) {
    $cur = ''
    # a call refused while the run's r1 registers (a loaded runner) answers no events: read again
    [void](Wait-Until -Seconds 60 { try { $e = Mcp-Events $R1 $cur 5; @($e.events | Where-Object { $_.event -eq 'item.assigned' -and $_.item -eq $Item }).Count -gt 0 } catch { $false } })
    [void](Mcp-Call 'item_accept' @{ item = $Item } $R1)
}
function Cur-Status { (Get-Plant).runtimes.'cursor-cloud' }
function Cur-Cleanup([int[]]$Items) {
    foreach ($n in $Items) { if ($n) { [void](Invoke-RestMethod "http://127.0.0.1:$($script:FakePort)/repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders/issues/$n" -Method PATCH -Headers @{ Authorization = 'Bearer fake-operator' } -Body (@{ state = 'closed' } | ConvertTo-Json) -ContentType 'application/json') } }
    # two sweeps after the close: an agent the gateway launched for a closed item from a plan made before it read the close (a relaunch after
    # session_end) is archived here, not left to show up in the next scenario
    $s0 = "$((Get-Plant).lastSweep)"; $script:curMid = $null
    [void](Wait-Until -Seconds 40 { $s1 = "$((Get-Plant).lastSweep)"; if ($s1 -ne $s0 -and -not $script:curMid) { $script:curMid = $s1 }; $script:curMid -and $s1 -ne $script:curMid })
    $script:curMid = $null
    foreach ($a in @((Cur-State).agents | Where-Object { -not $_.deleted -and $_.status -eq 'ACTIVE' })) { [void](Fake 'cursor-agent' @{ id = $a.id; status = 'ARCHIVED' }) }
    [void](Wait-Until -Seconds 20 { (Cur-Status).running -eq 0 })
    [void](Invoke-RestMethod "http://127.0.0.1:$($script:ApiPort)/api/v1/runtimes/cursor-cloud/reset" -Method POST -Headers @{ Authorization = 'Bearer test-token' })
}

$CursorScenarios = [ordered]@{

'cursor: an item labelled runtime:cursor-cloud launches one agent with the bootstrap prompt, the repository at master and the gateway''s /mcp with an r1; the agent works the item by MCP; session_end ends the run and keeps the agent as the item''s owner' = {
    $n = 0
    try {
        $n = New-Item-Fake 'Cursor docs item' 'Add a line to docs/cursor.md.' $CurLabels
        Check 'an agent is launched for the item' (Wait-Until -Seconds 30 { (Cur-Agents $n).Count -gt 0 })
        $a = (Cur-Agents $n)[0]; $b = $a.body
        Check 'the prompt is prompts/bootstrap/api.md with the item and phase' ("$($b.prompt.text)" -match 'You are a factory worker for ONE phase' -and "$($b.prompt.text)" -match "#$n, phase technical-design") "$($b.prompt.text)"
        Check 'the repository at master, autoCreatePR false' ($b.repos[0].url -eq 'https://github.com/clearmeasure-aisf-sample-apps/cmdemo1-workorders' -and $b.repos[0].startingRef -eq 'master' -and $b.autoCreatePR -eq $false) ($b | ConvertTo-Json -Compress -Depth 6)
        Check 'one MCP server fworker at the gateway''s public /mcp, with an r1 bearer' (@($b.mcpServers).Count -eq 1 -and $b.mcpServers[0].name -eq 'fworker' -and $b.mcpServers[0].url -eq 'http://gw.test/mcp' -and (Cur-R1 $a) -like 'r1.*')
        Check 'the model is Auto with the cost preference: the Cursor Router auto-smart, optimize_for cost (from /v1/models)' ($b.model.id -eq 'auto-smart' -and @($b.model.params).Count -eq 1 -and $b.model.params[0].id -eq 'optimize_for' -and $b.model.params[0].value -eq 'cost') ($b.model | ConvertTo-Json -Compress -Depth 4)
        Check 'the model Cursor reports for the agent is logged' (Wait-Until -Seconds 10 { @(Get-ChildItem $script:Work -Filter 'gateway*.out' | Select-String -SimpleMatch '"runtime":"cursor-agent-model"').Count -gt 0 })
        Check 'the agent''s name carries the environment and the item' ($b.name -like "fworker-tdd-i$n-cur-*")
        Check 'the prompt allows code changes and names the private build and acceptance gate' ("$($b.prompt.text)" -match 'Code and database changes are allowed' -and "$($b.prompt.text)" -match 'PrivateBuild\.ps1' -and "$($b.prompt.text)" -match 'AcceptanceTests\.ps1')
        Check 'no other credential in the launch body' (-not $b.PSObject.Properties['envVars'] -and -not (($b | ConvertTo-Json -Compress -Depth 8) -match 'fake-cursor|i1\.|w1\.|w2\.'))
        Check 'neither the key nor the r1 is in the gateway log' (@(Get-ChildItem $script:Work -Filter 'gateway*.out' | Select-String -SimpleMatch 'fake-cursor').Count -eq 0 -and @(Get-ChildItem $script:Work -Filter 'gateway*.out' | Select-String -SimpleMatch (Cur-R1 $a)).Count -eq 0)
        $st = Cur-Status
        Check 'status: the agent runs under the cursor-cloud runtime' ($st.running -eq 1 -and @($st.executions | Where-Object { $_.item -eq $n -and $_.execution -eq $a.id }).Count -eq 1) ($st | ConvertTo-Json -Compress -Depth 5)
        $r1 = Cur-R1 $a
        Check 'the r1 is a worker of the api mode: the worker tools' ((Mcp-ToolNames $r1) -contains 'get_prompt')
        $p = Mcp-Call 'get_prompt' @{} $r1
        Check 'get_prompt gives the technical-design prompt' (-not $p.isError -and $p.body.prompt -eq 'worker/technical-design')
        $cur = ''; $got = $false
        Check 'the offer arrives by next_event' (Wait-Until -Seconds 30 { $e = Mcp-Events $r1 $cur 5; @($e.events | Where-Object { $_.event -eq 'item.assigned' -and $_.item -eq $n }).Count -gt 0 })
        $ac = Mcp-Call 'item_accept' @{ item = $n } $r1
        Check 'the agent accepts its item' (-not $ac.isError) ($ac | ConvertTo-Json -Compress -Depth 4)
        Check 'the plant names the agent''s worker owner' (Wait-Until -Seconds 10 { $o = (Get-Plant).plant.owners; $o.PSObject.Properties["$n"] -and "$($o."$n")" -like "cur-$n-*" })
        # read until the row carries the execution: under a loaded runner the plant's row can lag the accept by a moment
        $row = @(); [void](Wait-Until -Seconds 15 { $script:row = @((Get-Plant).plant.workers | Where-Object { $_.name -like "cur-$n-*" }); $script:row.Count -eq 1 -and $script:row[0].mode -eq 'api' -and $script:row[0].execution -eq $a.id }); $row = $script:row
        Check 'its worker shows mode api' ($row.Count -eq 1 -and $row[0].mode -eq 'api' -and $row[0].execution -eq $a.id) ($row | ConvertTo-Json -Compress)
        [void](Mcp-Call 'session_end' @{ reason = 'scenario done' } $r1)
        $aurl = "https://cursor.com/agents/$($a.id)/fake"
        Check 'the launch comment carries the Cursor session link: the url Cursor answered, as it is' (Wait-Until -Seconds 30 { (Cop-Comments $n "*Cursor session: $aurl*").Count -ge 1 }) (Cop-Brief $n)
        # running or already ended (a slow runner may end it first): the execution, in executions or in last, carries the url
        Check 'status: the execution carries the url' (Wait-Until -Seconds 20 { $st = Cur-Status; @(@($st.executions) + @($st.last) | Where-Object { $_ -and $_.execution -eq $a.id -and $_.url -eq $aurl }).Count -ge 1 }) ((Cur-Status) | ConvertTo-Json -Compress -Depth 5)
        Check 'the dashboard links the item, the pull request and the agent id (new tab)' (Test-DashLinks)
        Check 'the item names its owner agent, once, and its state records it with runtime cursor-cloud' (Wait-Until -Seconds 30 { (Cop-Comments $n '*Owner:** Cursor agent*').Count -eq 1 -and (Get-ItemState $n).copilot_task.id -eq $a.id -and (Get-ItemState $n).copilot_task.runtime -eq 'cursor-cloud' }) (Cop-Brief $n)
        Check 'the acceptance comment carries the Cursor session link' (Wait-Until -Seconds 30 { @((Cop-Comments $n '*accepted*') | Where-Object { "$($_.body)" -like "*Cursor session: $aurl*" }).Count -ge 1 }) (Cop-Brief $n)
        Check 'the owner comment links the agent' ((Cop-Comments $n ("*Owner:** Cursor agent " + [WildcardPattern]::Escape("[$($a.id)]($aurl)") + "*")).Count -eq 1) (Cop-Brief $n)
        Check 'session_end ends the run; the agent is kept (not deleted) and owns the item' (Wait-Until -Seconds 45 { $x = @((Cur-State).agents | Where-Object { $_.id -eq $a.id }); $x.Count -eq 1 -and -not $x[0].deleted -and @((Cur-Status).owned | Where-Object { $_.item -eq $n -and $_.agent -eq $a.id }).Count -eq 1 }) ((Cur-Status) | ConvertTo-Json -Compress -Depth 5)
        Check 'status shows the model asked for and the model Cursor reports' (Wait-Until -Seconds 10 { $d = @((Cur-Status).last | Where-Object { $_.execution -eq $a.id }); $d.Count -eq 1 -and $d[0].detail.model.id -eq 'auto-smart' -and $d[0].detail.reportedModel.id -eq 'auto-smart' -and $d[0].detail.released }) ((Cur-Status).last | ConvertTo-Json -Compress -Depth 6)
        Check 'the next run of the item is a follow-up run of the same agent, with a fresh r1, never a second agent' (Wait-Until -Seconds 45 { (Cur-FollowUps $a.id).Count -ge 1 }) ((Cur-State) | ConvertTo-Json -Compress -Depth 6)
        $fu = (Cur-FollowUps $a.id)[0]
        Check 'the follow-up names the item, the phase and the ownership' ("$($fu.body.prompt.text)" -match "Follow-up run: you still own #$n" -and "$($fu.body.prompt.text)" -match "#$n, phase technical-design") "$($fu.body.prompt.text)"
        Check 'its r1 is new; the first run''s is revoked' ((Cur-FuR1 $fu) -like 'r1.*' -and (Cur-FuR1 $fu) -ne (Cur-R1 $a) -and (Wait-Until -Seconds 30 { (Mcp 'tools/list' @{} (Cur-R1 $a)).status -eq 401 }))
        Check 'one agent for the item' ((Cur-Agents $n).Count -eq 1)
        Check 'its token usage is logged' (Wait-Until -Seconds 10 { @(Get-ChildItem $script:Work -Filter 'gateway*.out' | Select-String -SimpleMatch "`"runtime`":`"cursor-usage`"").Count -gt 0 })
        # 'it' is this agent: the item is still open in its phase, so the gateway may already run the next agent for it (a relaunch after session_end)
        Check 'and the runtime no longer counts it' (Wait-Until -Seconds 15 { $st2 = Cur-Status; @($st2.executions | Where-Object { $_.execution -eq $a.id }).Count -eq 0 -and $st2.running -eq @($st2.executions | Where-Object { $_ }).Count }) ((Cur-Status) | ConvertTo-Json -Compress -Depth 4)
    } finally { Cur-Cleanup @($n) }
}

'cursor: code items are allowed: a code item in Development without a runtime: label is routed to cursor-cloud by default and gets an agent' = {
    $n = 0
    try {
        $n = New-Item-Fake 'Cursor code item' 'Change src/Foo.cs and its unit test.' @('factory worker', 'Auto Merge') 'Development'
        Check 'an agent is launched for it (the default route, no label)' (Wait-Until -Seconds 40 { (Cur-Agents $n).Count -eq 1 }) ((Cur-State) | ConvertTo-Json -Compress -Depth 4)
        Check 'with the development phase' ("$((Cur-Agents $n)[0].body.prompt.text)" -match "#$n, phase development")
        Check 'no runtime label was ignored' (@(Get-ChildItem $script:Work -Filter 'gateway*.out' | Select-String -SimpleMatch 'runtime-label-ignored').Count -eq 0)
    } finally { Cur-Cleanup @($n) }
}

'cursor: one agent owns the item to production: Development and Functional Testing are follow-up runs of the same agent, the gateway settles Development from its pull request, and the owner is done (the agent kept, never deleted) only after the merge, a green master and the production delivery' = {
    $n = 0
    try {
        $n = New-Item-Fake 'Cursor owned item' 'Change src/App/Cur.cs and its unit test.' @('factory worker', 'Auto Merge')
        Check 'the Technical Design agent is launched' (Wait-Until -Seconds 30 { (Cur-Agents $n).Count -gt 0 })
        $a = (Cur-Agents $n)[0]; $r1 = Cur-R1 $a
        Cur-Accept $r1 $n
        Check 'the owner is recorded' (Wait-Until -Seconds 30 { (Get-ItemState $n).copilot_task.id -eq $a.id }) (Cop-Brief $n)
        # the design is done (as item_complete would move it), then the run ends
        [void](Fake 'move' @{ number = $n; column = 'Development' })
        [void](Mcp-Call 'session_end' @{ reason = 'design done' } $r1)
        Check 'the run ends; the item stays owned by the same agent' (Wait-Until -Seconds 30 { @((Cur-Status).last | Where-Object { $_.execution -eq $a.id }).Count -ge 1 -and @((Cur-Status).owned | Where-Object { $_.item -eq $n -and $_.agent -eq $a.id }).Count -eq 1 }) ((Cur-Status) | ConvertTo-Json -Compress -Depth 5)
        # a Technical Design follow-up the gateway may have started before the move (the item was still in that column) is finished as Cursor would
        Check 'Development is a follow-up run of the same agent' (Wait-Until -Seconds 90 { $fx = Cur-FollowUps $a.id; $ok = @($fx | Where-Object { "$($_.body.prompt.text)" -match "#$n, phase development" }).Count -ge 1; if (-not $ok -and $fx.Count -gt 0 -and "$($fx[-1].body.prompt.text)" -match 'phase technical-design') { [void](Fake 'cursor-agent' @{ id = $a.id; status = 'IDLE'; run = 'FINISHED' }) }; $ok }) (Cop-Brief $n)
        $fd = @((Cur-FollowUps $a.id) | Where-Object { "$($_.body.prompt.text)" -match "#$n, phase development" })[-1]
        Check 'the development follow-up names the branch and the build gate' ("$($fd.body.prompt.text)" -match "factory-worker/$n-development" -and "$($fd.body.prompt.text)" -match 'private build') "$($fd.body.prompt.text)"
        $w1 = Cur-FuR1 $fd; Cur-Accept $w1 $n
        $br = "factory-worker/$n-development"
        [void](Fake 'push-branch' @{ branch = $br; files = @('src/App/Cur.cs', 'src/UnitTests/CurTests.cs') })
        $op = Cop-OpenPr $n $br
        Check 'pr_open opens the item''s pull request' ($op.ok) ($op | ConvertTo-Json -Compress -Depth 4)
        $prn = [int]$op.pullRequest.number
        [void](Mcp-Call 'session_end' @{ reason = 'pushed' } $w1)
        Check 'the gateway settles Development from the pull request and moves the item to Functional Testing' (Wait-Until -Seconds 120 { (Get-FakeIssue $n).column -eq 'Functional Testing' }) "$((Get-FakeIssue $n).column) $(Cop-Brief $n)"
        Check 'Functional Testing is the next follow-up run of the same agent' (Wait-Until -Seconds 60 { @((Cur-FollowUps $a.id) | Where-Object { "$($_.body.prompt.text)" -match "#$n, phase functional-testing" }).Count -ge 1 }) ((Cur-State) | ConvertTo-Json -Compress -Depth 6)
        $ff = @((Cur-FollowUps $a.id) | Where-Object { "$($_.body.prompt.text)" -match "#$n, phase functional-testing" })[-1]
        $w2 = Cur-FuR1 $ff; Cur-Accept $w2 $n
        $rv = Mcp-Call 'item_record_review' @{ item = $n; column = 'Functional Testing'; role = 'reviewer'; text = "Criteria exercised.`nREVIEW: COMPLETE" } $w2
        $v = Mcp-Call 'item_validation_verdict' @{ item = $n; passed = $true; summary = 'The criterion is met.'; pr = $prn; review_ref = "$($rv.body.review_ref)"; head_sha = "$((Cop-FakePr $prn).sha)" } $w2
        Check 'the PASS verdict is recorded' (-not $v.isError) ($v | ConvertTo-Json -Compress -Depth 5)
        [void](Mcp-Call 'session_end' @{ reason = 'verdict' } $w2)
        Check 'the gateway merges the pull request' (Wait-Until -Seconds 120 { (Cop-FakePr $prn).merged })
        Check 'not done before master is green and the delivery' ((Cop-Comments $n '*Owner done:*').Count -eq 0)
        Check 'master goes green for the merge' (Wait-Until -Seconds 120 { (Cop-Comments $n '*master is GREEN*').Count -ge 1 }) (Cop-Brief $n)
        if (-not (Wait-Until -Seconds 15 { (Cop-Comments $n '*Owner done:*').Count -gt 0 })) {
            Set-ItemState $n { param($st) $st.merge.release = '2.4.999'; $st.merge.done_at = (Get-Date).ToUniversalTime().ToString('o'); $st.merge.awaiting = $null }
        }
        Check 'delivered to production: the owner is done, with the agent''s link' (Wait-Until -Seconds 60 { (Cop-Comments $n '*Owner done:** Cursor agent*').Count -eq 1 }) (Cop-Brief $n)
        Check 'the done comment carries the agent''s session link' ((Cop-Comments $n "*Owner done:** Cursor agent*https://cursor.com/agents/$($a.id)/fake*").Count -eq 1) (Cop-Brief $n)
        Check 'and the agent is never deleted: its transcript stays readable' (Wait-Until -Seconds 10 { @((Cur-State).agents | Where-Object { $_.id -eq $a.id -and -not $_.deleted }).Count -eq 1 })
        Check 'one agent for the item, never a second' ((Cur-Agents $n).Count -eq 1)
        Check 'status: the item is no longer owned' (Wait-Until -Seconds 20 { @((Cur-Status).owned | Where-Object { $_.item -eq $n }).Count -eq 0 })
    } finally { Cur-Cleanup @($n) }
}

'cursor: a launch refused for the team''s usage limit is a quota block, not a failure: the item waits, starts pause, the owner action shows, and the probe after quotaRetryMinutes clears it' = {
    $n = 0
    try {
        [void](Fake 'cursor-usage-limit' @{ on = $true })
        $n = New-Item-Fake 'Cursor quota' 'x' $CurLabels
        Check 'status: quota-exhausted with Cursor''s message and the owner action' (Wait-Until -Seconds 40 { $q = (Cur-Status).quota; $q.exhausted -and "$($q.message)" -like '*prepaid balance*' -and "$($q.ownerAction)" -like 'buy Cursor team credits*' }) ((Cur-Status).quota | ConvertTo-Json -Compress)
        Check 'the refusal is no failure of the item and the runtime stays healthy' (@((Cur-Status).failures | Where-Object { $_.item -eq $n }).Count -eq 0 -and (Cur-Status).healthy) (Cur-Status | ConvertTo-Json -Compress -Depth 5)
        Check 'no agent exists while the quota is exhausted' ((Cur-Agents $n).Count -eq 0)
        [void](Fake 'cursor-usage-limit' @{ on = $false })
        Check 'after quotaRetryMinutes the probe launches an agent and clears the quota' (Wait-Until -Seconds 90 { (Cur-Agents $n).Count -ge 1 -and -not (Cur-Status).quota.exhausted }) ((Cur-Status).quota | ConvertTo-Json -Compress)
    } finally { [void](Fake 'cursor-usage-limit' @{ on = $false }); Cur-Cleanup @($n) }
}

'cursor: an agent that ends by itself is kept as the owner and its item taken back; a restarted gateway deletes no agent and keeps the owner agent' = {
    $n = 0
    try {
        $n = New-Item-Fake 'Cursor idle' 'x' $CurLabels
        Check 'an agent is launched' (Wait-Until -Seconds 30 { (Cur-Agents $n).Count -gt 0 })
        $a = (Cur-Agents $n)[0]
        [void](Fake 'cursor-agent' @{ id = $a.id; status = 'IDLE'; run = 'FINISHED' })
        Check 'its end is seen; the agent is kept as the owner' (Wait-Until -Seconds 20 { @((Cur-Status).last | Where-Object { $_.execution -eq $a.id }).Count -ge 1 -and @((Cur-State).agents | Where-Object { $_.id -eq $a.id -and -not $_.deleted }).Count -eq 1 }) ((Cur-Status) | ConvertTo-Json -Compress -Depth 5)
        Check 'the owner record names it' (Wait-Until -Seconds 20 { (Get-ItemState $n).copilot_task.id -eq $a.id })
        $o = (Fake 'cursor-add' @{ name = 'fworker-tdd-cur-999-abcdef'; ageMinutes = 30 }).id
        $young = (Fake 'cursor-add' @{ name = 'fworker-tdd-cur-997-abcdef' }).id
        $other = (Fake 'cursor-add' @{ name = 'fworker-prod-cur-998-abcdef'; ageMinutes = 30 }).id
        Restart-Gateway
        Check 'after a restart no agent is deleted: left-over ones keep their transcripts' (Wait-Until -Seconds 30 { @((Cur-State).agents | Where-Object { $_.id -in $o, $young -and -not $_.deleted }).Count -eq 2 })
        Check 'another environment''s agent is left alone' (@((Cur-State).agents | Where-Object { $_.id -eq $other -and -not $_.deleted }).Count -eq 1)
        Check 'the owner agent of the open item is kept' (@((Cur-State).agents | Where-Object { $_.id -eq $a.id -and -not $_.deleted }).Count -eq 1)
    } finally { Cur-Cleanup @($n) }
}

}

# The deadline has its own suite ('cursor-deadline', api.cursor.maxMinutes 0.25): in 'cursor' it would end the agent of any scenario that runs over
# 15 s on a loaded runner, so that suite keeps a generous maxMinutes.
$CursorDeadlineScenarios = [ordered]@{

'cursor: a failed launch frees the slot and the item gets a new agent; an agent past api.cursor.maxMinutes is cancelled and deleted' = {
    $n = 0
    try {
        [void](Fake 'cursor-launch-failures' @{ count = 1 })
        $n = New-Item-Fake 'Cursor relaunch' 'x' $CurLabels
        Check 'after the failed launch a new agent is launched' (Wait-Until -Seconds 40 { (Cur-Agents $n).Count -ge 1 })
        Check 'status: the failure was counted against the item' (@((Cur-Status).failures | Where-Object { $_.item -eq $n -and $_.count -ge 1 }).Count -eq 1) (Cur-Status | ConvertTo-Json -Compress -Depth 5)
        Check 'status: the failure shows the launch''s HTTP status and Cursor''s answer' (@((Cur-Status).failures | Where-Object { $_.item -eq $n -and "$($_.error)" -like 'launch-400: *fake: model not available for this account*' }).Count -eq 1) (Cur-Status | ConvertTo-Json -Compress -Depth 5)
        Check 'status: the ended execution in last[] keeps the answer (a park clears failures[])' (@((Cur-Status).last | Where-Object { $_.item -eq $n -and $_.outcome -eq 'start-failed' -and "$($_.error)" -like 'launch-400: *fake: model not available*' }).Count -ge 1) ((Cur-Status).last | ConvertTo-Json -Compress -Depth 3)
        Check 'the gateway log carries Cursor''s answer, not the key' (@(Get-ChildItem $script:Work -Filter 'gateway*.out' | Select-String -SimpleMatch 'fake: model not available for this account').Count -gt 0 -and @(Get-ChildItem $script:Work -Filter 'gateway*.out' | Select-String -SimpleMatch 'fake-cursor').Count -eq 0)
        $a = (Cur-Agents $n)[-1]
        Check 'the deadline (0.25 min in this suite) cancels its run (the owner agent is kept for the next run)' (Wait-Until -Seconds 60 { $x = @((Cur-State).agents | Where-Object { $_.id -eq $a.id }); $x.Count -eq 1 -and $x[0].cancelled })
        Check 'its r1 no longer works: the name is revoked when its agent ends' (Wait-Until -Seconds 30 { (Mcp 'tools/list' @{} (Cur-R1 $a)).status -eq 401 })
    } finally { Cur-Cleanup @($n) }
}
}
