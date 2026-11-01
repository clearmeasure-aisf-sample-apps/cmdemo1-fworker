# Tier 1 scenarios that START SAGAS on the v2 engine (W9.10d, #552): the suite 'saga'.
#
# Until this file no scenario of Tier 1 ran a WorkItemSaga: intake was off in every suite, so the v2 job claimed verbs and nothing else. Here a work item is created in the Todo column and the intake
# pulls it, and the scenario plays the owner session of that item from Technical Design to the merge. Every scenario runs on BOTH engines from the same script, so the golden writes of the suite (recorded
# on v1) are the oracle for v2 and what v2 does differently is declared in golden/v2-differences.txt (blocks saga/*):
#   v1  the rule script's intake (FACTORY_INTAKE=1) pulls the card; the verbs, the gateway's merge after the PASS and the sweep do the rest, the merge is a `rule` write with a record in the state comment;
#   v2  the engine's intake (config intake.enabled with intake.v2Plans, profile saga-docs or saga-feature of run.ps1; the rule script's intake skips the plans of intake.v2Plans, W9.10e) starts the
#       WorkItemSaga; the verbs are v2's; the merge is the saga's own call through the merge train (source `gateway`), the card goes to Done by the gateway's projection and no comment says that it merged.
# The suite is claimed by v2-claims.txt by scenarios, with the profile of each plan (the scenario of the docs plan with saga-docs, the others with saga-feature; the last scenario, W9.10j's slots, turns the scheduler on itself).
$script:SagaOwner = 'sg1'

# the owner session that gives the intake its free slot (a connected owner session with capacity); started once, left running for the suite's other scenarios
function Saga-EnsureOwner {
    $up = $false; try { $up = @((Get-Plant).plant.workers | Where-Object { $_.name -eq $script:SagaOwner -and $_.connected }).Count -gt 0 } catch { }
    if ($up) { return }
    if ($script:Workers.ContainsKey($script:SagaOwner)) { Stop-OwnerClient $script:SagaOwner }
    Start-OwnerClient $script:SagaOwner 'persistent-local' '--capacity 1 --no-auto-accept'
    $up = Wait-Until -Seconds 30 { @((Get-Plant).plant.workers | Where-Object { $_.name -eq $script:SagaOwner -and $_.connected }).Count -gt 0 }
    $p = $script:Workers[$script:SagaOwner].proc
    Check "the owner session $($script:SagaOwner) is connected" $up "client $(if ($p.HasExited) { "ended with exit $($p.ExitCode)" } else { 'running' }); events: $(@(Get-WorkerEvents $script:SagaOwner | ForEach-Object { $_.event }) -join ',')"
}
function Saga-Col([int]$n) { "$((Get-FakeIssue $n).column)" }
# the merge calls the fake saw for the pull request: PUT .../pulls/N/merge (and the asynchronous form), all sources
function Saga-FakeLog { , @(foreach ($e in @(Fake 'log')) { foreach ($l in @($e)) { "$l" } }) }   # (the log as one string per request, whether the answer comes enumerated or as one array)
function Saga-MergeCalls([int]$pr) { @((Saga-FakeLog) | Where-Object { $_ -match "^PUT /repos/[^/]+/[^/]+/pulls/$pr/merge" }).Count }
# the writes of the open record (golden mode compare or record; $null with TEST_GOLDEN=off), each with its source: rule, gateway or harness
function Saga-Writes { $w = Fake 'writes'; if ($null -ne (Get-RecField $w 'recording') -and (Get-RecField $w 'recording') -eq $false) { return $null }; return , @(Get-RecField $w 'writes') }
# the writes of one source to a path pattern
function Saga-WritesOf($Writes, [string]$Source, [string]$Method, [string]$PathPattern) { @($Writes | Where-Object { $_ -and "$(Get-RecField $_ 'source')" -eq $Source -and "$(Get-RecField $_ 'method')" -eq $Method -and "$(Get-RecField $_ 'path')" -match $PathPattern }) }

# The owner session's whole path for one item pulled by the intake: accept the offer, the design (docs-only or not), the development with its pull request, the validation and the PASS.
# Returns @{ n; pr } with the item and its pull request; every step is a check, so a broken step is named where it breaks.
function Saga-Play([string]$Title, [string]$Body, [bool]$DocsOnly, [string[]]$Labels = $null) {
    Saga-EnsureOwner
    $w = $script:SagaOwner
    $n = New-Item-Fake $Title $Body $(if ($Labels) { $Labels } else { $OwnerLabels }) 'Todo'
    $file = $(if ($DocsOnly) { 'docs/saga-trial.md' } else { 'src/SagaTrial.cs' })   # (no item number in a name: the golden file is the same whichever scenario ran before; the pull request of a code item changes code: a pull request of documentation only is refused for an item whose design is not docs-only)
    # v1: the next sweep; v2: the intake job (every minute on the leader, the first run 30 s after the start)
    $pulled = Wait-Until -Seconds 200 { (Saga-Col $n) -eq 'Technical Design' }
    if (-not $pulled) { (Saga-FakeLog) | Set-Content -Path (Join-Path $script:Work 'saga-fake-log.txt') }
    Check "the intake pulls #$n from Todo into Technical Design" $pulled "column $(Saga-Col $n)"
    if (-not $pulled) { return $null }
    $offered = Wait-Until -Seconds 90 { (Get-Assigned $w $n).Count -gt 0 }
    Check "the owner session is offered #$n" $offered "column $(Saga-Col $n)"
    if (-not $offered) { return $null }
    $ev = (Get-Assigned $w $n)[-1]
    $r = Fo $w @('accept', '--item', $n, '--assignment', $ev.assignment); CheckExit "accept #$n" $r 0
    $sec = "## Technical Design`n`n$(if ($DocsOnly) { '<!-- aisf:docs-only -->' })`n- change as described in the item.`n- Files: $file`n"
    $r = Fo $w @('append-body', '--item', $n, '--column', 'Technical Design', '--content-file', (TmpFile "sgtd$n.md" $sec)); CheckExit "design #$n append" $r 0
    $r = Fo $w @('record-review', '--item', $n, '--column', 'Technical Design', '--role', 'reviewer', '--text-file', (TmpFile "sgrv-td$n.md" "Section present and complete.`nREVIEW: COMPLETE")); CheckExit "design #$n review" $r 0
    $c = Fo $w @('complete', '--item', $n, '--column', 'Technical Design', '--review-ref', $r.review_ref, '--comment', 'REVIEW: COMPLETE - design done'); CheckExit "design #$n complete" $c 0
    Check "#$n continues into Development" ((Wait-Until -Seconds 40 { (Saga-Col $n) -eq 'Development' })) "column $(Saga-Col $n)"
    Saga-WaitStep $n @('Design') 'the design step is over'
    Fake 'push-branch' @{ branch = "factory-worker/$n-development"; files = @($file); profile = 'green' } | Out-Null
    $r = Fo $w @('open-pr', '--item', $n, '--head', "factory-worker/$n-development", '--title', 'Saga trial','--body-file', (TmpFile "sgpr$n.md" 'change')); CheckExit "open-pr #$n" $r 0
    $pr = [int]$r.pullRequest.number
    $rv = Fo $w @('record-review', '--item', $n, '--column', 'Development', '--role', 'reviewer', '--text-file', (TmpFile "sgrv-dev$n.md" "Diff matches the plan.`nREVIEW: COMPLETE")); CheckExit "development #$n review" $rv 0
    $c = Fo $w @('complete', '--item', $n, '--column', 'Development', '--review-ref', $rv.review_ref, '--comment', 'PR open, CI green. REVIEW: COMPLETE'); CheckExit "development #$n complete" $c 0
    Check "#$n is in Functional Testing" ((Wait-Until -Seconds 40 { (Saga-Col $n) -eq 'Functional Testing' })) "column $(Saga-Col $n); decisions $((Saga-Actions $n) -join ', '); view $((Saga-View $n) | ConvertTo-Json -Compress -Depth 4)"
    Saga-WaitStep $n @('Design', 'Develop') 'the development step is over'
    return @{ n = $n; pr = $pr }
}
# The PASS of Functional Testing by the owner session
function Saga-Pass($x) {
    $w = $script:SagaOwner
    $rv = Fo $w @('record-review', '--item', $x.n, '--column', 'Functional Testing', '--role', 'reviewer', '--text-file', (TmpFile "sgrv-ft$($x.n).md" "Criteria exercised.`nREVIEW: COMPLETE")); CheckExit "validation #$($x.n) review" $rv 0
    if (-not $rv.review_ref) { Write-Host "    (the saga's view of #$($x.n): column $(Saga-Col $x.n); decisions $((Saga-Actions $x.n) -join ', '); $((Saga-View $x.n) | ConvertTo-Json -Compress -Depth 4))" }
    $v = Fo $w @('validation-verdict', '--item', $x.n, '--passed', 'true', '--summary-file', (TmpFile "sgvs$($x.n).md" 'Both criteria met.'), '--pr', $x.pr, '--review-ref', $rv.review_ref)
    CheckExit "the PASS verdict of #$($x.n)" $v 0
    return $v
}
function Saga-MergeComments([int]$n, [int]$pr) { @((Get-FakeIssue $n).comments | Where-Object { "$($_.body)" -match "squash-merged PR #$pr " }).Count }

