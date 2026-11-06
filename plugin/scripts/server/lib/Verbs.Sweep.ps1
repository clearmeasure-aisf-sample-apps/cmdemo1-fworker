# The sweep (reconciliation + run watchdog), event intake and the secrets check. No model is involved in any of it.

# Issue 65: the items an item waits for, from "Depends on #N" in its body (any case, a colon allowed, several numbers on the same line: "Depends
# on #291 and #292"; a list on the following lines is not read). Its own number is never a dependency.
# W0.4 (system-gateway.md H5): a reference is "#N", "repo#N" or "owner/repo#N". "#N" is the home repository's item N; "repo#N" takes the home
# owner; a qualified ref that names the home repository (any case, GitHub being case-insensitive) is that home item. Anything else is a FOREIGN
# ref: recorded and shown, blocking nothing until the system gateway exists (v1 serves the home repository only).
$script:DepRefPattern = '(?:(?<![\w.\-/])(?:(?<o>[A-Za-z0-9][A-Za-z0-9\-]*)/(?<r>[A-Za-z0-9_.\-]+)|(?<r>[A-Za-z0-9_.\-]+))|)#(?<n>\d{1,7})\b'   # the empty alternative: a bare #N after anything ("#1/#2", "#1-#2") is a home ref, as before

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
            $mine = (Test-NameIn $c.label $labels) -and (Test-ItemInScope $labels)
            if ($act -eq 'labeled' -and $mine -and ($p.label.name -ieq $c.label -or $p.label.name -ieq (Get-EnvLabel))) {
                $col = try { (Get-ItemInfo $num).column } catch { $null }
                $out += New-Event 'item.labeled' $num $col $delivery
            } elseif ($act -in 'closed', 'reopened' -and $mine) { $out += New-Event "item.$act" $num $null $delivery }
        }
        'workflow_run' {
            $run = $p.workflow_run; $act = "$($p.action)"; $branch = "$($run.head_branch)"
            $kind = if ($act -eq 'completed') { 'completed' } elseif ($act -eq 'in_progress') { 'started' } else { $null }
            if ($kind) {
                if ($branch -match "^$([regex]::Escape($c.name))/(\d+)-") {
                    $out += New-Event "build.branch_$kind" ([int]$Matches[1]) $null $delivery @{ sha = $run.head_sha; conclusion = $run.conclusion; workflow = $run.name; run_id = $(if (Test-Has $run 'id') { $run.id } else { $null }); run_attempt = $(if (Test-Has $run 'run_attempt') { $run.run_attempt } else { $null }) }
                } elseif ($branch -eq $c.defaultBranch) {
                    $out += New-Event "build.master_$kind" $null $null $delivery @{ sha = $run.head_sha; conclusion = $run.conclusion; workflow = $run.name; run_id = $(if (Test-Has $run 'id') { $run.id } else { $null }); run_attempt = $(if (Test-Has $run 'run_attempt') { $run.run_attempt } else { $null }) }
                }
            }
        }
        'projects_v2_item' {
            if ("$($p.action)" -eq 'edited' -and (Test-Has $p 'changes') -and $p.changes.field_value.field_name -eq 'Status') {
                $q = 'query($id:ID!){ node(id:$id){ ... on Issue{ number repository{ nameWithOwner } labels(first:30){ nodes{ name } } } } }'
                $d = Invoke-GhGraphQL $q @{ id = $p.projects_v2_item.content_node_id }
                # the org-level webhook carries no repository: an issue of another repository linked to the board (or a draft or pull request, or
                # no node) makes no event
                $mine1 = $d.node -and $d.node.PSObject.Properties['repository'] -and $d.node.repository -and ("$($d.node.repository.nameWithOwner)" -ieq "$($c.owner)/$($c.repo)")
                if ($mine1) { $lb = @($d.node.labels.nodes | ForEach-Object { $_.name }) }
                if ($mine1 -and (Test-NameIn $c.label $lb) -and (Test-ItemInScope $lb)) {
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

