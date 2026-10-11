# Delivery: Octopus reads (GET only), release-candidate match, and settle (the post-merge state machine, no model needed).

function Invoke-Octo([string]$Path) {
    if (-not $env:OCTOPUS_API_KEY) { Reject 30 'secret-missing' 'OCTOPUS_API_KEY is not set on the gateway' }
    $o = (Get-Cfg).octopus
    $uri = "$($o.url.TrimEnd('/'))/$($Path.TrimStart('/'))"
    try { Invoke-RestMethod -Uri $uri -Headers @{ 'X-Octopus-ApiKey' = $env:OCTOPUS_API_KEY; 'User-Agent' = 'factory-worker-gateway' } -TimeoutSec 30 }
    catch {
        $code = try { [int]$_.Exception.Response.StatusCode } catch { 0 }
        if ($code -eq 404) { return $null }
        Reject 20 'octopus-error' "Octopus answered $code to GET $Path" @{ retryAfterSeconds = 30 }
    }
}

function Get-ReleaseCommit($Release) {
    $text = "$($Release.ReleaseNotes)"
    $bi = $Release.PSObject.Properties['BuildInformation']
    if ($bi -and $bi.Value) { foreach ($b in $bi.Value) { if ("$($b.VcsCommitNumber)" -match '^[0-9a-fA-F]{40}$') { return $b.VcsCommitNumber.ToLower() } } }
    $m = [regex]::Match($text, '\b[0-9a-fA-F]{40}\b'); if ($m.Success) { return $m.Value.ToLower() }
    return $null
}

function Get-Delivery([string]$Sha) {
    $o = (Get-Cfg).octopus; $sp = $o.space
    $proj = Invoke-Octo "api/$sp/projects/$($o.project)"
    if (-not $proj) { Reject 12 'octopus-project-not-found' "project '$($o.project)' not found in $sp" }
    $rels = @((Invoke-Octo "api/$sp/projects/$($proj.Id)/releases?take=30").Items)
    $cand = $null
    foreach ($r in $rels) { if ((Get-ReleaseCommit $r) -eq $Sha.ToLower()) { $cand = $r; break } }
    if (-not $cand -and $rels.Count -gt 0) {
        $newest = $rels | Sort-Object { [DateTime]$_.Assembled } -Descending | Select-Object -First 1
        $nc = Get-ReleaseCommit $newest
        if ($nc) {
            $cmp = Invoke-Gh -Path "$(Get-RepoPath)/compare/$($Sha)...$nc" -Allow 404, 422
            if ($cmp -isnot [hashtable] -and $cmp.status -in 'ahead', 'identical') { $cand = $newest }
        }
    }
    $res = @{ candidate = $null; deployments = @{}; prod_success = $false }
    if (-not $cand) { return $res }
    $res.candidate = @{ version = $cand.Version; release_id = $cand.Id; commit = (Get-ReleaseCommit $cand); assembled = $cand.Assembled }
    $envs = @{}; foreach ($e in (Invoke-Octo "api/$sp/environments?take=1000").Items) { $envs[$e.Id] = $e.Name }
    $deps = @((Invoke-Octo "api/$sp/deployments?projects=$($proj.Id)&take=30").Items | Where-Object { $_.ReleaseId -eq $cand.Id } | Sort-Object { [DateTime]$_.Created } -Descending)
    foreach ($d in $deps) {
        $name = $envs[$d.EnvironmentId]; if (-not $name -or $res.deployments.ContainsKey($name)) { continue }
        $task = Invoke-Octo "api/$sp/tasks/$($d.TaskId)"
        $res.deployments[$name] = @{ state = "$($task.State)"; completed = [bool]$task.IsCompleted; at = $d.Created; deployment = $d.Id }
    }
    $prodName = $o.environments.prod
    foreach ($k in $res.deployments.Keys) { if ($k -ieq $prodName -and $res.deployments[$k].state -eq 'Success') { $res.prod_success = $true } }
    return $res
}

