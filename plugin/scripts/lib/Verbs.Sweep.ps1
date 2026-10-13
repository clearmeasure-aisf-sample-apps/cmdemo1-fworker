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

function Verb_sync_board($a) {
    $c = Get-Cfg; $t = $c.timing
    $dry = (Test-Has $a 'dry') -and $a.dry
    $seenFile = Get-SeenFile
    $seen = if (Test-Path $seenFile) { Get-Content -Raw $seenFile | ConvertFrom-Json -AsHashtable } else { @{} }
    $offers = @(); $actions = @(); $errors = @(); $owners = @{}; $pins = @{}; $triggers = @()
    # owned: every live owned claim with what the digest needs of it (worker-messaging.md 3.9); settling: items whose merge is being settled, whose
    # owner still waits for item.complete although the claim ended at the merge
    $owned = @{}; $settling = @(); $unconfirmed = @()
    $list = Get-BoardList
    $last = Get-LastColumn
    foreach ($it in $list) {
        # a closed item is still followed when its merged pull request closed it and the flow after the merge is not finished
        if (-not (Test-NameIn $c.label $it.labels)) { continue }
        if (-not $it.open -and -not (("$($it.column)" -ieq $c.columns.validation) -or (Test-NameIn "$($it.column)" $c.columns.hold))) { continue }
        $n = $it.number; $col = "$($it.column)"
        if (Test-NameIn $c.rivalLabel $it.labels) { $actions += @{ item = $n; action = 'skip'; reason = 'ambiguous-owner' }; continue }
        if (-not $col -or $col -ieq $last) { continue }
        try {
            $isAi = Test-NameIn $col $c.columns.ai; $isHold = Test-NameIn $col $c.columns.hold
            if (-not $isAi -and -not $isHold) { $seen.Remove("$n"); continue }
            $st = Get-State $n; $s = $st.state
            $pin = @($it.labels | Where-Object { $_ -like 'worker:*' } | Select-Object -First 1)
            if ($pin.Count -gt 0) { $pins["$n"] = ([string]$pin[0]).Substring(7) }
            if (-not $it.open -and -not ((Test-Has $s 'merge') -and (Test-Has $s.merge 'awaiting') -and $s.merge.awaiting)) { continue }
            if ((Test-OwnedClaim $s) -and (Test-ClaimLive $s)) {
                $owners["$n"] = "$($s.claim.holder)"
                $owned["$n"] = @{ holder = "$($s.claim.holder)"; column = $col; lease_until = $s.claim.lease_until; heartbeat = $(if (Test-Has $s.claim 'heartbeat') { $s.claim.heartbeat } else { $null }); parked = [bool](Get-Park $s) }
            }
            if ((Test-Has $s 'merge') -and (Test-Has $s.merge 'awaiting') -and $s.merge.awaiting) {
                $settling += $n
                if (-not $dry) { $r = Invoke-Settle $n; $actions += @{ item = $n; action = 'settle'; result = $r } }
                if (-not $dry -and ($r.outcome -eq 'done' -or ($r.outcome -eq 'advanced' -and (Test-Has $r 'awaiting') -and $r.awaiting -eq 'release-queue'))) { $triggers += @{ item = $n; event = 'item.complete'; path = $(if (Test-Has $r 'path') { "$($r.path)" } else { '' }) } }
                elseif (-not $dry -and $r.outcome -eq 'parked') { $triggers += @{ item = $n; event = 'item.parked'; reason = $r.reason } }
                continue
            }
            if ($isHold) { continue }
            if (Test-NameIn $c.demoLabel $it.labels) { continue }
            # a person merged the pull request of a PASS verdict: pick the merge up
            if ($col -ieq $c.columns.validation -and (Test-Has $s 'verdict') -and $s.verdict.passed -and -not $s.verdict.processed) {
                $pr = try { Get-Pr ([int]$s.verdict.pr) } catch { $null }
                if ($pr -and $pr.merged -and -not $dry) {
                    $s.merge = @{ sha = $pr.merge_commit_sha; awaiting = 'master-green'; merged_at = ConvertTo-Iso (Get-UtcNow); pr = $pr.number }
                    $s.verdict.processed = $true; $s.Remove('claim'); Save-State $n $st; Remove-ClaimRef $n
                    Add-Comment $n "PR #$($pr.number) was merged as ``$($pr.merge_commit_sha.Substring(0,7))``; watching master CI, then the release candidate."
                    $actions += @{ item = $n; action = 'merge-detected'; sha = $pr.merge_commit_sha }
                }
                if ($pr -and $pr.merged) { continue }
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
                        $holderName = "$($s.claim.holder)"; $s.Remove('claim'); [void](Add-Journal $n $st 'released' @{ worker = $holderName; reason = "idle for more than $($c.idleReleaseMinutes) min on a hold" } "idle|$($held.at)"); Save-State $n $st; Remove-ClaimRef $n
                        $actions += @{ item = $n; action = 'owner-released-idle'; worker = $holderName }
                        $owned.Remove("$n")
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
    # PR QC environments: offers and teardowns only, never a start (design pr-qc-environment.md 3.5)
    $qcRecords = @(); $qcTeardowns = @(); $qcApprovals = @()
    if (Test-QcEnabled) {
        try { $qs = Invoke-QcSync $list $dry; $actions += @($qs.actions); $errors += @($qs.errors); $qcRecords = @($qs.records); $qcTeardowns = @($qs.teardowns); $qcApprovals = @($qs.approvals) }
        catch [FactoryReject] { $errors += @{ item = 0; code = $_.Exception.Code; reason = "qc-sync: $($_.Exception.Message)" } }
        catch { $errors += @{ item = 0; code = 'error'; reason = "qc-sync: $($_.Exception.Message)" } }
    }
    return @{ ok = $true; offers = $offers; actions = $actions; errors = $errors; owners = $owners; owned = $owned; settling = $settling; pins = $pins; triggers = $triggers; unconfirmed = $unconfirmed; qc = $qcRecords; qc_teardowns = $qcTeardowns; qc_approvals = $qcApprovals; at = ConvertTo-Iso (Get-UtcNow) }
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
