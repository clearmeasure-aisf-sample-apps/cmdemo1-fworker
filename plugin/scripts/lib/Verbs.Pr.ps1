# Verbs on pull requests and the validation flow: open-pr, update-pr, pr-checks, wait-for-checks, validation-verdict.
# The merge verb is in Verbs.Merge.ps1.

# The body of an item's pull request as the gateway writes it, for open-pr and update-pr alike: the worker's text, then the reference to the item.
function Get-PrBodyText([string]$Body, [int]$Item) {
    $t = "$Body".TrimEnd(); $ref = "Refs #$Item"
    if ($t -match "(^|\n)$([regex]::Escape($ref))\s*$") { return $t }
    return "$t`n`n$ref"
}

# update-pr: replaces the body of the item's own open pull request (a worker corrects what open-pr wrote; nothing else of the pull request changes).
# The pull request is the open one on the item's branch; --pr may name it, and any other number is refused.
function Verb_update_pr($a) {
    $n = Get-ItemNumber $a; $c = Get-Cfg
    $body = "$(Need $a 'body')"; if (-not $body.Trim()) { Reject 2 'usage' 'missing argument: body' }
    $info = Get-ItemInfo $n; Assert-Open $info
    # only while the item is in Development, the owner's column: after that the pull request has been read and approved as it is
    if ("$($info.column)" -ine "$($c.columns.development)") { Reject 10 'wrong-column' "the body of the pull request is changed only while the item is in '$($c.columns.development)'; it is in '$($info.column)'" }
    $own = Find-ItemPr $n
    if ($own -and $own.head.ref -notmatch "^$([regex]::Escape($c.name))/$n-") { $own = $null }
    if (Test-Has $a 'pr') {
        $want = [int]$a.pr
        if (-not $own -or [int]$own.number -ne $want) {
            $p = try { Get-Pr $want } catch [FactoryReject] { $null }
            if ($p -and "$($p.head.ref)" -match "^$([regex]::Escape($c.name))/$n-") { Reject 10 'pr-not-open' "pull request #$want of this item is $(if ($p.merged) { 'merged' } else { "$($p.state)" }); only an open pull request is changed" }
            Reject 10 'pr-not-own' "pull request #$want is not the pull request of item #$n"
        }
    }
    if (-not $own) {
        $s = (Get-State $n).state
        if ((Test-Has $s 'pr') -and (Test-Has $s.pr 'number')) { Reject 10 'pr-not-open' "pull request #$($s.pr.number) of this item is no longer open; only an open pull request is changed" }
        Reject 10 'no-open-pr' 'no open pull request linked to this work item'
    }
    $r = Invoke-Gh -Method PATCH -Path "$(Get-RepoPath)/pulls/$($own.number)" -Body @{ body = (Get-PrBodyText $body $n) }
    return @{ ok = $true; id = $n; updated = $true; pullRequest = @{ number = $own.number; url = $own.html_url; head_sha = $own.head.sha; branch = $own.head.ref } }
}

