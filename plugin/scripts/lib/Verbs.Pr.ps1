# Verbs on pull requests and the validation flow: open-pr, update-pr, pr-checks, wait-for-checks, validation-verdict.
# The merge verb is in Verbs.Merge.ps1.

# The body of an item's pull request as the gateway writes it, for open-pr and update-pr alike: the worker's text, then the reference to the item.
function Get-PrBodyText([string]$Body, [int]$Item) {
    $t = "$Body".TrimEnd(); $ref = "Refs #$Item"
    if ($t -match "(^|\n)$([regex]::Escape($ref))\s*$") { return $t }
    return "$t`n`n$ref"
}

# ---- push gate after repeated red checks -------------------------------------------------------------------------------
# Every head of an item's pull request whose checks went red is counted once (state checks_red). From timing.redChecksGate (2) red heads on,
# open-pr and update-pr are refused until the session attests with private-build --attest <sha> that the private build passed on the head of the
# branch; an item that owes no private build (docs-only, Get-BuildOwed) is not gated. A red check alone on one platform that the base branch fails
# too (likelyPreexisting) is not counted at first: wait-for-checks reruns its job once.
function Get-RedChecksGate { $t = (Get-Cfg).timing; if (Test-Has $t 'redChecksGate') { [int]$t.redChecksGate } else { 2 } }
function Get-ChecksRedCount($s) { if ((Test-Has $s 'checks_red') -and (Test-Has $s.checks_red 'count')) { [int]$s.checks_red.count } else { 0 } }

# counts a red head once; true when it was not counted before
function Add-ChecksRed($s, [string]$Sha) {
    if (-not $Sha) { return $false }
    if (-not (Test-Has $s 'checks_red')) { $s.checks_red = @{ count = 0; shas = @() } }
    $shas = @($s.checks_red.shas | Where-Object { $_ })
    if ($shas -contains $Sha) { return $false }
    $s.checks_red.shas = @(@($shas + $Sha) | Select-Object -Last 10); $s.checks_red.count = (Get-ChecksRedCount $s) + 1; $s.checks_red.last_at = ConvertTo-Iso (Get-UtcNow)
    return $true
}

# for a verb that holds no item lock (wait-for-checks): counted under the lock on a fresh read
function Save-ChecksRed([int]$n, [string]$Sha) {
    Use-ItemLock $n { $st = Get-State $n; if (Add-ChecksRed $st.state $Sha) { Save-State $n $st } }
}

# $HeadSha: the head the pull request has (or would have); empty for a pull request not yet created, where an attestation newer than the last red
# head is enough (the head of a branch that is not a pull request is not read)
function Assert-PushGate([int]$n, $info, $s, [string]$HeadSha, $PrNumber) {
    $limit = Get-RedChecksGate; $count = Get-ChecksRedCount $s
    if ($limit -le 0 -or $count -lt $limit) { return }
    if (Test-Has $s 'private_build') {
        $pb = $s.private_build
        if ($HeadSha -and "$($pb.sha)" -eq $HeadSha) { return }
        if (-not $HeadSha -and (ConvertFrom-Iso $pb.at) -ge (ConvertFrom-Iso $s.checks_red.last_at)) { return }
    }
    if ($PrNumber) { $bo = Get-BuildOwed $info ([int]$PrNumber) $s; if (-not $bo.build_owed) { return } }
    $shown = if ($HeadSha) { $HeadSha.Substring(0, [Math]::Min(7, $HeadSha.Length)) } else { 'the head of the branch' }
    Reject 10 'private-build-required' "the checks of this item's pull request went red on $count heads: run the private build on $shown until it passes, then record it with private-build --item $n --attest <full sha of that commit>, and call this again. No push without a passing private build." @{ checks_red = $count; head_sha = $HeadSha; gate = $limit }
}

# private-build --attest SHA: the session states that the private build passed on that commit. Recorded in the item's state for the push gate.
function Verb_private_build($a) {
    $n = Get-ItemNumber $a
    $sha = "$(Need $a 'attest')".Trim().ToLowerInvariant()
    if ($sha -notmatch '^[0-9a-f]{40}$') { Reject 2 'usage' 'attest takes the full 40-character sha of the commit the private build passed on' }
    $info = Get-ItemInfo $n; Assert-Open $info
    $holder = if (Test-Has $a 'holder') { "$($a.holder)" } else { 'anonymous' }
    $st = Get-State $n; $s = $st.state
    $s.private_build = @{ sha = $sha; at = ConvertTo-Iso (Get-UtcNow); holder = $holder }
    Save-State $n $st
    return @{ ok = $true; id = $n; attested = $true; sha = $sha; checks_red = (Get-ChecksRedCount $s); gate = (Get-RedChecksGate) }
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
    # the item's own branch, or the copilot/* branch its state records (Find-ItemPr matched it by the state): pr_update answered pr-not-open for
    # a taken-over Copilot pull request (#395)
    if ($own -and $own.head.ref -notmatch "^$([regex]::Escape($c.name))/$n-" -and "$($own.head.ref)" -notlike 'copilot/*') { $own = $null }
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
    Assert-PushGate $n $info (Get-State $n).state "$($own.head.sha)" $own.number
    $r = Invoke-Gh -Method PATCH -Path "$(Get-RepoPath)/pulls/$($own.number)" -Body @{ body = (Get-PrBodyText $body $n) }
    return @{ ok = $true; id = $n; updated = $true; pullRequest = @{ number = $own.number; url = $own.html_url; head_sha = $own.head.sha; branch = $own.head.ref } }
}

