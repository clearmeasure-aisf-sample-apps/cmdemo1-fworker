# The sweep (reconciliation + run watchdog), event intake and the secrets check. No model is involved in any of it.

function Get-SeenFile { Join-Path (Get-StateDir) 'seen.json' }

function Complete-AwaitingChecks([int]$n, $info, $st) {
    $c = Get-Cfg; $s = $st.state; $aw = $s.awaiting_checks
    $pr = Get-Pr ([int]$aw.pr); $chk = Get-ChecksFor $pr.head.sha
    if ($chk.status -eq 'success') {
        $s.Remove('fix_forward_rounds'); if ((Test-Has $s 'pr') -and (Test-Has $s.pr 'rejection')) { $s.pr.Remove('rejection') }
        $comment = "PR #$($pr.number) checks are green; development complete. $($aw.comment)"
        $r = Finish-ColumnExit $n $info $st $comment @{ prNumber = $pr.number }
        return @{ outcome = 'completed'; moved_to = $r.moved_to }
    }
    if ($chk.status -eq 'failure') {
        $rounds = $(if (Test-Has $s 'fix_forward_rounds') { [int]$s.fix_forward_rounds } else { 0 }) + 1
        $s.fix_forward_rounds = $rounds; $s.Remove('awaiting_checks'); $s.Remove('park')
        if ($rounds -gt [int]$c.timing.fixForwardMaxRounds) {
            Set-Park $s 'fix-forward-exhausted' ($c.timing.parkHours * 60); Save-State $n $st
            Add-Comment $n "PR #$($pr.number) has failed CI after $($rounds - 1) fix-forward Development rounds - parked for a human."
            return @{ outcome = 'parked'; reason = 'fix-forward-exhausted' }
        }
        if (-not (Test-Has $s 'pr')) { $s.pr = @{} }
        $s.pr.number = $pr.number; $s.pr.branch = $pr.head.ref; $s.pr.url = $pr.html_url
        $s.pr.rejection = "PR #$($pr.number) checks failed after the completion: $($chk.failed_checks -join ', ')"
        $s.pr.failed_checks = $chk.failed_checks
        $s.handoff = @{ column = $c.columns.development; at = ConvertTo-Iso (Get-UtcNow) }
        Save-State $n $st
        Add-Comment $n "REJECTED a Development completion - PR #$($pr.number) checks are 'failure' ($($chk.failed_checks -join ', ')). Re-dispatching Development to fix forward (round $rounds)."
        return @{ outcome = 'fix-forward'; round = $rounds; offer = $true }
    }
    $since = ConvertFrom-Iso $aw.since
    if ($since -and ((Get-UtcNow) - $since).TotalMinutes -gt $c.timing.devChecksTimeoutMinutes) {
        $s.Remove('awaiting_checks'); Set-Park $s 'stuck' ($c.timing.parkHours * 60); Save-State $n $st
        Add-Comment $n "PR #$($pr.number) checks were still not finished after $($c.timing.devChecksTimeoutMinutes) min - parked for a human (a hung CI run, not a code problem)."
        return @{ outcome = 'parked'; reason = 'stuck' }
    }
    return @{ outcome = 'waiting'; checks = (Get-PublicChecks $chk) }
}

# A pull request of the item was merged outside the merge verb (a person pressed the button): the item is settled from wherever it was. Under the
# item lock on a fresh read (the sweep holds no lock, and the merge verb may have recorded the same merge a moment ago): the merge is recorded once,
# the verdict is closed, and whatever held the item (claim, park, handoff, a wait for checks, an inspector lease) ends. Returns whether it recorded.
function Invoke-MergePickup([int]$n, $pr) {
    return Use-ItemLock $n {
        $f = Get-State $n; $fs = $f.state
        if ((Test-Has $fs 'merge') -and (Test-Has $fs.merge 'sha') -and $fs.merge.sha) { $false }
        else {
            $fs.merge = @{ sha = $pr.merge_commit_sha; awaiting = 'master-green'; merged_at = ConvertTo-Iso (Get-UtcNow); pr = [int]$pr.number; by = 'person' }
            if (Test-Has $fs 'verdict') { $fs.verdict.processed = $true; $fs.verdict.Remove('wait') }
            foreach ($k in 'claim', 'park', 'handoff', 'awaiting_checks', 'bot') { $fs.Remove($k) }
            Save-State $n $f; Remove-ClaimRef $n; $true
        }
    }
}

# ---- orphan claim refs (issue 47) --------------------------------------------------------------------------------------
# A claim is a ref (refs/factory/<instance>/claims/<item>) and the claim in the item's state; the ref is written first. A claim cut off between the
# two (a restart) leaves a ref with no claim, and refs of closed items were never removed. Each sweep (the first one runs at start-up) lists the
# refs and, under the item lock on a fresh read of the state, removes a ref that has no live claim: at once when the item is closed or gone, and
# when it is open once the ref has been seen without a claim for longer than a lease. When it was first seen so is kept in the state directory: a
# restart starts the wait again, which only delays the removal (an accept takes such a ref over anyway, Verb_claim_item).
function Get-ClaimOrphanFile { Join-Path (Get-StateDir) 'claim-orphans.json' }

