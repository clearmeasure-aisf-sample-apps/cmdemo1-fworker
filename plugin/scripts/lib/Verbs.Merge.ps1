# The merge verb: squash-merges the pull request named by a recorded PASS verdict, only for an item that carries the Auto Merge label.
# It does not approve the pull request (the default-branch ruleset needs 0 approvals). Authorized by the owner on 2026-10-10 for this demo system.

function Wait-HeadChange([int]$Pr, [string]$OldSha, [int]$Seconds) {
    $end = (Get-Date).AddSeconds($Seconds)
    do { Start-Sleep -Seconds 4; $p = Get-Pr $Pr; if ($p.head.sha -ne $OldSha) { return $p } } while ((Get-Date) -lt $end)
    return $p
}

function Verb_merge($a) {
    $n = Get-ItemNumber $a; $c = Get-Cfg; $t = $c.timing
    $info = Get-ItemInfo $n; Assert-Open $info
    if ($info.column -ine $c.columns.validation) { Reject 10 'wrong-column' "merging happens only from '$($c.columns.validation)'; the item is in '$($info.column)'" }
    $st = Get-State $n; $s = $st.state
    if (-not (Test-Has $s 'verdict') -or -not $s.verdict.passed -or $s.verdict.processed) { Reject 10 'no-pass-verdict' 'no unprocessed PASS verdict is recorded for this item' }
    $prNum = if (Test-Has $a 'pr') { [int]$a.pr } else { [int]$s.verdict.pr }
    if ($prNum -ne [int]$s.verdict.pr) { Reject 10 'pr-mismatch' "the verdict names PR #$($s.verdict.pr), not #$prNum" }
    # Without the Auto Merge label only the QC merge gate lets an item through: owner mode, mergePolicy peer-approved, qc.requireRun / qc.requireApproval
    # (design pr-qc-environment.md 3.6). Everywhere else the label is still required.
    $viaQcGate = $false
    if (-not (Test-Label $info $c.autoMergeLabel)) {
        if (-not (Test-QcGateActive)) { Reject 10 'no-auto-merge-label' "item does not carry '$($c.autoMergeLabel)'" }
        $viaQcGate = $true
    }
    # Merge queue: the Release workflow keeps only one queued run, so merging faster than releases are built drops releases.
    # With mergeQueueDepth N, merge only while fewer than N other merged items are still waiting for master CI or their release candidate.
    $depth = if (Test-Has $c 'mergeQueueDepth') { [int]$c.mergeQueueDepth } else { 0 }
    # A documentation-only change builds no release, so it never waits in the queue and never holds a place in it.
    $thisDocsOnly = try { Test-DocsOnlyFiles (Get-PrFiles $prNum) } catch { $false }
    if ($depth -gt 0 -and -not $thisDocsOnly) {
        $busy = @()
        foreach ($o in (Get-BoardList | Where-Object { $_.open -and $_.number -ne $n -and $_.column -ieq $c.columns.validation -and (Test-NameIn $c.label $_.labels) })) {
            $os = (Get-State $o.number).state
            if ((Test-Has $os 'merge') -and (Test-Has $os.merge 'awaiting') -and $os.merge.awaiting -in 'master-green', 'release-candidate') {
                $oDocs = if (Test-Has $os.merge 'docs_only') { [bool]$os.merge.docs_only } elseif (Test-Has $os.merge 'pr') { try { Test-DocsOnlyFiles (Get-PrFiles ([int]$os.merge.pr)) } catch { $false } } else { $false }
                if (-not $oDocs) { $busy += $o.number }
            }
        }
        if ($busy.Count -ge $depth) { Reject 21 'merge-queue' "merge queue is full: waiting for the release candidate of #$($busy -join ', #') before merging this one" @{ waiting_for = $busy; retryAfterSeconds = 60 } }
    }
    $park = { param($reason, $text) Set-Park $s $reason ($t.parkHours * 60); $s.verdict.processed = $true; $s.Remove('claim'); Remove-ClaimRef $n; Save-State $n $st; Add-Comment $n $text; return @{ ok = $true; id = $n; outcome = 'parked'; reason = $reason } }
    $pr = Get-Pr $prNum
    if (-not (Test-PrBelongs $pr $n)) { return & $park 'pr-mismatch' "PR #$prNum is not a PR belonging to this work item (branch '$($pr.head.ref)'); refusing to merge." }
    if ($pr.merged) {
        $s.merge = @{ sha = $pr.merge_commit_sha; awaiting = 'master-green'; merged_at = ConvertTo-Iso (Get-UtcNow); pr = $prNum }
        $s.verdict.processed = $true; $s.Remove('claim'); Remove-ClaimRef $n; Save-State $n $st
        return @{ ok = $true; id = $n; outcome = 'already-merged'; sha = $pr.merge_commit_sha }
    }
    if ($c.mergePolicy -ne 'auto') {
        if (Get-Park $s) { Reject 10 'held' "item is held: $($s.park.reason)" }
        if (-not (Test-Has $s 'approval') -or $s.approval.sha -ne $pr.head.sha) { Reject 10 'approval-missing' "merge policy is '$($c.mergePolicy)': the current head $($pr.head.sha.Substring(0,7)) has no recorded approval" }
        if (-not (Test-GitHubApproval $pr)) { Reject 10 'approval-not-on-github' 'no approval from the inspector or a named person exists on GitHub for the current head' }
        if ($viaQcGate) {
            $gate = Get-QcGate
            # (a) some environment for THIS head reached ready at least once (documentation-only changes need no environment)
            if ($gate.requireRun -and -not $thisDocsOnly) {
                $qrec = Get-QcRec $s $prNum
                if ($null -eq $qrec -or -not (Test-Has $qrec 'ran_sha') -or "$($qrec.ran_sha)" -ne "$($pr.head.sha)") {
                    Reject 10 'qc-not-run' "no QC environment for the current head $($pr.head.sha.Substring(0,7)) has reached ready; a person starts one from the link on the pull request. Wait for the qc.ready or qc.approved event (or a person); do not park." @{ wait_for = 'qc.approved'; sha = "$($pr.head.sha)" }
                }
            }
            # (b) a person in humanApprovers approved the current head on GitHub (the author never counts)
            if ($gate.requireApproval -and -not (Find-HumanApproval $pr)) {
                Reject 10 'qc-approval-missing' "no person in humanApprovers has approved the current head $($pr.head.sha.Substring(0,7)) on GitHub. Wait for the qc.approved event (or a person); do not park." @{ wait_for = 'qc.approved'; sha = "$($pr.head.sha)" }
            }
        }
    }
    if ($pr.state -ne 'open') { return & $park 'pr-mismatch' "PR #$prNum is $($pr.state), not open; refusing to merge." }
    $end = (Get-Date).AddSeconds($t.mergeabilityWaitSeconds)
    while ($null -eq $pr.mergeable -and (Get-Date) -lt $end) { Start-Sleep -Seconds $t.mergeabilityPollSeconds; $pr = Get-Pr $prNum }
    if ($pr.mergeable -eq $false) { $d = Invoke-Demote $n $info $st "GitHub reports PR #$prNum is NOT mergeable (mergeable_state '$($pr.mergeable_state)')" 'merge'; return @{ ok = $true; id = $n; outcome = $d.outcome; detail = $d } }
    $end = (Get-Date).AddSeconds($t.preMergeChecksWaitSeconds)
    while ($true) {
        $chk = Get-ChecksFor $pr.head.sha
        if ($chk.status -eq 'failure') { $d = Invoke-Demote $n $info $st "PR #$prNum checks are red right before merge ($($chk.failed_checks -join ', '))" 'merge'; return @{ ok = $true; id = $n; outcome = $d.outcome; detail = $d } }
        if ($chk.status -eq 'success' -or (Get-Date) -gt $end) { break }
        Start-Sleep -Seconds $t.preMergeChecksPollSeconds
    }
    for ($attempt = 1; $attempt -le [int]$t.outOfDateMergeAttempts; $attempt++) {
        $r = Invoke-Gh -Method PUT -Path "$(Get-RepoPath)/pulls/$prNum/merge" -Body @{ merge_method = 'squash'; sha = $pr.head.sha } -Allow 405, 409, 422
        if ($r -is [hashtable] -and $r.ContainsKey('status')) {
            $msg = "$($r.body.message)"
            if ($msg -match 'out of date|out-of-date|Head branch was modified|behind') {
                $u = Invoke-Gh -Method PUT -Path "$(Get-RepoPath)/pulls/$prNum/update-branch" -Body @{ expected_head_sha = $pr.head.sha } -Allow 202, 422, 409
                if ($u -is [hashtable] -and $u.ContainsKey('status') -and $u.status -ne 202) { $d = Invoke-Demote $n $info $st "could not update PR #${prNum}: $($u.body.message)" 'merge'; return @{ ok = $true; id = $n; outcome = $d.outcome; detail = $d } }
                $pr = Wait-HeadChange $prNum $pr.head.sha $t.updatedHeadWaitSeconds
                $end = (Get-Date).AddSeconds($t.preMergeChecksWaitSeconds)
                while ($true) { $chk = Get-ChecksFor $pr.head.sha; if ($chk.status -ne 'pending' -or (Get-Date) -gt $end) { break }; Start-Sleep -Seconds $t.preMergeChecksPollSeconds }
                if ($chk.status -eq 'failure') { $d = Invoke-Demote $n $info $st "PR #$prNum checks went red after updating the branch ($($chk.failed_checks -join ', '))" 'merge'; return @{ ok = $true; id = $n; outcome = $d.outcome; detail = $d } }
                Start-Sleep -Seconds ([Math]::Min(120, 15 * [Math]::Pow(2, $attempt - 1))); continue
            }
            if ($r.status -eq 405 -and $msg -match 'status check') { Start-Sleep -Seconds ([Math]::Min(120, 15 * [Math]::Pow(2, $attempt - 1))); $pr = Get-Pr $prNum; continue }
            $d = Invoke-Demote $n $info $st "HTTP $($r.status): $msg" 'merge'; return @{ ok = $true; id = $n; outcome = $d.outcome; detail = $d }
        }
        if (-not $r.merged -or -not $r.sha) { return & $park 'merge-refused' "merge of PR #$prNum returned no merge SHA; parked for a human." }
        $s.merge = @{ sha = $r.sha; awaiting = 'master-green'; merged_at = ConvertTo-Iso (Get-UtcNow); pr = $prNum }
        $s.verdict.processed = $true; $s.Remove('claim'); Remove-ClaimRef $n; Save-State $n $st
        Add-Comment $n "$(if ($viaQcGate) { 'validation PASSED, QC run and human approval recorded' } else { "validation PASSED and '$($c.autoMergeLabel)' present" }) - squash-merged PR #$prNum as ``$($r.sha.Substring(0,7))``; watching master CI, then the release candidate."
        return @{ ok = $true; id = $n; outcome = 'merged'; sha = $r.sha }
    }
    return & $park 'merge-out-of-date' "PR #$prNum kept falling behind master after $($t.outOfDateMergeAttempts) attempts; parked for a human."
}