function Verb_open_pr($a) {
    $n = Get-ItemNumber $a; $c = Get-Cfg
    $head = "$(Need $a 'head')"; $title = "$(Need $a 'title')"
    $info = Get-ItemInfo $n; Assert-Open $info
    if ($info.column -ine $c.columns.development) { Reject 10 'wrong-column' "pull requests are opened from '$($c.columns.development)'; the item is in '$($info.column)'" }
    if ($head -notmatch "^$([regex]::Escape($c.name))/$n-") { Reject 10 'bad-branch-name' "the branch must be named '$($c.name)/$n-<something>' so the pull request belongs to the item" }
    if ($title -notmatch "(^|[^0-9])#$n([^0-9]|$)") { $title = "$title (#$n)" }
    $base = if (Test-Has $a 'base') { "$($a.base)" } else { $c.defaultBranch }
    $body = if (Test-Has $a 'body') { "$($a.body)" } else { '' }
    $existing = Find-ItemPr $n
    if ($existing -and $existing.head.ref -eq $head) {
        $bo = Get-BuildOwed $info $existing.number (Get-State $n).state
        return @{ ok = $true; id = $n; created = $false; pullRequest = @{ number = $existing.number; url = $existing.html_url; head_sha = $existing.head.sha; branch = $head }; build_owed = $bo.build_owed; build_reason = $bo.build_reason }
    }
    $r = Invoke-Gh -Method POST -Path "$(Get-RepoPath)/pulls" -Allow 422 -Body @{ title = $title; head = $head; base = $base; body = (Get-PrBodyText $body $n); draft = $false }
    if ($r -is [hashtable] -and $r.ContainsKey('status')) {
        $msg = try { ($r.body.errors | ForEach-Object { $_.message }) -join '; ' } catch { '' }
        Reject 10 'pr-not-created' "GitHub refused to create the pull request: $($r.body.message) $msg. This usually means '$head' has no commits ahead of '$base' or was not pushed. Do not push an empty commit; if the item is already satisfied, report complete with succeeded=false." @{ detail = $msg }
    }
    $st = Get-State $n; $st.state.pr = @{ number = $r.number; branch = $head; url = $r.html_url }; Save-State $n $st
    $bo = Get-BuildOwed $info ([int]$r.number) $st.state
    return @{ ok = $true; id = $n; created = $true; pullRequest = @{ number = $r.number; url = $r.html_url; head_sha = $r.head.sha; branch = $head }; build_owed = $bo.build_owed; build_reason = $bo.build_reason }
}

# pr-checks: the status of the checks of a head and, for every failed or cancelled check, why it failed and whether the base branch fails it
# too (lib/Checks.ps1). Read-only.
#   --item N | --pr P | --sha S   which head (a call that names an item reads that item's pull request only)
#   --check NAME                  one check only, with its detail whatever its conclusion
#   --tail N                      lines of each failed step in the log excerpt (default 200, at most 5000; the excerpt stays inside the budget)
#   --brief                       the status only, as before: no detail, no extra GitHub calls
#   --full-log [--check NAME] [--page N]            one page of the whole log of one job (the client's --save FILE writes every page to a file)
#   --artifact NAME [--entry PATH] [--page N]       the files of an artifact of the failed run, or one page of a text file in it
# The status fields and the exit code are what they always were, whatever happens while the detail is collected: a failure there becomes
# detail = { available: false } in the answer. Only a wrong argument (exit 2) or an unknown check name (exit 12) of the call itself is refused.
function Verb_pr_checks($a, [int]$DetailSeconds = 60) {
    $t = Resolve-ChecksTarget $a
    $chk = Get-ChecksFor $t.sha
    if ((Test-Has $a 'full_log') -and "$($a.full_log)" -ne 'False') { return Get-FullLogPage $chk $a $t.sha }
    if (Test-Has $a 'artifact') { return Get-ArtifactPage $chk $a $t.sha }
    $pub = Get-PublicChecks $chk; $pub.ok = $true; $pub.sha = $t.sha
    if ((Test-Has $a 'brief') -and "$($a.brief)" -ne 'False') { return $pub }
    try { Add-CheckDetail $pub $chk $t $a $DetailSeconds }
    catch [FactoryReject] { if ($_.Exception.Exit -in 2, 12) { throw }; $pub.detail = @{ available = $false; code = 'detail-failed'; reason = "the detail of the checks could not be collected ($($_.Exception.Code)); the status above is complete" } }
    catch { $pub.detail = @{ available = $false; code = 'detail-failed'; reason = 'the detail of the checks could not be collected; the status above is complete' } }
    return $pub
}