# The draft pull request the Copilot cloud agent opened on its branch: taken over for the item (title names #N, marked ready for review, recorded
# in the state as the item's pull request). $null when there is none.
function Use-CopilotDraft([int]$n, [string]$Head, [string]$Title, [int]$Superseding = 0) {
    $c = Get-Cfg
    $pr = @(Invoke-Gh -Path "$(Get-RepoPath)/pulls?state=open&head=$([uri]::EscapeDataString("$($c.owner):$Head"))&per_page=5") | Select-Object -First 1
    if (-not $pr) { return $null }
    # a draft that names another item, or that another item's state already holds, is not this item's to take
    $others = @([regex]::Matches("$($pr.title) $($pr.body)", '(?<![0-9A-Za-z])#(\d+)\b') | ForEach-Object { [int]$_.Groups[1].Value } | Where-Object { $_ -ne $n -and $_ -ne $Superseding })
    # a pull request it names is no other item: a Copilot follow-up opens its own draft "which merges into #<the item's pull request>" (#474 on #470)
    $others = @($others | Where-Object { $op = Invoke-Gh -Path "$(Get-RepoPath)/pulls/$_" -Allow 404; $op -is [hashtable] -and $op.ContainsKey('status') })
    if ($others.Count) { Reject 10 'branch-of-other-item' "the pull request on '$Head' names #$($others[0]), not #$n" }
    # a draft Copilot opened against the item's previous branch (a task started from it) goes to the default branch: it carries that branch's commits.
    # GitHub refuses that for a draft that is part of a stack (422 "part of a stack", #395/#398): the draft is closed, its branch kept, and the
    # caller opens the item's pull request on the branch itself
    if ("$($pr.base.ref)" -ne "$((Get-Cfg).defaultBranch)") {
        $rb = Invoke-Gh -Method PATCH -Path "$(Get-RepoPath)/pulls/$($pr.number)" -Body @{ base = "$((Get-Cfg).defaultBranch)" } -Allow 422
        if ($rb -is [hashtable] -and $rb.ContainsKey('status')) {
            [void](Invoke-Gh -Method POST -Path "$(Get-RepoPath)/issues/$($pr.number)/comments" -Body @{ body = "Closed by the factory worker gateway: GitHub does not retarget this stacked draft to $((Get-Cfg).defaultBranch); the pull request of #$n is opened on the same branch instead." } -Allow 404, 422)
            [void](Invoke-Gh -Method PATCH -Path "$(Get-RepoPath)/pulls/$($pr.number)" -Body @{ state = 'closed' })
            return $null
        }
    }
    if ("$($pr.title)" -notmatch "(^|[^0-9])#$n([^0-9]|$)") { Invoke-Gh -Method PATCH -Path "$(Get-RepoPath)/pulls/$($pr.number)" -Body @{ title = $Title } | Out-Null }
    if ($pr.draft) { [void](Invoke-GhGraphQL 'mutation($id:ID!){ markPullRequestReadyForReview(input:{pullRequestId:$id}){ pullRequest{ number } } }' @{ id = "$($pr.node_id)" }) }
    $st = Get-State $n; $st.state.pr = @{ number = $pr.number; branch = $Head; url = $pr.html_url }; Save-State $n $st
    return (Get-Pr ([int]$pr.number))
}

# The item's open pull request for the Copilot runtime (CopilotCloudRuntime: a task of an item that has one starts from its branch)
function Verb_item_pr($a) {
    $n = Get-ItemNumber $a
    # an owner task's item: the open pull request of the task's branch (a recorded one that was closed or superseded is replaced)
    # item-pr runs without the item lock (NoLockVerbs): the resolution writes the state, so it takes the lock; busy: what the state records
    $ow = try { Use-ItemLock $n { Resolve-OwnerPr $n } 20 } catch [FactoryReject] { if ($_.Exception.Code -eq 'item-busy') { $null } else { throw } }
    $pr = if ($ow) { $ow.pr } else { Find-ItemPr $n }
    if (-not $pr) { return @{ ok = $true; id = $n; found = $false } }
    return @{ ok = $true; id = $n; found = $true; number = [int]$pr.number; branch = "$($pr.head.ref)" }
}

# CI release (docs/design/copilot-cloud-agent.md, "CI release"): GitHub holds the workflow runs of a Copilot cloud agent's push in
# action_required, and POST .../approve refuses them; a re-run by an identity with write access starts them. Called by the gateway itself (never
# a session) with the items whose runtime is copilot-cloud ($a.items) and the run ids it already handled ($a.skip). A run is released only when
# its head branch is copilot/*, its item is open, in this gateway's env scope, carries no other runtime: label, and the item's pull request has
# that branch and the run's head_sha as its head. No file guard: the owner's decision of 2026-10-20. Released with the gateway's own identity
# (the author App); a 403 is reported, never retried with another credential.
function Verb_ci_release($a) {
    $items = @(@($a.items) | Where-Object { "$_" -match '^\d{1,7}$' } | ForEach-Object { [int]"$_" } | Select-Object -Unique)
    $skip = @(@($a.skip) | ForEach-Object { "$_" })
    $all = @((Invoke-Gh -Path "$(Get-RepoPath)/actions/runs?status=action_required&per_page=100").workflow_runs)
    $runs = @($all | Where-Object { "$($_.head_branch)" -like 'copilot/*' -and "$($_.id)" -notin $skip })
    $out = @()
    # the App's Actions permission, read once per process by the gateway (probe): the installation as the App sees it (its JWT)
    $perm = $null
    if ($a.probe -eq $true -and $env:FACTORY_AUTHOR_APP -eq '1' -and -not $env:FACTORY_FAKE_APPS) { $perm = try { Get-AppActionsPermission 'author' } catch { '' } }
    elseif ($a.probe -eq $true -and $env:FACTORY_FAKE_APPS) { $perm = "$env:FACTORY_FAKE_APP_ACTIONS" }
    # routeAll (the gateway routes every phase of an unlabelled item to copilot-cloud): the items of the held runs' pull requests are candidates too;
    # the recorded pull request below still has to name the branch
    $routeAll = $a.routeAll -eq $true
    if ($routeAll -and $runs.Count -gt 0) {
        $open = @(Invoke-Gh -Path "$(Get-RepoPath)/pulls?state=open&per_page=100")
        foreach ($r in $runs) { foreach ($p in @($open | Where-Object { "$($_.head.ref)" -eq "$($r.head_branch)" })) { if ("$($p.title)" -match '#(\d{1,7})\b') { $items += [int]$Matches[1] } } }
        $items = @($items | Select-Object -Unique)
    }
    $given = @(@($a.items) | ForEach-Object { "$_" })
    if ($runs.Count -eq 0 -or $items.Count -eq 0) { return @{ ok = $true; pending = $runs.Count; runs = $out; actions = $perm } }
    $heads = @{}   # head branch -> @{ item; pr; sha }
    foreach ($n in $items) {
        $info = try { Get-ItemInfo $n } catch [FactoryReject] { $out += @{ item = $n; result = 'skipped'; reason = $_.Exception.Code }; continue }
        if (-not $info.open) { continue }
        $rtl = @($info.labels | Where-Object { "$_" -like 'runtime:*' })
        if (-not ($rtl.Count -eq 0 -or ($rtl.Count -eq 1 -and "$($rtl[0])" -ieq 'runtime:copilot-cloud'))) { $out += @{ item = $n; result = 'skipped'; reason = 'other-runtime' }; continue }
        if ($rtl.Count -eq 0 -and -not $routeAll -and "$n" -notin $given) { $out += @{ item = $n; result = 'skipped'; reason = 'not-copilot' }; continue }
        # only the pull request pr_open recorded for the item (number and copilot/ branch), never a branch-name match
        $sp = try { (Get-State $n).state } catch { $null }
        if (-not ($sp -and (Test-Has $sp 'pr') -and (Test-Has $sp.pr 'branch') -and (Test-Has $sp.pr 'number') -and "$($sp.pr.branch)" -like 'copilot/*')) { continue }
        $pr = try { Get-Pr ([int]$sp.pr.number) } catch { $null }
        if (-not $pr -or "$($pr.state)" -ne 'open' -or "$($pr.head.ref)" -ne "$($sp.pr.branch)") { continue }
        $hb = "$($pr.head.ref)"
        if ($heads.ContainsKey($hb)) { $heads[$hb] = @{ item = 0 }; continue }   # two items name one branch: nobody's
        $heads[$hb] = @{ item = $n; pr = [int]$pr.number; sha = "$($pr.head.sha)" }
    }
    foreach ($r in $runs) {
        $h = $heads["$($r.head_branch)"]
        $o = @{ run = [long]$r.id; branch = "$($r.head_branch)"; sha = "$($r.head_sha)" }
        if (-not $h -or -not $h.item) { $o.result = 'skipped'; $o.reason = 'no-item'; $out += $o; continue }
        if ([int]"$($r.run_attempt)" -gt 1) { $o.result = 'skipped'; $o.reason = 'already-rerun'; $out += $o; continue }   # re-run before (by this gateway before a restart, or by someone else)
        $o.item = $h.item; $o.pr = $h.pr
        if ($h.sha -ne "$($r.head_sha)") { $o.result = 'skipped'; $o.reason = 'head-mismatch'; $out += $o; continue }
        $x = Invoke-GhRaw -Method POST -Path "$(Get-RepoPath)/actions/runs/$([long]$r.id)/rerun" -Tries 1
        $o.status = [int]$x.status
        if ($x.status -ge 200 -and $x.status -lt 300) { $o.result = 'released' }
        else {
            $msg = if ($x.body -and $x.body.PSObject.Properties['message']) { "$($x.body.message)" } else { '' }
            if ($msg.Length -gt 300) { $msg = $msg.Substring(0, 300) }
            $o.result = if ($x.status -eq 403 -and (Get-GhRateLimitWait 403 $x.headers $msg) -lt 0) { 'refused' } else { 'failed' }
            $o.message = $msg
        }
        $out += $o
    }
    return @{ ok = $true; pending = $runs.Count; runs = $out; actions = $perm }
}

