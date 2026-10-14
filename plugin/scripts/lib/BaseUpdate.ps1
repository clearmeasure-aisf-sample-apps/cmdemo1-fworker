# Master moved: the standing back-merge trigger (docs/design/plant-manager.md 6a). Two admin verbs the gateway calls; no model is involved.
#   base-watch          the head of the default branch, and for each owned item its pull request, how far its branch is behind, and the files the
#                       new master commits changed that the item's pull request changes too (overlap: a likely conflict)
#   base-update-branch  GitHub's "update branch" for one item's pull request, only when it is open, still at the head the trigger saw, has no
#                       conflict with master and is behind; anything else is left to the owner session

function Get-BaseHead { $c = Get-Cfg; "$((Invoke-Gh -Path "$(Get-RepoPath)/branches/$($c.defaultBranch)").commit.sha)" }

# behind_by of the default branch against a branch (per_page=1: the count is all that is read, not the commit list)
function Get-BehindBy([string]$Branch) {
    $c = Get-Cfg
    $cmp = Invoke-Gh -Path "$(Get-RepoPath)/compare/$($c.defaultBranch)...$($Branch)?per_page=1" -Allow 404, 422
    if ($cmp -is [hashtable]) { return -1 }
    return [int]$cmp.behind_by
}

function Verb_base_watch($a) {
    $c = Get-Cfg
    $known = if (Test-Has $a 'known') { "$($a.known)" } else { '' }
    $merged = @(if (Test-Has $a 'mergedPrs') { @($a.mergedPrs) | ForEach-Object { [int]$_ } })
    $head = Get-BaseHead
    $changed = $known -ne '' -and $head -ne $known
    # the files master changed since the last head the gateway saw (bounded: 300 files)
    $baseFiles = @()
    if ($changed) {
        $cmp = Invoke-Gh -Path "$(Get-RepoPath)/compare/$known...$head" -Allow 404, 422
        if ($cmp -isnot [hashtable] -and $cmp.PSObject.Properties['files']) { $baseFiles = @($cmp.files | Select-Object -First 300 | ForEach-Object { "$($_.filename)" }) }
    }
    $rows = @()
    $items = @(if (Test-Has $a 'items') { @($a.items) | ForEach-Object { [int]$_ } | Select-Object -First 50 })
    if ($items.Count -gt 0) {
        $open = @(Invoke-Gh -Path "$(Get-RepoPath)/pulls?state=open&per_page=100")
        foreach ($n in $items) {
            $row = @{ item = $n; pr = 0; branch = ''; head = ''; behind = 0; overlap = @(); skip = '' }
            try {
                $s = (Get-State $n).state
                # a PASS verdict waiting for its merge, or a merge being settled: the gateway owns the next step, not a back-merge
                if ((Test-Has $s 'verdict') -and $s.verdict.passed -and -not $s.verdict.processed) { $row.skip = 'pass-unmerged' }
                elseif ((Test-Has $s 'merge') -and (Test-Has $s.merge 'awaiting') -and $s.merge.awaiting) { $row.skip = 'merged' }
                $pr = @($open | Where-Object { $_.head.ref -match "^$([regex]::Escape($c.name))/$n-" }) | Select-Object -First 1
                if ($pr) { $row.pr = [int]$pr.number; $row.branch = "$($pr.head.ref)"; $row.head = "$($pr.head.sha)"; if ($row.pr -in $merged -and -not $row.skip) { $row.skip = 'merged' } }
                elseif ((Test-Has $s 'pr') -and (Test-Has $s.pr 'number') -and $s.pr.number) { if (-not $row.skip) { $row.skip = 'pr-closed' } }
                elseif ((Test-Has $s 'pr') -and (Test-Has $s.pr 'branch') -and $s.pr.branch) { $row.branch = "$($s.pr.branch)" }
                elseif (-not $row.skip) { $row.skip = 'no-branch' }
                if (-not $row.skip) {
                    $row.behind = Get-BehindBy $row.branch
                    if ($changed -and $row.pr -gt 0 -and $baseFiles.Count -gt 0) {
                        $mine = @(Get-PrFiles $row.pr | ForEach-Object { "$($_.filename)" })
                        $row.overlap = @($baseFiles | Where-Object { $mine -contains $_ } | Select-Object -First 20)
                    }
                }
            } catch [FactoryReject] { $row.skip = 'error'; $row.code = $_.Exception.Code }
            $rows += $row
        }
    }
    return @{ head = $head; known = $known; changed = $changed; baseFiles = $baseFiles.Count; rows = $rows }
}

function Verb_base_update_branch($a) {
    $n = [int](Need $a 'item'); $num = [int](Need $a 'pr'); $expected = "$(Need $a 'expected')"
    $pr = Get-Pr $num
    if ("$($pr.state)" -ne 'open') { return @{ item = $n; pr = $num; outcome = 'not-open' } }
    if ("$($pr.head.sha)" -ne $expected) { return @{ item = $n; pr = $num; outcome = 'head-moved' } }
    # GitHub computes mergeability after a push to either side: null until then
    for ($i = 0; $i -lt 5 -and $null -eq $pr.mergeable; $i++) { Start-Sleep -Seconds 2; $pr = Get-Pr $num }
    if ($pr.mergeable -eq $false -or "$($pr.mergeable_state)" -eq 'dirty') { return @{ item = $n; pr = $num; outcome = 'conflict' } }
    if ($null -eq $pr.mergeable) { return @{ item = $n; pr = $num; outcome = 'unknown' } }
    $behind = Get-BehindBy "$($pr.head.ref)"
    if ($behind -eq 0) { return @{ item = $n; pr = $num; outcome = 'current' } }
    $u = Invoke-Gh -Method PUT -Path "$(Get-RepoPath)/pulls/$num/update-branch" -Body @{ expected_head_sha = $expected } -Allow 409, 422
    if ($u -is [hashtable] -and $u.ContainsKey('status')) { return @{ item = $n; pr = $num; outcome = 'refused'; status = $u.status } }
    return @{ item = $n; pr = $num; outcome = 'updated'; behind = $behind }
}
