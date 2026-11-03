# Tier 1 scenarios. Each one drives the real gateway and rule scripts against the fake GitHub; the scripted worker stands in for Claude.
# Tier tag: a scenario whose name ends in " [full]" is full-tier only; the untagged ones are the fast-tier smoke checks (see Get-ScenarioTier in lib.ps1).
$L = @('factory worker', 'Auto Merge')

$Scenarios = [ordered]@{

# Session trouble does not spend an item's relaunch budget: a claim the gateway took back (taken_back) is relaunched without counting against
# maxRelaunches, up to maxSessionRelaunches (10) in a row, whose park is session-relaunch-budget; a plain lapse counts as before; a Functional
# Testing round that runs out without a verdict is parked as validation-no-verdict, not as a relaunch budget.
'relaunch budget: session take-backs do not spend it, their own limit parks with session-relaunch-budget, and the park codes say why' = {
    $n = New-Item-Fake 'Relaunch budget' 'x' @('factory worker', 'Auto Merge')
    $r = F 'w1' @('claim-item', '--item', $n, '--column', 'Technical Design'); CheckExit 'first claim' $r 0
    foreach ($i in 1..10) { Expire-Claim $n 'client-accepted-not-started'; $r = F 'w1' @('claim-item', '--item', $n, '--column', 'Technical Design'); if ($r.exit -ne 0) { break } }
    CheckExit 'ten session take-backs in a row: still relaunched' $r 0
    $st = Get-ItemState $n
    Check 'the item budget is untouched, the session count is 10' ([int]$st.claim.launches -eq 1 -and [int]$st.claim.session_launches -eq 10) ($st.claim | ConvertTo-Json -Compress)
    Expire-Claim $n 'no-answer-to-item-check'; $r = F 'w1' @('claim-item', '--item', $n, '--column', 'Technical Design')
    CheckExit 'the eleventh is parked as session trouble' $r 10 'parked'
    $st = Get-ItemState $n
    Check 'park code session-relaunch-budget with the last take-back' ($st.park.reason -eq 'session-relaunch-budget' -and $st.park.last_take_back -eq 'no-answer-to-item-check') ($st.park | ConvertTo-Json -Compress)

    $m = New-Item-Fake 'Relaunch budget, item trouble' 'x' @('factory worker', 'Auto Merge')
    $r = F 'w1' @('claim-item', '--item', $m, '--column', 'Technical Design'); CheckExit 'first claim of the second item' $r 0
    Expire-Claim $m 'client-accepted-not-started'; $r = F 'w1' @('claim-item', '--item', $m, '--column', 'Technical Design'); CheckExit 'a take-back' $r 0
    foreach ($i in 1..3) { Expire-Claim $m; $r = F 'w1' @('claim-item', '--item', $m, '--column', 'Technical Design') }
    CheckExit 'three plain lapses are within the budget of 3' $r 0
    Check 'a plain lapse resets the run of session take-backs' (-not (Get-ItemState $m).claim.ContainsKey('session_launches'))
    Expire-Claim $m; $r = F 'w1' @('claim-item', '--item', $m, '--column', 'Technical Design')
    CheckExit 'the fourth lapse parks' $r 10 'parked'
    Check 'park code watchdog-relaunch-budget' ((Get-ItemState $m).park.reason -eq 'watchdog-relaunch-budget') ((Get-ItemState $m) | ConvertTo-Json -Compress -Depth 4)

    $v = New-Item-Fake 'Relaunch budget, no verdict' 'x' @('factory worker', 'Auto Merge') 'Functional Testing'
    $r = F 'w1' @('claim-item', '--item', $v, '--column', 'Functional Testing'); CheckExit 'claim in Functional Testing' $r 0
    foreach ($i in 1..4) { Expire-Claim $v; $r = F 'w1' @('claim-item', '--item', $v, '--column', 'Functional Testing') }
    CheckExit 'the budget runs out in Functional Testing' $r 10 'parked'
    Check 'park code validation-no-verdict' ((Get-ItemState $v).park.reason -eq 'validation-no-verdict') ((Get-ItemState $v).park | ConvertTo-Json -Compress)
}


'happy-docs-only (auto policy)' = {
    $n = New-Item-Fake 'Add a docs file' 'Add docs/a.md' $L
    Do-TechnicalDesign $n
    Check 'moved to Development' ((Get-FakeIssue $n).column -eq 'Development')
    $pr = Do-Development $n
    $r = F 'w1' @('wait-for-checks', '--item', $n); CheckExit 'checks green' $r 0
    $r = Do-DevComplete $n; CheckExit 'development complete' $r 0
    Check 'moved to Functional Testing' ((Get-FakeIssue $n).column -eq 'Functional Testing')
    $v = Do-Verdict $n $pr; CheckExit 'verdict PASS' $v 0; Check 'next is merge' ($v.next -eq 'merge')
    $m = F 'w1' @('merge', '--item', $n, '--pr', $pr); CheckExit 'merge' $m 0; Check 'merged outcome (V3.2: the saga may have merged the PASS already)' ($m.outcome -in 'merged', 'already-merged') "$($m.outcome)"
    Check 'PR merged in fake' (Wait-Until -Seconds 30 { (Get-FakePr $pr).merged })
    $done = Wait-Until -Seconds 40 { (Get-FakeIssue $n).column -eq 'Done' }
    Check 'sweep settled the docs-only item to Done' $done "column $((Get-FakeIssue $n).column)"
    Check 'issue closed' (Wait-Until -Seconds 30 { -not (Get-FakeIssue $n).open })
}

'orphan claim refs: an accept takes one over, the sweep removes those of closed items and keeps a young one (issue 47)' = {
    $inst = "$((Get-Content -Raw (Join-Path $script:Work 'instance.test.json') | ConvertFrom-Json).name)"
    $repo = "http://127.0.0.1:$($script:FakePort)/repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders"
    $refs = { @((Invoke-RestMethod "$repo/git/matching-refs/factory/$inst/claims/" -Headers @{ Authorization = 'Bearer fake-operator' }) | ForEach-Object { $_ } | ForEach-Object { "$($_.ref)" }) }
    # a ref left by an accept cut off between the ref and the state: the item state holds no claim
    $n = New-Item-Fake 'Orphan ref' 'x' $L
    Gh-As 'fake-operator' 'git/refs' @{ ref = "refs/factory/$inst/claims/$n"; sha = ('0' * 40) } | Out-Null
    $r = F 'w1' @('claim-item', '--item', $n, '--column', 'Technical Design'); CheckExit 'the claim takes the orphan ref over' $r 0
    Check 'and says so' ($r.orphan_ref_taken_over -eq $true) ($r | ConvertTo-Json -Compress)
    $r = F 'w2' @('claim-item', '--item', $n, '--column', 'Technical Design'); CheckExit 'a ref behind a live claim is still refused' $r 40 'claim-held'
    # a closed item's ref goes at the next sweep; an open item's orphan is kept until it is older than a lease (30 min here)
    $c = New-Item-Fake 'Closed with a ref' 'x' $L; $o = New-Item-Fake 'Young orphan' 'x' $L
    Gh-As 'fake-operator' 'git/refs' @{ ref = "refs/factory/$inst/claims/$c"; sha = ('0' * 40) } | Out-Null
    Gh-As 'fake-operator' 'git/refs' @{ ref = "refs/factory/$inst/claims/$o"; sha = ('0' * 40) } | Out-Null
    Invoke-RestMethod "$repo/issues/$c" -Method PATCH -Headers @{ Authorization = 'Bearer fake-operator' } -Body (@{ state = 'closed' } | ConvertTo-Json) -ContentType 'application/json' | Out-Null
    $gone = Wait-Until -Seconds 30 { "refs/factory/$inst/claims/$c" -notin (& $refs) }
    Check 'the sweep removed the claim ref of the closed item' $gone ((& $refs) -join ',')
    $now = & $refs
    Check 'the young orphan of an open item and the live claim are kept' ("refs/factory/$inst/claims/$o" -in $now -and "refs/factory/$inst/claims/$n" -in $now) ($now -join ',')
}

# Issue 400: the reap takes the item lock without waiting. It waited 45 s (Use-ItemLock's default) for a lock a verb held, and so stalled the whole
# sweep. The harness holds the lock of a closed item with a claim ref: the sweeps go on and leave the ref; once the lock is free a later one removes it.
'orphan claim refs: a busy item lock does not stall the sweep; a later sweep removes the ref once the lock is free (issue 400) [full]' = {
    $inst = "$((Get-Content -Raw (Join-Path $script:Work 'instance.test.json') | ConvertFrom-Json).name)"
    $repo = "http://127.0.0.1:$($script:FakePort)/repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders"
    $refs = { @((Invoke-RestMethod "$repo/git/matching-refs/factory/$inst/claims/" -Headers @{ Authorization = 'Bearer fake-operator' }) | ForEach-Object { $_ } | ForEach-Object { "$($_.ref)" }) }
    $c = New-Item-Fake 'Closed with a ref, busy' 'x' $L
    Invoke-RestMethod "$repo/issues/$c" -Method PATCH -Headers @{ Authorization = 'Bearer fake-operator' } -Body (@{ state = 'closed' } | ConvertTo-Json) -ContentType 'application/json' | Out-Null
    $ref = "refs/factory/$inst/claims/$c"
    # the lock a verb would hold (the rule scripts' item lock file), taken before the ref exists: no sweep can reap the ref before the lock is held
    $lock = Lock-ItemFile $c
    try {
        Gh-As 'fake-operator' 'git/refs' @{ ref = $ref; sha = ('0' * 40) } | Out-Null
        # two full sweeps after the ref, each with a reap that meets the lock; with the 45 s wait the first of them alone outlasted this
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $swept = try { Wait-Sweeps -Count 2 -Seconds 30; $true } catch { $_.Exception.Message }
        Check 'the sweeps go on while the item lock is held' ($swept -eq $true) "$swept; $([int]$sw.Elapsed.TotalSeconds) s"
        Check 'and leave the ref of the busy item for a later sweep' ($ref -in (& $refs)) ((& $refs) -join ',')
    } finally { $lock.Dispose() }
    Check 'once the lock is free a later sweep removes the ref' (Wait-Until -Seconds 30 { $ref -notin (& $refs) }) ((& $refs) -join ',')
}

# Issue 411: a verb that creates a work item (the keeper's create-item and split-item) holds the board-intake lock from the new issue until its card is
# in its column, and the board guard places a card only under that lock, taken without waiting. Here the harness holds the lock, as such a verb does,
# while a labelled issue has no card: two full sweeps finish within 30 s and leave the issue to the holder; once the lock is free a later sweep's guard
# places it and journals it once.
'board guard: an issue a verb is still putting on the board is left to it; the guard places it once the intake lock is free (issue 411) [full]' = {
    $lock = Lock-IntakeFile
    $n = 0
    try {
        $n = (Fake 'issue' @{ title = 'Created by a verb that has not placed it yet'; body = 'x'; labels = $L }).number
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $swept = try { Wait-Sweeps -Count 2 -Seconds 30; $true } catch { $_.Exception.Message }
        Check 'the sweeps go on while the intake lock is held' ($swept -eq $true) "$swept; $([int]$sw.Elapsed.TotalSeconds) s"
        Check 'and leave the issue to the holder of the lock' ($null -eq (Get-FakeIssue $n).column -and @((Get-FakeIssue $n).comments | Where-Object { $_.body -match 'plant event=board-added ' }).Count -eq 0) ((Get-FakeIssue $n) | ConvertTo-Json -Compress -Depth 3)
    } finally { $lock.Dispose() }
    Check 'once the lock is free a later sweep puts it in Technical Design' (Wait-Until -Seconds 40 { (Get-FakeIssue $n).column -eq 'Technical Design' }) ((Get-FakeIssue $n) | ConvertTo-Json -Compress -Depth 3)
    Check 'and journals it once' (Wait-Until -Seconds 20 { @((Get-FakeIssue $n).comments | Where-Object { $_.body -match 'plant event=board-added ' }).Count -eq 1 })
}

'gates refuse what they should [full]' = {
    $n = New-Item-Fake 'Gates' 'x' $L
    $r = F 'w1' @('claim-item', '--item', $n, '--column', 'Technical Design'); CheckExit 'claim' $r 0
    $r = F 'w2' @('claim-item', '--item', $n, '--column', 'Technical Design'); CheckExit 'second holder refused' $r 40 'claim-held'
    $r = F 'w1' @('complete', '--item', $n, '--column', 'Technical Design', '--comment', 'REVIEW: COMPLETE'); CheckExit 'no section' $r 10 'missing-required-section'
    $r = F 'w1' @('complete', '--item', $n, '--column', 'Development', '--comment', 'x'); CheckExit 'stale column' $r 11 'stale-column'
    $r = F 'w1' @('append-body', '--item', $n, '--column', 'Technical Design', '--content-file', (TmpFile 'g.md' "## Technical Design`n- x`n")); CheckExit 'append' $r 0
    $r = F 'w1' @('complete', '--item', $n, '--column', 'Technical Design', '--comment', 'done'); CheckExit 'no verdict line' $r 10 'reviewer-verdict-missing'
    $r = F 'w1' @('complete', '--item', $n, '--column', 'Technical Design', '--comment', 'REVIEW: COMPLETE', '--review-ref', 'rv-forged'); CheckExit 'forged review ref' $r 10 'review-ref-invalid'
    $r = F 'w1' @('record-review', '--item', $n, '--column', 'Technical Design', '--role', 'reviewer', '--text-file', (TmpFile 'bad.md' "fine`nREVIEW: LGTM")); CheckExit 'bad verdict line' $r 10 'no-verdict-line'
    $r = F 'w1' @('record-review', '--item', $n, '--column', 'Technical Design', '--role', 'implementer', '--text-file', (TmpFile 'imp.md' "x`nREVIEW: COMPLETE")); CheckExit 'only the reviewer records a review' $r 10 'not-reviewer'
    foreach ($i in 1..3) { $r = F 'w1' @('record-review', '--item', $n, '--column', 'Technical Design', '--role', 'reviewer', '--text-file', (TmpFile "f$i.md" "gap`nREVIEW: FINDINGS`n- x")) }
    $r = F 'w1' @('record-review', '--item', $n, '--column', 'Technical Design', '--role', 'reviewer', '--text-file', (TmpFile 'f4.md' "gap`nREVIEW: FINDINGS`n- x")); CheckExit 'review rounds capped at 3' $r 10 'review-rounds-exhausted'
    $r = F 'w1' @('merge', '--item', $n, '--pr', 1); CheckExit 'merge outside Functional Testing' $r 10 'wrong-column'
    $r = F 'w1' @('move-next', '--item', $n); CheckExit 'move-next out of an AI column' $r 10 'completion-owns-exit'
    $r = F 'w1' @('complete', '--item', $n, '--column', 'Technical Design', '--succeeded', 'false', '--comment', 'giving up'); CheckExit 'failure report accepted' $r 0
    $r = F 'w9' @('claim-item', '--item', $n, '--column', 'Technical Design'); CheckExit 'parked item cannot be claimed' $r 10 'parked'
    $x = New-Item-Fake 'Both labels' 'x' @('factory worker', 'AI Factory'); $r = F 'w1' @('claim-item', '--item', $x, '--column', 'Technical Design'); CheckExit 'ambiguous owner' $r 10 'ambiguous-owner'
    $y = New-Item-Fake 'No label' 'x' @('Auto Merge'); $r = F 'w1' @('claim-item', '--item', $y, '--column', 'Technical Design'); CheckExit 'no worker label' $r 10 'no-worker-label'
    $z = New-Item-Fake 'Demo' 'x' @('factory worker', 'Demo'); $r = F 'w1' @('claim-item', '--item', $z, '--column', 'Technical Design'); CheckExit 'demo exclusion' $r 10 'demo-exclusion'
}

'CI alongside: pending checks do not park Development when the gateway asks for Functional Testing to start beside them [full]' = {
    # the feature plan of the v2 engine (W7.3, delivery-speed.md 5.1): the gateway's v2 feed adds ftWithPendingChecks to the completion of an item whose saga watches the checks (the rule script run directly, as the feed's call reaches it)
    $n = New-Item-Fake 'CI alongside' 'x' $L; Do-TechnicalDesign $n
    $pr = Do-Development $n @('docs/c.md') 'w1' 'red'
    $r = Do-DevComplete $n; CheckExit 'a red head is refused as before' $r 10 'checks-not-green'
    Fake 'push-branch' @{ branch = "factory-worker/$n-development"; files = @('docs/c.md'); profile = 'green'; delaySeconds = 30 } | Out-Null   # long enough that two slow verbs (record-review, complete) under load still find the checks pending
    $rv = F 'w1' @('record-review', '--item', $n, '--column', 'Development', '--role', 'reviewer', '--text-file', (TmpFile "rv-alongside$n.md" "Diff matches the plan.`nREVIEW: COMPLETE"))
    $r = Script-Complete @{ item = $n; column = 'Development'; review_ref = $rv.review_ref; comment = 'PR open, CI running. REVIEW: COMPLETE'; holder = 'w1'; ftWithPendingChecks = $true }
    CheckExit 'pending checks are no reason to park: the completion goes through' $r 0
    Check 'the answer says the checks are pending and CI runs alongside' ("$($r.checks)" -eq 'pending' -and "$($r.ci)" -eq 'alongside' -and "$($r.moved_to)" -eq 'Functional Testing') ($r | ConvertTo-Json -Compress)
    Check 'the card is in Functional Testing at once' ((Get-FakeIssue $n).column -eq 'Functional Testing') "column $((Get-FakeIssue $n).column)"
}

'verdict gates and demotion' = {
    $n = New-Item-Fake 'Verdicts' 'x' $L; Do-TechnicalDesign $n
    $pr = Do-Development $n; Do-DevComplete $n | Out-Null
    $r = F 'w1' @('claim-item', '--item', $n, '--column', 'Functional Testing'); CheckExit 'FT claim' $r 0
    $r = F 'w1' @('validation-verdict', '--item', $n, '--passed', 'true', '--summary-file', (TmpFile 'v.md' 'ok'), '--pr', $pr); CheckExit 'PASS without a recorded review' $r 10 'review-ref-invalid'
    $r = F 'w1' @('merge', '--item', $n, '--pr', $pr); CheckExit 'merge before a verdict' $r 10 'no-pass-verdict'
    $r = F 'w1' @('validation-verdict', '--item', $n, '--passed', 'true', '--summary-file', (TmpFile 'v2.md' 'ok'), '--pr', 0); CheckExit 'PASS needs a PR number' $r 2
    $r = F 'w1' @('validation-verdict', '--item', $n, '--passed', 'false', '--summary-file', (TmpFile 'v3.md' 'criterion 2 not met'), '--pr', $pr); CheckExit 'FAIL verdict' $r 0
    Check 'FAIL demoted the item to Development' ((Get-FakeIssue $n).column -eq 'Development') "column $((Get-FakeIssue $n).column)"
    $g = F 'w1' @('get-item', '--item', $n); Check 'rejection text reaches the next Development run' ($g.existing_pr.rejection -like '*criterion 2*') "$($g.existing_pr | ConvertTo-Json -Compress)"
}

'video label: PASS without an MP4 is downgraded [full]' = {
    $n = New-Item-Fake 'Video' 'x' ($L + 'video'); Do-TechnicalDesign $n
    $pr = Do-Development $n; Do-DevComplete $n | Out-Null
    $r = F 'w1' @('claim-item', '--item', $n, '--column', 'Functional Testing'); CheckExit 'FT claim' $r 0
    $rv = F 'w1' @('record-review', '--item', $n, '--column', 'Functional Testing', '--role', 'reviewer', '--text-file', (TmpFile 'vr.md' "seen`nREVIEW: COMPLETE"))
    $r = F 'w1' @('validation-verdict', '--item', $n, '--passed', 'true', '--summary-file', (TmpFile 'vv.md' 'looks right'), '--pr', $pr, '--review-ref', $rv.review_ref)
    Check 'downgraded' ((Pv $r 'downgraded') -eq $true -and (Pv $r 'passed') -eq $false) ($r | ConvertTo-Json -Compress)
}

'video label: publish-demo creates the release, a second publish replaces the MP4, and the PASS stands' = {
    $n = New-Item-Fake 'Video published' 'x' ($L + 'video'); Do-TechnicalDesign $n
    $pr = Do-Development $n; Do-DevComplete $n | Out-Null
    $r = F 'w1' @('claim-item', '--item', $n, '--column', 'Functional Testing'); CheckExit 'FT claim' $r 0
    $mp4 = New-TestMp4 'demo-a.mp4' 4096
    $r = F 'w1' @('publish-demo', '--item', $n, '--file', $mp4); CheckExit 'publish-demo' $r 0
    Check 'the answer names the release and the asset' ($r.release -eq "demo-$n" -and $r.asset -eq "demo-$n.mp4" -and $r.size -eq 4096 -and $r.created -eq $true -and $r.replaced -eq $false -and "$($r.release_url)" -like "*/releases/tag/demo-$n") ($r | ConvertTo-Json -Compress)
    $rel = Invoke-RestMethod "http://127.0.0.1:$($script:FakePort)/repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders/releases/tags/demo-$n"
    Check 'the release has the shape of a demo release' ($rel.prerelease -eq $true -and $rel.target_commitish -eq 'master' -and $rel.name -eq "Demo video for #$n" -and @($rel.assets).Count -eq 1 -and $rel.assets[0].size -eq 4096 -and $rel.assets[0].content_type -eq 'video/mp4') ($rel | ConvertTo-Json -Compress -Depth 4)
    $r = F 'w1' @('publish-demo', '--item', $n, '--file', (New-TestMp4 'demo-b.mp4' 8192)); CheckExit 'a second publish' $r 0
    Check 'the second publish reuses the release and replaces the asset' ($r.created -eq $false -and $r.replaced -eq $true -and $r.size -eq 8192) ($r | ConvertTo-Json -Compress)
    $rel = Invoke-RestMethod "http://127.0.0.1:$($script:FakePort)/repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders/releases/tags/demo-$n"
    Check 'one asset, the new one' (@($rel.assets).Count -eq 1 -and $rel.assets[0].size -eq 8192) ($rel.assets | ConvertTo-Json -Compress)
    $bad = TmpFile 'not-a-video.mp4' 'plain text that is named like a video'
    $r = F 'w1' @('publish-demo', '--item', $n, '--file', $bad); CheckExit 'a file that is not an MP4 is refused by the client' $r 2 'not-mp4'
    $r = Invoke-WebRequest "http://127.0.0.1:$($script:ApiPort)/api/v1/demo/$n" -Method POST -InFile $bad -ContentType 'video/mp4' -Headers @{ Authorization = 'Bearer test-token' } -SkipHttpErrorCheck
    Check 'and by the gateway' ([int]$r.StatusCode -eq 415 -and ($r.Content | ConvertFrom-Json).code -eq 'not-mp4') "$([int]$r.StatusCode) $($r.Content)"
    $big = Join-Path $script:Work 'big.mp4'; $fs = [IO.File]::Create($big); try { $fs.Write([byte[]](0, 0, 0, 24) + [Text.Encoding]::ASCII.GetBytes('ftypisom'), 0, 12); $fs.SetLength(101MB) } finally { $fs.Dispose() }
    $r = F 'w1' @('publish-demo', '--item', $n, '--file', $big); CheckExit 'a video over 100 MB is refused' $r 2 'too-large'
    Remove-Item $big -Force
    $r = F 'w1' @('publish-demo', '--item', $n, '--file', (Join-Path $script:Work 'missing.mp4')); CheckExit 'a missing file' $r 2 'no-file'
    $r = Invoke-WebRequest "http://127.0.0.1:$($script:ApiPort)/api/v1/verbs/publish-demo" -Method POST -Body (@{ item = $n; file = '/etc/hostname' } | ConvertTo-Json) -ContentType 'application/json' -Headers @{ Authorization = 'Bearer test-token' } -SkipHttpErrorCheck
    Check 'the verb route does not take a file path' ([int]$r.StatusCode -eq 400 -and ($r.Content | ConvertFrom-Json).code -eq 'use-endpoint') "$([int]$r.StatusCode) $($r.Content)"
    # three uploads at once: the gateway takes two, the third is refused busy, and the first two complete
    Fake 'latency' @{ ms = 5000; match = '^POST /_uploads/' } | Out-Null
    $u = "http://127.0.0.1:$($script:ApiPort)/api/v1/demo/$n"; $f1 = New-TestMp4 'demo-c.mp4' 1024
    $n2 = New-Item-Fake 'Video two' 'x' ($L + 'video') 'Functional Testing'
    $jobs = @($n, $n2) | ForEach-Object { $ui = "http://127.0.0.1:$($script:ApiPort)/api/v1/demo/$_"; Start-ThreadJob { Invoke-WebRequest $using:ui -Method POST -InFile $using:f1 -ContentType 'video/mp4' -Headers @{ Authorization = 'Bearer test-token' } -SkipHttpErrorCheck } }
    Start-Sleep -Seconds 2
    $r3 = Invoke-WebRequest $u -Method POST -InFile $f1 -ContentType 'video/mp4' -Headers @{ Authorization = 'Bearer test-token' } -SkipHttpErrorCheck
    Check 'a third concurrent upload is refused busy' ([int]$r3.StatusCode -eq 429 -and ($r3.Content | ConvertFrom-Json).code -eq 'busy') "$([int]$r3.StatusCode) $($r3.Content)"
    $done = @($jobs | Wait-Job -Timeout 120 | Receive-Job); Fake 'latency' @{ ms = 0 } | Out-Null
    Check 'the uploads in progress complete' ($done.Count -eq 2 -and @($done | Where-Object { [int]$_.StatusCode -eq 200 }).Count -eq 2) (($done | ForEach-Object { "$([int]$_.StatusCode)" }) -join ',')
    $rv = F 'w1' @('record-review', '--item', $n, '--column', 'Functional Testing', '--role', 'reviewer', '--text-file', (TmpFile 'vpr.md' "seen at 00:12`nREVIEW: COMPLETE"))
    $r = F 'w1' @('validation-verdict', '--item', $n, '--passed', 'true', '--summary-file', (TmpFile 'vpv.md' 'criterion 1 at 00:12'), '--pr', $pr, '--review-ref', $rv.review_ref)
    Check 'with the MP4 published the PASS is not downgraded' ((Pv $r 'passed') -eq $true -and (Pv $r 'downgraded') -eq $false) ($r | ConvertTo-Json -Compress)
    $o = New-Item-Fake 'No video label' 'x' $L 'Functional Testing'
    $r = F 'w1' @('publish-demo', '--item', $o, '--file', $mp4); CheckExit 'an item without the video label owes no video' $r 10 'not-video-item'
    $t = New-Item-Fake 'Video in design' 'x' ($L + 'video') 'Technical Design'
    $r = F 'w1' @('publish-demo', '--item', $t, '--file', $mp4); CheckExit 'not in Technical Design' $r 10 'wrong-column'
}

# Issue 426: a sweep answers within its budget. Its plan (the item loop) is made first; the tail steps (claim-ref reap, intake, board guard, replenish)
# run only while budget remains and are deferred otherwise, and every step reports its milliseconds, so a slow step is named. The automatic promotion
# is no step of the sweep (issue 429: the verb auto-promote, its own loop).
'sync-board: with its budget used up the tail steps are deferred, the plan is still answered and each step reports its time' = {
    $n = New-Item-Fake 'Budgeted' 'x' $L
    $sw = F 'w1' @('sync-board', '--budget_seconds', '0.001'); $swj = $sw | ConvertTo-Json -Depth 12 -Compress
    Check 'the sweep succeeds and still sees the item' ((Pv $sw 'ok') -eq $true -and (($swj -match "`"item`":$n[,}]") -or (@(Pv $sw 'unconfirmed') -contains $n))) $swj
    $sk = @(Pv $sw 'skipped')
    Check 'the claim-ref reap, the replenish and the board guard are deferred' (($sk -contains 'claim-ref-reap-deferred') -and ($sk -contains 'replenish-deferred') -and ($sk -contains 'board-guard-deferred')) $swj
    Check 'the answer has the milliseconds of the items step and a total' ($null -ne (Pv $sw 'elapsed_ms') -and $null -ne (Pv (Pv $sw 'steps') 'items')) $swj
    $sw2 = F 'w1' @('sync-board'); $swj2 = $sw2 | ConvertTo-Json -Depth 12 -Compress
    $sk2 = @(Pv $sw2 'skipped')
    Check 'with the default budget nothing is deferred and every tail step reports its time' ((-not ($sk2 | Where-Object { $_ -like '*-deferred' })) -and $null -eq (Pv (Pv $sw2 'steps') 'auto-promote') -and $null -ne (Pv (Pv $sw2 'steps') 'replenish')) $swj2
}

# A card of another repository linked to the board (issue 269): its number is not an issue of this repository. The sweep never lists it, writes
# nothing on the issue of that number and never moves its card; a projects_v2_item webhook about it makes no event, one about a home card does.
'board: a card of another repository linked to the board is never seen: no sweep entry, no comment, no column event' = {
    $f = (Fake 'issue' @{ title = 'Foreign'; body = 'x'; labels = @(@($L) + @($script:DefaultEnvLabel | Where-Object { $_ })); column = 'Technical Design'; repo = 'clearmeasure-aisf-sample-apps/other-repo' }).number
    $n = New-Item-Fake 'Home' 'x' $L
    $sw = F 'w1' @('sync-board'); $swj = $sw | ConvertTo-Json -Depth 12 -Compress
    Check 'the sweep sees the home card' (($swj -match "`"item`":$n[,}]") -or (@(Pv $sw 'unconfirmed') -contains $n)) $swj
    # the sweep names an item as "item":N in its entries, as a bare number in its lists and as a key of its maps
    $named = $swj -match "`"item`":$f[,}]"
    foreach ($k in 'unconfirmed', 'settling', 'holds', 'unparked') { if (@(Pv $sw $k) -contains $f) { $named = $true } }
    foreach ($k in 'owners', 'pins', 'priorities') { $mp = Pv $sw $k; if ($mp -is [System.Collections.IDictionary] -and $mp.Contains("$f")) { $named = $true } }
    Check 'the sweep never names the card of the other repository' (-not $named) $swj
    Start-Sleep -Seconds 6
    $fi = Get-FakeIssue $f
    Check 'after several sweeps: no comment on the issue of the other repository' (@($fi.comments | Where-Object { $_ }).Count -eq 0) ($fi.comments | ConvertTo-Json -Compress -Depth 4)
    Check 'and its card was never moved' ($fi.column -eq 'Technical Design') "column $($fi.column)"
    $hook = {
        param([int]$Num, [string]$Delivery)
        $body = @{ event = 'projects_v2_item'; delivery = $Delivery; payload = @{ action = 'edited'; projects_v2_item = @{ content_node_id = "I_$Num" }; changes = @{ field_value = @{ field_name = 'Status'; to = @{ name = 'Development' } } } } }
        $r = Invoke-WebRequest -Uri "$(Get-GatewayBase)/api/v1/verbs/ingest-event" -Method POST -Body ($body | ConvertTo-Json -Compress -Depth 10) -ContentType 'application/json' -Headers @{ Authorization = 'Bearer test-token' } -SkipHttpErrorCheck -TimeoutSec 120
        try { return ("$($r.Content)" | ConvertFrom-Json -AsHashtable) } catch { return @{ ok = $false; code = "http-$([int]$r.StatusCode)" } }
    }
    $ev = & $hook $f 'd-foreign'
    Check 'a projects_v2_item webhook about the card of the other repository makes no event' ($ev.ok -and @($ev.events | Where-Object { $_ }).Count -eq 0) ($ev | ConvertTo-Json -Compress -Depth 6)
    $evH = & $hook $n 'd-home'
    Check 'the same webhook about the home card makes its item.column_changed' ($evH.ok -and @($evH.events | Where-Object { $_ -and $_.event -eq 'item.column_changed' -and $_.item -eq $n -and $_.column -eq 'Development' }).Count -eq 1) ($evH | ConvertTo-Json -Compress -Depth 6)
}

'model ladder: loops escalate the tier, never back down [full]' = {
    $n = New-Item-Fake 'Ladder' 'x' $L
    $r = F 'w1' @('claim-item', '--item', $n, '--column', 'Technical Design'); CheckExit 'claim' $r 0
    $t = F 'w1' @('model-tier', '--item', $n); Check 'a fresh run starts on the economy tier' ($t.tier -eq 0) "$($t.tier)"
    $mf = F 'w1' @('model-for', '--role', 'subagent', '--item', $n); Check 'client resolves the economy rung (haiku 5.5, xhigh)' ((Pv $mf 'model') -eq 'claude-haiku-5-5' -and (Pv $mf 'effort') -eq 'xhigh') "$($mf | ConvertTo-Json -Compress)"
    Check 'the answer carries the short alias haiku' ((Pv $mf 'alias') -eq 'haiku')
    foreach ($i in 1..3) { F 'w1' @('complete', '--item', $n, '--column', 'Technical Design', '--comment', 'x') | Out-Null }
    $t = F 'w1' @('model-tier', '--item', $n); Check 'three refused calls escalate to standard' ($t.tier -eq 1) "tier $($t.tier) $($t.reasons -join ',')"
    $mf = F 'w1' @('model-for', '--role', 'orchestrator', '--item', $n); Check 'client resolves sonnet 5.5 medium' ((Pv $mf 'model') -eq 'claude-sonnet-5-5' -and (Pv $mf 'effort') -eq 'medium') "$($mf | ConvertTo-Json -Compress)"
    Check 'the answer carries the short alias sonnet' ((Pv $mf 'alias') -eq 'sonnet')
    foreach ($i in 1..3) { F 'w1' @('complete', '--item', $n, '--column', 'Technical Design', '--comment', 'x') | Out-Null }
    $t = F 'w1' @('model-tier', '--item', $n); Check 'six refused calls escalate to premium' ($t.tier -eq 2) "tier $($t.tier)"
    $mf = F 'w1' @('model-for', '--role', 'subagent', '--item', $n); Check 'client resolves opus 5.5 medium' ((Pv $mf 'model') -eq 'claude-opus-5-5' -and (Pv $mf 'effort') -eq 'medium') "$($mf | ConvertTo-Json -Compress)"
    Check 'the answer carries the short alias opus' ((Pv $mf 'alias') -eq 'opus')
    $m = New-Item-Fake 'Ladder 2' 'x' $L; Do-TechnicalDesign $m
    $pr = Do-Development $m; Do-DevComplete $m | Out-Null
    $v = Do-Verdict $m $pr 'w1' $false
    $t = F 'w1' @('model-tier', '--item', $m); Check 'a failed validation (demotion) escalates the next Development run' ($t.tier -eq 1 -and $t.column -eq 'Development') "tier $($t.tier) $($t.column) $($t.reasons -join ',')"
    $cfgFixed = TmpFile 'models-fixed.json' (@{ mode = 'fixed'; fixed = @{ orchestrator = @{ model = 'claude-sonnet-5-5'; effort = 'high' }; subagent = @{ model = 'claude-haiku-5-5'; effort = 'high' } } } | ConvertTo-Json -Depth 5)
    $env:FACTORY_MODELS_CONFIG = $cfgFixed
    $mf = F 'w1' @('model-for', '--role', 'subagent', '--item', $m); Remove-Item Env:FACTORY_MODELS_CONFIG
    Check 'fixed mode on a machine pins the model whatever the tier' ((Pv $mf 'model') -eq 'claude-haiku-5-5' -and (Pv $mf 'mode') -eq 'fixed') "$($mf | ConvertTo-Json -Compress)"
}

'testing profile: the cheapest model, pinned even when the item loops [full]' = {
    $n = New-Item-Fake 'Stress' 'replace foo with bar in docs/a.md' ($L + 'factory test')
    $r = F 'w1' @('claim-item', '--item', $n, '--column', 'Technical Design'); CheckExit 'claim' $r 0
    $mf = F 'w1' @('model-for', '--role', 'subagent', '--item', $n)
    Check 'a labelled item uses haiku 5.5 at low effort' ((Pv $mf 'model') -eq 'claude-haiku-5-5' -and (Pv $mf 'effort') -eq 'low' -and (Pv $mf 'mode') -eq 'profile') "$($mf | ConvertTo-Json -Compress)"
    foreach ($i in 1..6) { F 'w1' @('complete', '--item', $n, '--column', 'Technical Design', '--comment', 'x') | Out-Null }
    $t = F 'w1' @('model-tier', '--item', $n); Check 'the gateway still sees the loop (tier 2)' ($t.tier -eq 2) "tier $($t.tier)"
    $mf = F 'w1' @('model-for', '--role', 'orchestrator', '--item', $n)
    Check 'but the testing profile does not climb to a dearer model' ((Pv $mf 'model') -eq 'claude-haiku-5-5' -and (Pv $mf 'effort') -eq 'low') "$($mf | ConvertTo-Json -Compress)"
    $m = New-Item-Fake 'Plain' 'x' $L
    $env:FACTORY_MODEL_PROFILE = 'testing'; $mf = F 'w1' @('model-for', '--role', 'subagent', '--item', $m); Remove-Item Env:FACTORY_MODEL_PROFILE
    Check 'a machine can force the profile with an environment variable' ((Pv $mf 'effort') -eq 'low' -and (Pv $mf 'mode') -eq 'profile') "$($mf | ConvertTo-Json -Compress)"
    $mf = F 'w1' @('model-for', '--role', 'subagent', '--item', $m)
    Check 'an unlabelled item still uses the ladder' ((Pv $mf 'mode') -eq 'auto' -and (Pv $mf 'effort') -eq 'xhigh') "$($mf | ConvertTo-Json -Compress)"
}

'docs-only mismatch: a docs PR without the marker is refused' = {
    $n = New-Item-Fake 'Mismatch' 'x' $L; Do-TechnicalDesign $n 'w1' $false
    $pr = Do-Development $n @('docs/only.md')
    $r = Do-DevComplete $n; CheckExit 'docs-only PR without the marker' $r 10 'development-docs-only-pr'
}


'escalate earlier: the same criterion cited twice parks the item with a question; timing criteria are flagged at Technical Design' = {
    $crit = 'The work order list shows the room number.'
    $rec = { param($n, $name, $text, $col = 'Technical Design') F 'w1' @('record-review', '--item', $n, '--column', $col, '--role', 'reviewer', '--text-file', (TmpFile "$name.md" $text)) }
    # two consecutive review rounds that cite the same criterion
    $n = New-Item-Fake 'Room number' "Acceptance criteria:`n- $crit`n- The list loads within 2 seconds for 500 work orders." $L
    $r = F 'w1' @('claim-item', '--item', $n, '--column', 'Technical Design'); CheckExit 'claim' $r 0
    Check 'the timing criterion is named in the claim answer' (@(Pv $r 'timing_criteria').Count -eq 1 -and "$(@(Pv $r 'timing_criteria')[0])" -like '*within 2 seconds*') ($r | ConvertTo-Json -Compress)
    Check 'and flagged on the item, once' (@((Get-FakeIssue $n).comments | Where-Object { $_.body -like '*Timing or statistical acceptance criteria*' }).Count -eq 1)
    $r = F 'w1' @('append-body', '--item', $n, '--column', 'Technical Design', '--content-file', (TmpFile 'es.md' "## Technical Design`n`nFiles: src/a.cs`n- add the column.`n")); CheckExit 'design' $r 0
    $r = & $rec $n 'es1' "REVIEW: FINDINGS`nFINDING: criterion `"$crit`" - the design adds no column"; CheckExit 'round 1 cites the criterion' $r 0
    $r = & $rec $n 'es2' "REVIEW: FINDINGS`nFINDING: criterion `"The work order list shows the   room number.`" - still missing"
    CheckExit 'round 2 citing the same criterion is refused' $r 10 'same-criterion-repeated'
    $g = F 'w1' @('get-item', '--item', $n); $park = Pv $g.state 'park'
    Check 'the item is parked for the owner' ($park -and $park.reason -eq 'same-criterion-repeated') ($g.state | ConvertTo-Json -Compress -Depth 5)
    Check 'with a precise question naming the criterion' (@((Get-FakeIssue $n).comments | Where-Object { $_.body -like '*Question for the owner*' -and $_.body -like "*$crit*" }).Count -eq 1)
    $r = F 'w1' @('claim-item', '--item', $n, '--column', 'Technical Design'); CheckExit 'a parked item is not claimed again' $r 10 'parked'
    $cited = Pv $g.state 'cited'
    Check 'parking forgets the cited records, so the round after the answer starts fresh' ($null -eq $cited -or @($cited.PSObject.Properties).Count -eq 0) ($g.state | ConvertTo-Json -Compress -Depth 5)
    # a criterion that itself contains quotes is counted
    $qc = 'The button reads "Save order" in bold.'
    $k = New-Item-Fake 'Quoted repeat' "Acceptance criteria:`n- $qc" $L
    $r = F 'w1' @('claim-item', '--item', $k, '--column', 'Technical Design')
    $r = F 'w1' @('append-body', '--item', $k, '--column', 'Technical Design', '--content-file', (TmpFile 'esq.md' "## Technical Design`n`nFiles: src/a.cs`n- add the button.`n"))
    $r = & $rec $k 'esq1' "REVIEW: FINDINGS`nFINDING: criterion `"$qc`" - no bold"; CheckExit 'quoted round 1' $r 0
    $r = & $rec $k 'esq2' "REVIEW: FINDINGS`nFINDING: criterion `"$qc`" - still no bold"; CheckExit 'quoted round 2 parks' $r 10 'same-criterion-repeated'
    # different criteria, or a COMPLETE between, do not escalate
    $m = New-Item-Fake 'Two criteria' "Acceptance criteria:`n- $crit`n- The list is sorted by the created date." $L
    $r = F 'w1' @('claim-item', '--item', $m, '--column', 'Technical Design'); Check 'no timing criterion, no flag' ($null -eq (Pv $r 'timing_criteria'))
    $r = F 'w1' @('append-body', '--item', $m, '--column', 'Technical Design', '--content-file', (TmpFile 'es3.md' "## Technical Design`n`nFiles: src/a.cs`n- add the column.`n"))
    $r = & $rec $m 'es4' "REVIEW: FINDINGS`nFINDING: criterion `"$crit`" - missing"; CheckExit 'round 1' $r 0
    $r = & $rec $m 'es5' "REVIEW: FINDINGS`nFINDING: criterion `"The list is sorted by the created date.`" - missing"; CheckExit 'round 2 cites another criterion' $r 0
    $r = & $rec $m 'es6' "REVIEW: FINDINGS`nFINDING: criterion `"$crit`" - missing again"; CheckExit 'not consecutive: round 3 goes on' $r 0
    # two consecutive FAIL verdicts that cite the same criterion
    $v = New-Item-Fake 'Verdict repeat' "Acceptance criteria:`n- $crit" $L; Do-TechnicalDesign $v
    $pr = Do-Development $v; Do-DevComplete $v | Out-Null
    $r = F 'w1' @('claim-item', '--item', $v, '--column', 'Functional Testing'); CheckExit 'FT claim' $r 0
    $r = F 'w1' @('validation-verdict', '--item', $v, '--passed', 'false', '--summary-file', (TmpFile 'ev1.md' "criterion `"$crit`" is not met: no column"), '--pr', $pr); CheckExit 'FAIL 1' $r 0
    Check 'FAIL 1 demotes as before' ($r.next -eq 'demoted' -and (Get-FakeIssue $v).column -eq 'Development') ($r | ConvertTo-Json -Compress -Depth 4)
    $r = F 'w1' @('claim-item', '--item', $v, '--column', 'Development'); CheckExit 'Development claim after the demotion' $r 0
    $r = Do-DevComplete $v; CheckExit 'development complete again' $r 0
    $r = F 'w1' @('claim-item', '--item', $v, '--column', 'Functional Testing'); CheckExit 'FT claim 2' $r 0
    $r = F 'w1' @('validation-verdict', '--item', $v, '--passed', 'false', '--summary-file', (TmpFile 'ev2.md' "criterion `"$crit`" is still not met"), '--pr', $pr); CheckExit 'FAIL 2' $r 0
    Check 'FAIL 2 on the same criterion parks instead of demoting' ($r.next -eq 'parked' -and $r.reason -eq 'same-criterion-repeated') ($r | ConvertTo-Json -Compress -Depth 4)
    Check 'with the question to the owner' (@((Get-FakeIssue $v).comments | Where-Object { $_.body -like '*Question for the owner*FAIL verdicts*' }).Count -eq 1)
}

'docs-only fast path: one design review round, a second only for a finding that quotes the item' = {
    $crit = 'The README names the health check route /_healthcheck.'
    $rec = { param($n, $name, $text, $col = 'Technical Design') F 'w1' @('record-review', '--item', $n, '--column', $col, '--role', 'reviewer', '--text-file', (TmpFile "$name.md" $text)) }
    $n = New-Item-Fake 'Name the route' "Acceptance criteria:`n- $crit" $L
    $r = F 'w1' @('claim-item', '--item', $n, '--column', 'Technical Design'); CheckExit 'claim' $r 0
    $r = F 'w1' @('append-body', '--item', $n, '--column', 'Technical Design', '--content-file', (TmpFile 'fp.md' "## Technical Design`n`nFiles: README.md`n<!-- aisf:docs-only -->`n- add one sentence under the heading Health.`n")); CheckExit 'design with the marker' $r 0
    $r = & $rec $n 'fp1' "The wording could be nicer.`nREVIEW: FINDINGS`n- could also mention the port"; CheckExit 'style and could-also remarks are no finding' $r 10 'finding-without-criterion'
    $r = & $rec $n 'fp2' "REVIEW: FINDINGS`nFINDING: criterion `"The README lists every environment variable.`" - missing"; CheckExit 'a quote that is not in the item is no finding' $r 10 'finding-without-criterion'
    $r = & $rec $n 'fp3' "REVIEW: FINDINGS`nFINDING: criterion `"add one sentence under the heading Health.`" - vague"; CheckExit 'the design quoting itself is no criterion' $r 10 'finding-without-criterion'
    $r = & $rec $n 'fp4' "REVIEW: FINDINGS`nFINDING: file `"src/Startup.cs`" - outside the scope"; CheckExit 'a file the design does not name is no finding' $r 10 'finding-without-criterion'
    Check 'a refused review used no round' ($null -eq (Pv (Pv (F 'w1' @('get-item', '--item', $n)).state 'reviews') 'Technical Design'))
    $r = & $rec $n 'fp5' "CRITERION: `"$crit`" - NO`nFINDING: criterion `"The README names the   health check route /_healthcheck.`" - the design never names the route`nFINDING: nicer wording please`nREVIEW: FINDINGS"
    CheckExit 'a finding that quotes the criterion counts' $r 0
    Check 'round 1 of 2, one finding kept, one dropped' ($r.verdict -eq 'FINDINGS' -and $r.round -eq 1 -and (Pv $r 'max_rounds') -eq 2 -and @(Pv $r 'findings').Count -eq 1 -and (Pv $r 'dropped') -eq 1) ($r | ConvertTo-Json -Compress -Depth 4)
    $r = & $rec $n 'fp6' "REVIEW: FINDINGS`nFINDING: file `"README.md`" - outside the scope"; CheckExit 'a file named in the design counts (round 2)' $r 0
    $r = & $rec $n 'fp7' "FILE: `"README.md`" - YES`nREVIEW: COMPLETE"; CheckExit 'a docs-only design has two rounds, not three' $r 10 'review-rounds-exhausted'
    # a criterion that itself contains double quotes is quoted whole
    $q = New-Item-Fake 'Quoted' "Acceptance criteria:`n- The guide says `"Ready`" in bold." $L
    $r = F 'w1' @('claim-item', '--item', $q, '--column', 'Technical Design')
    $r = F 'w1' @('append-body', '--item', $q, '--column', 'Technical Design', '--content-file', (TmpFile 'fpq.md' "## Technical Design`n`nFiles: docs/guide.md`n<!-- aisf:docs-only -->`n- edit the guide.`n"))
    $r = & $rec $q 'fpq1' "REVIEW: FINDINGS`nFINDING: criterion `"The guide says `"Ready`" in bold.`" - the design does not say bold"; CheckExit 'a criterion with quotes inside counts' $r 0
    # the same reviews on an item without the marker: the free form and the three rounds are unchanged
    $m = New-Item-Fake 'Code item' "Acceptance criteria:`n- $crit" $L
    $r = F 'w1' @('claim-item', '--item', $m, '--column', 'Technical Design'); CheckExit 'claim (no marker)' $r 0
    $r = F 'w1' @('append-body', '--item', $m, '--column', 'Technical Design', '--content-file', (TmpFile 'fpm.md' "## Technical Design`n`nFiles: src/a.cs`n- change the handler.`n")); CheckExit 'design without the marker' $r 0
    foreach ($i in 1..2) { $r = & $rec $m "fpm$i" "gap`nREVIEW: FINDINGS`n- x" }
    Check 'without the marker a free-form finding counts and a third round exists' ($r.exit -eq 0 -and $r.round -eq 2 -and (Pv $r 'max_rounds') -eq 3 -and -not $r.ContainsKey('findings')) ($r | ConvertTo-Json -Compress -Depth 4)
    $r = & $rec $m 'fpm3' "fixed`nREVIEW: COMPLETE"; Check 'round 3 completes' ($r.exit -eq 0 -and $r.round -eq 3 -and $r.review_ref) ($r | ConvertTo-Json -Compress)
    # one clean round takes a docs-only item out of Technical Design, and the Development review of that item keeps the free form
    $d = New-Item-Fake 'Clean docs item' "Acceptance criteria:`n- $crit" $L; Do-TechnicalDesign $d
    Check 'one round, then Development' ((Get-FakeIssue $d).column -eq 'Development')
    $r = F 'w1' @('claim-item', '--item', $d, '--column', 'Development'); $r = & $rec $d 'fpd' "gap`nREVIEW: FINDINGS`n- x" 'Development'
    Check 'the fast path is the design review only' ($r.exit -eq 0 -and (Pv $r 'max_rounds') -eq 3) ($r | ConvertTo-Json -Compress)
}

'docs-only fast path: the build is not owed only with the marker and documentation files; Functional Testing reads the diff' = {
    $open = { param($n, $files) Fake 'push-branch' @{ branch = "factory-worker/$n-development"; files = $files; profile = 'green'; delaySeconds = 0 } | Out-Null
        F 'w1' @('open-pr', '--item', $n, '--head', "factory-worker/$n-development", '--title', "Trial $n", '--body-file', (TmpFile "bo$n.md" 'change')) }
    $n = New-Item-Fake 'Docs' 'x' $L; Do-TechnicalDesign $n
    $r = F 'w1' @('claim-item', '--item', $n, '--column', 'Development'); $o = & $open $n @('docs/a.md', 'README.md'); CheckExit 'open-pr (docs)' $o 0
    Check 'marker and documentation files: no build owed' ((Pv $o 'build_owed') -eq $false -and "$(Pv $o 'build_reason')" -match 'no private build') ($o | ConvertTo-Json -Compress -Depth 4)
    $o2 = & $open $n @('docs/a.md', 'README.md'); Check 'the same answer for the pull request that already exists' ((Pv $o2 'created') -eq $false -and (Pv $o2 'build_owed') -eq $false) ($o2 | ConvertTo-Json -Compress -Depth 4)
    $g = F 'w1' @('get-item', '--item', $n); Check 'get-item in Development says the same' ((Pv $g 'build_owed') -eq $false -and (Pv $g.existing_pr 'docs_only_files') -eq $true) ($g | ConvertTo-Json -Compress -Depth 3)
    $r = F 'w1' @('wait-for-checks', '--item', $n); $r = Do-DevComplete $n; CheckExit 'complete' $r 0; Check 'complete reports that no build was owed' ((Pv $r 'build_owed') -eq $false) ($r | ConvertTo-Json -Compress)
    $r = F 'w1' @('claim-item', '--item', $n, '--column', 'Functional Testing'); $g = F 'w1' @('get-item', '--item', $n)
    Check 'Functional Testing verifies a documentation change against the diff' ((Pv $g 'verification') -eq 'diff') ($g | ConvertTo-Json -Compress -Depth 3)
    # the marker alone is not enough: a marked item whose pull request touches code owes the build and is tested in the application
    $k = New-Item-Fake 'Marked, but code' 'x' $L; Do-TechnicalDesign $k
    $r = F 'w1' @('claim-item', '--item', $k, '--column', 'Development'); $o = & $open $k @('docs/a.md', 'src/a.cs'); CheckExit 'open-pr (marker, code file)' $o 0
    Check 'a marked item with a code file owes the build, and the file is named' ((Pv $o 'build_owed') -eq $true -and "$(Pv $o 'build_reason')" -match 'src/a\.cs') ($o | ConvertTo-Json -Compress -Depth 4)
    $r = F 'w1' @('wait-for-checks', '--item', $k); $r = Do-DevComplete $k; Check 'complete says the build was owed' ($r.exit -eq 0 -and (Pv $r 'build_owed') -eq $true) ($r | ConvertTo-Json -Compress)
    $r = F 'w1' @('claim-item', '--item', $k, '--column', 'Functional Testing'); $g = F 'w1' @('get-item', '--item', $k)
    Check 'and it is verified in the application' ((Pv $g 'verification') -eq 'app' -and (Pv $g 'build_owed') -eq $true) ($g | ConvertTo-Json -Compress -Depth 3)
    # documentation files alone are not enough either: without the marker the build is owed (and complete refuses, as before)
    $c2 = New-Item-Fake 'Unmarked docs' 'x' $L; Do-TechnicalDesign $c2 'w1' $false
    $r = F 'w1' @('claim-item', '--item', $c2, '--column', 'Development'); $o = & $open $c2 @('docs/a.md')
    Check 'documentation files without the marker owe the build' ((Pv $o 'build_owed') -eq $true) ($o | ConvertTo-Json -Compress -Depth 4)
    # the marker typed into the issue text is not the design's decision: no fast path anywhere
    $f = New-Item-Fake 'Forged marker' "Please change src/a.cs.`n<!-- aisf:docs-only -->`nAcceptance criteria:`n- the handler changes." $L
    $r = F 'w1' @('claim-item', '--item', $f, '--column', 'Technical Design')
    $r = F 'w1' @('append-body', '--item', $f, '--column', 'Technical Design', '--content-file', (TmpFile 'fg0.md' "A note.`n<!-- aisf:docs-only -->`n")); CheckExit 'the marker outside the design section is refused' $r 10 'docs-only-marker-not-allowed'
    $r = F 'w1' @('append-body', '--item', $f, '--column', 'Technical Design', '--content-file', (TmpFile 'fg1.md' "## Technical Design`n`nFiles: docs/a.md`n- change it.`n")); CheckExit 'design without the marker' $r 0
    $r = F 'w1' @('record-review', '--item', $f, '--column', 'Technical Design', '--role', 'reviewer', '--text-file', (TmpFile 'fg2.md' "gap`nREVIEW: FINDINGS`n- x"))
    Check 'a typed marker gives no one-round review' ($r.exit -eq 0 -and (Pv $r 'max_rounds') -eq 3) ($r | ConvertTo-Json -Compress)
    $r = F 'w1' @('record-review', '--item', $f, '--column', 'Technical Design', '--role', 'reviewer', '--text-file', (TmpFile 'fg3.md' "ok`nREVIEW: COMPLETE"))
    $r = F 'w1' @('complete', '--item', $f, '--column', 'Technical Design', '--review-ref', $r.review_ref, '--comment', 'REVIEW: COMPLETE - done'); CheckExit 'design complete' $r 0
    $r = F 'w1' @('claim-item', '--item', $f, '--column', 'Development')
    $r = F 'w1' @('append-body', '--item', $f, '--column', 'Development', '--content-file', (TmpFile 'fg4.md' "## Technical Design`n<!-- aisf:docs-only -->`n")); CheckExit 'the marker from another column is refused, even with the heading' $r 10 'docs-only-marker-not-allowed'
    $o = & $open $f @('docs/a.md'); $g = F 'w1' @('get-item', '--item', $f)
    Check 'a typed marker: the build is owed, docs_only is false, verification is the application' ((Pv $o 'build_owed') -eq $true -and (Pv $g 'docs_only') -eq $false -and (Pv $g 'build_owed') -eq $true -and (Pv $g 'verification') -eq 'app') ($g | ConvertTo-Json -Compress -Depth 3)
    $r = F 'w1' @('wait-for-checks', '--item', $f); $r = Do-DevComplete $f; CheckExit 'a typed marker does not let a docs pull request through' $r 10 'development-docs-only-pr'
    # the design section and the marker both typed by the author, and a design append without the heading: completing the design records "not docs-only"
    $h = New-Item-Fake 'Forged section' "## Technical Design`n<!-- aisf:docs-only -->`n- typed by the author." $L
    $r = F 'w1' @('claim-item', '--item', $h, '--column', 'Technical Design')
    $r = F 'w1' @('append-body', '--item', $h, '--column', 'Technical Design', '--content-file', (TmpFile 'fh1.md' "Files: docs/a.md`n- change it.`n")); CheckExit 'append without the heading' $r 0
    $r = F 'w1' @('record-review', '--item', $h, '--column', 'Technical Design', '--role', 'reviewer', '--text-file', (TmpFile 'fh2.md' "ok`nREVIEW: COMPLETE"))
    $r = F 'w1' @('complete', '--item', $h, '--column', 'Technical Design', '--review-ref', $r.review_ref, '--comment', 'REVIEW: COMPLETE - done'); CheckExit 'design complete (typed section)' $r 0
    $r = F 'w1' @('claim-item', '--item', $h, '--column', 'Development'); $o = & $open $h @('docs/a.md')
    Check 'a typed section and marker: the build is owed' ((Pv $o 'build_owed') -eq $true) ($o | ConvertTo-Json -Compress -Depth 4)
    $r = F 'w1' @('wait-for-checks', '--item', $h); $r = Do-DevComplete $h; CheckExit 'and the docs pull request is refused' $r 10 'development-docs-only-pr'
    # an item designed by an older version: past the design column, marker in the body, no field in its state: the body marker still counts
    # (created before timing.docsOnlyLegacyBefore: only such an item is read by its body)
    $old = (Fake 'issue' @{ title = 'Legacy item'; body = "Add docs/a.md`n`n## Technical Design`n`n<!-- aisf:docs-only -->`n- change as described.`n"; labels = $L; column = 'Development'; createdAt = '2019-06-01T00:00:00Z' }).number
    $r = F 'w1' @('claim-item', '--item', $old, '--column', 'Development'); CheckExit 'claim (legacy state)' $r 0
    $o = & $open $old @('docs/a.md'); Check 'legacy state: no build owed' ((Pv $o 'build_owed') -eq $false) ($o | ConvertTo-Json -Compress -Depth 4)
    $r = F 'w1' @('wait-for-checks', '--item', $old); $r = Do-DevComplete $old; CheckExit 'legacy state: the docs pull request completes' $r 0
    # a video item is shown in the application even when it is documentation
    $v = New-Item-Fake 'Docs with video' 'x' ($L + 'video'); Do-TechnicalDesign $v
    $r = F 'w1' @('claim-item', '--item', $v, '--column', 'Development'); $o = & $open $v @('docs/a.md'); $g = F 'w1' @('get-item', '--item', $v)
    Check 'a video item: no build owed, but verified in the application' ((Pv $g 'build_owed') -eq $false -and (Pv $g 'verification') -eq 'app') ($g | ConvertTo-Json -Compress -Depth 3)
}

}

# ---- merge queue (policy auto, mergeQueueDepth 1) -----------------------------------------------------------------------------------
$QueueScenarios = [ordered]@{


# DbUp migration numbers (defect of 2026-10-23: items 580 and 589 each added 040 and both merged): Development's complete and the merge refuse a
# pull request whose src/Database/scripts/Update/NNN_*.sql has the number of a different script on the base branch
'migration number: a script numbered as one on master is refused at complete, and the renumbered one passes' = {
    $dir = 'src/Database/scripts/Update'
    $n = New-Item-Fake 'Access notes' 'x' $L; Do-TechnicalDesign $n 'w1' $false
    [void](Fake 'master-merge' @{ files = @("$dir/039_Earlier.sql", "$dir/040_AddFloorToWorkOrder.sql") })
    $pr = Do-Development $n @("$dir/040_AddAccessNotesToWorkOrder.sql", 'src/Core/Model/WorkOrder.cs')
    $r = Do-DevComplete $n; CheckExit 'complete is refused' $r 10 'migration-number-taken'
    Check 'the reason names both scripts and the next free number' ("$($r.reason)" -like '*040_AddAccessNotesToWorkOrder.sql*' -and "$($r.reason)" -like '*040_AddFloorToWorkOrder.sql*' -and "$($r.reason)" -like '*041*') "$($r.reason)"
    Fake 'push-branch' @{ branch = "factory-worker/$n-development"; files = @("$dir/041_AddAccessNotesToWorkOrder.sql", 'src/Core/Model/WorkOrder.cs') } | Out-Null
    $r = Do-DevComplete $n; CheckExit "renumbered to 041: complete passes (PR #$pr)" $r 0
}

'migration number: two pull requests that took the same number race, and the second is refused at merge' = {
    $dir = 'src/Database/scripts/Update'
    $a = New-Item-Fake 'Floor' 'x' $L; Do-TechnicalDesign $a 'w1' $false
    $b = New-Item-Fake 'Access notes' 'x' $L; Do-TechnicalDesign $b 'w1' $false
    $pa = Do-Development $a @("$dir/040_AddFloorToWorkOrder.sql")
    $pb = Do-Development $b @("$dir/040_AddAccessNotesToWorkOrder.sql")
    CheckExit 'A completes' (Do-DevComplete $a) 0
    CheckExit 'B completes (master has no 040 yet)' (Do-DevComplete $b) 0
    CheckExit 'B PASS' (Do-Verdict $b $pb) 0
    # A lands on master outside the factory's merge (as a person's merge would). The sweep picks that merge up (Invoke-MergePickup), and A then
    # holds a place in the merge queue (merge.awaiting master-green, then release-candidate) until a release candidate carries it. B's merge used
    # to race that sweep: when the sweep came first, B was refused with merge-queue (waiting for A) instead of reaching the migration guard. So A's
    # merge is built into a release, as on the real system, and B merges once A has left the queue (the card moved on to the Release Queue).
    [void](Fake 'master-merge' @{ number = $pa }); Check 'A merged and takes 040' ((Get-FakePr $pa).merged)
    [void](Fake 'octo-release' @{ version = '2.9.0'; sha = (Get-FakePr $pa).mergeSha })
    [void](Wait-Until -Seconds 120 { (Get-FakeIssue $a).column -in 'Release Queue', 'Done' })
    $m = F 'w1' @('merge', '--item', $b, '--pr', $pb); CheckExit 'B is refused at merge' $m 10 'migration-number-taken'
    Check 'the refusal names both scripts and 041' ("$($m.reason)" -like '*040_AddAccessNotesToWorkOrder.sql*' -and "$($m.reason)" -like '*040_AddFloorToWorkOrder.sql*' -and "$($m.reason)" -like '*041*') "$($m.reason)"
    Check 'B is not merged' (-not (Get-FakePr $pb).merged)
}
}