# copilot-settle: the gateway owns the completion of a Copilot Development task (CopilotCloudRuntime). The coding agent's session ends before CI
# does, so item_complete may never succeed and the agent may even report a failure while its pull request is fine (#393 to #395). Admin only (the
# gateway's runner); never a worker's verb. Body: item, stage (launch | end), head (end: the branch of the task's artifacts).
#   launch: before a Development task is started. The item already has a pull request with changes and no fix-forward or rework is asked for
#           (no pr.rejection): no task; the gateway waits for its checks instead (action awaiting). A rework: the head it starts from is recorded
#           (copilot_base), so its end can tell whether it pushed anything. Otherwise: action launch.
#   end:    after a Development task ended, whatever the agent reported. The task's branch (head) with changes is taken over as the item's pull
#           request (open-pr: its draft, or a new pull request, superseding the old one); then a pull request with changes and a head the task
#           did not start from waits for its checks in the gateway: Development is done pending checks (awaiting_checks, the sweep moves the item
#           to the next column when they are green and hands it back to Development to fix forward when they are red). Otherwise action none
#           (the relaunch cap decides).
# A park other than the agent's worker-failure is left alone; so is an item that is not in Development or already waits for its checks.
function Verb_copilot_settle($a) {
    $n = Get-ItemNumber $a; $c = Get-Cfg
    $stage = "$(Need $a 'stage')"; if ($stage -notin 'launch', 'end') { Reject 2 'usage' 'stage is launch or end' }
    $head = if (Test-Has $a 'head') { "$($a.head)" } else { '' }
    $info = Get-ItemInfo $n
    $none = { param($why) @{ ok = $true; id = $n; action = $(if ($stage -eq 'launch') { 'launch' } else { 'none' }); reason = $why } }
    if (-not $info.open) { return (& $none 'closed') }
    if ("$($info.column)" -ine "$($c.columns.development)") { return (& $none 'not-in-development') }
    # an owner task's item: its pull request is the open one of the task's branch (a closed or superseded record is replaced first)
    try { [void](Resolve-OwnerPr $n) } catch { }
    $s = (Get-State $n).state
    if (Test-Has $s 'awaiting_checks') { return @{ ok = $true; id = $n; action = 'awaiting'; already = $true; pr = $s.awaiting_checks.pr } }
    if ((Test-Has $s 'merge') -and (Test-Has $s.merge 'sha') -and $s.merge.sha) { return (& $none 'merged') }
    $park = Get-Park $s
    if ($park -and "$($park.reason)" -ne 'worker-failure') { return (& $none "parked $($park.reason)") }
    $rework = (Test-Has $s 'pr') -and (Test-Has $s.pr 'rejection')
    $pr = Find-ItemPr $n
    if ($stage -eq 'launch') {
        if (-not $pr) { return (& $none 'no-pr') }
        if ($rework) { $rs = Get-State $n; $rs.state.copilot_base = "$($pr.head.sha)"; Save-State $n $rs; return @{ ok = $true; id = $n; action = 'launch'; reason = 'rework'; pr = [int]$pr.number } }
    } elseif ($head -match '^copilot/[A-Za-z0-9._/-]{1,150}$' -and -not ($pr -and "$($pr.head.ref)" -eq $head)) {
        # the task's own branch: taken over when it carries changes beyond the item's pull request (or the default branch when it has none)
        $from = if ($pr) { "$($pr.head.sha)" } else { "$($c.defaultBranch)" }
        $cmp = Invoke-Gh -Path "$(Get-RepoPath)/compare/$([uri]::EscapeDataString($from))...$([uri]::EscapeDataString($head))" -Allow 404
        $ok = $cmp -and -not ($cmp -is [hashtable] -and $cmp.ContainsKey('body'))
        $files = if ($ok -and $cmp.PSObject.Properties['files']) { @($cmp.files | Where-Object { $_ }).Count } elseif ($ok) { [int]$cmp.ahead_by } else { 0 }
        $ahead = $ok -and "$($cmp.status)" -eq 'ahead' -and [int]$cmp.ahead_by -gt 0 -and ($pr -or $files -gt 0)
        if ($ahead) {
            try { [void](Verb_open_pr @{ item = $n; head = $head; title = "$($info.title)" }) }
            catch [FactoryReject] { return @{ ok = $true; id = $n; action = 'none'; reason = "takeover-refused $($_.Exception.Code)" } }
            $pr = Find-ItemPr $n
        } elseif ($pr) { Close-StrayCopilotDrafts $n @("$($pr.head.ref)") ([int]$pr.number) }
    }
    if (-not $pr) { return (& $none 'no-pr') }
    $prd = Get-Pr ([int]$pr.number)
    if ([int]$prd.changed_files -eq 0) { return (& $none 'empty-pr') }
    # the item's pull request is never left a draft: GitHub merges no draft, and nobody would mark it ready (#418, #421)
    if ($prd.draft) { [void](Invoke-GhGraphQL 'mutation($id:ID!){ markPullRequestReadyForReview(input:{pullRequestId:$id}){ pullRequest{ number } } }' @{ id = "$($prd.node_id)" }) }
    $st = Get-State $n; $s = $st.state
    if ($stage -eq 'end' -and (Test-Has $s 'copilot_base') -and "$($s.copilot_base)" -eq "$($prd.head.sha)") { return (& $none 'no-new-commits') }
    if ((Test-DocsOnlyFiles (Get-PrFiles $pr.number)) -and -not (Test-ItemDocsOnly $s $info)) { return (& $none 'docs-only-pr') }
    $rerun = if ((Test-Has $s 'pr') -and (Test-Has $s.pr 'rerun') -and [int]$s.pr.number -eq [int]$pr.number) { $s.pr.rerun } else { $null }
    $s.pr = @{ number = $pr.number; branch = "$($pr.head.ref)"; url = $pr.html_url }; if ($null -ne $rerun) { $s.pr.rerun = $rerun }
    $why = if ($stage -eq 'launch') { 'it already has its pull request with changes; no new Development task is started' } else { 'the Copilot task ended' }
    $s.awaiting_checks = @{ pr = $pr.number; sha = "$($prd.head.sha)"; comment = "Completed by the gateway: $why."; since = ConvertTo-Iso (Get-UtcNow); column = "$($info.column)" }
    if ($park) { $s.Remove('park') }
    Set-Park $s 'development:awaiting-checks' $c.timing.awaitingChecksParkMinutes
    $s.Remove('copilot_base'); $s.Remove('claim'); Save-State $n $st; Remove-ClaimRef $n
    Add-Comment $n "Development is done pending checks: $why, and PR #$($pr.number) carries the change (head ``$("$($prd.head.sha)".Substring(0, [Math]::Min(7, "$($prd.head.sha)".Length)))``). The gateway waits for its checks and moves the item on when they are green; red checks go back to Development to fix forward."
    return @{ ok = $true; id = $n; action = 'awaiting'; pr = [int]$pr.number; sha = "$($prd.head.sha)"; overrode = $(if ($park) { "$($park.reason)" } else { '' }) }
}

