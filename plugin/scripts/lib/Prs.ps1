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
# -Ignore: check names left out of the verdict (status, failed, pending); they stay readable in `runs`. Master's verdict ignores the checks of the
# workflows that a finished Build starts (masterIgnoredChecks: Release, Architecture): GitHub attaches those to master's head at the time they
# start, not to the commit that was built, so they say nothing about that commit and a skipped or failed one must not decide its fate.
function Get-ChecksFor([string]$Sha, [string[]]$Ignore = @()) {
    $runs = @((Invoke-Gh -Path "$(Get-RepoPath)/commits/$Sha/check-runs?per_page=100").check_runs)
    $latest = @{}
    foreach ($r in $runs) {
        $k = $r.name
        if (-not $latest.ContainsKey($k) -or ([DateTime]$r.started_at) -gt ([DateTime]$latest[$k].started_at)) { $latest[$k] = $r }
    }
    $failed = @(); $pending = @(); $ok = @()
    $judged = @($latest.Keys | Where-Object { -not (Test-NameIn "$_" $Ignore) })
    foreach ($k in $judged) {
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
        if (Test-NameIn $g $Ignore) { continue }   # an ignored check is no gate either
        if (-not $latest.ContainsKey($g)) { $gates[$g] = 'absent' }
        elseif ($failed -contains $g) { $gates[$g] = 'failure' }
        elseif ($pending -contains $g) { $gates[$g] = 'pending' }
        else { $gates[$g] = 'success' }
    }
    $status = 'success'
    if ($failed.Count -gt 0) { $status = 'failure' }
    elseif ($judged.Count -eq 0 -or $pending.Count -gt 0 -or ($gates.Values -contains 'absent') -or ($gates.Values -contains 'pending')) { $status = 'pending' }
    return @{ status = $status; total = $judged.Count; failed = $failed.Count; failed_checks = $failed; pending_checks = $pending; gates = $gates; runs = $latest }
}

# The checks a master commit is judged without (see Get-ChecksFor -Ignore).
function Get-MasterIgnoredChecks { $c = Get-Cfg; if (Test-Has $c 'masterIgnoredChecks') { @($c.masterIgnoredChecks | ForEach-Object { "$_" }) } else { @('release', 'packages', 'architecture') } }

function Get-PublicChecks($c) {
    @{ status = $c.status; total = $c.total; failed = $c.failed; failed_checks = $c.failed_checks; pending_checks = $c.pending_checks; gates = $c.gates }
}

# Docs-only is what the design step recorded in the item state (append-body in the design column); marker text in the body alone proves nothing,
# because anyone who can edit the issue can type it.
# The design step always writes the field, true or false (append-body, and complete of the design column when no design append set it), and once it
# exists the body text is ignored. Only a state with NO field on an item that is already past its design column AND was created before
# timing.docsOnlyLegacyBefore (the items an older version designed) falls back to the marker in the body; pass $Info to allow that fallback.
# A newer item without the field is not docs-only, whatever its body says.
function Get-LegacyCutoff { $t = (Get-Cfg).timing; if (Test-Has $t 'docsOnlyLegacyBefore') { return ConvertFrom-Iso $t.docsOnlyLegacyBefore }; return $null }
function Test-ItemDocsOnly($State, $Info = $null) {
    if ((Test-Has $State 'design') -and (Test-Has $State.design 'docs_only')) { return [bool]$State.design.docs_only }
    $c = Get-Cfg
    if ($null -eq $Info -or (Test-Has $c.requiredBodySection "$($Info.column)")) { return $false }
    $cut = Get-LegacyCutoff; $made = if (Test-Has $Info 'createdAt') { try { ConvertFrom-Iso $Info.createdAt } catch { $null } } else { $null }
    if ($null -eq $cut -or $null -eq $made -or $made -ge $cut) { return $false }
    return "$($Info.body)".Contains($c.docsOnlyMarker)
}

# Docs-only fast path (plant-manager.md section 10a). The private build is NOT owed for a pull request only when BOTH facts hold: the item body carries
# the docs-only marker (written by its technical design) and every changed file matches docsOnlyPaths. Anything else owes the build: an item without
# the marker, a marked item whose pull request touches a file that is not documentation, or a pull request whose files could not be read.
# verification says how Functional Testing checks the change: 'diff' (read the diff against the criteria, no build, no application) under the same
# two facts and no video label; otherwise 'app'.
function Get-BuildOwed($Info, [int]$PrNumber, $State) {
    $c = Get-Cfg; $marker = Test-ItemDocsOnly $State $Info
    $files = $null; try { $files = @(Get-PrFiles $PrNumber) } catch { }
    if ($null -eq $files) { return @{ build_owed = $true; docs_only_files = $false; verification = 'app'; build_reason = 'the files of the pull request could not be read; the private build is owed' } }
    $docs = Test-DocsOnlyFiles $files
    $other = @($files | Where-Object { -not (Test-DocsOnlyFiles @($_)) } | ForEach-Object { "$($_.filename)" } | Select-Object -First 5)
    if ($marker -and $docs) {
        $video = Test-Label $Info $c.videoLabel
        return @{ build_owed = $false; docs_only_files = $true; verification = $(if ($video) { 'app' } else { 'diff' })
            build_reason = "every changed file is documentation and the technical design recorded the item as docs-only: no private build and no SDK are owed; the checks of the pull request are the gate" }
    }
    if ($marker) { return @{ build_owed = $true; docs_only_files = $false; verification = 'app'; not_documentation = $other
        build_reason = "the technical design recorded the item as docs-only but the pull request changes files that are not documentation ($($other -join ', ')): the private build is owed before complete" } }
    return @{ build_owed = $true; docs_only_files = $docs; verification = 'app'; build_reason = "the technical design did not record the item as docs-only: the private build is owed" }
}