function Verb_wait_for_checks($a) {
    $cap = if (Test-Has $a 'timeoutSeconds') { [Math]::Min([int]$a.timeoutSeconds, 100) } else { 60 }
    $end = (Get-Date).AddSeconds($cap)
    # the polls ask for the status only; the detail (logs, annotations, baseline) is read once, when the checks are red
    $poll = @{}; foreach ($k in $a.Keys) { if ($k -notin 'full_log', 'artifact', 'entry', 'page', 'check') { $poll[$k] = $a[$k] } }; $poll.brief = $true
    do {
        $r = Verb_pr_checks $poll
        if ($r.status -eq 'success') { return $r }
        if ($r.status -eq 'failure') {
            $poll.Remove('brief'); $poll.Remove('tail')   # (a wrong --tail must not change this answer)
            $d = try { Verb_pr_checks $poll 25 } catch { $r }   # 25 s: the gateway ends this verb 40 s after the longest wait
            Reject 10 'checks-red' "checks failed: $($r.failed_checks -join ', '). 'checks' holds, per failed check, its log excerpt or annotations (data.*) and its baseline on the base branch (likelyPreexisting)." @{ checks = $d }
        }
        Start-Sleep -Seconds 10
    } while ((Get-Date) -lt $end)
    Reject 21 'still-pending' 'checks are still running; call again' @{ checks = $r }
}

function Invoke-Demote([int]$n, $info, $st, [string]$Reason, [string]$Cause) {
    $s = $st.state; $c = Get-Cfg
    $d = if (Test-Has $s 'demotions') { [int]$s.demotions } else { 0 }
    $s.demotions = $d + 1
    $owned = Test-OwnedClaim $s
    $s.Remove('verdict'); $s.Remove('bot')
    if (-not $owned -or $s.demotions -gt [int]$c.timing.maxDemotions) { $s.Remove('claim'); Remove-ClaimRef $n }
    if ($s.demotions -gt [int]$c.timing.maxDemotions) {
        Set-Park $s $(if ($Cause -eq 'verdict') { 'verdict-failed' } else { 'demotion-limit' }) ($c.timing.parkHours * 60)
        Save-State $n $st
        Add-Comment $n "failed $($s.demotions) time(s), over the demotion limit of $($c.timing.maxDemotions); parked for human review: $Reason"
        return @{ outcome = 'parked'; reason = $s.park.reason }
    }
    Move-Card $info $c.columns.development
    if (-not (Test-Has $s 'pr')) { $s.pr = @{} }
    $s.pr.rejection = $Reason
    if ($owned) { Move-OwnerTo $n $s $c.columns.development } else { $s.handoff = @{ column = $c.columns.development; at = ConvertTo-Iso (Get-UtcNow) } }
    Save-State $n $st
    Add-Comment $n "$(if ($Cause -eq 'verdict') {'validation FAILED'} else {'merge failed'}): $Reason - demoting to **Development** (round-trip $($s.demotions) of $($c.timing.maxDemotions))."
    return @{ outcome = 'demoted'; reason = $Reason; round_trip = $s.demotions }
}