# copilot-owner: the item's owner task (docs/design/copilot-cloud-agent.md, "One task, resumed"). The gateway's own verb (refused over HTTP).
# Body: item, action get | set | close | status; set: task {id, url, branch, pr, phase}. The item state's copilot_task is the durable record:
#   get:    the record (null when the item has none, or its task is done)
#   set:    records the owner task; the first record of a task comments the ownership on the item; later sets update branch and pull request
#   close:  the owner is done (merged, master green): done_at, a comment with the task's link
#   status: what the owner waits on: ci (its checks), next-phase, parked:<reason>, master-green, master-broken (with the failed checks and the
#           merge commit), done (master green for the merge), closed
function Verb_copilot_owner($a) {
    $n = Get-ItemNumber $a
    $action = "$(Need $a 'action')"; if ($action -notin 'get', 'set', 'close', 'status') { Reject 2 'usage' 'action is get, set, close or status' }
    $st = Get-State $n; $s = $st.state
    $own = if (Test-Has $s 'copilot_task') { $s.copilot_task } else { $null }
    if ($action -eq 'get') { return @{ ok = $true; id = $n; task = $(if ($own -and -not ((Test-Has $own 'done_at') -and $own.done_at)) { $own } else { $null }) } }
    if ($action -eq 'set') {
        $t = Need $a 'task'
        $id = "$($t.id)"; if ($id -notmatch '^[A-Za-z0-9-]{1,80}$') { Reject 2 'usage' 'task.id is the agent task id' }
        $url = "$($t.url)"; if ($url -and $url -notmatch '^https://[A-Za-z0-9./_?=&%~-]{1,300}$') { $url = '' }
        $branch = "$($t.branch)"; if ($branch -and $branch -notmatch '^[A-Za-z0-9._/-]{1,200}$') { $branch = '' }
        $pr = if ("$($t.pr)" -match '^\d{1,7}$') { [int]"$($t.pr)" } else { 0 }
        # the runtime of the owner: copilot-cloud (a Copilot task, the default) or cursor-cloud (a Cursor agent with follow-up runs)
        $rt = if ((Test-Has $t 'runtime') -and "$($t.runtime)" -eq 'cursor-cloud') { 'cursor-cloud' } else { 'copilot-cloud' }
        $first = -not $own -or "$($own.id)" -ne $id
        if ($first) {
            $prev = @(if ($own -and -not ((Test-Has $own 'done_at') -and $own.done_at)) { @($own.previous) + @("$($own.id)") | Where-Object { $_ } })
            $s.copilot_task = @{ id = $id; url = $url; branch = $branch; pr = $pr; phase = "$($t.phase)"; since = ConvertTo-Iso (Get-UtcNow); previous = $prev; runtime = $rt }
        } else {
            if ($branch) { $own.branch = $branch }; if ($pr -gt 0) { $own.pr = $pr }; if ($url) { $own.url = $url }; if ("$($t.phase)") { $own.phase = "$($t.phase)" }
        }
        Save-State $n $st
        if ($first) {
            $link = if ($url) { "[$id]($url)" } else { "``$id``" }
            if ($rt -eq 'cursor-cloud') { Add-Comment $n "**Owner:** Cursor agent $link owns #$n from $(if ("$($t.phase)") { "$($t.phase)" } else { 'now' }) until the change is in production. Between phases the gateway holds the item for this agent and continues it with a follow-up run; no other worker is offered the item." }
            else { Add-Comment $n "**Owner:** Copilot task $link owns #$n from $(if ("$($t.phase)") { "$($t.phase)" } else { 'now' }) until its pull request is merged and master is green. Between phases the gateway holds the item for this task and resumes it with a follow-up session; no other worker is offered the item." }
        }
        return @{ ok = $true; id = $n; task = $s.copilot_task; first = $first }
    }
    if ($action -eq 'close') {
        if (-not $own) { return @{ ok = $true; id = $n; closed = $false; reason = 'no-owner' } }
        if ((Test-Has $own 'done_at') -and $own.done_at) { return @{ ok = $true; id = $n; closed = $false; reason = 'already' } }
        $own.done_at = ConvertTo-Iso (Get-UtcNow); Save-State $n $st
        if ((Test-Has $a 'unmerged') -and $a.unmerged) { $own.ended = 'closed-unmerged'; Save-State $n $st; return @{ ok = $true; id = $n; closed = $true; unmerged = $true } }
        $sha = if ((Test-Has $s 'merge') -and (Test-Has $s.merge 'sha')) { "$($s.merge.sha)" } else { '' }
        $link = if ("$($own.url)") { "[$($own.id)]($($own.url))" } else { "``$($own.id)``" }
        $merged = if ([int]$own.pr -gt 0) { ": PR #$($own.pr) merged" } else { '' }
        if ($sha) { $merged += ' as `' + $sha.Substring(0, [Math]::Min(7, $sha.Length)) + '`' }
        $sessionLink = if ("$($own.url)") { "$($own.url)" } else { "$($own.id)" }
        $rel = if ((Test-Has $s 'merge') -and (Test-Has $s.merge 'release') -and $s.merge.release) { " in release $($s.merge.release)" } else { '' }
        $oc = (Get-Cfg).octopus; $prodAt = if ((Test-Has $oc 'prodUrl') -and "$($oc.prodUrl)") { " ($($oc.prodUrl))" } else { '' }
        $who = if ((Test-Has $own 'runtime') -and "$($own.runtime)" -eq 'cursor-cloud') { 'Cursor agent' } else { 'Copilot task' }
        Add-Comment $n "**Owner done:** $who $link finished #$n$merged, master is green and the change is delivered to production$rel$prodAt. Session link: $sessionLink"
        return @{ ok = $true; id = $n; closed = $true }
    }
    # status
    $info = Get-ItemInfo $n
    # before the merge: the item's pull request is the open one of the owner task's branch (a closed or superseded record is replaced)
    if ($info.open -and -not ((Test-Has $s 'merge') -and (Test-Has $s.merge 'sha') -and $s.merge.sha)) {
        $ow = try { Resolve-OwnerPr $n } catch { $null }
        if ($ow) { $st = Get-State $n; $s = $st.state }
    }
    $m = if (Test-Has $s 'merge') { $s.merge } else { $null }
    $park = Get-Park $s
    $o = @{ ok = $true; id = $n; open = [bool]$info.open; column = "$($info.column)"; branch = $(if ($own) { "$($s.copilot_task.branch)" } else { '' }); pr = $(if ((Test-Has $s 'pr') -and (Test-Has $s.pr 'number')) { [int]$s.pr.number } else { 0 }) }
    if ($m -and (Test-Has $m 'sha') -and $m.sha) {
        $o.merge_sha = "$($m.sha)"
        # the owner's work ends in production (Jeffrey 2026-10-21): delivered (done_at, the Release Queue saw the prod deployment)
        # (a merged item closed by any other route, a person's Done or a docs-only path that sets no done_at, ends the ownership too)
        if (((Test-Has $m 'done_at') -and $m.done_at) -or (-not $info.open -and -not ((Test-Has $m 'awaiting') -and $m.awaiting))) { $o.waiting = 'done'; if (Test-Has $m 'release') { $o.release = "$($m.release)" } }
        elseif ("$($m.awaiting)" -eq 'release-queue') { $o.waiting = $(if ((Test-Has $m 'prod_failed') -and $m.prod_failed) { 'prod-failed' } else { 'prod-deploy' }) }
        elseif ("$($m.awaiting)" -in 'release-candidate', 'late-rc') { $o.waiting = 'release-candidate' }
        elseif ("$($m.awaiting)" -eq 'master-broken') { $o.waiting = 'master-broken'; $o.failed = @(if ($park -and (Test-Has $park 'failed')) { @($park.failed) }) }
        else { $o.waiting = 'master-green' }
    }
    elseif (-not $info.open) { $o.waiting = 'closed' }
    elseif (Test-Has $s 'awaiting_checks') { $o.waiting = 'ci' }
    elseif ($park) { $o.waiting = "parked:$($park.reason)" }
    else { $o.waiting = 'next-phase' }
    if ((Test-Has $s 'pr') -and (Test-Has $s.pr 'rejection')) { $rj = "$($s.pr.rejection)"; $o.rework = $rj.Substring(0, [Math]::Min(600, $rj.Length)) }
    return $o
}