function Get-ClaimRefItems {
    $prefix = "refs/$(Get-RefPath)/"; $seen = [System.Collections.Generic.HashSet[int]]::new(); $out = @()
    # matching-refs may ignore the page parameter (as Get-RevokedRefs): end on an empty page or on one that brings nothing new
    for ($page = 1; $page -le 50; $page++) {
        $rows = @(Invoke-Gh -Path "$(Get-RepoPath)/git/matching-refs/$(Get-RefPath)/`?per_page=100&page=$page")
        if ($rows.Count -eq 0) { break }
        $new = 0
        foreach ($r in $rows) {
            $ref = "$($r.ref)"
            if ($ref.StartsWith($prefix, [StringComparison]::Ordinal) -and $ref.Substring($prefix.Length) -match '^\d{1,9}$' -and $seen.Add([int]$Matches[0])) { $out += [int]$Matches[0]; $new++ }
        }
        if ($new -eq 0) { break }
    }
    return , @($out)
}

function Invoke-ClaimRefReap($List) {
    $t = (Get-Cfg).timing; $f = Get-ClaimOrphanFile
    $first = if (Test-Path $f) { try { Get-Content -Raw $f | ConvertFrom-Json -AsHashtable -DateKind String } catch { @{} } } else { @{} }
    if ($null -eq $first) { $first = @{} }
    $board = @{}; foreach ($it in $List) { $board["$($it.number)"] = $it }
    $acts = @(); $keep = @{}; $now = Get-UtcNow
    foreach ($n in (Get-ClaimRefItems)) {
        try {
            $open = if ($board.ContainsKey("$n")) { [bool]$board["$n"].open } else { try { [bool](Get-ItemInfo $n).open } catch [FactoryReject] { if ($_.Exception.Code -in 'github-not-found', 'item-not-found') { $false } else { throw } } }
            $since = if ($first.ContainsKey("$n")) { ConvertFrom-Iso $first["$n"] } else { $null }
            if (-not $since) { $since = $now }
            $aged = ($now - $since).TotalMinutes -ge [double]$t.leaseMinutes
            $res = Use-ItemLock $n {
                $st = try { Get-State $n } catch [FactoryReject] { if ($_.Exception.Code -in 'github-not-found', 'item-not-found') { $null } else { throw } }
                if ($st -and (Test-ClaimLive $st.state)) { return 'live' }
                if (-not $open -or $aged) { Remove-ClaimRef $n; return 'removed' }
                return 'orphan'
            }
            if ($res -eq 'removed') { $acts += @{ item = $n; action = 'claim-ref-removed'; reason = $(if (-not $open) { 'item-closed' } else { 'orphan' }) } }
            elseif ($res -eq 'orphan') { $keep["$n"] = ConvertTo-Iso $since; $acts += @{ item = $n; action = 'orphan-claim'; since = $keep["$n"] } }
        } catch [FactoryReject] { $acts += @{ item = $n; action = 'skip'; reason = "claim-ref-reap: $($_.Exception.Code)" } }
    }
    $keep | ConvertTo-Json -Depth 3 | Set-Content $f
    return , @($acts)
}

# Issue 82: removes the park of a closed item under the item lock, on a fresh read, and journals it. Returns the park's code, or $null when there
# was none any more or the item is open again (reopened meanwhile: its park is a person's to release).
function Clear-ClosedPark([int]$n) {
    Use-ItemLock $n {
        $info = Get-ItemInfo $n; if ($info.open) { return $null }
        $f = Get-State $n; $fs = $f.state
        if (-not (Test-Has $fs 'park')) { return $null }
        if ((Test-Has $fs 'merge') -and (Test-Has $fs.merge 'awaiting') -and $fs.merge.awaiting) { return $null }
        $park = $fs.park; $code = "$($park.reason)"; $fs.Remove('park')
        [void](Add-Journal $n $f 'unparked' @{ column = "$($info.column)"; code = $code; by = 'the sweep'; reason = 'the item is closed'; closed = $true } "closed|$($park.at)")
        Save-State $n $f
        return $code
    }
}

