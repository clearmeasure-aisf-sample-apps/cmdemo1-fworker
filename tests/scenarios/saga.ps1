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
function Saga-Play([string]$Title, [string]$Body, [bool]$DocsOnly) {
    Saga-EnsureOwner
    $w = $script:SagaOwner
    $n = New-Item-Fake $Title $Body $OwnerLabels 'Todo'
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
        $st = Pm-State $x.n; $mr = $(if ($st.ContainsKey('merge')) { $st['merge'] } else { $null })   # (the sweep's record may not be there yet: strict mode refuses a key that is not)
        # (W9.10e: the rule script's sweep leaves the saga's item alone, so it takes the saga's merge for no person's and writes no wait record of its own)
        Check 'v2: the state comment holds no wait record of the rule script''s merge (awaiting master CI, done_at)' ($null -eq $mr -or (-not $mr.ContainsKey('awaiting') -and -not $mr.ContainsKey('done_at'))) ($mr | ConvertTo-Json -Compress -Depth 4)
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
