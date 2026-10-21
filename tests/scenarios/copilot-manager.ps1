# Tier 1 scenarios of the plant manager on demand (gateway/Runtimes/ManagerCopilot.cs, docs/design/agents-roles-launch.md): when the digest holds
# something that needs a judgement and no supervisor is present, the gateway launches ONE short Copilot task for the manager role, against the fake
# agent tasks API of tests/FakeGitHub. The scenario plays the agent: it exchanges the task's one-time code for the r1 and calls /mcp. Suite
# 'copilot-manager' (owner mode, manager enabled: 15 s cooldown, 3 tasks a day, a question is late after 3 s). Uses the helpers of copilot.ps1,
# messaging.ps1 and mcp.ps1. The scenarios run in order and share the suite's gateway (the cooldown and the daily count carry over).

function CopMgr-Tasks { , @((Cop-State).tasks | Where-Object { "$($_.body.prompt)" -match 'plant manager for ONE short patrol' }) }
function CopMgr-Status { (Get-Plant).manager.copilot }
function CopMgr-Ask([string]$Text) { $r = Msg-Post '/api/v1/messages' @{ type = 'question'; text = $Text } (Msg-Token 'alice'); return $r }
function CopMgr-R1($Task) { $x = Cop-Exchange (Cop-Code $Task) (Cop-Bootstrap); return "$($x.body.token)" }
function CopMgr-Open { @((Invoke-RestMethod "http://127.0.0.1:$($script:ApiPort)/api/v1/questions?state=open" -Headers @{ Authorization = 'Bearer test-token' }).questions) }