function Verb_sync_board($a) {
    $c = Get-Cfg; $t = $c.timing
    $dry = (Test-Has $a 'dry') -and $a.dry
    $seenFile = Get-SeenFile
    $seen = if (Test-Path $seenFile) { Get-Content -Raw $seenFile | ConvertFrom-Json -AsHashtable } else { @{} }
    $offers = @(); $actions = @(); $errors = @(); $owners = @{}; $pins = @{}; $triggers = @()
    # owned: every live owned claim with what the digest needs of it (worker-messaging.md 3.9); settling: items whose merge is being settled, whose
    # owner still waits for item.complete although the claim ended at the merge
    $owned = @{}; $settling = @(); $unconfirmed = @()
    # pass_unmerged: every item with a PASS verdict that the gateway is to merge and has not merged yet, with why (a reason code) and since when
    $passUnmerged = @()
    # release_queue: the items that wait in the Release Queue for the production deployment, with their release and since when (enhancement 9)
    $releaseQueue = @()
    # unparked: items whose park the sweep removed because they are closed (issue 82); the plant forgets their parks
    $unparked = @()
    $list = Get-BoardList
    $last = Get-LastColumn
    foreach ($it in $list) {
        # a closed item is still followed when its merged pull request closed it and the flow after the merge is not finished
        if (-not (Test-NameIn $c.label $it.labels)) { continue }
        # (issue 82: a closed item in any column the factory works is read too, so a park it was left with is removed below)
        if (-not $it.open -and -not ((Test-NameIn "$($it.column)" $c.columns.ai) -or (Test-NameIn "$($it.column)" $c.columns.hold))) { continue }
        $n = $it.number; $col = "$($it.column)"
        if (Test-NameIn $c.rivalLabel $it.labels) { $actions += @{ item = $n; action = 'skip'; reason = 'ambiguous-owner' }; continue }
        if (-not $col -or $col -ieq $last) { continue }
        try {
            $isAi = Test-NameIn $col $c.columns.ai; $isHold = Test-NameIn $col $c.columns.hold
            if (-not $isAi -and -not $isHold) { $seen.Remove("$n"); continue }
            $st = Get-State $n; $s = $st.state
            $pin = @($it.labels | Where-Object { $_ -like 'worker:*' } | Select-Object -First 1)
            if ($pin.Count -gt 0) { $pins["$n"] = ([string]$pin[0]).Substring(7) }
            if (-not $it.open -and -not ((Test-Has $s 'merge') -and (Test-Has $s.merge 'awaiting') -and $s.merge.awaiting)) {
                # issue 82: a closed item whose flow has ended keeps no park (280 stayed parked master-broken after it was closed)
                if ((Test-Has $s 'park') -and -not $dry) { $r = Clear-ClosedPark $n; if ($r) { $actions += @{ item = $n; action = 'park-cleared'; reason = 'item-closed'; code = $r }; $unparked += $n } }
                continue
            }
            # a closed item outside Functional Testing and the hold columns is followed no further, as before (only its park was of interest)
            if (-not $it.open -and -not (("$($it.column)" -ieq $c.columns.validation) -or (Test-NameIn "$($it.column)" $c.columns.hold))) { continue }
            if ((Test-OwnedClaim $s) -and (Test-ClaimLive $s)) {
                $owners["$n"] = "$($s.claim.holder)"
                $owned["$n"] = @{ holder = "$($s.claim.holder)"; column = $col; lease_until = $s.claim.lease_until; heartbeat = $(if (Test-Has $s.claim 'heartbeat') { $s.claim.heartbeat } else { $null }); parked = [bool](Get-Park $s) }
            }
            if ((Test-Has $s 'merge') -and (Test-Has $s.merge 'awaiting') -and $s.merge.awaiting) {
                $settling += $n
                if (-not $dry) { $r = Invoke-Settle $n; $actions += @{ item = $n; action = 'settle'; result = $r } }
                if (-not $dry -and $r.outcome -in 'advanced', 'waiting' -and (Test-Has $r 'awaiting') -and $r.awaiting -eq 'release-queue') {
                    $releaseQueue += @{ item = $n; release = $(if (Test-Has $r 'release') { "$($r.release)" } else { '' }); since = $(if (Test-Has $r 'queued_at') { $r.queued_at } else { $null }) }
                }
                if (-not $dry -and ($r.outcome -eq 'done' -or ($r.outcome -eq 'advanced' -and (Test-Has $r 'awaiting') -and $r.awaiting -eq 'release-queue'))) { $triggers += @{ item = $n; event = 'item.complete'; path = $(if (Test-Has $r 'path') { "$($r.path)" } else { '' }) } }
                elseif (-not $dry -and $r.outcome -eq 'parked') { $triggers += @{ item = $n; event = 'item.parked'; reason = $r.reason } }
                continue
            }
            if ($isHold) { continue }
            if (Test-NameIn $c.demoLabel $it.labels) { continue }
            # A person merged the item's pull request: pick the merge up, whatever state the item was in (a PASS waiting for the gateway's merge, a
            # PASS parked for a person because the item has no Auto Merge label, an item still in Development, a parked or unowned item). The pull
            # request is the one of the PASS verdict, else the one open-pr recorded; not one whose merge a FAIL verdict already judged (residual gaps).
            $unprocessedPass = $col -ieq $c.columns.validation -and (Test-Has $s 'verdict') -and $s.verdict.passed -and -not $s.verdict.processed
            $ownPr = 0
            if ((Test-Has $s 'verdict') -and $s.verdict.passed -and [int]$s.verdict.pr -gt 0) { $ownPr = [int]$s.verdict.pr }
            elseif ((Test-Has $s 'pr') -and (Test-Has $s.pr 'number') -and -not (Test-Has $s.pr 'residual')) { $ownPr = [int]$s.pr.number }
            $pr = $null
            if ($ownPr -gt 0 -and ($unprocessedPass -or -not (Test-Has $s 'merge'))) { $pr = try { Get-Pr $ownPr } catch { $null } }
            if ($pr -and $pr.merged -and ($unprocessedPass -or (Test-PrBelongs $pr $n))) {
                if ($dry) { continue }
                if (-not (Invoke-MergePickup $n $pr)) { continue }
                # the flow after a merge starts from the validation column (settle moves the card on from there)
                if ((Get-ColumnIndex $col) -ge 0 -and (Get-ColumnIndex $col) -lt (Get-ColumnIndex $c.columns.validation)) { Move-Card (Get-ItemInfo $n) $c.columns.validation }
                Add-Comment $n "PR #$($pr.number) was merged as ``$($pr.merge_commit_sha.Substring(0,7))``$(if (-not $unprocessedPass) { ' by a person' }); watching master CI, then the release candidate."
                $actions += @{ item = $n; action = 'merge-detected'; sha = $pr.merge_commit_sha }
                $settling += $n; $triggers += @{ item = $n; event = 'item.merged' }
                continue
            }
            if ($unprocessedPass) {
                # the pull request of a PASS verdict was closed without merging: the verdict is void, so the item goes back to Development
                if ($pr -and -not $pr.merged -and "$($pr.state)" -eq 'closed') {
                    if (-not $dry) { $info = Get-ItemInfo $n; $dr = Invoke-Demote $n $info $st "PR #$($pr.number) was closed without merging; the PASS verdict no longer applies." 'pr-closed'; $actions += @{ item = $n; action = 'demote'; reason = 'pr-closed' } }
                    continue
                }
                $held = Get-Park $s
                if ($held) {
                    if ($held.reason -eq 'inspector-escalated' -and $pr -and -not $dry) {
                        $info = Get-ItemInfo $n
                        if (Invoke-ClearHoldIfApproved $n $info $st $pr) {
                            $actions += @{ item = $n; action = 'hold-cleared' }
                            $triggers += @{ item = $n; event = 'hold.cleared' }
                            if (-not (Test-ClaimLive $s)) { $offers += @{ item = $n; column = $col; reason = 'ready-to-merge'; kind = 'own-item'; mode = 'resume' } }
                        }
                    }
                    elseif ((Test-OwnedClaim $s) -and ((Get-UtcNow) - (ConvertFrom-Iso $held.at)).TotalMinutes -gt [double]$c.idleReleaseMinutes -and -not $dry) {
                        # an owner waiting out a hold for too long gives the item up; the next trigger assigns it again
                        $holderName = "$($s.claim.holder)"
                        # (under the item lock on a fresh read, as above; given up only if the same owner still waits on the same hold)
                        $did = Use-ItemLock $n {
                            $f = Get-State $n; $fs = $f.state; $fh = Get-Park $fs
                            if ((Test-OwnedClaim $fs) -and "$($fs.claim.holder)" -eq $holderName -and $fh -and "$($fh.at)" -eq "$($held.at)") {
                                $fs.Remove('claim'); [void](Add-Journal $n $f 'released' @{ worker = $holderName; reason = "idle for more than $($c.idleReleaseMinutes) min on a hold" } "idle|$($held.at)"); Save-State $n $f; Remove-ClaimRef $n; $true
                            } else { $false }
                        }
                        if (-not $did) { continue }
                        $actions += @{ item = $n; action = 'owner-released-idle'; worker = $holderName }
                        $owned.Remove("$n")
                    }
                    continue
                }
                # Merge policy auto with the gateway merging (Verbs.Merge.ps1): the item is the gateway's from the verdict to the merge. It is never
                # offered to a worker in this state (a session could not claim it: verdict-already-recorded), whether its owner is still there or not.
                # The merge runs under the item lock on a fresh read, so a second sweep or the session's own merge call finds the verdict processed.
                if ($c.mergePolicy -eq 'auto' -and (Test-GatewayMerge)) {
                    if (-not $pr -or $dry) { continue }
                    $vat = ConvertFrom-Iso $s.verdict.at; $age = if ($vat) { ((Get-UtcNow) - $vat).TotalSeconds } else { 1e9 }
                    $grace = if (Test-Has $t 'passMergeGraceSeconds') { [double]$t.passMergeGraceSeconds } else { 60 }
                    # the grace leaves the verdict call (and the session's merge call right after it) alone; once the gateway has tried, none is needed
                    if ($age -lt $grace -and -not (Test-Has $s.verdict 'wait')) { $passUnmerged += @{ item = $n; code = 'grace'; since = $s.verdict.at; pr = [int]$s.verdict.pr }; $unconfirmed += $n; continue }
                    $gm = try {
                        Use-ItemLock $n {
                            $f = Get-State $n; $fs = $f.state
                            if (-not ((Test-Has $fs 'verdict') -and $fs.verdict.passed -and -not $fs.verdict.processed)) { @{ outcome = 'skipped'; reason = 'settled-meanwhile' } }
                            elseif (-not (Test-Label (Get-ItemInfo $n) $c.autoMergeLabel)) {
                                # the label was taken off after the verdict: what the verdict does for an item without it
                                $fs.verdict.processed = $true; Set-Park $fs 'pass-without-auto-merge' ($t.parkHours * 60); $fs.Remove('claim'); Save-State $n $f; Remove-ClaimRef $n
                                Add-Comment $n "validation PASSED - awaiting human merge (the '$($c.autoMergeLabel)' label is no longer on the item; the gateway will not merge)."
                                @{ outcome = 'parked'; reason = 'pass-without-auto-merge' }
                            }
                            else { Verb_merge @{ item = $n } -Gateway }
                        } 3
                    } catch [FactoryReject] {
                        # item-busy: a verb of the item is running (the session's own merge call): the next sweep looks again. Anything else the merge
                        # verb refuses (the card was moved, the item closed) is not a merge the gateway may make.
                        if ($_.Exception.Code -in 'github-not-found', 'item-not-found') { throw }
                        @{ outcome = $(if ($_.Exception.Code -eq 'item-busy') { 'waiting' } else { 'skipped' }); reason = $_.Exception.Code }
                    }
                    $actions += @{ item = $n; action = 'pass-merge'; outcome = "$($gm.outcome)"; reason = $(if (Test-Has $gm 'reason') { "$($gm.reason)" } else { '' }); roundTrip = $(if ((Test-Has $gm 'detail') -and (Test-Has $gm.detail 'round_trip')) { [int]$gm.detail.round_trip } else { 0 }) }
                    $ownedLive = (Test-OwnedClaim $s) -and (Test-ClaimLive $s)
                    switch ("$($gm.outcome)") {
                        { $_ -in 'merged', 'already-merged' } { $settling += $n; $triggers += @{ item = $n; event = 'item.merged' } }
                        'demoted' {
                            # back in Development: its owner is told as for checks that failed (fix forward); without an owner it is offered
                            $rt = if ((Test-Has $gm 'detail') -and (Test-Has $gm.detail 'round_trip')) { [int]$gm.detail.round_trip } else { 1 }
                            if ($ownedLive) { $triggers += @{ item = $n; event = 'checks.failed'; column = $c.columns.development; reason = 'merge-demoted'; roundTrip = $rt } }
                            else { $offers += @{ item = $n; column = $c.columns.development; reason = 'handoff' } }
                        }
                        'parked' { $triggers += @{ item = $n; event = 'item.parked'; reason = "$($gm.reason)" } }
                        'waiting' { $passUnmerged += @{ item = $n; code = "$($gm.reason)"; since = $s.verdict.at; pr = [int]$s.verdict.pr }; if ("$($gm.reason)" -in 'item-busy', 'mergeability-unknown', 'branch-updated') { $unconfirmed += $n } }
                    }
                    continue
                }
                if ($pr) {
                    $has = (Test-Has $s 'approval') -and $s.approval.sha -eq $pr.head.sha
                    if ($c.mergePolicy -eq 'auto') { if (-not (Test-ClaimLive $s)) { $offers += @{ item = $n; column = $col; reason = 'ready-to-merge'; kind = 'own-item'; mode = 'resume' } } }
                    elseif (Test-OwnerMode) {
                        if (-not $has) { if (-not (Test-BotLive $s)) { $offers += @{ item = $n; column = $col; reason = 'needs-approval'; kind = 'bot-job'; role = 'inspector'; pr = [int]$s.verdict.pr } } }
                        elseif (-not (Test-ClaimLive $s)) { $offers += @{ item = $n; column = $col; reason = 'ready-to-merge'; kind = 'own-item'; mode = 'resume' } }
                    }
                    elseif (-not (Test-ClaimLive $s)) { $offers += @{ item = $n; column = $col; reason = $(if ($has) { 'ready-to-merge' } else { 'needs-approval' }) } }
                }
                continue
            }
            $park = Get-Park $s
            if ($park) {
                if ($park.reason -eq 'development:awaiting-checks' -and (Test-Has $s 'awaiting_checks') -and -not $dry) {
                    $info = Get-ItemInfo $n
                    $r = Complete-AwaitingChecks $n $info $st
                    $actions += @{ item = $n; action = 'awaiting-checks'; result = $r }
                    if ($r.outcome -in 'completed', 'fix-forward') { $seen.Remove("$n") }
                    if (Test-OwnedClaim $s) {
                        if ($r.outcome -eq 'completed') { $triggers += @{ item = $n; event = 'column.advanced'; to = $r.moved_to } }
                        elseif ($r.outcome -eq 'fix-forward') { $triggers += @{ item = $n; event = 'checks.failed'; column = $c.columns.development } }
                        elseif ($r.outcome -eq 'parked') { $triggers += @{ item = $n; event = 'item.parked'; reason = $r.reason } }
                    } else {
                        if ($r.outcome -eq 'completed') { $offers += @{ item = $n; column = $r.moved_to; reason = 'handoff' } }
                        if ($r.outcome -eq 'fix-forward') { $offers += @{ item = $n; column = $c.columns.development; reason = 'fix-forward' } }
                    }
                }
                continue
            }
            if (Test-ClaimLive $s) { continue }
            if (Test-Has $s 'claim') { $offers += @{ item = $n; column = $col; reason = 'relaunch'; kind = 'own-item'; mode = $(if (Test-OwnedClaim $s) { 'resume' } else { 'relaunch' }) }; continue }
            if ((Test-Has $s 'handoff') -and $s.handoff.column -ieq $col) { $offers += @{ item = $n; column = $col; reason = 'handoff' }; continue }
            $prev = if ($seen.ContainsKey("$n")) { $seen["$n"] } else { @{ column = ''; count = 0 } }
            $count = if ($prev.column -ieq $col) { [int]$prev.count + 1 } else { 1 }
            $seen["$n"] = @{ column = $col; count = $count }
            if ($count -ge [int]$t.sweepSeenCount) { $offers += @{ item = $n; column = $col; reason = 'board-move' } }
            else { $unconfirmed += $n }   # seen, not yet offered: the gateway looks again soon instead of a whole interval later
        } catch [FactoryReject] {
            # an item (or its card) that vanished while the sweep was reading it is not an error: the next sweep sees the board as it is
            if ($_.Exception.Code -in 'github-not-found', 'item-not-found') { $actions += @{ item = $n; action = 'skip'; reason = 'vanished-during-sweep' } }
            else { $errors += @{ item = $n; code = $_.Exception.Code; reason = $_.Exception.Message } }
        }
        catch { $errors += @{ item = $n; code = 'error'; reason = "$($_.Exception.Message)" } }
    }
    if (-not $dry) { $seen | ConvertTo-Json -Depth 5 | Set-Content $seenFile }
    # claim refs with no live claim behind them (issue 47): removed at once for a closed item, after a lease for an open one
    if (-not $dry) {
        try { $actions += @(Invoke-ClaimRefReap $list) }
        catch [FactoryReject] { $errors += @{ item = 0; code = $_.Exception.Code; reason = "claim-ref-reap: $($_.Exception.Message)" } }
        catch { $errors += @{ item = 0; code = 'error'; reason = "claim-ref-reap: $($_.Exception.Message)" } }
    }
    # work intake (issue 104): with a worker slot free and nothing waiting in Technical Design, the next item comes onto the board from Todo
    if (-not $dry) {
        try { $in = Invoke-Intake $a $list $offers $unconfirmed $pins $seen $seenFile; if ($in) { $actions += $in.action; $offers += $in.offer } }
        catch [FactoryReject] { $errors += @{ item = 0; code = $_.Exception.Code; reason = "intake: $($_.Exception.Message)" } }
        catch { $errors += @{ item = 0; code = 'error'; reason = "intake: $($_.Exception.Message)" } }
    }
    # PR QC environments: offers and teardowns only, never a start (design pr-qc-environment.md 3.5)
    $qcRecords = @(); $qcTeardowns = @(); $qcApprovals = @()
    if (Test-QcEnabled) {
        try { $qs = Invoke-QcSync $list $dry; $actions += @($qs.actions); $errors += @($qs.errors); $qcRecords = @($qs.records); $qcTeardowns = @($qs.teardowns); $qcApprovals = @($qs.approvals) }
        catch [FactoryReject] { $errors += @{ item = 0; code = $_.Exception.Code; reason = "qc-sync: $($_.Exception.Message)" } }
        catch { $errors += @{ item = 0; code = 'error'; reason = "qc-sync: $($_.Exception.Message)" } }
    }
    return @{ ok = $true; offers = $offers; actions = $actions; errors = $errors; owners = $owners; owned = $owned; settling = $settling; release_queue = $(if ($dry) { $null } else { $releaseQueue }); pins = $pins; triggers = $triggers; unconfirmed = $unconfirmed; pass_unmerged = $passUnmerged; unparked = $unparked; qc = $qcRecords; qc_teardowns = $qcTeardowns; qc_approvals = $qcApprovals; at = ConvertTo-Iso (Get-UtcNow) }
}