# The newest releases to prod that contain the change (releases are cumulative on master): used when the item's own release has
# scrolled out of the recent list or was never promoted itself because a later release carried it to prod.
function Get-ProdContaining([string]$Sha) {
    $o = (Get-Cfg).octopus; $sp = $o.space
    $proj = Invoke-Octo "api/$sp/projects/$($o.project)"; if (-not $proj) { return $null }
    $envs = @{}; foreach ($e in (Invoke-Octo "api/$sp/environments?take=1000").Items) { $envs[$e.Id] = $e.Name }
    $prodName = $o.environments.prod
    foreach ($d in @((Invoke-Octo "api/$sp/deployments?projects=$($proj.Id)&take=30").Items)) {
        if (-not $envs[$d.EnvironmentId] -or $envs[$d.EnvironmentId] -ine $prodName) { continue }
        $task = Invoke-Octo "api/$sp/tasks/$($d.TaskId)"; if (-not $task -or "$($task.State)" -ne 'Success') { continue }
        $rel = Invoke-Octo "api/$sp/releases/$($d.ReleaseId)"; if (-not $rel) { continue }
        $c = Get-ReleaseCommit $rel; if (-not $c) { continue }
        if ($c -eq $Sha.ToLower()) { return "$($rel.Version)" }
        $cmp = Invoke-Gh -Path "$(Get-RepoPath)/compare/$($Sha)...$c" -Allow 404, 422
        if ($cmp -isnot [hashtable] -and $cmp.status -in 'ahead', 'identical') { return "$($rel.Version)" }
    }
    return $null
}

function Verb_get_delivery($a) {
    $sha = if (Test-Has $a 'commit') { "$($a.commit)" } else {
        $st = Get-State (Get-ItemNumber $a)
        if (-not (Test-Has $st.state 'merge')) { Reject 10 'not-merged' 'the item has no merge commit yet' }
        $st.state.merge.sha }
    $d = Get-Delivery $sha; $d.ok = $true; $d.commit = $sha
    return $d
}