$CopilotManagerScenarios = [ordered]@{

'copilot manager: nothing pending launches nothing' = {
    Start-Sleep -Seconds 12
    Check 'no manager task was created' ((CopMgr-Tasks).Count -eq 0) ((Cop-State).tasks | ConvertTo-Json -Compress -Depth 3)
    $m = CopMgr-Status
    Check 'status: manager.copilot is on, idle, with no launch' ($m.enabled -eq $true -and $m.present -eq $false -and $m.launches -eq 0 -and $m.today -eq 0 -and $m.maxPerDay -eq 3 -and $m.maxMinutes -eq 1) ($m | ConvertTo-Json -Compress)
}

'copilot manager: a late question launches one task with the manager prompt; its token reaches the manager tools only; session_end clears present' = {
    $q = CopMgr-Ask 'Which file holds the vendor email check?'
    Check 'a worker question is accepted and open' ($q.status -lt 300) ($q | ConvertTo-Json -Compress)
    Check 'one manager task is created for it' (Wait-Until -Seconds 40 { (CopMgr-Tasks).Count -eq 1 }) ((CopMgr-Status) | ConvertTo-Json -Compress)
    $t = (CopMgr-Tasks)[0]; $b = $t.body
    Check 'the body: base_ref master, no pull request by Copilot, no model, nothing else' ($b.base_ref -eq 'master' -and $b.create_pull_request -eq $false -and -not $b.PSObject.Properties['model'] -and @($b.PSObject.Properties.Name).Count -eq 3) ($b | ConvertTo-Json -Compress)
    Check 'the prompt is the manager bootstrap: terse, edits nothing, carries a code and no credential' ("$($b.prompt)" -match 'no\s+file edits' -and "$($b.prompt)" -match 'fworker_connect' -and (Cop-Code $t) -ne '' -and -not ("$($b.prompt)" -match 'r1\.|b1\.|i1\.|w1\.|fake-copilot'))
    $m = CopMgr-Status
    Check 'status: present while the task runs, with the launch counts and the task link' ($m.present -eq $true -and $m.running -eq $true -and $m.launches -eq 1 -and $m.today -eq 1 -and $m.task -eq $t.id -and "$($m.url)" -like 'https://*') ($m | ConvertTo-Json -Compress)
    $r1 = CopMgr-R1 $t
    Check 'the code and the bootstrap token give an r1' ($r1 -like 'r1.*')
    $names = @((Mcp-ToolNames $r1) | ForEach-Object { $_ } | Sort-Object)
    $want = (@('manager_digest', 'manager_questions', 'manager_answer', 'manager_note', 'manager_action', 'manager_item', 'manager_patrol_ack', 'session_end', 'get_prompt') | Sort-Object) -join ','
    Check 'tools/list shows the manager tools only' ((($names -join ',') -eq $want)) ($names -join ',')
    $gp = Mcp-Call 'get_prompt' @{} $r1
    Check 'get_prompt serves manager/patrol' (-not $gp.isError -and "$($gp.body.prompt)" -eq 'manager/patrol') ($gp | ConvertTo-Json -Compress -Depth 4)
    # a worker verb, a merge, a verdict, a pull request tool: refused
    foreach ($tool in 'item_accept', 'item_complete', 'pr_open', 'item_validation_verdict', 'item_comment', 'merge') {
        $x = Mcp-Call $tool @{ item = 1; column = 'Development'; comment = 'x'; head = 'x'; title = 'x'; passed = $true; summary = 'x'; pr = 1; head_sha = 'x' } $r1
        Check "the manager token cannot call $tool" ($x.isError -and $x.body.code -in 'forbidden', 'unknown-tool') ($x | ConvertTo-Json -Compress -Depth 4)
    }
    $mg = Mcp-Call 'manager_action' @{ action = 'merge'; item = 1; policy = 'operator-request'; reason = 'x' } $r1
    Check 'manager_action refuses a merge' ($mg.isError) ($mg | ConvertTo-Json -Compress -Depth 4)
    $dg = Mcp-Call 'manager_digest' @{} $r1
    Check 'manager_digest answers' (-not $dg.isError -and $dg.body.ok -eq $true) ($dg | ConvertTo-Json -Compress -Depth 4)
    $qs = Mcp-Call 'manager_questions' @{} $r1
    $open = @($qs.body.questions | Where-Object { "$($_.text)" -match 'vendor email' })
    Check 'manager_questions lists the open question' (-not $qs.isError -and $open.Count -eq 1) ($qs | ConvertTo-Json -Compress -Depth 5)
    $an = Mcp-Call 'manager_answer' @{ inReplyTo = "$($open[0].id)"; text = 'Look in the Vendor entity validation.'; note = 'answered from the item' } $r1
    Check 'manager_answer answers it' (-not $an.isError) ($an | ConvertTo-Json -Compress -Depth 5)
    Check 'the question is no longer open' (Wait-Until -Seconds 10 { @(CopMgr-Open | Where-Object { "$($_.text)" -match 'vendor email' }).Count -eq 0 })
    $nt = Mcp-Call 'manager_note' @{ text = 'nothing else needs you'; subject = 'patrol' } $r1
    Check 'manager_note reaches the operator' (-not $nt.isError) ($nt | ConvertTo-Json -Compress -Depth 5)
    $se = Mcp-Call 'session_end' @{ reason = 'patrol done' } $r1
    Check 'session_end is accepted' (-not $se.isError) ($se | ConvertTo-Json -Compress -Depth 4)
    Check 'present is cleared and the end is recorded' (Wait-Until -Seconds 15 { $s = CopMgr-Status; $s.present -eq $false -and $s.running -eq $false -and $s.launches -eq 1 -and $s.lastEnd }) ((CopMgr-Status) | ConvertTo-Json -Compress)
    Check 'the task link stays in the status' ("$((CopMgr-Status).url)" -like 'https://*')
    Check 'the r1 is refused after the end' (Wait-Until -Seconds 30 { (Mcp 'tools/list' @{} $r1).status -eq 401 })
    Check 'the code is spent' ((Cop-Exchange (Cop-Code $t) (Cop-Bootstrap)).status -eq 401)
    Check 'the end is logged with the task' (Wait-Until -Seconds 10 { @(Get-ChildItem $script:Work -Filter 'gateway*.out' | Select-String -SimpleMatch '"manager":"copilot-ended"').Count -gt 0 })
    Check 'neither token nor code is in the gateway log' (@(Get-ChildItem $script:Work -Filter 'gateway*.out' | Select-String -SimpleMatch (Cop-Code $t)).Count -eq 0)
}

'copilot manager: with nothing pending after the cooldown there is no second launch' = {
    Start-Sleep -Seconds 20
    Check 'still one task, one launch' ((CopMgr-Tasks).Count -eq 1 -and (CopMgr-Status).launches -eq 1) ((CopMgr-Status) | ConvertTo-Json -Compress)
    Check 'status says nothing is pending' ("$((CopMgr-Status).next)" -eq 'nothing-pending') ((CopMgr-Status) | ConvertTo-Json -Compress)
}

'copilot manager: the cooldown holds a new question back; the second and third tasks launch after it; a task that ends by itself clears present; the daily cap holds the fourth' = {
    [void](CopMgr-Ask 'Second question about the room number?')
    Check 'the second task launches' (Wait-Until -Seconds 40 { (CopMgr-Tasks).Count -eq 2 }) ((CopMgr-Status) | ConvertTo-Json -Compress)
    $t2 = (CopMgr-Tasks)[1]
    Check 'only one task runs at a time' ((CopMgr-Status).launches -eq 2 -and (CopMgr-Status).present -eq $true)
    $r2 = CopMgr-R1 $t2
    [void](Mcp-Call 'session_end' @{ reason = 'done' } $r2)
    Check 'session_end ends the second' (Wait-Until -Seconds 15 { (CopMgr-Status).present -eq $false })
    [void](CopMgr-Ask 'Third question about the building?')
    Start-Sleep -Seconds 6
    Check 'inside the cooldown no new task is created' ((CopMgr-Tasks).Count -eq 2) ((CopMgr-Status) | ConvertTo-Json -Compress)
    Check 'status says cooldown' ("$((CopMgr-Status).next)" -eq 'cooldown') ((CopMgr-Status) | ConvertTo-Json -Compress)
    Check 'after the cooldown the third task launches' (Wait-Until -Seconds 45 { (CopMgr-Tasks).Count -eq 3 }) ((CopMgr-Status) | ConvertTo-Json -Compress)
    $t3 = (CopMgr-Tasks)[2]
    $r3 = CopMgr-R1 $t3
    Check 'the third task has its r1' ($r3 -like 'r1.*')
    [void](Fake 'copilot-task' @{ id = $t3.id; state = 'completed'; credits = 1000 })
    Check 'a task that ends by itself clears present' (Wait-Until -Seconds 30 { $s = CopMgr-Status; $s.present -eq $false -and $s.running -eq $false }) ((CopMgr-Status) | ConvertTo-Json -Compress)
    Check 'its usage is reported in the status' (Wait-Until -Seconds 15 { (CopMgr-Status).usage.ai_credits -eq 1000 }) ((CopMgr-Status) | ConvertTo-Json -Compress)
    Check 'the r1 of the ended task is refused' (Wait-Until -Seconds 30 { (Mcp 'tools/list' @{} $r3).status -eq 401 })
    [void](CopMgr-Ask 'Fourth question about the vendor phone?')
    Start-Sleep -Seconds 25
    Check 'the daily cap of 3 holds: no fourth task' ((CopMgr-Tasks).Count -eq 3 -and (CopMgr-Status).launches -eq 3 -and (CopMgr-Status).today -eq 3) ((CopMgr-Status) | ConvertTo-Json -Compress)
    Check 'status says daily-cap' ("$((CopMgr-Status).next)" -eq 'daily-cap') ((CopMgr-Status) | ConvertTo-Json -Compress)
}

}