# ---- work intake (issue 104) -----------------------------------------------------------------------------------------
# When a worker slot is free (the gateway passes intake.freeSlots: owner sessions that could take a new work item now) and nothing waits in Technical
# Design (no unpinned own-item offer there, no item there seen and not yet offered), the highest-priority open issue labelled for the factory in
# intake.fromColumn (Todo) is moved to Technical Design and offered at once. Priority is the order of the board column (its top first; a person
# orders it by dragging). Left in Todo: a closed issue, one without the label or with the rival label, the demo label, a hold label (hold:* or
# intake.holdLabels), a worker:* pin (a person placed it for one worker), and one whose Depends-on items are not merged or closed. At most one
# item per sweep, and none while the AI columns hold intake.wipLimit items or more. Switch: intake.enabled (FACTORY_INTAKE 0 or 1 overrides it;
# FACTORY_INTAKE_WIP overrides the limit). Returns $null or @{ action; offer }.
function Get-IntakeSettings {
    $c = Get-Cfg; $i = if (Test-Has $c 'intake') { $c.intake } else { @{} }
    $on = (Test-Has $i 'enabled') -and [bool]$i.enabled
    if ($env:FACTORY_INTAKE -in '0', '1') { $on = $env:FACTORY_INTAKE -eq '1' }
    $wip = if ($env:FACTORY_INTAKE_WIP -match '^\d+$') { [int]$env:FACTORY_INTAKE_WIP } elseif (Test-Has $i 'wipLimit') { [int]$i.wipLimit } else { 6 }
    return @{ enabled = $on; from = $(if (Test-Has $i 'fromColumn') { "$($i.fromColumn)" } else { 'Todo' }); wip = $wip
        hold = @(if (Test-Has $i 'holdLabels') { @($i.holdLabels) }) }
}

