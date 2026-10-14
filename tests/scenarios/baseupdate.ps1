# The standing back-merge trigger (docs/design/plant-manager.md 6a): when master moves, the owners of items with an open branch get base.updated;
# a clean branch that is only behind is updated by the gateway after a delay. Runs in the owner suite (messaging off: base.updated is a plant
# trigger like item.assigned, not a message). The suite sets baseUpdate.autoUpdateDelaySeconds 6, quietSeconds 3, sweepCheckSeconds 3 and
# timing.baseUpdateDebounceSeconds 4 (run.ps1).

function Bu-Worker([string]$Name) {
    if (-not $script:Workers.ContainsKey($Name) -or $script:Workers[$Name].proc.HasExited) { Start-OwnerClient $Name 'persistent-local' '--capacity 20' }
}
# An item owned by $W with an open pull request of $Files, in Development.
function Bu-OwnedPr([string]$W, [string[]]$Files) {
    $n = New-Item-Fake "Back-merge $W" 'x' ($OwnerLabels + "worker:$W")
    [void](Wait-Until -Seconds 40 { (Get-Assigned $W $n).Count -gt 0 })
    $ev = (Get-Assigned $W $n)[0]
    $r = Fo $W @('accept', '--item', $n, '--assignment', $ev.assignment); CheckExit "accept $n" $r 0
    $r = Fo $W @('append-body', '--item', $n, '--column', 'Technical Design', '--content-file', (TmpFile "butd$n.md" "## Technical Design`n`n- change as described.`n")); CheckExit "append $n" $r 0
    $r = Fo $W @('record-review', '--item', $n, '--column', 'Technical Design', '--role', 'reviewer', '--text-file', (TmpFile "burv$n.md" "ok`nREVIEW: COMPLETE")); CheckExit "review $n" $r 0
    $c = Fo $W @('complete', '--item', $n, '--column', 'Technical Design', '--review-ref', $r.review_ref, '--comment', 'REVIEW: COMPLETE - design done'); CheckExit "complete TD $n" $c 0
    $br = "factory-worker/$n-backmerge"
    Fake 'push-branch' @{ branch = $br; files = $Files } | Out-Null
    $o = Fo $W @('open-pr', '--item', $n, '--head', $br, '--title', "Back-merge $n", '--body-file', (TmpFile "bupr$n.md" 'change')); CheckExit "open-pr $n" $o 0
    return @{ item = $n; pr = [int]$o.pullRequest.number; branch = $br }
}
function Bu-Status { (Get-Plant).baseUpdate }
# the gateway has seen the current head of master (its first look, or the last trigger): a merge after this is a change
function Bu-Synced { [void](Wait-Until -Seconds 30 -Message 'the gateway to see the head of master' { (Bu-Status).head -eq (Get-FakeState).masterSha }) }
function Bu-Events([string]$W, [int]$Item) { , @(Get-WorkerEvents $W | Where-Object { $_.event -eq 'base.updated' -and $_.item -eq $Item }) }
function Bu-MergeHook([int]$Pr, [string]$Ref, [string]$Sha) {
    Qc-Hook 'pull_request' @{ action = 'closed'; repository = @{ full_name = 'clearmeasure-aisf-sample-apps/cmdemo1-workorders' }; sender = @{ login = 'a-person' }
        pull_request = @{ number = $Pr; merged = $true; merge_commit_sha = $Sha; base = @{ ref = 'master' }; head = @{ ref = $Ref; sha = 'f' * 40; repo = @{ full_name = 'clearmeasure-aisf-sample-apps/cmdemo1-workorders' } } } }
}
function Bu-Journal([int]$n, [string]$Event) { @((Get-FakeIssue $n).comments | Where-Object { $_.body -match "<!-- plant event=$Event " }).Count -gt 0 }
function Bu-Puts([int]$Pr) { @((Fake 'log') | Where-Object { $_ -match "^PUT /repos/[^ ]+/pulls/$Pr/update-branch$" }).Count }

