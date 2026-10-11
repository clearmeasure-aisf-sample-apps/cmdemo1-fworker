# Pull requests and CI: lookup, ownership, check classification, docs-only test.

function Find-ItemPr([int]$Item) {
    $name = (Get-Cfg).name
    $open = @(Invoke-Gh -Path "$(Get-RepoPath)/pulls?state=open&per_page=100")
    $mine = @($open | Where-Object { $_.head.ref -match "^$([regex]::Escape($name))/$Item-" })
    if ($mine.Count -eq 0) { $mine = @($open | Where-Object { $_.head.ref -match "/$Item-" }) }
    if ($mine.Count -eq 0) { return $null }
    return $mine[0]
}

function Get-Pr([int]$Number) { Invoke-Gh -Path "$(Get-RepoPath)/pulls/$Number" }

# Word-boundary match: '#12' does not match '#123'.
function Test-PrBelongs($Pr, [int]$Item) {
    if ($Pr.head.ref -match "/$Item-") { return $true }
    return ($Pr.title -match "(^|[^0-9])#$Item([^0-9]|$)")
}

function Get-PrFiles([int]$Number) {
    $all = @(); $page = 1
    do { $chunk = @(Invoke-Gh -Path "$(Get-RepoPath)/pulls/$Number/files?per_page=100&page=$page"); $all += $chunk; $page++ } while ($chunk.Count -eq 100)
    return $all
}

function Test-DocsOnlyFiles($Files) {
    $pats = (Get-Cfg).docsOnlyPaths
    if (@($Files).Count -eq 0) { return $false }
    foreach ($f in $Files) {
        $ok = $false
        foreach ($p in $pats) { if ($f.filename -match $p) { $ok = $true; break } }
        if (-not $ok) { return $false }
    }
    return $true
}

# Strict classification. Divergence from the live factory, on purpose: a head with zero check runs is 'pending', never green,
# and every configured gate must be present and successful. Any failed run on the head is a failure.
function Get-ChecksFor([string]$Sha) {
    $runs = @((Invoke-Gh -Path "$(Get-RepoPath)/commits/$Sha/check-runs?per_page=100").check_runs)
    $latest = @{}
    foreach ($r in $runs) {
        $k = $r.name
        if (-not $latest.ContainsKey($k) -or ([DateTime]$r.started_at) -gt ([DateTime]$latest[$k].started_at)) { $latest[$k] = $r }
    }
    $failed = @(); $pending = @(); $ok = @()
    foreach ($k in $latest.Keys) {
        $r = $latest[$k]
        if ($r.status -ne 'completed') { $pending += $k; continue }
        switch ($r.conclusion) {
            { $_ -in 'failure', 'timed_out', 'action_required', 'startup_failure' } { $failed += $k }
            'cancelled' { $pending += $k }
            default { $ok += $k }
        }
    }
    $gates = [ordered]@{}
    foreach ($g in (Get-Cfg).ciGates) {
        if (-not $latest.ContainsKey($g)) { $gates[$g] = 'absent' }
        elseif ($failed -contains $g) { $gates[$g] = 'failure' }
        elseif ($pending -contains $g) { $gates[$g] = 'pending' }
        else { $gates[$g] = 'success' }
    }
    $status = 'success'
    if ($failed.Count -gt 0) { $status = 'failure' }
    elseif ($latest.Count -eq 0 -or $pending.Count -gt 0 -or ($gates.Values -contains 'absent') -or ($gates.Values -contains 'pending')) { $status = 'pending' }
    return @{ status = $status; total = $latest.Count; failed = $failed.Count; failed_checks = $failed; pending_checks = $pending; gates = $gates; runs = $latest }
}

function Get-PublicChecks($c) {
    @{ status = $c.status; total = $c.total; failed = $c.failed; failed_checks = $c.failed_checks; pending_checks = $c.pending_checks; gates = $c.gates }
}