# One idempotent step of the post-merge flow for one item: master green, release candidate, docs-only skip, Release Queue, Done.
function Invoke-Settle([int]$n) {
    $c = Get-Cfg; $t = $c.timing
    $info = Get-ItemInfo $n; $st = Get-State $n; $s = $st.state
    if (-not (Test-Has $s 'merge') -or -not (Test-Has $s.merge 'awaiting') -or -not $s.merge.awaiting) { return @{ outcome = 'nothing-to-settle' } }
    # A merged pull request that says "Closes #N" closes the item itself: that is not the end of the flow. Only an item closed without a merge stops here.
    if (-not $info.open -and -not (Test-Has $s.merge 'sha')) { $s.merge.awaiting = $null; Save-State $n $st; return @{ outcome = 'closed' } }
    $m = $s.merge; $sha = $m.sha; $now = Get-UtcNow
    $finish = {
        param($text)
        Add-Comment $n $text
        Move-Card $info (Get-LastColumn); Close-Issue $n
        $m.awaiting = $null; $m.done_at = ConvertTo-Iso (Get-UtcNow); Save-State $n $st
    }
    if ($m.awaiting -eq 'master-green') {
        $chk = Get-ChecksFor $sha
        $mergedAt = ConvertFrom-Iso $m.merged_at
        if ($chk.status -eq 'failure') {
            if (-not (Test-Has $m 'rerun_attempted') -or -not $m.rerun_attempted) {
                $m.rerun_attempted = $true
                $runs = @((Invoke-Gh -Path "$(Get-RepoPath)/actions/runs?head_sha=$sha&per_page=50").workflow_runs | Where-Object { $_.conclusion -eq 'failure' })
                foreach ($r in $runs) { Invoke-Gh -Method POST -Path "$(Get-RepoPath)/actions/runs/$($r.id)/rerun-failed-jobs" -Allow 403, 409, 422 | Out-Null }
                Add-Comment $n "master checks failed at ``$($sha.Substring(0,7))`` ($($chk.failed_checks -join ', ')) - re-running failed jobs once, then re-polling."
                Save-State $n $st; return @{ outcome = 'rerun'; failed = $chk.failed_checks }
            }
            if ($chk.pending_checks.Count -eq 0) {
                Set-Park $s 'master-broken' ($t.parkHours * 60); $m.awaiting = $null; Save-State $n $st
                Add-Comment $n "**master is BROKEN** at ``$($sha.Substring(0,7))`` after the merge ($($chk.failed_checks -join ', ')) - parked for a human. Auto-revert is out of scope."
                return @{ outcome = 'parked'; reason = 'master-broken' }
            }
        }
        if ($chk.status -eq 'success') {
            $m.green_at = ConvertTo-Iso $now
            $m.awaiting = 'release-candidate'; $m.rc_deadline = ConvertTo-Iso ($now.AddMinutes($t.releaseCandidateTimeoutMinutes))
            Add-Comment $n "master is GREEN at ``$($sha.Substring(0,7))`` - waiting for the release candidate that carries it."
            Save-State $n $st
            return @{ outcome = 'advanced'; awaiting = 'release-candidate' }
        }
        if ($mergedAt -and ($now - $mergedAt).TotalMinutes -gt $t.masterGreenTimeoutMinutes) {
            Set-Park $s 'master-green-timeout' ($t.parkHours * 60); $m.awaiting = $null; Save-State $n $st
            Add-Comment $n "master CI did not go green within $($t.masterGreenTimeoutMinutes) min of the merge - parked for a human."
            return @{ outcome = 'parked'; reason = 'master-green-timeout' }
        }
        return @{ outcome = 'waiting'; awaiting = 'master-green'; checks = (Get-PublicChecks $chk) }
    }
    if ($m.awaiting -in 'release-candidate', 'late-rc') {
        $chk = Get-ChecksFor $sha
        $rel = $chk.runs[$c.releaseCheckName]
        if ($rel -and $rel.status -eq 'completed' -and $rel.conclusion -eq 'skipped') {
            & $finish "the master build published no release candidate for ``$($sha.Substring(0,7))`` - its publish step was skipped because no code changed. A docs-only change has nothing to deploy, so it is moved straight to **$(Get-LastColumn)**."
            return @{ outcome = 'done'; path = 'docs-only' }
        }
        # A docs-only change builds no release. If the Release workflow never produced a 'release' check (for instance its run was dropped by the
        # workflow's concurrency group), do not wait for one: finish once master has been green for the grace period.
        if (-not (Test-Has $m 'docs_only') -and (Test-Has $m 'pr')) { try { $m.docs_only = Test-DocsOnlyFiles (Get-PrFiles ([int]$m.pr)) } catch { } }
        $since = if (Test-Has $m 'green_at') { ConvertFrom-Iso $m.green_at } else { ConvertFrom-Iso $m.merged_at }
        if ((Test-Has $m 'docs_only') -and $m.docs_only -and -not $rel -and $since -and ($now - $since).TotalSeconds -ge [double]$t.docsOnlyReleaseGraceSeconds) {
            & $finish "the change is documentation only and no release was built for ``$($sha.Substring(0,7))`` ($($t.docsOnlyReleaseGraceSeconds) s after master went green). A docs-only change has nothing to deploy, so it is moved straight to **$(Get-LastColumn)**."
            return @{ outcome = 'done'; path = 'docs-only-no-release' }
        }
        $d = Get-Delivery $sha
        if ($d.candidate) {
            $next = Get-NextColumn $info.column
            if ($next -and $next -ine (Get-LastColumn)) { Move-Card $info $next }
            $m.awaiting = 'release-queue'; $m.release = $d.candidate.version
            Add-Comment $n "release candidate **$($d.candidate.version)** carries ``$($sha.Substring(0,7))`` - waiting in **$next** for the production deployment."
            Save-State $n $st
            return @{ outcome = 'advanced'; awaiting = 'release-queue'; release = $d.candidate.version }
        }
        $deadline = ConvertFrom-Iso $m.rc_deadline
        if ($m.awaiting -eq 'release-candidate' -and $deadline -and $now -gt $deadline) {
            $m.awaiting = 'late-rc'; $m.late_deadline = ConvertTo-Iso ($now.AddHours($t.lateReleaseCandidateHours))
            Set-Park $s 'fv:release-candidate-timeout' ($t.lateReleaseCandidateHours * 60)
            Add-Comment $n "master went green but no release candidate carrying it was published within $($t.releaseCandidateTimeoutMinutes) min - parked for a human. The worker keeps looking for $($t.lateReleaseCandidateHours) h."
            Save-State $n $st
            return @{ outcome = 'parked'; reason = 'release-candidate-timeout'; still_looking = $true }
        }
        if ($m.awaiting -eq 'late-rc' -and (ConvertFrom-Iso $m.late_deadline) -lt $now) { $m.awaiting = $null; Save-State $n $st; return @{ outcome = 'gave-up' } }
        return @{ outcome = 'waiting'; awaiting = $m.awaiting }
    }
    if ($m.awaiting -eq 'release-queue') {
        $d = Get-Delivery $sha
        if ($d.prod_success) {
            & $finish "Delivered to **Prod** - moved to **$(Get-LastColumn)**. Release $($m.release)."
            return @{ outcome = 'done'; path = 'prod' }
        }
        $later = Get-ProdContaining $sha
        if ($later) {
            & $finish "Delivered to **Prod** in release $later, which contains this change - moved to **$(Get-LastColumn)**."
            return @{ outcome = 'done'; path = 'prod-later-release'; release = $later }
        }
        return @{ outcome = 'waiting'; awaiting = 'release-queue'; deployments = $d.deployments }
    }
    return @{ outcome = 'unknown-state'; awaiting = $m.awaiting }
}

function Verb_settle($a) {
    $n = Get-ItemNumber $a
    $r = Invoke-Settle $n; $r.ok = $true; $r.id = $n
    return $r
}