$BaseUpdateScenarios = [ordered]@{

'base update: a merge to master tells the other owners of open branches with the overlapping files, not the merged pull request''s own worker' = {
    Bu-Worker 'bm1'; Bu-Worker 'bm2'; Bu-Worker 'bm3'
    $a = Bu-OwnedPr 'bm1' @('docs/a.md', 'src/Shared.cs'); $b = Bu-OwnedPr 'bm2' @('docs/b.md'); $c = Bu-OwnedPr 'bm3' @('src/Shared.cs', 'docs/c.md')
    Bu-Synced
    $before = Bu-Status
    $m = Fake 'master-merge' @{ number = $c.pr }
    Check 'the merge webhook is accepted' ((Bu-MergeHook $c.pr $c.branch $m.sha) -eq 202)
    $got = Wait-Until -Seconds 30 { (Bu-Events 'bm1' $a.item).Count -gt 0 -and (Bu-Events 'bm2' $b.item).Count -gt 0 }
    Check 'both other owners got base.updated' $got
    $ea = (Bu-Events 'bm1' $a.item)[0]; $eb = (Bu-Events 'bm2' $b.item)[0]
    Check 'the event names the new master, the merged pull request, the item''s pull request and how far it is behind' ($ea.sha -eq $m.sha -and @($ea.prs) -contains $c.pr -and $ea.pr -eq $a.pr -and $ea.behind -ge 1 -and $ea.base -eq 'master') ($ea | ConvertTo-Json -Compress)
    Check 'the files changed on both sides are listed' (@($ea.overlap) -contains 'src/Shared.cs' -and @($ea.overlap).Count -eq 1) ($ea.overlap -join ',')
    Check 'no overlap for a pull request that shares no file' (@($eb.overlap).Count -eq 0) ($eb.overlap -join ',')
    Start-Sleep -Seconds 2
    Check 'the worker whose pull request was merged gets none' ((Bu-Events 'bm3' $c.item).Count -eq 0)
    $w = Fo 'bm1' @('wait-event', '--item', $a.item, '--after', '0', '--timeout', '2')
    Check 'wait-event sees it too (messaging is off in this suite)' (@($w.events | Where-Object { $_.event -eq 'base.updated' }).Count -eq 1) ($w | ConvertTo-Json -Compress -Depth 5)
    Check 'journal: base-updated on the item' (Wait-Until -Seconds 20 { Bu-Journal $a.item 'base-updated' })
    $after = Bu-Status
    Check '/status counts the trigger and the events' ($after.triggers -eq $before.triggers + 1 -and $after.eventsSent -ge $before.eventsSent + 2) ($after | ConvertTo-Json -Compress)
}

'base update: three merges in a burst give one trigger with the latest master [full]' = {
    Bu-Worker 'bm1'
    $a = Bu-OwnedPr 'bm1' @('docs/burst.md')
    Bu-Synced
    $before = Bu-Status; $prs = @()
    foreach ($k in 1..3) { $m = Fake 'master-merge' @{ files = @("docs/other$k.md") }; $prs += $m.number; [void](Bu-MergeHook $m.number $m.ref $m.sha); Start-Sleep -Milliseconds 500 }
    [void](Wait-Until -Seconds 30 { (Bu-Events 'bm1' $a.item).Count -gt 0 })
    Start-Sleep -Seconds 8
    $ev = Bu-Events 'bm1' $a.item
    Check 'one base.updated for the burst' ($ev.Count -eq 1) "$($ev.Count)"
    Check 'it carries the latest master and all three pull requests' ($ev[0].sha -eq $m.sha -and @($ev[0].prs).Count -eq 3 -and $ev[0].behind -eq 3) ($ev[0] | ConvertTo-Json -Compress)
    Check 'one trigger counted' ((Bu-Status).triggers -eq $before.triggers + 1)
}

'base update: without the webhook the sweep sees master move and triggers [full]' = {
    Bu-Worker 'bm1'
    $a = Bu-OwnedPr 'bm1' @('docs/sweep.md')
    Bu-Synced
    $m = Fake 'master-merge' @{ files = @('docs/elsewhere.md') }
    $got = Wait-Until -Seconds 30 { (Bu-Events 'bm1' $a.item).Count -gt 0 }
    Check 'base.updated arrives from the sweep' ($got -and (Bu-Events 'bm1' $a.item)[0].via -eq 'sweep-confirmed' -and (Bu-Events 'bm1' $a.item)[0].sha -eq $m.sha) ((Bu-Events 'bm1' $a.item) | ConvertTo-Json -Compress)
}

'base update: a clean pull request that is only behind is updated by the gateway when the owner does not act [full]' = {
    Bu-Worker 'bm1'
    $a = Bu-OwnedPr 'bm1' @('docs/clean.md')
    Bu-Synced
    $before = Bu-Status; $head0 = (Get-FakePr $a.pr).sha
    [void](Fake 'master-merge' @{ files = @('docs/x.md') })
    [void](Wait-Until -Seconds 30 { (Bu-Events 'bm1' $a.item).Count -gt 0 })
    Check 'not before the delay' ((Bu-Puts $a.pr) -eq 0)
    $done = Wait-Until -Seconds 40 { (Bu-Puts $a.pr) -gt 0 }
    Check 'the gateway called update branch' $done
    Check 'the branch moved' ((Get-FakePr $a.pr).sha -ne $head0)
    Check 'journal: branch-updated' (Wait-Until -Seconds 20 { Bu-Journal $a.item 'branch-updated' })
    Check '/status counts it' ((Bu-Status).gatewayUpdates -eq $before.gatewayUpdates + 1)
}

'base update: a pull request in conflict with master is left to its owner [full]' = {
    Bu-Worker 'bm1'
    $a = Bu-OwnedPr 'bm1' @('docs/conflict.md')
    Fake 'pr' @{ number = $a.pr; conflict = $true } | Out-Null
    Bu-Synced
    [void](Fake 'master-merge' @{ files = @('docs/conflict.md') })
    $got = Wait-Until -Seconds 30 { (Bu-Events 'bm1' $a.item).Count -gt 0 }
    Check 'the owner is told' $got
    Check 'the overlap names the file' (@((Bu-Events 'bm1' $a.item)[0].overlap) -contains 'docs/conflict.md')
    Start-Sleep -Seconds 14
    Check 'the gateway does not update the branch' ((Bu-Puts $a.pr) -eq 0)
}

'base update: with autoUpdateBranch off the gateway updates nothing [full]' = {
    Bu-Worker 'bm1'
    $a = Bu-OwnedPr 'bm1' @('docs/off.md')
    Bu-Synced
    $h = @{ Authorization = 'Bearer test-token' }
    try {
        $s = Invoke-RestMethod "$(Get-GatewayBase)/api/v1/base-update?autoUpdateBranch=false" -Method POST -Headers $h
        Check 'the switch is off' ($s.autoUpdateBranch -eq $false)
        [void](Fake 'master-merge' @{ files = @('docs/y.md') })
        $got = Wait-Until -Seconds 30 { (Bu-Events 'bm1' $a.item).Count -gt 0 }
        Check 'the owner is still told' $got
        Start-Sleep -Seconds 14
        Check 'no update branch' ((Bu-Puts $a.pr) -eq 0)
    } finally { [void](Invoke-RestMethod "$(Get-GatewayBase)/api/v1/base-update?autoUpdateBranch=true" -Method POST -Headers $h) }
}

'base update: an item whose PASS verdict waits for its merge gets no trigger [full]' = {
    Bu-Worker 'bm1'
    $a = Bu-OwnedPr 'bm1' @('docs/pass.md'); $b = Bu-OwnedPr 'bm1' @('docs/pass2.md')
    # the state of a PASS verdict that the gateway has not merged yet (pass-unmerged)
    $sc = @((Get-FakeIssue $a.item).comments | Where-Object { $_.body -like '<!-- factory-state:*' })[0]
    $mm = [regex]::Match($sc.body, '(?s)```json\s*(.*?)\s*```'); $st = $mm.Groups[1].Value | ConvertFrom-Json -AsHashtable
    $st.verdict = @{ passed = $true; processed = $false; pr = $a.pr }
    $body = $sc.body.Substring(0, $mm.Groups[1].Index) + ($st | ConvertTo-Json -Depth 12 -Compress) + $sc.body.Substring($mm.Groups[1].Index + $mm.Groups[1].Length)
    [void](Invoke-RestMethod "http://127.0.0.1:$($script:FakePort)/repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders/issues/comments/$($sc.id)" -Method PATCH -Body (@{ body = $body } | ConvertTo-Json) -ContentType 'application/json')
    Bu-Synced
    [void](Fake 'master-merge' @{ files = @('docs/z.md') })
    $got = Wait-Until -Seconds 30 { (Bu-Events 'bm1' $b.item).Count -gt 0 }
    Check 'the other item is told' $got
    Check 'the pass-unmerged item is not' ((Bu-Events 'bm1' $a.item).Count -eq 0)
}

'base update: a session that reconnects is replayed the latest base.updated only [full]' = {
    Bu-Worker 'bm4'
    $a = Bu-OwnedPr 'bm4' @('docs/replay.md')
    Bu-Synced
    # (the gateway's own update would bring the branch up to date between the two merges: off for this scenario)
    $h = @{ Authorization = 'Bearer test-token' }; [void](Invoke-RestMethod "$(Get-GatewayBase)/api/v1/base-update?autoUpdateBranch=false" -Method POST -Headers $h)
    try {
    Stop-OwnerClient 'bm4'
    [void](Wait-Until -Seconds 20 { @((Get-Plant).plant.workers | Where-Object { $_.name -eq 'bm4' -and $_.connected }).Count -eq 0 })
    $t0 = (Bu-Status).eventsSent
    [void](Fake 'master-merge' @{ files = @('docs/r1.md') }); [void](Wait-Until -Seconds 30 { (Bu-Status).eventsSent -ge $t0 + 1 })
    $m = Fake 'master-merge' @{ files = @('docs/r2.md') }; [void](Wait-Until -Seconds 30 { (Bu-Status).eventsSent -ge $t0 + 2 })
    $out = Join-Path $script:Work 'bm4-replay.txt'
    & curl -s -N --max-time 4 -H "Authorization: Bearer $($script:Workers['bm4'].token)" -H 'Last-Event-ID: 0' "$(Get-GatewayBase)/api/v1/events?name=bm4&type=persistent-local&capacity=20&roles=owner" -o $out 2>$null
    $lines = @(Get-Content $out -ErrorAction SilentlyContinue | Where-Object { $_ -like 'data:*' } | ForEach-Object { try { $_.Substring(5).Trim() | ConvertFrom-Json } catch { $null } } | Where-Object { $_ -and $_.event -eq 'base.updated' -and $_.item -eq $a.item })
    Check 'one base.updated replayed, the latest' ($lines.Count -eq 1 -and $lines[0].sha -eq $m.sha) "$($lines.Count) $(@($lines | ForEach-Object { $_.sha }) -join ',') want $($m.sha)"
    $w = Fo 'bm4' @('wait-event', '--item', $a.item, '--after', '0', '--timeout', '2')
    Check 'the item''s mailbox holds the latest only' (@($w.events | Where-Object { $_.event -eq 'base.updated' }).Count -eq 1) ($w | ConvertTo-Json -Compress -Depth 5)
    } finally { [void](Invoke-RestMethod "$(Get-GatewayBase)/api/v1/base-update?autoUpdateBranch=true" -Method POST -Headers $h) }
}
}
foreach ($k in $BaseUpdateScenarios.Keys) { $OwnerScenarios[$k] = $BaseUpdateScenarios[$k] }