# Close a pull request with a comment and delete its branch (a copilot/* branch or the factory's own branch of the item only)
function Close-PrWithBranch($Pr, [int]$n, [string]$Why) {
    $c = Get-Cfg
    [void](Invoke-Gh -Method POST -Path "$(Get-RepoPath)/issues/$($Pr.number)/comments" -Body @{ body = (Add-SessionFooter $Why) } -Allow 404, 422)
    [void](Invoke-Gh -Method PATCH -Path "$(Get-RepoPath)/pulls/$($Pr.number)" -Body @{ state = 'closed' })
    $ref = "$($Pr.head.ref)"
    if ($ref -like 'copilot/*' -or $ref -match "^$([regex]::Escape($c.name))/$n-") { [void](Invoke-Gh -Method DELETE -Path "$(Get-RepoPath)/git/refs/heads/$ref" -Allow 404, 422) }
}

# Extra drafts a Copilot task opened against the item's branches (a task that started from the item's pull request and opened its own draft,
# then fixed the item's pull request another way): closed, branch deleted. Only open copilot/* drafts whose base is one of $Bases, never the
# default branch, never $Keep.
function Close-StrayCopilotDrafts([int]$n, [string[]]$Bases, [int]$Keep) {
    $def = "$((Get-Cfg).defaultBranch)"
    foreach ($p in @(Invoke-Gh -Path "$(Get-RepoPath)/pulls?state=open&per_page=100")) {
        if (-not $p -or [int]$p.number -eq $Keep -or -not $p.draft -or "$($p.head.ref)" -notlike 'copilot/*') { continue }
        $b = "$($p.base.ref)"; if ($b -eq $def -or $b -notin $Bases) { continue }
        Close-PrWithBranch $p $n "Closed by the factory worker gateway: an extra draft of a task of #$n; the item's pull request is #$Keep."
    }
}

# The item's pull request of a Copilot owner task (docs/design/copilot-cloud-agent.md, "One task, resumed"): always the open pull request of the
# owner task's current branch (copilot_task.branch). A recorded pull request that was closed or superseded (a migration, a takeover) is replaced
# by it in the state (pr, copilot_task.pr, awaiting_checks). Every owner pull request targets the default branch: one stacked on another branch is
# retargeted once that base has no open pull request (merged or closed); while the base's pull request is still open, the stack is consolidated
# onto the owner's pull request when its branch holds the base's commits (the commit-ancestry guard of #192), the base's pull request is closed as
# superseded. Returns $null when the item has no owner branch with an open pull request; else @{ pr; changed; retargeted; superseded; refused }.
# An owner pull request still a draft is marked ready for review (GraphQL): GitHub merges no draft and QC is not offered for one (#483, #488:
# a PASS parked pass-unmerged on a successor's draft). Returns the pull request as it is afterwards.
function Set-PrReady($pr) {
    if (-not $pr -or -not $pr.draft) { return $pr }
    try { [void](Invoke-GhGraphQL 'mutation($id:ID!){ markPullRequestReadyForReview(input:{pullRequestId:$id}){ pullRequest{ number } } }' @{ id = "$($pr.node_id)" }) } catch { return $pr }
    return (Get-Pr ([int]$pr.number))
}