function Invoke-Intake($a, $List, $Offers, $Unconfirmed, $Pins, $Seen, [string]$SeenFile) {
    $set = Get-IntakeSettings
    if (-not $set.enabled -or -not (Test-Has $a 'intake') -or -not (Test-Has $a.intake 'freeSlots') -or [int]$a.intake.freeSlots -le 0) { return $null }
    $c = Get-Cfg; $td = "$(@($c.columns.ai)[0])"
    $byNumber = @{}; foreach ($x in $List) { $byNumber[[int]$x.number] = $x }
    # something already waits in Technical Design for a session: no intake (an item pinned to one worker waits for that worker, not for a slot)
    $waiting = @(@($Offers | Where-Object { "$($_.column)" -ieq $td -and (-not $_.ContainsKey('kind') -or "$($_.kind)" -eq 'own-item') -and -not $Pins.ContainsKey("$($_.item)") }) +
        @($Unconfirmed | Where-Object { $byNumber.ContainsKey([int]$_) -and "$($byNumber[[int]$_].column)" -ieq $td -and -not $Pins.ContainsKey("$_") }))
    if ($waiting.Count -gt 0) { return $null }
    $wip = @($List | Where-Object { $_.open -and (Test-NameIn $c.label $_.labels) -and (Test-NameIn "$($_.column)" $c.columns.ai) }).Count
    if ($wip -ge $set.wip) { return $null }
    $depCache = @{}
    foreach ($it in $List) {
        if (-not $it.open -or "$($it.column)" -ine $set.from -or -not (Test-NameIn $c.label $it.labels)) { continue }
        if ((Test-NameIn $c.rivalLabel $it.labels) -or (Test-NameIn $c.demoLabel $it.labels)) { continue }
        if (@($it.labels | Where-Object { "$_" -like 'hold:*' -or "$_" -like 'worker:*' -or (Test-NameIn "$_" $set.hold) }).Count -gt 0) { continue }
        if (-not $it.cardId) { continue }
        # Depends-on (issue 65): read with the sweep's own functions when this gateway has them
        if (Get-Command Get-ItemDependencies -ErrorAction SilentlyContinue) {
            $body = if ($it.ContainsKey('body')) { "$($it.body)" } else { "$((Get-ItemInfo ([int]$it.number)).body)" }
            $deps = @(Get-ItemDependencies ([int]$it.number) $body)
            if (@($deps | Where-Object { -not (Test-DependencyMet $_ $byNumber $depCache) }).Count -gt 0) { continue }
        }
        $n = [int]$it.number
        Move-Card @{ number = $n; cardId = $it.cardId } $td
        # offered at once by this sweep, and by the next ones as a card that was seen in its column
        $Seen["$n"] = @{ column = $td; count = [int]$c.timing.sweepSeenCount }; $Seen | ConvertTo-Json -Depth 5 | Set-Content $SeenFile
        [void](Add-Journal $n $null 'pulled' @{ column = $td; from = $set.from; slots = [int]$a.intake.freeSlots; wip = $wip; wipLimit = $set.wip } "intake|$((Get-UtcNow).Ticks)")
        return @{ action = @{ item = $n; action = 'intake-pulled'; column = $td; from = $set.from; wip = $wip }; offer = @{ item = $n; column = $td; reason = 'intake' } }
    }
    return $null
}