function Verb_validation_verdict($a) {
    $n = Get-ItemNumber $a; $c = Get-Cfg
    if (-not (Test-Has $a 'passed') -or -not (Test-Has $a 'summary') -or "$($a.summary)".Trim() -eq '') {
        Reject 2 'verdict-fields' 'the body must hold "passed": true or false and "summary": a one-paragraph verdict. Nothing was recorded.'
    }
    $passed = "$($a.passed)" -ieq 'true'
    $summary = "$($a.summary)".Trim()
    $pr = if (Test-Has $a 'pr') { [int]$a.pr } else { 0 }
    $info = Get-ItemInfo $n; Assert-Open $info
    if ($info.column -ine $c.columns.validation) { Reject 10 'wrong-column' "item is in '$($info.column)'" }
    if ($passed -and $pr -le 0) { Reject 2 'pass-requires-prNumber' 'a PASS verdict requires a valid prNumber' }
    $st = Get-State $n; $s = $st.state
    $holder = if (Test-Has $a 'holder') { "$($a.holder)" } else { 'anonymous' }
    if ((Test-Has $s 'claim') -and (Test-ClaimLive $s) -and $s.claim.holder -ne $holder) { Reject 40 'claim-held' "the claim is held by $($s.claim.holder)" }
    if ($passed) {
        $ref = if (Test-Has $a 'review_ref') { "$($a.review_ref)" } else { '' }
        $rv = if ((Test-Has $s 'reviews') -and (Test-Has $s.reviews $info.column)) { $s.reviews[$info.column] } else { $null }
        if (-not $rv -or -not $rv.ref -or $rv.ref -ne $ref) { Reject 10 'review-ref-invalid' 'a PASS needs a recorded REVIEW: COMPLETE (record-review) and its review_ref' }
    }
    $downgraded = $false
    if ($passed -and (Test-Label $info $c.videoLabel)) {
        $rel = Invoke-Gh -Path "$(Get-RepoPath)/releases/tags/demo-$n" -Allow 404
        $hasMp4 = ($rel -isnot [hashtable]) -and @($rel.assets | Where-Object { $_.name -like '*.mp4' }).Count -gt 0
        if (-not $hasMp4) {
            $passed = $false; $downgraded = $true
            $summary += " FACTORY GATE: this item carries the '$($c.videoLabel)' label, but no MP4 is published on the 'demo-$n' release. PASS downgraded to FAIL."
        }
    }
    $headSha = ''; if ($pr -gt 0) { try { $headSha = (Get-Pr $pr).head.sha } catch { } }
    $s.verdict = @{ passed = $passed; pr = $pr; head_sha = $headSha; summary = $summary; at = ConvertTo-Iso (Get-UtcNow); round = $(if (Test-Has $s 'demotions') { [int]$s.demotions } else { 0 }); processed = $false }
    Add-Comment $n "validation verdict for **$($info.column)**: $(if ($passed) {'PASS'} else {'FAIL'}) - $summary"
    $res = @{ ok = $true; id = $n; recorded = $true; passed = $passed; downgraded = $downgraded; column = $info.column }
    if (-not $passed) {
        $s.verdict.processed = $true
        if ($pr -gt 0) {
            $prd = try { Get-Pr $pr } catch { $null }
            $cause = if ($prd -and $prd.merged) { "PR #$pr already merged as $($prd.merge_commit_sha) - residual gaps only (open a NEW branch from master): $summary" } else { $summary }
            # (a merge that a FAIL verdict judged is not picked up by the sweep as a finished change: the gaps are still to be closed)
            if ($prd -and $prd.merged) { if (-not (Test-Has $s 'pr')) { $s.pr = @{} }; $s.pr.residual = $true }
            $d = Invoke-Demote $n $info $st $cause 'verdict'
            $res.next = $d.outcome; $res.detail = $d
        } else {
            Set-Park $s 'verdict-failed' ($c.timing.parkHours * 60); $s.Remove('claim'); Remove-ClaimRef $n; Save-State $n $st
            Add-Comment $n 'validation FAILED with no pull request to fix; parked for human review.'
            $res.next = 'parked'
        }
        return $res
    }
    # Without the Auto Merge label a PASS is parked for a person to merge, except under the QC merge gate (peer-approved): there the item stays on the
    # approval path and the merge verb enforces the QC run and the human approval.
    if (-not (Test-Label $info $c.autoMergeLabel) -and -not (Test-QcGateActive)) {
        $s.verdict.processed = $true
        Set-Park $s 'pass-without-auto-merge' ($c.timing.parkHours * 60); $s.Remove('claim'); Remove-ClaimRef $n; Save-State $n $st
        Add-Comment $n "validation PASSED - awaiting human merge (no '$($c.autoMergeLabel)' label; the worker will not merge)."
        $res.next = 'parked'; return $res
    }
    Save-State $n $st
    $res.next = if ($c.mergePolicy -eq 'auto') { 'merge' } else { 'approve' }
    # The gateway merges an approved change itself (Verbs.Merge.ps1): in this call when nothing stands in the way, otherwise the sweep does it as
    # soon as it can. 'merge' in the answer says what happened; 'next' stays "merge" so the session's one merge call (answered already-merged)
    # still confirms it, and names the new state only when the merge sent the item back or parked it.
    if ($c.mergePolicy -eq 'auto' -and (Test-GatewayMerge) -and (Test-Label $info $c.autoMergeLabel)) {
        $m = try { Verb_merge @{ item = $n; pr = $pr } -Gateway } catch [FactoryReject] { @{ ok = $true; id = $n; outcome = 'waiting'; reason = $_.Exception.Code } }
        $res.merge = $m
        if ($m.outcome -in 'demoted', 'parked') { $res.next = $m.outcome; if (Test-Has $m 'detail') { $res.detail = $m.detail } }
    }
    return $res
}