# The merge record of the item's state comment as v1 reads it: the commit and the pull request, what is awaited, whether it is done, and whether the PASS verdict is processed
function Saga-V1Merge([int]$n) {
    $st = Pm-State $n
    $m = $(if ($st.ContainsKey('merge')) { $st['merge'] } else { @{} })
    $vd = $(if ($st.ContainsKey('verdict')) { $st['verdict'] } else { @{} })
    return @{
        sha = $(if ($m.ContainsKey('sha') -and $m['sha']) { "$($m['sha'])" } else { '' })
        pr = $(if ($m.ContainsKey('pr') -and $m['pr']) { [int]$m['pr'] } else { 0 })
        awaiting = $(if ($m.ContainsKey('awaiting') -and $m['awaiting']) { "$($m['awaiting'])" } else { '' })
        done = [bool]($m.ContainsKey('done_at') -and $m['done_at'])
        processed = [bool]($vd.ContainsKey('processed') -and $vd['processed'])
    }
}

# What the two engines do once the PASS is recorded, checked on the same item: one merge write by either, and the differences W6.6 and W7.3 declare (golden/v2-differences.txt)
function Saga-CheckMerged($x, [string]$Plan) {
    Check "the pull request of #$($x.n) is merged by the gateway" (Wait-Until -Seconds 150 { (Get-FakePr $x.pr).merged }) "merged $((Get-FakePr $x.pr).merged), column $(Saga-Col $x.n)"
    Start-Sleep -Seconds 4   # (a second merge call would come within the settle of the first)
    Check "exactly one merge write for PR #$($x.pr) (the gateway's merge is on)" ((Saga-MergeCalls $x.pr) -eq 1) "merge calls: $(Saga-MergeCalls $x.pr)"
    $writes = Saga-Writes
    if ($script:GoldenEngine -eq 'v2') {
        if ($null -ne $writes) {
            $gw = @(Saga-WritesOf $writes 'gateway' 'PUT' "/pulls/$($x.pr)/merge$")
            $rule = @(Saga-WritesOf $writes 'rule' 'PUT' "/pulls/$($x.pr)/merge$")
            Check 'v2: the merge write is the gateway''s own (the saga through the merge train), none is the rule script''s' ($gw.Count -eq 1 -and $rule.Count -eq 0) "gateway $($gw.Count), rule $($rule.Count)"
            $ruleMoves = @($writes | Where-Object { $_ -and "$(Get-RecField $_ 'source')" -eq 'rule' -and "$(Get-RecField $_ 'path')" -eq '/graphql' -and "$(Get-RecField $_ 'field')" -eq 'updateProjectV2ItemFieldValue' -and (Get-RecField $_ 'cause') -eq $null })
            Check 'v2: no card move after the PASS is the rule script''s (the projection moves the card)' ($ruleMoves.Count -eq 0) "rule card moves outside a verb call: $($ruleMoves.Count)"
        }
        Check 'v2: no comment on the item says that the gateway merged it (the saga''s history is the record)' ((Saga-MergeComments $x.n $x.pr) -eq 0) "merge comments: $(Saga-MergeComments $x.n $x.pr)"
        # W9.10i: the state comment follows the saga in the format v1 reads (the rollback path of FACTORY_V2_OFF). The rule script's sweep writes no wait record of its own (it leaves the saga's item alone, W9.10e); the record that is
        # there is the gateway's projection of the saga's merge: the commit and the pull request, the PASS processed, and the docs plan (which ends at the merge) done at once, the feature plan awaiting master's CI as v1 leaves it after a merge
        $recorded = Wait-Until -Seconds 60 { $r = Saga-V1Merge $x.n; $r.sha -ne '' -and $r.processed }
        $r = Saga-V1Merge $x.n
        Check 'v2: the state comment holds the merge record of the saga''s merge (commit, pull request) and the PASS is processed' ($recorded -and $r.pr -eq $x.pr) ($r | ConvertTo-Json -Compress)
        if ($Plan -eq 'docs') {
            $done = Wait-Until -Seconds 30 { (Saga-V1Merge $x.n).done }
            Check 'v2: the docs item is done in the state comment (done_at, nothing awaited)' ($done -and (Saga-V1Merge $x.n).awaiting -eq '') ((Saga-V1Merge $x.n) | ConvertTo-Json -Compress)
        } else {
            Check 'v2: the feature item awaits master CI in the state comment, as v1 leaves it after a merge (or has gone on)' ($r.awaiting -ne '' -or $r.done) ($r | ConvertTo-Json -Compress)
        }
    } else {
        Check 'v1: the merge is recorded on the item once (the rule script''s comment and merge record)' ((Wait-Until -Seconds 30 { (Saga-MergeComments $x.n $x.pr) -eq 1 })) "merge comments: $(Saga-MergeComments $x.n $x.pr)"
        Check 'v1: the state comment holds the merge record' ((Pm-State $x.n).ContainsKey('merge')) ((Pm-State $x.n) | ConvertTo-Json -Compress -Depth 4)
    }
    # The issue is closed when the docs item is done, on both engines (v1's settle closes it; the pull request has no closing keyword, so nothing else does). The golden files held this write as a tail line that
    # v2-differences.txt removed for v2, so a v2 that never closed the issue passed the comparison: the state of the issue is asserted here, and the write is the gateway's (CloseWorkItemIssue), once.
    if ($Plan -eq 'docs') {
        $closed = Wait-Until -Seconds 90 { $i = Get-FakeIssue $x.n; $null -ne $i -and -not $i.open }
        Check "the issue of #$($x.n) is closed when the docs item is done" $closed "issue open: $((Get-FakeIssue $x.n).open), column $(Saga-Col $x.n)"
        if ($script:GoldenEngine -eq 'v2' -and $null -ne ($w2 = Saga-Writes)) {
            $closes = @(Saga-WritesOf $w2 'gateway' 'PATCH' "/issues/$($x.n)$" | Where-Object { "$(Get-RecField $_ 'body')" -match 'closed' })
            Check 'v2: the close is one write of the gateway, none is the rule script''s' ($closes.Count -eq 1 -and @(Saga-WritesOf $w2 'rule' 'PATCH' "/issues/$($x.n)$" | Where-Object { "$(Get-RecField $_ 'body')" -match 'closed' }).Count -eq 0) "gateway closes: $($closes.Count)"
        }
    }
}