function Resolve-OwnerPr([int]$n) {
    $c = Get-Cfg; $def = "$($c.defaultBranch)"
    $st = Get-State $n; $s = $st.state
    if (-not (Test-Has $s 'copilot_task') -or -not "$($s.copilot_task.branch)") { return $null }
    if ((Test-Has $s.copilot_task 'done_at') -and $s.copilot_task.done_at) { return $null }
    $br = "$($s.copilot_task.branch)"
    $pr = @(Invoke-Gh -Path "$(Get-RepoPath)/pulls?state=open&head=$([uri]::EscapeDataString("$($c.owner):$br"))&per_page=5") | Where-Object { $_ -and "$($_.head.ref)" -eq $br } | Select-Object -First 1
    # a follow-up session may open its own draft on a new branch, stacked on the owner's ("merges into #<pr>", #468 on #466, #474 on #470): the
    # owner's work goes on there, so its current branch is the newest such successor (followed a few levels)
    for ($hop = 0; $hop -lt 3; $hop++) {
        $succ = @(Invoke-Gh -Path "$(Get-RepoPath)/pulls?state=open&base=$([uri]::EscapeDataString($br))&per_page=20") | Where-Object { $_ -and ("$($_.head.ref)" -like 'copilot/*' -or "$($_.head.ref)" -match "^$([regex]::Escape($c.name))/$n-") -and "$($_.base.ref)" -eq $br } | Sort-Object { [int]$_.number } | Select-Object -Last 1
        if (-not $succ) { break }
        $br = "$($succ.head.ref)"; $pr = $succ
    }
    # the item's recorded pull request, when open on a copilot/* branch and newer than the one found, is the owner's current one: a task's later
    # branch that is not stacked on the owner's (based on the default branch) is found by no base lookup, and the owner branch must not go back
    # to the closed older pull request (#455: FT sessions were sent to the closed #470, never connected)
    $recNo = if ((Test-Has $s 'pr') -and (Test-Has $s.pr 'number')) { [int]$s.pr.number } else { 0 }
    if ($recNo -gt 0 -and (-not $pr -or $recNo -gt [int]$pr.number)) {
        $rp = try { Get-Pr $recNo } catch { $null }
        if ($rp -and "$($rp.state)" -eq 'open' -and -not $rp.merged -and "$($rp.head.ref)" -like 'copilot/*') { $pr = $rp; $br = "$($rp.head.ref)" }
    }
    if (-not $pr) { return $null }
    if ("$($s.copilot_task.branch)" -ne $br) { $s.copilot_task.branch = $br; Save-State $n $st }
    $o = @{ pr = $pr; changed = $false; retargeted = $false; superseded = 0; refused = '' }
    if ("$($pr.base.ref)" -ne $def) {
        $basePr = @(Invoke-Gh -Path "$(Get-RepoPath)/pulls?state=open&head=$([uri]::EscapeDataString("$($c.owner):$($pr.base.ref)"))&per_page=5") | Where-Object { $_ -and "$($_.head.ref)" -eq "$($pr.base.ref)" } | Select-Object -First 1
        $go = $true
        if ($basePr) {
            # the base's pull request is still open: consolidate only when the owner's branch holds its commits
            $cmp = Invoke-Gh -Path "$(Get-RepoPath)/compare/$($basePr.head.sha)...$($pr.head.sha)" -Allow 404
            if (-not $cmp -or ($cmp -is [hashtable] -and $cmp.ContainsKey('status') -and $cmp.ContainsKey('body')) -or "$($cmp.status)" -notin 'ahead', 'identical') { $go = $false; $o.refused = "stacked on #$($basePr.number), whose commits '$br' does not hold" }
        }
        if ($go) {
            $rb = Invoke-Gh -Method PATCH -Path "$(Get-RepoPath)/pulls/$($pr.number)" -Body @{ base = $def } -Allow 422
            if ($rb -is [hashtable] -and $rb.ContainsKey('status')) { $o.refused = "GitHub refused to retarget #$($pr.number) to ${def}: $($rb.body.message)" }
            else {
                $o.retargeted = $true; $pr = Get-Pr ([int]$pr.number); $o.pr = $pr
                Add-Comment $n "PR #$($pr.number), the pull request of the owner task's branch ``$br``, was stacked on ``$($pr.base.ref)``$(if ($basePr) { " (#$($basePr.number))" }); the gateway retargeted it to **$def**, where CI runs."
                # the base's pull request only after the retarget: deleting its branch first would close the stacked one with it
                if ($basePr) { Close-PrWithBranch $basePr $n "Superseded by #$($pr.number), the pull request of #$n now: it holds this branch's commits and the owner task's work. Closed by the factory worker gateway."; $o.superseded = [int]$basePr.number }
            }
        }
    }
    $st = Get-State $n; $s = $st.state
    $rec = if ((Test-Has $s 'pr') -and (Test-Has $s.pr 'number')) { [int]$s.pr.number } else { 0 }
    # only a recorded pull request is replaced: none recorded yet (a Technical Design draft) stays unrecorded until pr_open or the settle takes it
    if ($rec -gt 0 -and $rec -ne [int]$pr.number) {
        # what the record says about the work (a rework asked for, the failed checks) stays; the re-run record was of the old pull request
        $s.pr.number = [int]$pr.number; $s.pr.branch = $br; $s.pr.url = "$($pr.html_url)"; if (Test-Has $s.pr 'rerun') { $s.pr.Remove('rerun') }
        $o.changed = $true
    }
    if ((Test-Has $s 'awaiting_checks') -and [int]$s.awaiting_checks.pr -ne [int]$pr.number) { $s.awaiting_checks.pr = [int]$pr.number; $s.awaiting_checks.sha = "$($pr.head.sha)"; $s.awaiting_checks.since = ConvertTo-Iso (Get-UtcNow); $o.changed = $true }
    if ([int]$s.copilot_task.pr -ne [int]$pr.number) { $s.copilot_task.pr = [int]$pr.number; $o.changed = $true }
    # a recorded owner pull request (a successor adopted after Development included) is never left a draft
    if ($rec -gt 0 -and $pr.draft) { $pr = Set-PrReady $pr; $o.pr = $pr }
    if ($o.changed) {
        Save-State $n $st
        if ($rec -gt 0 -and $rec -ne [int]$pr.number) { Add-Comment $n "The item's pull request is now #$($pr.number), the open pull request of the owner task's branch ``$br`` (the recorded #$rec is closed or superseded)." }
    }
    return $o
}

function Verb_open_pr($a) {
    $n = Get-ItemNumber $a; $c = Get-Cfg
    $head = "$(Need $a 'head')"; $title = "$(Need $a 'title')"
    $info = Get-ItemInfo $n; Assert-Open $info
    if ($info.column -ine $c.columns.development) { Reject 10 'wrong-column' "pull requests are opened from '$($c.columns.development)'; the item is in '$($info.column)'" }
    # the Copilot cloud agent pushes only copilot/* branches (docs/design/copilot-cloud-agent.md): such a branch is the item's when the item is the
    # Copilot runtime's (no runtime: label routes it there by default, or runtime:copilot-cloud); the state records it as the item's branch
    $copilotHead = $head -match '^copilot/[A-Za-z0-9._/-]{1,150}$' -and -not @($info.labels | Where-Object { $_ -match '^runtime:' -and $_ -ne 'runtime:copilot-cloud' }).Count
    if (-not $copilotHead -and $head -notmatch "^$([regex]::Escape($c.name))/$n-") { Reject 10 'bad-branch-name' "the branch must be named '$($c.name)/$n-<something>' so the pull request belongs to the item" }
    if ($title -notmatch "(^|[^0-9])#$n([^0-9]|$)") { $title = "$title (#$n)" }
    $base = if (Test-Has $a 'base') { "$($a.base)" } else { $c.defaultBranch }
    $body = if (Test-Has $a 'body') { "$($a.body)" } else { '' }
    $existing = Find-ItemPr $n
    # the item already has its pull request on another branch (fix-forward, rework): a Copilot task started from that branch and pushed its own
    # (CopilotCloudRuntime.BaseRef); the new branch must hold the old one's commits, then it becomes the item's pull request and the old one is
    # closed as superseded, its branch deleted
    $prev = if ($existing -and $existing.head.ref -ne $head) { $existing } else { $null }
    if ($prev -and $copilotHead) {
        $cmp = Invoke-Gh -Path "$(Get-RepoPath)/compare/$($prev.head.sha)...$head" -Allow 404
        if (-not $cmp -or "$($cmp.status)" -notin 'ahead', 'identical') { Reject 10 'not-on-item-branch' "#$n already has its pull request #$($prev.number) on '$($prev.head.ref)'; '$head' does not hold its commits. Work on a branch made from '$($prev.head.ref)' (the task's own branch is) and pass that." }
    }
    if ($copilotHead -and -not ($existing -and $existing.head.ref -eq $head)) { $existing = Use-CopilotDraft $n $head $title $(if ($prev) { [int]$prev.number } else { 0 }) }
    if ($prev -and $copilotHead -and -not $existing) {
        # no draft on the branch: the item's pull request is opened on it below, then the old one is superseded
        Assert-PushGate $n $info (Get-State $n).state '' $null
        $r = Invoke-Gh -Method POST -Path "$(Get-RepoPath)/pulls" -Allow 422 -Body @{ title = $title; head = $head; base = $c.defaultBranch; body = (Get-PrBodyText $body $n); draft = $false }
        if ($r -is [hashtable] -and $r.ContainsKey('status')) { Reject 10 'pr-not-created' "GitHub refused to create the pull request on '$head': $($r.body.message)" }
        $st = Get-State $n; $st.state.pr = @{ number = $r.number; branch = $head; url = $r.html_url }; Save-State $n $st
        $existing = Get-Pr ([int]$r.number)
    }
    if ($prev -and $copilotHead -and $existing -and [int]$existing.number -ne [int]$prev.number) {
        Assert-PushGate $n $info (Get-State $n).state "$($existing.head.sha)" $existing.number
        Close-StrayCopilotDrafts $n @("$($prev.head.ref)", $head) ([int]$existing.number)
        Close-PrWithBranch $prev $n "Superseded by #$($existing.number), the pull request of #$n now: it holds this branch's commits and the fix. Closed by the factory worker gateway."
    }
    $gs = (Get-State $n).state
    if ($existing -and $existing.head.ref -eq $head) {
        if ($copilotHead) { Close-StrayCopilotDrafts $n @($head) ([int]$existing.number) }
        Assert-PushGate $n $info $gs "$($existing.head.sha)" $existing.number
        $bo = Get-BuildOwed $info $existing.number $gs
        return @{ ok = $true; id = $n; created = $false; pullRequest = @{ number = $existing.number; url = $existing.html_url; head_sha = $existing.head.sha; branch = $head }; build_owed = $bo.build_owed; build_reason = $bo.build_reason }
    }
    Assert-PushGate $n $info $gs '' $null
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
            if (Test-Has $a 'item') {
                $n = [int]$a.item
                if (Invoke-PreexistingRerun $n $r $d) { Reject 21 'rerun-started' "the one failed check ($(@($r.failed_checks) -join ', ')) fails on the base branch too (likelyPreexisting): the gateway reran its job once. Call wait-for-checks again." @{ checks = $d } }
                Save-ChecksRed $n "$($r.sha)"
            }
            Reject 10 'checks-red' "checks failed: $($r.failed_checks -join ', '). 'checks' holds, per failed check, its log excerpt or annotations (data.*) and its baseline on the base branch (likelyPreexisting)." @{ checks = $d }
        }
        Start-Sleep -Seconds 10
    } while ((Get-Date) -lt $end)
    Reject 21 'still-pending' 'checks are still running; call again' @{ checks = $r }
}