# ---- event intake ---------------------------------------------------------------------------------------------------
function New-Event([string]$Event, $Item, $Column, [string]$Ref, [hashtable]$Extra = @{}) {
    $e = @{ provider = 'github'; event = $Event; item = $Item; column = $Column; ref = $Ref; at = ConvertTo-Iso (Get-UtcNow) }
    foreach ($k in $Extra.Keys) { $e[$k] = $Extra[$k] }
    return $e
}

function Verb_ingest_event($a) {
    $c = Get-Cfg
    $event = "$(Need $a 'event')"; $delivery = if (Test-Has $a 'delivery') { "$($a.delivery)" } else { '' }
    $p = $a.payload
    $out = @()
    $repoOk = $true
    if ((Test-Has $p 'repository')) { $repoOk = ($p.repository.full_name -ieq "$($c.owner)/$($c.repo)") }
    if (-not $repoOk) { return @{ ok = $true; events = @(); dropped = 'other-repository' } }
    switch ($event) {
        'issues' {
            $act = "$($p.action)"; $num = [int]$p.issue.number
            $labels = @($p.issue.labels | ForEach-Object { $_.name })
            $mine = (Test-NameIn $c.label $labels)
            if ($act -eq 'labeled' -and $p.label.name -ieq $c.label) {
                $col = try { (Get-ItemInfo $num).column } catch { $null }
                $out += New-Event 'item.labeled' $num $col $delivery
            } elseif ($act -in 'closed', 'reopened' -and $mine) { $out += New-Event "item.$act" $num $null $delivery }
        }
        'workflow_run' {
            $run = $p.workflow_run; $act = "$($p.action)"; $branch = "$($run.head_branch)"
            $kind = if ($act -eq 'completed') { 'completed' } elseif ($act -eq 'in_progress') { 'started' } else { $null }
            if ($kind) {
                if ($branch -match "^$([regex]::Escape($c.name))/(\d+)-") {
                    $out += New-Event "build.branch_$kind" ([int]$Matches[1]) $null $delivery @{ sha = $run.head_sha; conclusion = $run.conclusion; workflow = $run.name }
                } elseif ($branch -eq $c.defaultBranch) {
                    $out += New-Event "build.master_$kind" $null $null $delivery @{ sha = $run.head_sha; conclusion = $run.conclusion; workflow = $run.name }
                }
            }
        }
        'projects_v2_item' {
            if ("$($p.action)" -eq 'edited' -and (Test-Has $p 'changes') -and $p.changes.field_value.field_name -eq 'Status') {
                $q = 'query($id:ID!){ node(id:$id){ ... on Issue{ number labels(first:30){ nodes{ name } } } } }'
                $d = Invoke-GhGraphQL $q @{ id = $p.projects_v2_item.content_node_id }
                if ($d.node -and (Test-NameIn $c.label @($d.node.labels.nodes | ForEach-Object { $_.name }))) {
                    $out += New-Event 'item.column_changed' ([int]$d.node.number) "$($p.changes.field_value.to.name)" $delivery
                }
            }
        }
        'pull_request' {
            $act = "$($p.action)"; $pull = $p.pull_request
            $branch = "$($pull.head.ref)"
            # any pull request merged into the default branch moves master: the gateway tells the owners of open branches (base.updated)
            if ($act -eq 'closed' -and (Test-Has $pull 'merged') -and $pull.merged -and (Test-Has $pull 'base') -and "$($pull.base.ref)" -eq $c.defaultBranch) { $out += New-Event 'base.merged' $null $null $delivery @{ pr = [int]$pull.number; sha = "$($pull.merge_commit_sha)" } }
            if ($branch -match "^$([regex]::Escape($c.name))/(\d+)-") {
                $num = [int]$Matches[1]; $full = if ((Test-Has $pull 'head') -and (Test-Has $pull.head 'repo')) { "$($pull.head.repo.full_name)" } else { '' }
                if ($full -ine "$($c.owner)/$($c.repo)") { return @{ ok = $true; events = @(); dropped = 'pr.fork' } }
                $x = @{ pr = [int]$pull.number; sha = "$($pull.head.sha)"; login = $(if (Test-Has $p 'sender') { "$($p.sender.login)" } else { '' }) }
                if ($act -in 'opened', 'reopened', 'ready_for_review') { $out += New-Event 'pr.opened' $num $null $delivery $x }
                elseif ($act -eq 'synchronize') { $out += New-Event 'pr.head_changed' $num $null $delivery $x }
                elseif ($act -eq 'closed') { $out += New-Event $(if ((Test-Has $pull 'merged') -and $pull.merged) { 'pr.merged' } else { 'pr.closed' }) $num $null $delivery $x }
                elseif ($act -eq 'converted_to_draft') { $out += New-Event 'pr.drafted' $num $null $delivery $x }
            }
        }
        'pull_request_review' {
            $act = "$($p.action)"; $pull = $p.pull_request; $branch = "$($pull.head.ref)"
            if ($act -in 'submitted', 'dismissed' -and $branch -match "^$([regex]::Escape($c.name))/(\d+)-") {
                $num = [int]$Matches[1]
                $out += New-Event 'pr.reviewed' $num $null $delivery @{ pr = [int]$pull.number; sha = "$($pull.head.sha)"; login = "$($p.review.user.login)"; state = "$($p.review.state)".ToUpperInvariant(); commit_id = "$($p.review.commit_id)"; action = $act }
            }
        }
        default { }
    }
    return @{ ok = $true; events = $out }
}