$SagaScenarios = [ordered]@{}

$SagaScenarios['saga: a docs item pulled from Todo is designed, built and verified by its owner session and merged once by the gateway'] = {
    $x = Saga-Play 'Saga docs trial' "Change the documentation.`n`n<!-- aisf:docs-only -->" $true
    if (-not $x) { return }
    $v = Saga-Pass $x
    Saga-CheckMerged $x 'docs'
}

$SagaScenarios['saga: a code item pulled from Todo (the feature plan) is designed, built and verified by its owner session and merged once by the gateway'] = {
    $x = Saga-Play 'Saga feature trial' "Change the code, as the criteria say.`n`n- criterion one`n- criterion two" $false
    if (-not $x) { return }
    $v = Saga-Pass $x
    Saga-CheckMerged $x 'feature'
}

# ---- W9.10f: the sweep's item steps in the saga (v2 only) ---------------------------------------------------------------------------------------------------------------
# What v1's sweep does for every item after the PASS (the settle's wait for master CI and its parks, the demotion of a PASS whose pull request was closed) the saga does for the items it owns, with the watcher
# `item-steps` (every minute on the leader; the profile's first run is at once). On v1 the rule script does them and the scenarios of the suites `auto` and `owner` test that, so these scenarios have nothing to
# play there. They read the saga through the dashboard (the `v2Items` block: step, park and the newest decisions); the comments of the rule script's settle are not written on v2 (the saga's history is the record).
function Saga-V2Only { if ($script:GoldenEngine -eq 'v2') { return $true }; Check 'v2 only: on v1 the sweep of the rule script does this step (suite auto)' $true; return $false }
function Saga-View([int]$n) {
    try {
        $d = Invoke-RestMethod "$(Get-GatewayBase)/api/v1/dashboard" -Headers @{ Authorization = 'Bearer test-token' } -TimeoutSec 5
        foreach ($r in @($d.v2Items.repositories)) { foreach ($i in @($r.items)) { if ([int]$i.number -eq $n) { return $i } } }
    } catch { }
    return $null
}
# v2 only: the verb moves the card at once, the saga takes the completion from its queue (the first use of a handler generates its code, seconds on a loaded runner). A verb of the next step sent before the saga has left the
# step is recorded in the step it left (a review of Development read as one of Design) and the completion is lost; the owner session of a real run is far slower than that, so the scenario waits for the saga as a session would.
function Saga-WaitStep([int]$n, [string[]]$Left, [string]$What) {
    if ($script:GoldenEngine -ne 'v2') { return }
    $over = Wait-Until -Seconds 60 { $v = Saga-View $n; $null -ne $v -and "$($v.step)" -notin $Left }
    Check "the saga of #$n has taken the completion ($What)" $over "view $((Saga-View $n) | ConvertTo-Json -Compress -Depth 4)"
}
function Saga-Actions([int]$n) { $v = Saga-View $n; if ($null -eq $v) { return @() }; return @($v.history | ForEach-Object { "$($_.action)" }) }
# the fake numbers its issues from 1 in every scenario, while the saga's database keeps what the scenarios before started (an item that is open is not started again): a scenario of this group takes numbers above those
function Saga-NumbersAbove([int]$Count) { foreach ($i in 1..$Count) { Fake 'issue' @{ title = "filler $i"; body = 'x'; labels = @(); column = 'Done' } | Out-Null } }

$SagaScenarios['saga: sweep step: a merged code item waits for the CI of master, and a green master ends the wait'] = {
    if (-not (Saga-V2Only)) { return }
    Saga-NumbersAbove 10
    $x = Saga-Play 'Saga master green' "Change the code, as the criteria say.`n`n- criterion one" $false
    if (-not $x) { return }
    Saga-Pass $x | Out-Null
    Check "the pull request of #$($x.n) is merged by the gateway" (Wait-Until -Seconds 150 { (Get-FakePr $x.pr).merged }) "merged $((Get-FakePr $x.pr).merged)"
    $green = Wait-Until -Seconds 240 { 'master-green' -in (Saga-Actions $x.n) }
    Check 'the saga judged master green for the merge commit (decision master-green)' $green "decisions: $((Saga-Actions $x.n) -join ', ')"
    $v = Saga-View $x.n
    Check 'the item waits for its delivery, not parked' ($null -ne $v -and "$($v.step)" -eq 'Deliver' -and -not $v.parked) ($v | ConvertTo-Json -Compress -Depth 4)
}

$SagaScenarios['saga: sweep step: a red master after the one re-run parks the merged item master-broken, and a green master lifts the park by itself'] = {
    if (-not (Saga-V2Only)) { return }
    Saga-NumbersAbove 20
    Fake 'config' @{ masterRed = $true; rerunSeconds = 3; rerunConclusion = 'failure' } | Out-Null
    $x = Saga-Play 'Saga master broken' "Change the code, as the criteria say.`n`n- criterion one" $false
    if (-not $x) { return }
    Saga-Pass $x | Out-Null
    Check "the pull request of #$($x.n) is merged by the gateway" (Wait-Until -Seconds 150 { (Get-FakePr $x.pr).merged }) "merged $((Get-FakePr $x.pr).merged)"
    $parked = Wait-Until -Seconds 300 { 'master-broken' -in (Saga-Actions $x.n) }
    Check 'the saga parked the item master-broken once the new attempt of the failed jobs had failed' $parked "decisions: $((Saga-Actions $x.n) -join ', ')"
    Check 'the failed jobs were re-run once' ((Get-FakeState).reruns -eq 1) "reruns $((Get-FakeState).reruns)"
    $v = Saga-View $x.n
    Check 'the item is parked master-broken in its Deliver step' ($null -ne $v -and $v.parked -and "$($v.parkReason)" -like 'master-broken*' -and "$($v.step)" -eq 'Deliver') ($v | ConvertTo-Json -Compress -Depth 4)
    # master goes green again at a later commit that contains the merge (a person merges another change, its Build passes): the park is lifted by the saga itself (no person), and the wait is over
    Fake 'master-merge' @{ files = @('docs/later.md') } | Out-Null
    $lifted = Wait-Until -Seconds 240 { $w = Saga-View $x.n; $null -ne $w -and -not $w.parked -and 'master-green' -in (Saga-Actions $x.n) }
    Check 'the park was lifted by the saga when master was green again (decisions resumed and master-green)' $lifted "decisions: $((Saga-Actions $x.n) -join ', ')"
}