# One failed check, red on the base branch as well, an Actions job: its job is rerun once per head (state preexisting_reruns). True when the rerun
# was started; a rerun GitHub refuses (no Actions write permission, a run still going) leaves the red as it is.
function Invoke-PreexistingRerun([int]$n, $r, $d) {
    $failed = @($r.failed_checks | Where-Object { $_ })
    if ($failed.Count -ne 1 -or -not "$($r.sha)") { return $false }
    $ck = @(@($d.checks) | Where-Object { $_ -and "$($_.name)" -eq "$($failed[0])" }) | Select-Object -First 1
    if (-not $ck -or -not $ck.likelyPreexisting -or -not $ck.job -or -not $ck.job.available -or -not $ck.job.id) { return $false }
    return [bool](Use-ItemLock $n {
        $st = Get-State $n; $s = $st.state
        $done = @(if (Test-Has $s 'preexisting_reruns') { $s.preexisting_reruns | Where-Object { $_ } })
        if ($done -contains "$($r.sha)") { return $false }
        $x = Invoke-Gh -Method POST -Path "$(Get-RepoPath)/actions/jobs/$($ck.job.id)/rerun" -Allow 403, 404, 409, 422
        if ($x -is [hashtable] -and $x.ContainsKey('status') -and $x.status -in 403, 404, 409, 422) { return $false }
        $s.preexisting_reruns = @(@($done + "$($r.sha)") | Select-Object -Last 10); Save-State $n $st
        return $true
    })
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

# The demo video of a `video` item (issue #42). Called only by the gateway's POST /api/v1/demo/{item}, which has streamed the MP4 to a temp file,
# checked its size and its header, and authorized the caller. Creates release demo-<n> if missing (pre-release, on the default branch, the shape
# of the demo releases a person published) and uploads the asset demo-<n>.mp4, replacing one of that name. Idempotent.
function Verb_publish_demo($a) {
    $n = Get-ItemNumber $a; $c = Get-Cfg
    $file = "$(Need $a 'file')"
    $tmpRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $full = [IO.Path]::GetFullPath($file)
    if (-not $full.StartsWith($tmpRoot, [StringComparison]::Ordinal) -or [IO.Path]::GetFileName($full) -notmatch "^demo-$n-[0-9a-f]{32}\.mp4$" -or -not (Test-Path -LiteralPath $full -PathType Leaf)) {
        Reject 2 'use-endpoint' 'send the MP4 as the body of POST /api/v1/demo/{item} (client: factory.ps1 publish-demo --item N --file F)'
    }
    $info = Get-ItemInfo $n; Assert-Open $info
    if (-not (Test-Label $info $c.videoLabel)) { Reject 10 'not-video-item' "item #$n does not carry the '$($c.videoLabel)' label: it owes no demo video" }
    if ($info.column -notin @($c.columns.development, $c.columns.validation)) { Reject 10 'wrong-column' "item is in '$($info.column)'; a demo video is published in $($c.columns.development) or $($c.columns.validation)" }
    $st = Get-State $n; $s = $st.state
    $holder = if (Test-Has $a 'holder') { "$($a.holder)" } else { 'anonymous' }
    if ((Test-Has $s 'claim') -and (Test-ClaimLive $s) -and $s.claim.holder -ne $holder -and $holder -ne 'anonymous') { Reject 40 'claim-held' "the claim is held by $($s.claim.holder)" }
    $tag = "demo-$n"; $assetName = "demo-$n.mp4"
    $rel = Invoke-Gh -Path "$(Get-RepoPath)/releases/tags/$tag" -Allow 404
    $created = $false
    if ($rel -is [hashtable] -and $rel.status -eq 404) {
        $r = Invoke-GhRaw -Method POST -Path "$(Get-RepoPath)/releases" -Body @{ tag_name = $tag; target_commitish = $c.defaultBranch; name = "Demo video for #$n"
            body = "Functional Validation demo media for #$n (published by the factory worker gateway)."; prerelease = $true; draft = $false }
        if ($r.status -eq 201) { $rel = $r.body; $created = $true }
        elseif ($r.status -eq 422) { $rel = Invoke-Gh -Path "$(Get-RepoPath)/releases/tags/$tag" }   # created at the same moment by another call
        else { Reject 20 'github-error' "GitHub $($r.status) creating release $tag" @{ retryAfterSeconds = 30 } }
    }
    $replaced = $false
    foreach ($old in @($rel.assets | Where-Object { $_ -and $_.name -eq $assetName })) {
        Invoke-Gh -Method DELETE -Path "$(Get-RepoPath)/releases/assets/$($old.id)" -Allow 404 | Out-Null; $replaced = $true
    }
    $up = ("$($rel.upload_url)" -replace '\{.*$', '') + "?name=$assetName"
    $h = @{ Authorization = "Bearer $(Get-GhToken)"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28'; 'User-Agent' = 'factory-worker-gateway' }
    $sc = 0; $asset = $null
    for ($try = 1; $try -le 2; $try++) {
        try { $asset = Invoke-RestMethod -Uri $up -Method POST -Headers $h -ContentType 'video/mp4' -InFile $full -SkipHttpErrorCheck -StatusCodeVariable 'sc' -OperationTimeoutSeconds 480 -ConnectionTimeoutSeconds 30 }
        catch { if ($try -ge 2) { Reject 20 'upload-failed' "the upload to GitHub did not finish: $($_.Exception.Message)" @{ retryAfterSeconds = 60 } }; continue }
        if ($sc -eq 201) { break }
        if ($try -ge 2 -or $sc -notin 502, 503, 504) { Reject 20 'upload-failed' "GitHub $sc on the asset upload" @{ retryAfterSeconds = 60 } }
    }
    Add-Comment $n "demo video $(if ($replaced) {'replaced'} else {'published'}): [$assetName]($($asset.browser_download_url)) on release [$tag]($($rel.html_url)) ($([Math]::Round([double]$asset.size / 1MB, 1)) MB)."
    return @{ ok = $true; id = $n; release = $tag; release_url = "$($rel.html_url)"; asset = $assetName; asset_url = "$($asset.browser_download_url)"; size = [long]$asset.size; created = $created; replaced = $replaced }
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
    # the head the validator tested (--head-sha): a PASS for another head than the current one is refused; without it (older skills) the head
    # read here is recorded, and a head that could not be read leaves the verdict without one, which the gateway never merges
    if ($passed -and (Test-Has $a 'head_sha') -and "$($a.head_sha)") {
        if (-not $headSha -or "$($a.head_sha)" -ne $headSha) { Reject 10 'head-mismatch' "the PASS was given for $("$($a.head_sha)".Substring(0, [Math]::Min(7, "$($a.head_sha)".Length))), but the head of PR #$pr is $(if ($headSha) { $headSha.Substring(0,7) } else { 'unknown' }); test the current head. Nothing was recorded." }
    }
    $s.verdict = @{ passed = $passed; pr = $pr; head_sha = $headSha; summary = $summary; at = ConvertTo-Iso (Get-UtcNow); round = $(if (Test-Has $s 'demotions') { [int]$s.demotions } else { 0 }); processed = $false }
    Add-Comment $n "validation verdict for **$($info.column)**: $(if ($passed) {'PASS'} else {'FAIL'}) - $summary"
    $res = @{ ok = $true; id = $n; recorded = $true; passed = $passed; downgraded = $downgraded; column = $info.column }
    if (-not $passed) {
        $s.verdict.processed = $true
        # escalate earlier (enhancement 10): two consecutive FAIL verdicts that cite the same acceptance criterion park the item with a question
        $repeated = @(Test-RepeatedCriteria $s 'verdict' @(Get-CitedCriteria $info $summary))
        if ($repeated.Count -gt 0) {
            Invoke-ParkRepeatedCriterion $n $st 'FAIL verdicts' $repeated
            $res.next = 'parked'; $res.reason = 'same-criterion-repeated'; $res.criteria = $repeated
            return $res
        }
        if ($pr -gt 0) {
            $prd = try { Get-Pr $pr } catch { $null }
            $cause = if ($prd -and $prd.merged) { "PR #$pr already merged as $($prd.merge_commit_sha) - residual gaps only (open a NEW branch from master): $summary" } else { $summary }
            # (a merge that a FAIL verdict judged is not picked up by the sweep as a finished change: the gaps are still to be closed)
            if ($prd -and $prd.merged) { if (-not (Test-Has $s 'pr')) { $s.pr = @{} }; $s.pr.residual = $true }
            $d = Invoke-Demote $n $info $st $cause 'verdict'
            # auto-unpark (Verbs.Sweep.ps1): the head this park judged, so the sweep can lift it once the pull request is green on a newer head
            if ($d.outcome -eq 'parked' -and $prd -and -not $prd.merged -and (Test-Has $s 'park')) { $s.park.pr = $pr; $s.park.head = $(if ($headSha) { "$headSha" } else { "$($prd.head.sha)" }); Save-State $n $st }
            $res.next = $d.outcome; $res.detail = $d
        } else {
            Set-Park $s 'verdict-failed' ($c.timing.parkHours * 60); $s.Remove('claim'); Remove-ClaimRef $n; Save-State $n $st
            Add-Comment $n 'validation FAILED with no pull request to fix; parked for human review.'
            $res.next = 'parked'
        }
        return $res
    }
    [void](Test-RepeatedCriteria $s 'verdict' @() -Clear)
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