# ---- secrets (read-only probes; names and pass/fail only, never a value) -------------------------------------------
function Verb_check_secrets($a) {
    $c = Get-Cfg; $checks = @()
    $add = { param($name, $ok, $detail) $script:checks += @{ name = $name; ok = [bool]$ok; detail = $detail } }
    $script:checks = @()
    try { $login = Get-ServerLogin; & $add 'github-token' $true "authenticated as $login" } catch { & $add 'github-token' $false 'token missing or rejected' }
    try {
        $r = Invoke-Gh -Path (Get-RepoPath); $perm = $r.permissions
        & $add 'repo-access' ($perm.push) "push=$($perm.push) admin=$($perm.admin)"
    } catch { & $add 'repo-access' $false 'repository not readable' }
    try { $m = Get-ProjectMeta; & $add 'project-read' ($m.options.Count -gt 0) "Status options: $($m.options.Count)" } catch { & $add 'project-read' $false 'project not readable' }
    try { $missing = @($c.columns.ai + $c.columns.hold + $c.columns.done | Where-Object { -not (Get-ProjectMeta).options.Contains($_) }); & $add 'board-columns' ($missing.Count -eq 0) $(if ($missing.Count) { "missing: $($missing -join ', ')" } else { 'all expected columns present' }) } catch { & $add 'board-columns' $false 'unreadable' }
    try {
        $labels = @(Invoke-Gh -Path "$(Get-RepoPath)/labels?per_page=100" | ForEach-Object { $_.name })
        & $add 'worker-label' (Test-NameIn $c.label $labels) "label '$($c.label)' $(if (Test-NameIn $c.label $labels) {'exists'} else {'is missing'})"
    } catch { & $add 'worker-label' $false 'labels unreadable' }
    $ws = $env:FACTORY_WEBHOOK_SECRET
    & $add 'webhook-secret' ($ws -and $ws.Length -ge 32) $(if ($ws) { "set, $($ws.Length) chars" } else { 'not set' })
    if ($env:OCTOPUS_API_KEY) {
        try {
            $me = Invoke-Octo 'api/users/me'; & $add 'octopus-key' ($null -ne $me) 'authenticated'
            $proj = Invoke-Octo "api/$($c.octopus.space)/projects/$($c.octopus.project)"; & $add 'octopus-project' ($null -ne $proj) "project $($c.octopus.project)"
        } catch { & $add 'octopus-key' $false 'rejected or unreachable' }
    } else { & $add 'octopus-key' $false 'not set' }
    $bad = @($script:checks | Where-Object { -not $_.ok })
    if ($bad.Count -gt 0) { Reject 30 'secrets-check-failed' "$($bad.Count) check(s) failed" @{ checks = $script:checks } }
    return @{ ok = $true; checks = $script:checks }
}