$SagaScenarios['saga: sweep step: a PASS whose pull request was closed without merging is void, and the item goes back to Development'] = {
    if (-not (Saga-V2Only)) { return }
    Saga-NumbersAbove 30
    $x = Saga-Play 'Saga closed pull request' "Change the code, as the criteria say.`n`n- criterion one" $false
    if (-not $x) { return }
    # the saga reads the hold label at each card event (SagaIngress.Hold), and the move of the card to Functional Testing is one: a label added before the board poll has seen that move parks the item in Verify at once,
    # and a verdict that comes after finds the item parked and is not taken (the Merge step is never reached), so the label waits until the poll has been through (the sweep's cadence is 4 s)
    Start-Sleep -Seconds 8
    # a person holds the item (hold:owner) before the verdict: the PASS is recorded and the merge waits, so the pull request can be closed before the gateway merges it
    Gh-As 'fake-operator' "issues/$($x.n)/labels" @{ labels = @('hold:owner') } | Out-Null
    Saga-Pass $x | Out-Null
    Check 'the item is in its Merge step with the PASS, the merge waits for the hold' (Wait-Until -Seconds 90 { $v = Saga-View $x.n; $null -ne $v -and "$($v.step)" -eq 'Merge' }) ((Saga-View $x.n) | ConvertTo-Json -Compress -Depth 4)
    Check 'the pull request is not merged' (-not (Get-FakePr $x.pr).merged)
    Fake 'pr' @{ number = $x.pr; open = $false } | Out-Null
    $back = Wait-Until -Seconds 240 { $v = Saga-View $x.n; $null -ne $v -and "$($v.step)" -eq 'Develop' -and 'demoted' -in (Saga-Actions $x.n) }
    Check 'the saga demoted the item to Development (decision demoted)' $back "view $((Saga-View $x.n) | ConvertTo-Json -Compress -Depth 4)"
    Check 'the card follows the saga to Development' (Wait-Until -Seconds 60 { (Saga-Col $x.n) -eq 'Development' }) "column $(Saga-Col $x.n)"
}

$SagaScenarios['saga: sweep step: a merge by a person ends the Merge step wherever the item was, and the wait for master CI begins'] = {
    if (-not (Saga-V2Only)) { return }
    Saga-NumbersAbove 40
    $x = Saga-Play 'Saga merged by a person' "Change the code, as the criteria say.`n`n- criterion one" $false
    if (-not $x) { return }
    # a person merges the item's pull request while it is validated (the item has no PASS): the merge reaches the saga as the merge webhook says it (ItemMerged)
    $mm = Fake 'master-merge' @{ number = $x.pr }
    Bu-MergeHook $x.pr "factory-worker/$($x.n)-development" "$($mm.sha)" | Out-Null
    $merged = Wait-Until -Seconds 150 { 'merged' -in (Saga-Actions $x.n) }
    Check 'the saga took the merge of the person (decision merged)' $merged "decisions: $((Saga-Actions $x.n) -join ', ')"
    Check 'the gateway made no merge call of its own' ((Saga-MergeCalls $x.pr) -eq 0) "merge calls: $(Saga-MergeCalls $x.pr)"
    $green = Wait-Until -Seconds 240 { 'master-green' -in (Saga-Actions $x.n) }
    Check 'the saga judged master green for the merge commit (decision master-green)' $green "decisions: $((Saga-Actions $x.n) -join ', ')"
    $v = Saga-View $x.n
    Check 'the item waits for its delivery, not parked' ($null -ne $v -and "$($v.step)" -eq 'Deliver' -and -not $v.parked) ($v | ConvertTo-Json -Compress -Depth 4)
}

