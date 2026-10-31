# Tier 1 scenarios that START SAGAS on the v2 engine (W9.10d, #552): the suite 'saga'.
#
# Until this file no scenario of Tier 1 ran a WorkItemSaga: intake was off in every suite, so the v2 job claimed verbs and nothing else. Here a work item is created in the Todo column and the intake
# pulls it, and the scenario plays the owner session of that item from Technical Design to the merge. Every scenario runs on BOTH engines from the same script, so the golden writes of the suite (recorded
# on v1) are the oracle for v2 and what v2 does differently is declared in golden/v2-differences.txt (blocks saga/*):
#   v1  the rule script's intake (FACTORY_INTAKE=1) pulls the card; the verbs, the gateway's merge after the PASS and the sweep do the rest, the merge is a `rule` write with a record in the state comment;
#   v2  the engine's intake (config intake.enabled with intake.v2Plans, profile saga-docs or saga-feature of run.ps1; the rule script's intake skips the plans of intake.v2Plans, W9.10e) starts the
#       WorkItemSaga; the verbs are v2's; the merge is the saga's own call through the merge train (source `gateway`), the card goes to Done by the gateway's projection and no comment says that it merged.
# The suite is claimed by v2-claims.txt twice, with the profile of each plan (the scenario of the docs plan with saga-docs, the one of the feature plan with saga-feature).
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
    Fake 'push-branch' @{ branch = "factory-worker/$n-development"; files = @($file); profile = 'green' } | Out-Null
    $r = Fo $w @('open-pr', '--item', $n, '--head', "factory-worker/$n-development", '--title', 'Saga trial','--body-file', (TmpFile "sgpr$n.md" 'change')); CheckExit "open-pr #$n" $r 0
    $pr = [int]$r.pullRequest.number
    $rv = Fo $w @('record-review', '--item', $n, '--column', 'Development', '--role', 'reviewer', '--text-file', (TmpFile "sgrv-dev$n.md" "Diff matches the plan.`nREVIEW: COMPLETE")); CheckExit "development #$n review" $rv 0
    $c = Fo $w @('complete', '--item', $n, '--column', 'Development', '--review-ref', $rv.review_ref, '--comment', 'PR open, CI green. REVIEW: COMPLETE'); CheckExit "development #$n complete" $c 0
    Check "#$n is in Functional Testing" ((Wait-Until -Seconds 40 { (Saga-Col $n) -eq 'Functional Testing' })) "column $(Saga-Col $n)"
    return @{ n = $n; pr = $pr }
}
# The PASS of Functional Testing by the owner session
function Saga-Pass($x) {
    $w = $script:SagaOwner
    $rv = Fo $w @('record-review', '--item', $x.n, '--column', 'Functional Testing', '--role', 'reviewer', '--text-file', (TmpFile "sgrv-ft$($x.n).md" "Criteria exercised.`nREVIEW: COMPLETE")); CheckExit "validation #$($x.n) review" $rv 0
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