# ---- W9.10j (#562, the second pull request): the sessions of the scheduler's slots against the fake Octopus, across a gateway restart (v2 only) ---------------------------------------------------------
# The audit of 2026-10-30 (finding 20): the scheduler counted each session of its own ledger once and the runtime's manager counted it again, so the day budget and the concurrency were reached at half, and a restart brought
# the double count back. The unit and wiring tests of the first pull request pin the arithmetic; this is the proof through the gateway: the scheduler is on (scheduler.featureSlots), the runtime is the fake Octopus (one Start
# worker run is one session), the day budget is 3, and the gateway is killed between the second and the third session. More than half the budget in sessions (three of three) must be started, none of them held for
# runtime-budget before the budget is spent, the manager must not count the ledger's sessions again after the restart, and the fourth item must wait for the budget (it is the budget, not a double count, that holds it).
# The runtime's side is played as the host does: the hosts report the containers of the runs, and the container's worker redeems its nonce and takes the item (the intake pulls the next item only when no item waits in
# Technical Design for an owner). v1 has no scheduler: its sessions are the plant's (suites octopus and runtime).
$script:SlotsBudget = 3
function Slots-HostOf([string]$Machine) { switch ($Machine) { 'Machines-1' { return 'host-a' } 'Machines-2' { return 'host-b' } default { return '' } } }
# every host reports the containers of the Start worker runs that are executing (a run the report does not list ends: reportGraceSeconds)
function Slots-Report {
    # (the Start worker task of a detached worker ends at once; the container lives on until a Stop worker run names it)
    $state = Oct-State
    $stopped = @($state.runs | Where-Object { $_.runbook -eq 'Stop worker' } | ForEach-Object { "$($_.values.Name)" })
    $runs = @($state.runs | Where-Object { $_.runbook -eq 'Start worker' -and $_.state -in 'Executing', 'Success' -and "$($_.values.Name)" -notin $stopped })
    foreach ($h in 'host-a', 'host-b', 'host-c') {
        $cs = @($runs | Where-Object { @($_.machines).Count -gt 0 -and (Slots-HostOf "$($_.machines[0])") -eq $h } | ForEach-Object { @{ name = "fw-$($_.values.Name)"; item = [int]$_.values.Item; state = 'running'; startedAt = (Get-Date).ToUniversalTime().ToString('o') } })
        [void](Oct-Report $h $cs)
    }
}
# waits for the condition and keeps the hosts' reports coming (every 2 s)
function Slots-WaitFor([int]$Seconds, [scriptblock]$Cond) {
    $end = (Get-Date).AddSeconds($Seconds); $last = [DateTime]::MinValue
    while ((Get-Date) -lt $end) {
        if (((Get-Date) - $last).TotalSeconds -ge 2) { Slots-Report; $last = Get-Date }
        if (& $Cond) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}
function Slots-Runs([int]$n) { , @((Oct-State).runs | Where-Object { $_.runbook -eq 'Start worker' -and "$($_.values.Item)" -eq "$n" }) }
function Slots-AllRuns { , @((Oct-State).runs | Where-Object { $_.runbook -eq 'Start worker' }) }
# the container of a run takes the item: it redeems the nonce, connects as the worker and accepts the offer
function Slots-Take([int]$n, $Run) {
    $name = "$($Run.values.Name)"
    $r = Rt-Redeem "$($Run.values.Nonce)"
    Check "the nonce of the run for #$n is redeemed" ($r.status -eq 200) ($r | ConvertTo-Json -Compress)
    if ($r.status -ne 200) { return $name }
    Start-RuntimeClient $name "$($r.body.token)"
    $own = Rt-Own $name $n; CheckExit "the worker $name owns #$n" $own 0
    return $name
}
function Slots-Reasons([int]$n) { $v = Saga-View $n; if ($null -eq $v) { return '' }; (@($v.history | ForEach-Object { "$($_.action) $(@($_.reasons) -join ' ')" }) -join ' | ') }

$SagaScenarios['saga: slots: three sessions against a day budget of three are started, one after the other and across a gateway restart, the fourth item waits for the budget'] = {
    if ($script:GoldenEngine -ne 'v2') { Check 'v2 only: the scheduler is the v2 engine''s; v1''s sessions are the plant''s (suites octopus and runtime)' $true; return }
    Saga-NumbersAbove 60
    $cfgPath = Join-Path $script:Work 'instance.test.json'; $orig = Get-Content -Raw $cfgPath
    $envNames = 'OCTOPUS_RUNBOOK_API_KEY', 'FACTORY_OCTOPUS_HOST_TOKEN', 'FACTORY_TRANSCRIPT_SAVE_SECONDS', 'FACTORY_INTAKE_WIP'
    $oldEnv = @{}; foreach ($e in $envNames) { $oldEnv[$e] = [Environment]::GetEnvironmentVariable($e) }
    $items = @(); $clients = @()
    try {
        $cfg = $orig | ConvertFrom-Json -AsHashtable
        $cfg.scheduler.featureSlots = $true
        $cfg.messaging = @{ enabled = $true; allowedWorkers = @('*'); testHooks = $true }
        $cfg.runtimes = @{ preferConnected = $true; routing = @(); octopus = @{ enabled = $true; serverUrl = "http://127.0.0.1:$($script:FakePort)"; spaceId = 'Spaces-355'; project = 'cmdemo1-fworker-runtime'; environment = 'tdd'; hostTag = 'fworker-host'
            agent = 'codex'; perHostCapacity = 2; overflowRoles = @(); overflowMax = 0; maxConcurrent = 10; maxExecutionsPerDay = $script:SlotsBudget; maxFailuresPerPhase = 2; unhealthyAfterFailures = 100; ackSeconds = 600; listSeconds = 1; reportGraceSeconds = 2 } }
        $cfg | ConvertTo-Json -Depth 12 | Set-Content $cfgPath
        $env:OCTOPUS_RUNBOOK_API_KEY = ('API-' + [guid]::NewGuid().ToString('N').ToUpperInvariant()); $env:FACTORY_OCTOPUS_HOST_TOKEN = [guid]::NewGuid().ToString('N'); $env:FACTORY_TRANSCRIPT_SAVE_SECONDS = '2'; $env:FACTORY_INTAKE_WIP = '10'
        [void](Fake 'octo-reset'); Oct-Machines $OctDefaultMachines
        # an owner session with room for four items gives the intake its free slots (the runtime's workers come later)
        if ($script:Workers.ContainsKey($script:SagaOwner)) { Stop-OwnerClient $script:SagaOwner }
        Restart-Gateway
        [void](Wait-Until -Seconds 120 { (Get-V2State).state -eq 'running' })
        Start-OwnerClient $script:SagaOwner 'persistent-local' '--capacity 4 --no-auto-accept'
        $clients += $script:SagaOwner
        Slots-Report
        $st = Oct-Status
        Check 'the Octopus runtime is on with the day budget of the scenario' ($st.enabled -eq $true -and $st.maxExecutionsPerDay -eq $script:SlotsBudget) ($st | ConvertTo-Json -Compress -Depth 4)

        # the first two sessions, before the restart: each item is pulled by the intake, granted a slot, started on the runtime and taken by its worker
        foreach ($k in 1, 2) {
            $n = New-Item-Fake "Slots session $k" "Change the code, as the criteria say.`n`n- criterion one" ($OctLabels) 'Todo'
            $items += $n
            Check "the intake pulls #$n from Todo (session $k)" (Slots-WaitFor 200 { (Saga-Col $n) -eq 'Technical Design' }) "column $(Saga-Col $n); $(Slots-Reasons $n)"
            Check "the scheduler's slot starts the session of #$n on the Octopus runtime (session $k)" (Slots-WaitFor 90 { (Slots-Runs $n).Count -ge 1 }) "runs: $((Slots-Runs $n).Count); $(Slots-Reasons $n); runtime: $((Oct-Status) | ConvertTo-Json -Compress -Depth 3)"
            if ((Slots-Runs $n).Count -lt 1) { return }
            $clients += (Slots-Take $n (Slots-Runs $n)[0])
        }
        $before = Slots-AllRuns
        $st = Oct-Status
        Check 'two sessions are running and counted once each by the runtime (today 2)' ($before.Count -eq 2 -and $st.running -eq 2 -and $st.today -eq 2) ($st | ConvertTo-Json -Compress -Depth 4)

        # the gateway dies and comes back: its memory is gone, the scheduler's ledger and the runtime's history are not
        Restart-Gateway
        Check 'the Wolverine host runs again' (Wait-Until -Seconds 120 { (Get-V2State).state -eq 'running' }) ((Get-V2State) | ConvertTo-Json -Compress)
        Slots-Report
        Check 'the runtime adopts both sessions from its history and does not count them again' (Slots-WaitFor 30 { $s = Oct-Status; $s.relisted -and $s.running -eq 2 -and $s.today -le 2 }) ((Oct-Status) | ConvertTo-Json -Compress -Depth 4)
        Start-Sleep -Seconds 6; Slots-Report
        Check 'no second session is started for an item that has one' ((Slots-AllRuns).Count -eq 2) ((Slots-AllRuns) | ForEach-Object { "$($_.values.Item)" })
        # the plant lost its owners with the restart: the workers are offered their items again and take them (a waiting offer would hold the intake back)
        foreach ($n in $items) { $w = @($clients | Where-Object { $_ -like "oct-$n-*" })[0]; if ($w) { $own = Rt-Own $w $n; CheckExit "the worker $w owns #$n again" $own 0 } }

        # the third session: more than half of the budget (the restart already counted the first two)
        $n3 = New-Item-Fake 'Slots session 3' "Change the code, as the criteria say.`n`n- criterion one" ($OctLabels) 'Todo'
        $items += $n3
        Check "the intake pulls #$n3 from Todo (session 3)" (Slots-WaitFor 200 { (Saga-Col $n3) -eq 'Technical Design' }) "column $(Saga-Col $n3); $(Slots-Reasons $n3)"
        Check "the third session starts after the restart: the budget of $($script:SlotsBudget) was not spent by two sessions counted twice" (Slots-WaitFor 90 { (Slots-Runs $n3).Count -ge 1 }) "runs: $((Slots-Runs $n3).Count); $(Slots-Reasons $n3); runtime: $((Oct-Status) | ConvertTo-Json -Compress -Depth 3)"
        if ((Slots-Runs $n3).Count -lt 1) { return }
        $clients += (Slots-Take $n3 (Slots-Runs $n3)[0])
        $st = Oct-Status
        Check 'three sessions in all, the budget is spent exactly' ((Slots-AllRuns).Count -eq $script:SlotsBudget -and $st.today -eq $script:SlotsBudget) ($st | ConvertTo-Json -Compress -Depth 4)
        Check 'none of the three was held for the budget before it was spent' (@($items | Where-Object { (Slots-Reasons $_) -match 'runtime-budget' }).Count -eq 0) (($items | ForEach-Object { "#$_ $(Slots-Reasons $_)" }) -join ' || ')

        # the fourth item: the budget is spent, so the scheduler holds it, and no run is made
        $n4 = New-Item-Fake 'Slots session 4' "Change the code, as the criteria say.`n`n- criterion one" ($OctLabels) 'Todo'
        $items += $n4
        Check "the intake pulls #$n4 from Todo (the item the budget holds)" (Slots-WaitFor 200 { (Saga-Col $n4) -eq 'Technical Design' }) "column $(Saga-Col $n4)"
        Check "the scheduler holds #$n4 for the day budget (runtime-budget)" (Slots-WaitFor 60 { (Slots-Reasons $n4) -match 'runtime-budget' }) "decisions: $(Slots-Reasons $n4)"
        Start-Sleep -Seconds 5
        Check "no session is started for #${n4}: the day budget of $($script:SlotsBudget) is the limit" ((Slots-Runs $n4).Count -eq 0 -and (Slots-AllRuns).Count -eq $script:SlotsBudget) "runs: $((Slots-AllRuns).Count)"
    } finally {
        foreach ($n in $items) { Rt-Close $n }
        foreach ($c in $clients) { if ($c -and $script:Workers.ContainsKey($c)) { Stop-OwnerClient $c } }
        try { foreach ($r in @((Oct-State).runs | Where-Object { $_.state -eq 'Executing' })) { [void](Fake 'octo-task' @{ id = $r.task; state = 'Success' }) } } catch { }
        Set-Content -Path $cfgPath -Value $orig -NoNewline
        foreach ($e in $envNames) { [Environment]::SetEnvironmentVariable($e, $oldEnv[$e]) }
        Restart-Gateway
        [void](Wait-Until -Seconds 120 { (Get-V2State).state -eq 'running' })
        try { [void](Fake 'octo-reset') } catch { }
    }
}

# ---- V1.2 and V1.3 (v2-only plan, #622): the intake's slots from the runtime's capacity, the runtime of an item from its labels, the pin of an offer, and the plan of a hotfix label (v2 only) ------------------------------
# tdd has no owner session: the sessions are the Octopus runtime's, started by the plant per item and phase, so the plant's count of free owner slots is always 0 and the intake never pulled. The intake now adds the scheduler's
# free capacity of the runtime (maxConcurrent less what runs and waits, the day budget, the hosts' room). These scenarios play it against the fake Octopus, as the slots scenario above does, and need no owner session.
function Lbl-Enter([scriptblock]$Edit) {
    $cfgPath = Join-Path $script:Work 'instance.test.json'; $orig = Get-Content -Raw $cfgPath
    $envNames = 'OCTOPUS_RUNBOOK_API_KEY', 'FACTORY_OCTOPUS_HOST_TOKEN', 'FACTORY_TRANSCRIPT_SAVE_SECONDS', 'FACTORY_INTAKE_WIP'
    $oldEnv = @{}; foreach ($e in $envNames) { $oldEnv[$e] = [Environment]::GetEnvironmentVariable($e) }
    $cfg = $orig | ConvertFrom-Json -AsHashtable
    $cfg.scheduler.featureSlots = $true
    $cfg.messaging = @{ enabled = $true; allowedWorkers = @('*'); testHooks = $true }
    $cfg.runtimes = @{ preferConnected = $true; routing = @(); octopus = @{ enabled = $true; serverUrl = "http://127.0.0.1:$($script:FakePort)"; spaceId = 'Spaces-355'; project = 'cmdemo1-fworker-runtime'; environment = 'tdd'; hostTag = 'fworker-host'
        agent = 'codex'; perHostCapacity = 2; overflowRoles = @(); overflowMax = 0; maxConcurrent = 1; maxExecutionsPerDay = 20; maxFailuresPerPhase = 2; unhealthyAfterFailures = 100; ackSeconds = 600; listSeconds = 1; reportGraceSeconds = 2 } }
    if ($Edit) { & $Edit $cfg }
    $cfg | ConvertTo-Json -Depth 12 | Set-Content $cfgPath
    $env:OCTOPUS_RUNBOOK_API_KEY = ('API-' + [guid]::NewGuid().ToString('N').ToUpperInvariant()); $env:FACTORY_OCTOPUS_HOST_TOKEN = [guid]::NewGuid().ToString('N'); $env:FACTORY_TRANSCRIPT_SAVE_SECONDS = '2'; $env:FACTORY_INTAKE_WIP = '10'
    [void](Fake 'octo-reset'); Oct-Machines $OctDefaultMachines
    # no owner session: the one of an earlier scenario goes
    if ($script:Workers.ContainsKey($script:SagaOwner)) { Stop-OwnerClient $script:SagaOwner }
    Restart-Gateway
    [void](Wait-Until -Seconds 120 { (Get-V2State).state -eq 'running' })
    Slots-Report
    return @{ path = $cfgPath; orig = $orig; env = $oldEnv }
}
function Lbl-Leave($Saved, [int[]]$Items, [string[]]$Clients) {
    # the sagas of the scenario's items stay in the database for the scenarios after this one: parked, so that no session is offered them and none of them holds the intake back (an unowned offer in Technical Design does)
    foreach ($n in $Items) { try { [void](Invoke-RestMethod "$(Get-GatewayBase)/api/v1/verbs/park-item" -Method POST -Body (@{ item = $n; code = 'scenario-done' } | ConvertTo-Json -Compress) -ContentType 'application/json' -Headers @{ Authorization = 'Bearer test-token' } -TimeoutSec 20) } catch { } }
    foreach ($n in $Items) { Rt-Close $n }
    foreach ($c in $Clients) { if ($c -and $script:Workers.ContainsKey($c)) { Stop-OwnerClient $c } }
    try { foreach ($r in @((Oct-State).runs | Where-Object { $_.state -eq 'Executing' })) { [void](Fake 'octo-task' @{ id = $r.task; state = 'Success' }) } } catch { }
    Set-Content -Path $Saved.path -Value $Saved.orig -NoNewline
    foreach ($e in $Saved.env.Keys) { [Environment]::SetEnvironmentVariable($e, $Saved.env[$e]) }
    Restart-Gateway
    [void](Wait-Until -Seconds 120 { (Get-V2State).state -eq 'running' })
    try { [void](Fake 'octo-reset') } catch { }
}

$SagaScenarios['saga: intake: a Todo item is pulled with no owner session on the runtime''s free capacity (never past maxConcurrent), a runtime: label with no capacity passes the item over, a hotfix label starts the hotfix plan'] = {
    if ($script:GoldenEngine -ne 'v2') { Check 'v2 only: the scheduler''s capacity, the slot request and the plan of the saga are the v2 engine''s (v1''s intake counts owner sessions only)' $true; return }
    Saga-NumbersAbove 100
    $items = @(); $clients = @()
    # the runtime runs two sessions at most; the plans of the intake name the hotfix plan too (as config/deploy.overrides.json does)
    $saved = Lbl-Enter { param($cfg) $cfg.runtimes.octopus.maxConcurrent = 2; $cfg.intake.v2Plans = @('docs', 'feature', 'hotfix', 'redeliver') }
    try {
        Check 'no owner session is connected' (@((Get-Plant).plant.workers | Where-Object { $_.connected -and $_.roles -contains 'owner' }).Count -eq 0) ((Get-Plant).plant.workers | ConvertTo-Json -Compress -Depth 3)
        # V1.2: the intake pulls with the runtime's capacity alone
        $a = New-Item-Fake 'Intake on capacity' "Change the code, as the criteria say.`n`n- criterion one" $OwnerLabels 'Todo'
        $items += $a
        Check "the intake pulls #$a from Todo with the runtime's capacity alone" (Slots-WaitFor 200 { (Saga-Col $a) -eq 'Technical Design' }) "column $(Saga-Col $a); $(Slots-Reasons $a)"
        Check "the scheduler's slot starts the session of #$a on the Octopus runtime (no label: the default runtime)" (Slots-WaitFor 90 { (Slots-Runs $a).Count -ge 1 }) "runs: $((Slots-Runs $a).Count); $(Slots-Reasons $a)"
        if ((Slots-Runs $a).Count -lt 1) { return }
        $clients += (Slots-Take $a (Slots-Runs $a)[0])
        Check "#$a was not held for a runtime" ((Slots-Reasons $a) -notmatch 'runtime-off') (Slots-Reasons $a)
        # V1.3: the label names copilot-cloud, which is off in this environment: the intake has no capacity for that runtime and passes the item over, while octopus has room (the hotfix below is pulled after it)
        $b = New-Item-Fake 'Label routes to copilot' "Change the code, as the criteria say.`n`n- criterion one" ($OwnerLabels + 'runtime:copilot-cloud') 'Todo'
        $items += $b
        # V1.3 (D3): a person's hotfix label starts the hotfix plan: no design step, and the card goes to Development
        $h = New-Item-Fake 'Hotfix by a person' "Change the code, as the criteria say.`n`n- criterion one" ($OwnerLabels + 'hotfix') 'Todo'
        $items += $h
        Check "the intake pulls #$h from Todo" (Slots-WaitFor 200 { $null -ne (Saga-View $h) }) "column $(Saga-Col $h)"
        $v = Saga-View $h
        Check "the saga of #$h is on the hotfix plan, in Develop (no design step)" ($null -ne $v -and "$($v.plan)" -eq 'hotfix' -and "$($v.step)" -eq 'Develop') ($v | ConvertTo-Json -Compress -Depth 4)
        Check "the card follows the saga to Development" (Slots-WaitFor 60 { (Saga-Col $h) -eq 'Development' }) "column $(Saga-Col $h)"
        Check "the session of the hotfix starts on the Octopus runtime" (Slots-WaitFor 90 { (Slots-Runs $h).Count -ge 1 }) "runs: $((Slots-Runs $h).Count); $(Slots-Reasons $h)"
        # V1.2: the runtime runs its two sessions (maxConcurrent 2): the next Todo item stays in Todo, over two runs of the intake job
        $d = New-Item-Fake 'Intake bound by capacity' "Change the code, as the criteria say.`n`n- criterion one" $OwnerLabels 'Todo'
        $items += $d
        $pulledD = Slots-WaitFor 75 { (Saga-Col $d) -ne 'Todo' }
        Check "#$d stays in Todo while the runtime runs its two sessions (maxConcurrent 2)" (-not $pulledD) "column $(Saga-Col $d)"
        Check "no third session is started" ((Slots-AllRuns).Count -eq 2) "runs: $((Slots-AllRuns).Count)"
        Check "#$b, whose runtime has no capacity, was passed over all the time (still in Todo, no session)" ((Saga-Col $b) -eq 'Todo' -and (Slots-Runs $b).Count -eq 0) "column $(Saga-Col $b)"
    } finally { Lbl-Leave $saved $items $clients }
}

$SagaScenarios['saga: intake: a worker: pin is honoured on the saga''s offer (only the pinned session is offered the item)'] = {
    if ($script:GoldenEngine -ne 'v2') { Check 'v2 only: the offers of saga items are the v2 engine''s' $true; return }
    Saga-NumbersAbove 160
    if ($script:Workers.ContainsKey($script:SagaOwner)) { Stop-OwnerClient $script:SagaOwner }
    Start-OwnerClient 'pn1' 'persistent-local' '--capacity 1 --no-auto-accept'
    Start-OwnerClient 'pn2' 'persistent-local' '--capacity 1 --no-auto-accept'
    $iso = { param($t) $t.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffZ') }
    # the pinned card was put into Technical Design by a person (the intake does not pull a pinned item from Todo): the adoption makes its saga at the start
    $n = New-Item-Fake 'Pinned to pn2' "Change the code, as the criteria say.`n`n- criterion one" ($OwnerLabels + 'worker:pn2') 'Technical Design'
    try {
        Adopt-State $n @{ design = @{ docs_only = $false } }
        Restart-Gateway
        Check 'both sessions are connected again' (Wait-Until -Seconds 60 { @((Get-Plant).plant.workers | Where-Object { $_.name -in 'pn1', 'pn2' -and $_.connected }).Count -eq 2 }) ((Get-Plant).plant.workers | ConvertTo-Json -Compress -Depth 3)
        Check "#$n is the saga's, in Design" (Wait-Until -Seconds 150 { $v = Saga-View $n; $null -ne $v -and "$($v.step)" -eq 'Design' }) ((Saga-View $n) | ConvertTo-Json -Compress -Depth 4)
        Check "the pinned session pn2 is offered #$n" (Wait-Until -Seconds 240 { (Get-Assigned 'pn2' $n).Count -gt 0 }) "events pn2: $(@(Get-WorkerEvents 'pn2' | ForEach-Object { $_.event }) -join ','); pn1: $(@(Get-WorkerEvents 'pn1' | ForEach-Object { $_.event }) -join ',')"
        Check "the other session pn1 is not offered #$n" ((Get-Assigned 'pn1' $n).Count -eq 0) "events pn1: $(@(Get-WorkerEvents 'pn1' | ForEach-Object { $_.event }) -join ',')"
    } finally {
        try { [void](Invoke-RestMethod "$(Get-GatewayBase)/api/v1/verbs/park-item" -Method POST -Body (@{ item = $n; code = 'scenario-done' } | ConvertTo-Json -Compress) -ContentType 'application/json' -Headers @{ Authorization = 'Bearer test-token' } -TimeoutSec 20) } catch { }
        foreach ($c in 'pn1', 'pn2') { Stop-OwnerClient $c }
    }
}

# ---- W9.9b (#361, the second pull request): the adoption at a start with v1 items in flight (v2 only) -----------------------------------------------------------------------------------------
# Where the engine acts it reads, at every start, every open item of the board with its state comment and maps it to a saga (docs/design/fworker-wolverine-implementation.md "W9.9b as built"). The items of this
# scenario are what v1 leaves behind: one in Development with a live owner claim (v1's session still holds it), one in Technical Design that nobody holds yet. Neither has a saga; the gateway is started; the
# adoption makes the two rows (the slot of the owner's session goes to the scheduler's ledger where the scheduler is on: AdoptionRunnerTests A1; it is off in this profile) and reports it in the v2 block of /api/v1/status (adopted, upserted, skipped by reason). The next start is the restart of a gateway
# that adopted already: nothing is adopted again, nothing is decided again (the decisions of the item are the same). v1 has no engine to adopt for (the suites auto and owner test the sweep).
function Adopt-Last {
    try { return (Invoke-RestMethod "$(Get-GatewayBase)/api/v1/status" -Headers @{ Authorization = 'Bearer test-token' } -TimeoutSec 5).v2.adoption } catch { return $null }
}
# the latest run of the adoption (the block has none before the first); $null while there is none
function Adopt-Run { $a = Adopt-Last; if ($null -eq $a) { return $null }; $p = $a.PSObject.Properties['last']; if ($p) { return $p.Value }; return $null }
function Adopt-State([int]$n, [hashtable]$More) {
    $iso = { param($t) $t.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffZ') }
    $s = [ordered]@{ schema = 1; item = $n; rev = 3; updated = (& $iso (Get-Date).AddMinutes(-5)) }
    foreach ($k in $More.Keys) { $s[$k] = $More[$k] }
    $body = "<!-- factory-state:factory-worker v1 -->`nstate`n`n``````json`n" + ($s | ConvertTo-Json -Compress -Depth 8) + "`n```````n"
    [void](Gh-As 'fake-operator' "issues/$n/comments" @{ body = $body })
}

$SagaScenarios['saga: adoption: a start with v1 items in flight adopts them (the item with its owner, the item in Technical Design), reports it in the v2 status, and the next start adopts nothing again'] = {
    if ($script:GoldenEngine -ne 'v2') { Check 'v2 only: v1 has no engine to adopt for (the sweep of the rule script owns the items; suites auto and owner)' $true; return }
    Saga-NumbersAbove 90
    # no owner session of an earlier scenario is left to be offered the items
    if ($script:Workers.ContainsKey($script:SagaOwner)) { Stop-OwnerClient $script:SagaOwner }
    $iso = { param($t) $t.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffZ') }
    $a = New-Item-Fake 'Adoption developing' "Change the code, as the criteria say.`n`n- criterion one" $OwnerLabels 'Development'
    $b = New-Item-Fake 'Adoption designing' "Change the code, as the criteria say.`n`n- criterion one" $OwnerLabels 'Technical Design'
    $items = @($a, $b)
    try {
        $now = Get-Date
        Adopt-State $a @{ design = @{ docs_only = $false }; claim = @{ holder = 'w1'; column = 'Development'; started = (& $iso $now.AddMinutes(-20)); heartbeat = (& $iso $now.AddMinutes(-1)); lease_until = (& $iso $now.AddMinutes(40)); launches = 1; owner = $true; worker = 'w1'; assignment = 'a1' } }
        Adopt-State $b @{ design = @{ docs_only = $false } }
        Check 'neither item has a saga before the start' ($null -eq (Saga-View $a) -and $null -eq (Saga-View $b)) "views: $((Saga-View $a) | ConvertTo-Json -Compress -Depth 3) $((Saga-View $b) | ConvertTo-Json -Compress -Depth 3)"

        Restart-Gateway
        $adopted = Wait-Until -Seconds 150 { $x = Adopt-Run; $null -ne $x -and "$($x.outcome)" -eq 'done' -and [int]$x.adopted -ge 2 }
        $x = Adopt-Run
        Check 'the adoption of the start is done and adopted both items (v2 status: adoption.last)' $adopted ((Adopt-Last) | ConvertTo-Json -Compress -Depth 5)
        if (-not $adopted) { return }
        Check 'the report counts the rows it made as upserted, and names no error' ([int]$x.upserted -ge 2 -and [int]$x.errors -eq 0 -and $x.afterBreak -eq $false) ($x | ConvertTo-Json -Compress -Depth 4)
        Check 'the items it skipped are counted by reason (the fillers have no state comment)' ([int]$x.skipped -ge 1 -and $null -ne $x.skippedBy.PSObject.Properties['no-state-comment']) ($x.skippedBy | ConvertTo-Json -Compress)
        # (the views are read at most every 5 seconds, the health block every 10: an answer from before the adoption can still stand)
        [void](Wait-Until -Seconds 40 { $null -ne (Saga-View $a) -and $null -ne (Saga-View $b) })
        $va = Saga-View $a; $vb = Saga-View $b
        Check "#$a is the saga's, in Develop" ($null -ne $va -and "$($va.step)" -eq 'Develop') ($va | ConvertTo-Json -Compress -Depth 4)
        Check "#$b is the saga's, in Design" ($null -ne $vb -and "$($vb.step)" -eq 'Design') ($vb | ConvertTo-Json -Compress -Depth 4)
        Check 'each has the one decision of the adoption' ((@(Saga-Actions $a) -eq 'adopted').Count -eq 1 -and (@(Saga-Actions $b) -eq 'adopted').Count -eq 1) "#${a}: $((Saga-Actions $a) -join ','); #${b}: $((Saga-Actions $b) -join ',')"
        Check 'the cards stay where v1 left them' ((Saga-Col $a) -eq 'Development' -and (Saga-Col $b) -eq 'Technical Design') "columns $(Saga-Col $a) / $(Saga-Col $b)"

        # the next start: the same database, a new process. The rule of the upsert finds the rows in place and the comments as they were
        $decisions = @(Saga-Actions $a).Count + @(Saga-Actions $b).Count
        Restart-Gateway
        $again = Wait-Until -Seconds 150 { $y = Adopt-Run; $null -ne $y -and "$($y.outcome)" -eq 'done' }
        $y = Adopt-Run
        Check 'the second start runs the adoption to its end' $again ((Adopt-Last) | ConvertTo-Json -Compress -Depth 5)
        if (-not $again) { return }
        Check 'and adopts nothing: both rows are kept, nothing is written again' ([int]$y.adopted -eq 0 -and [int]$y.upserted -eq 0 -and ([int]$y.kept + [int]$y.unchanged) -ge 2) ($y | ConvertTo-Json -Compress -Depth 4)
        Check 'the decisions of the two items are the same as after the first start' ((@(Saga-Actions $a).Count + @(Saga-Actions $b).Count) -eq $decisions) "#${a}: $((Saga-Actions $a) -join ','); #${b}: $((Saga-Actions $b) -join ',')"
    } finally {
        # the items stay the saga's: parked, so that no session is offered them to the scenarios after this one
        foreach ($n in $items) { try { [void](Invoke-RestMethod "$(Get-GatewayBase)/api/v1/verbs/park-item" -Method POST -Body (@{ item = $n; code = 'scenario-done' } | ConvertTo-Json -Compress) -ContentType 'application/json' -Headers @{ Authorization = 'Bearer test-token' } -TimeoutSec 20) } catch { } }
    }
}
