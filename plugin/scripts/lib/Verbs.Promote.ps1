# The automatic promotion (owner decision 2026-10-23, docs/design/auto-promote.md). The acting gateway promotes a release of octopus.project
# from tdd to uat, then to prod, by itself, when the release qualifies ("gated by checks"): every item it carries (the items of the Release Queue
# whose recorded release it is) has the Auto Merge label and no Hold label, was merged by the gateway after a Functional Testing PASS verdict
# and, for a video item, has its demo MP4 published (release demo-N); the release's deployment to the previous environment succeeded; and that
# environment's app answers healthy. Otherwise the release waits and each item is told why, once per reason. A deployment is created only when
# Octopus lists none of that release to that environment (read just before), so a restart or a second sweep never promotes twice. The Sign-off
# step (a manual intervention) of a deployment the gateway created is answered by the gateway: Proceed when the gates still hold, Abort when not.

# the key that may create deployments and answer interruptions: the runbook service account's (OCTOPUS_RUNBOOK_API_KEY), else the reader key
function Invoke-OctoPost([string]$Path, $Body, [string]$Method = 'POST') {
    $key = if ($env:OCTOPUS_RUNBOOK_API_KEY) { $env:OCTOPUS_RUNBOOK_API_KEY } else { $env:OCTOPUS_API_KEY }
    if (-not $key) { Reject 30 'secret-missing' 'neither OCTOPUS_RUNBOOK_API_KEY nor OCTOPUS_API_KEY is set on the gateway' }
    $o = (Get-Cfg).octopus
    $uri = "$($o.url.TrimEnd('/'))/$($Path.TrimStart('/'))"
    $json = if ($null -eq $Body) { '{}' } else { $Body | ConvertTo-Json -Depth 6 -Compress }
    $h = @{ 'X-Octopus-ApiKey' = $key; 'User-Agent' = 'factory-worker-gateway' }
    $cause = Get-FactoryCause; if ($cause) { $h['X-Fake-Cause'] = $cause }   # (test-only: the golden writes' cause id, lib/Core.ps1 Get-FactoryCause)
    try { $res = Invoke-WebRequest -Uri $uri -Method $Method -Headers $h -ContentType 'application/json' -Body $json -SkipHttpErrorCheck -TimeoutSec 30 }
    catch { return @{ status = 0; body = $null } }
    $b = try { "$($res.Content)" | ConvertFrom-Json } catch { $null }
    return @{ status = [int]$res.StatusCode; body = $b }
}

# the permission Octopus names in a refusal ("Missing permission: X"), never any other text of the answer
function Get-OctoMissingPermission($Body) {
    $msg = if ($Body -and $Body.PSObject.Properties['ErrorMessage']) { "$($Body.ErrorMessage)" } else { '' }
    $m = [regex]::Match($msg,'Missing permission: ([A-Za-z]{1,60})')
    if ($m.Success) { return $m.Groups[1].Value }
    return ''
}

# the deployments of one release by version: @{ release_id; deployments = @{ <environment name, lower case> = @{ state; completed; deployment;
# task } } } (the newest per environment), or $null when Octopus has no such release
function Get-ReleaseDeployments([string]$Version) {
    $o = (Get-Cfg).octopus; $sp = $o.space
    $proj = Get-PromoOcto "api/$sp/projects/$($o.project)"; if (-not $proj) { return $null }
    $rel = Invoke-Octo "api/$sp/projects/$($proj.Id)/releases/$Version"; if (-not $rel) { return $null }
    $envs = @{}; foreach ($e in (Get-PromoOcto "api/$sp/environments?take=1000").Items) { $envs[$e.Id] = $e.Name }
    $res = @{ release_id = "$($rel.Id)"; commit = (Get-ReleaseCommit $rel); deployments = @{} }
    $dl = Invoke-Octo "api/$sp/releases/$($rel.Id)/deployments?take=100"
    $dItems = if ($dl -and $dl.PSObject.Properties['Items']) { @($dl.Items) } else { @() }
    foreach ($d in @($dItems | Where-Object { $_ } | Sort-Object { [DateTime]$_.Created } -Descending)) {
        $name = "$($envs[$d.EnvironmentId])".ToLower(); if (-not $name -or $res.deployments.ContainsKey($name)) { continue }
        $task = Invoke-Octo "api/$sp/tasks/$($d.TaskId)"; if (-not $task) { continue }   # (gone since the list was read)
        $res.deployments[$name] = @{ state = "$($task.State)"; completed = [bool]$task.IsCompleted; deployment = "$($d.Id)"; task = "$($d.TaskId)"
            completed_at = $(if ($task.PSObject.Properties['CompletedTime'] -and $task.CompletedTime) { "$($task.CompletedTime)" } else { $null }) }
    }
    return $res
}

function Get-HealthSetting([string]$Name, [double]$Default) {
    $o = (Get-Cfg).octopus
    if ((Test-Has $o 'health') -and (Test-Has $o.health $Name)) { return [double]$o.health[$Name] }
    return $Default
}
# one read of the health endpoint: 'Healthy', another overallStatus, or 'unreachable' after octopus.health.retries tries retrySeconds apart
function Read-EnvHealth([string]$Uri) {
    $tries = [Math]::Max(1, [int](Get-HealthSetting 'retries' 3)); $gap = Get-HealthSetting 'retrySeconds' 20
    for ($t = 1; $t -le $tries; $t++) {
        try {
            $r = Invoke-WebRequest -Uri $Uri -SkipHttpErrorCheck -TimeoutSec 15
            $j = try { "$($r.Content)" | ConvertFrom-Json } catch { $null }
            if ($j -and $j.PSObject.Properties['overallStatus']) { return "$($j.overallStatus)" }
            return "status $([int]$r.StatusCode) without overallStatus"
        } catch { if ($t -lt $tries) { Start-Sleep -Milliseconds ([int]($gap * 1000 + (Get-Random -Maximum ([int]([Math]::Max(1, $gap * 500)))))) } }
    }
    return 'unreachable'
}

# The health gate of an environment, the rule of cmdemo1-system's ui-auto-promote: the app (octopus.<env>Url) runs the release (GET /_build:
# version), and its octopus.healthPath answers overallStatus Healthy; no non-Healthy read since the release's deployment there finished; at least
# two Healthy reads octopus.health.spacingMinutes (5) apart, the later settleMinutes (10) after that deployment finished. One read per call; the
# reads are kept in the state directory per release and environment (lost on a restart: collecting starts again). An empty URL skips the gate.
# one read now of an environment's app, for the Sign-off of a deployment already created: '' when Healthy, else the reason (the version match
# and the reads window decided the creation; they are not asked again, so a newer release there or a restart of the gateway does not abort it)
function Test-EnvHealthNow([string]$EnvKey) {
    $o = (Get-Cfg).octopus; $prop = "$($EnvKey)Url"
    $url = if (Test-Has $o $prop) { "$($o.$prop)".TrimEnd('/') } else { '' }
    if (-not $url) { return "no health URL for $EnvKey (octopus.$prop is empty)" }
    $path = if ((Test-Has $o 'healthPath') -and "$($o.healthPath)") { "$($o.healthPath)" } else { '/_healthcheck/detailed' }
    $st = Read-EnvHealth "$url$path"
    if ($st -eq 'Healthy') { return '' }
    return "the $EnvKey app answered $st on $path"
}

function Test-EnvHealth([string]$EnvKey, [string]$Version, $FinishedAt, [string]$ReleaseId = "", [int]$StoreItem = 0) {
    $o = (Get-Cfg).octopus; $prop = "$($EnvKey)Url"
    $url = if (Test-Has $o $prop) { "$($o.$prop)".TrimEnd('/') } else { '' }
    if (-not $url) { return @{ gap = "no health URL for $EnvKey (octopus.$prop is empty)"; note = '' } }
    $path = if ((Test-Has $o 'healthPath') -and "$($o.healthPath)") { "$($o.healthPath)" } else { '/_healthcheck/detailed' }
    $running = try { $b = Invoke-RestMethod -Uri "$url/_build" -TimeoutSec 15; if ($b -and $b.PSObject.Properties['version']) { "$($b.version)" } else { '' } } catch { '' }
    if ($running -ne $Version) { return @{ gap = "the $EnvKey app does not run release $Version yet (/_build: $(if ($running) { $running } else { 'no version' }))"; note = '' } }
    $fin = ConvertFrom-Iso $FinishedAt; if (-not $fin) { $fin = Get-UtcNow }
    # the reads are kept in the state comment of the release's first item (promote_health.<env>|<release id>): a gateway restart keeps them
    $hkey = "$EnvKey|$ReleaseId"
    $reads = @()
    try { $s0 = (Get-State $StoreItem).state; if ((Test-Has $s0 'promote_health') -and (Test-Has $s0.promote_health $hkey)) { $reads = @($s0.promote_health[$hkey]) } } catch { $reads = @() }
    $reads = @($reads | Where-Object { $_ -and (ConvertFrom-Iso $_.at) -ge $fin })
    $now = Get-UtcNow
    $reads += @{ at = ConvertTo-Iso $now; status = (Read-EnvHealth "$url$path") }
    $keep = @($reads | Select-Object -Last 30)
    try {
        [void](Use-ItemLock $StoreItem {
            $f = Get-State $StoreItem
            if (-not (Test-Has $f.state 'promote_health')) { $f.state.promote_health = @{} }
            foreach ($k in @($f.state.promote_health.Keys)) { if ($k -ne $hkey -and "$k" -like "$EnvKey|*") { $f.state.promote_health.Remove($k) } }
            $f.state.promote_health[$hkey] = $keep; Save-State $StoreItem $f } 10)
    }
    catch { [Console]::Error.WriteLine("promote: the health reads of $EnvKey could not be saved on #$($StoreItem): $($_.Exception.Message)"); return @{ gap = "the health reads of $EnvKey could not be saved on #$StoreItem (see the gateway log)"; note = '' } }
    $bad = @($reads | Where-Object { "$($_.status)" -ne 'Healthy' }) | Select-Object -First 1
    if ($bad) { return @{ gap = "the $EnvKey app answered $($bad.status) on $path at $($bad.at), after its deployment of $Version finished: the release waits for a new deployment there"; note = '' } }
    $spacing = Get-HealthSetting 'spacingMinutes' 5; $settle = Get-HealthSetting 'settleMinutes' 10
    $times = @($reads | ForEach-Object { ConvertFrom-Iso $_.at })
    $late = @($times | Where-Object { ($_ - $fin).TotalMinutes -ge $settle })
    $ok = @($late | Where-Object { $l = $_; @($times | Where-Object { ($l - $_).TotalMinutes -ge $spacing }).Count -gt 0 }).Count -gt 0
    if (-not $ok) { return @{ gap = "the $EnvKey app has not been Healthy long enough yet (two Healthy reads $spacing min apart, the later $settle min after the deployment finished)"; note = '' } }
    return @{ gap = ''; note = '' }
}

# the commit running in an environment: that of the release of the newest successful deployment of octopus.project there; $null when unknown
function Get-EnvDeployedCommit([string]$EnvName) {
    $o = (Get-Cfg).octopus; $sp = $o.space
    $proj = Get-PromoOcto "api/$sp/projects/$($o.project)"; if (-not $proj) { return $null }
    $eid = $null; foreach ($e in (Get-PromoOcto "api/$sp/environments?take=1000").Items) { if ("$($e.Name)" -ieq $EnvName) { $eid = "$($e.Id)" } }
    if (-not $eid) { return $null }
    $dl = Invoke-Octo "api/$sp/deployments?projects=$($proj.Id)&environments=$eid&take=30"
    $ds = if ($dl -and $dl.PSObject.Properties['Items']) { @($dl.Items) } else { @() }
    foreach ($d in @($ds | Where-Object { $_ -and "$($_.EnvironmentId)" -eq $eid } | Sort-Object { [DateTime]$_.Created } -Descending)) {
        $task = Invoke-Octo "api/$sp/tasks/$($d.TaskId)"; if (-not $task -or "$($task.State)" -ne 'Success') { continue }
        $rel = Invoke-Octo "api/$sp/releases/$($d.ReleaseId)"; if (-not $rel) { return $null }
        $cm = Get-ReleaseCommit $rel
        return $(if ($cm) { @{ version = "$($rel.Version)"; commit = $cm } } else { $null })
    }
    return $null
}

# What the release carries beyond what runs in the target environment: every master commit in (deployed, release]. Returns @{ skip; gaps; items }:
# skip when the release is not newer than what runs there; gaps (fail closed) when the range cannot be read or a commit is not the merge commit of
# a queued item; items: the queued items whose merge commits are in the range. $BySha: merge sha -> item of the Release Queue.
function Get-ReleaseContent($Rd, [string]$TargetName, $BySha) {
    if ($null -eq $script:PromoStore -or -not $Rd.commit) { return (Get-ReleaseContentRead $Rd $TargetName $BySha) }
    # The judgment of a release's content is kept (promote-judgments.json), keyed by the release, the target and the commit range (the commit running in
    # the target, the release's commit). It holds while every item it names has the state comment it had (their stamps) and, for ten minutes at most,
    # so a pass in which nothing changed reads neither the range nor the commits' items again. A range with a commit that is no work item's (hard)
    # is judged again when the state of any other item changed: that commit may have become an item's merge.
    $td = Get-Date
    $dep = if ($script:PromoMemo.envCommit.ContainsKey($TargetName)) { $script:PromoMemo.envCommit[$TargetName] } else { Get-EnvDeployedCommit $TargetName }
    $script:PromoMemo.envCommit[$TargetName] = $dep
    Add-PromoTime 'env-deployed-commit' $td
    if (-not $dep) { return (Get-ReleaseContentRead $Rd $TargetName $BySha) }
    $ck = "$($Rd.release_id)|$TargetName|$($dep.commit)|$($Rd.commit)"
    $e = $script:PromoStore.content[$ck]
    if ($e -and ((Get-UtcNow) - (ConvertFrom-Iso $e.at)).TotalMinutes -lt 10) {
        $ok = $true
        foreach ($k in @($e.stamps.Keys)) { $se = $script:PromoStore.states["$k"]; if (-not $se -or $se.stamp -ne $e.stamps[$k]) { $ok = $false; break } }
        if ($ok -and $e.open) { foreach ($k in @($script:PromoChanged.Keys)) { if (-not $e.stamps.ContainsKey("$k")) { $ok = $false; break } } }
        if ($ok) { $script:PromoMemo.hits++; return $e.content }
    }
    $r = Get-ReleaseContentRead $Rd $TargetName $BySha
    if ((Test-Has $r 'deferred') -and $r.deferred) { return $r }
    $stamps = @{}; $all = $true
    foreach ($n in @($r.items)) { [void](Get-PromoState ([int]$n)); $se = $script:PromoStore.states["$n"]; if ($se -and $se.stamp) { $stamps["$n"] = $se.stamp } else { $all = $false } }
    if ($all) { $script:PromoStore.content[$ck] = @{ at = (ConvertTo-Iso (Get-UtcNow)); stamps = $stamps; open = [bool]((Test-Has $r 'hard') -and @($r.hard).Count -gt 0); content = $r } }
    return $r
}
function Get-ReleaseContentRead($Rd, [string]$TargetName, $BySha) {
    if (-not $Rd.commit) { return @{ skip = $false; gaps = @("the release names no commit (its build information or notes)"); items = @() } }
    $td = Get-Date
    $dep = if ($null -ne $script:PromoMemo -and $script:PromoMemo.envCommit.ContainsKey($TargetName)) { $script:PromoMemo.envCommit[$TargetName] } else { Get-EnvDeployedCommit $TargetName }
    if ($null -ne $script:PromoMemo) { $script:PromoMemo.envCommit[$TargetName] = $dep }
    Add-PromoTime 'env-deployed-commit' $td
    if (-not $dep) { return @{ skip = $false; gaps = @("the commit running in $TargetName is not known (no successful deployment of a release with a commit)"); items = @() } }
    if ($dep.commit -eq $Rd.commit) { return @{ skip = $true; gaps = @(); items = @() } }
    $tc = Get-Date; $cmp = Invoke-Gh -Path "$(Get-RepoPath)/compare/$($dep.commit)...$($Rd.commit)" -Allow 404, 422; Add-PromoTime 'compare' $tc
    if ($cmp -is [hashtable]) { return @{ skip = $false; gaps = @("GitHub cannot compare the commit running in $TargetName ($($dep.commit.Substring(0,7)), release $($dep.version)) with the release's"); items = @() } }
    if ("$($cmp.status)" -in 'behind', 'identical') { return @{ skip = $true; gaps = @(); items = @() } }
    $cs = if ($cmp.PSObject.Properties['commits']) { @($cmp.commits) } else { @() }
    $total = if ($cmp.PSObject.Properties['total_commits']) { [int]$cmp.total_commits } else { $cs.Count }
    if ("$($cmp.status)" -ne 'ahead' -or $cs.Count -eq 0 -or $total -gt $cs.Count) { return @{ skip = $false; gaps = @("the commits between $TargetName ($($dep.commit.Substring(0,7))) and the release cannot all be read (compare: $($cmp.status), $($cs.Count) of $total)"); items = @() } }
    # first parent: the commits of master itself, walked back from the release's commit
    $byCommit = @{}; foreach ($x in $cs) { $byCommit["$($x.sha)".ToLower()] = $x }
    $chain = @(); $cur = $Rd.commit.ToLower()
    while ($byCommit.ContainsKey($cur)) { $chain += $cur; $p = @($byCommit[$cur].parents)[0]; $cur = if ($p) { "$($p.sha)".ToLower() } else { '' } }
    if ($cur -ne $dep.commit.ToLower()) { return @{ skip = $false; gaps = @("the first-parent history from the release back to $TargetName ($($dep.commit.Substring(0,7))) cannot be followed"); items = @() } }
    $gaps = @(); $items = @(); $hardGaps = @()   # hard: a failure the Sign-off aborts on (the others are waiting reasons)
    foreach ($sha in $chain) {
        if (Test-PromoOver) { return @{ skip = $false; gaps = @(); items = @(); deferred = $true } }   # the sweep's budget is used up: judged first in the next
        if ($BySha.ContainsKey($sha)) { $items += [int]$BySha[$sha]; continue }
        # not a queued item's: the factory item whose merge it is, in any column (a docs-only item went straight to Done; a merge not settled yet)
        $msg = if ($byCommit[$sha].PSObject.Properties['commit'] -and $byCommit[$sha].commit.PSObject.Properties['message']) { "$($byCommit[$sha].commit.message)" } else { '' }
        $tf = Get-Date; $f = Find-CommitItem $sha $msg; Add-PromoTime 'commit-item' $tf
        if ($f.item -gt 0) { $items += $f.item; if ($f.gap) { $gaps += $f.gap } }
        else { $g1 = "commit $($sha.Substring(0,7)) in the release is not the merge commit of a factory work item"; $gaps += $g1; $hardGaps += $g1 }
    }
    return @{ skip = $false; gaps = $gaps; hard = $hardGaps; items = @($items | Sort-Object -Unique) }
}

# The factory work item a master commit is the merge of, from the pull request its squash message names ("... (#P)"): that pull request was merged
# as this commit, and an item it names (branch <name>/<N>-..., "#N" in the title, "Closes #N" in the body) records the merge (merge.sha) or, not
# picked up yet, the pull request. @{ item (0: none); gap ('' when settled) }.
# A settled answer (an item whose merge record is final) is kept in the state directory, keyed by repository and commit: the automatic promotion
# judges every commit of a release's range at each sweep, and without this each sweep paid two or more GitHub reads per commit (issue 426). A hit is
# never trusted alone: the item's own state must still name the commit as its settled merge (one read), else the entry is dropped and the commit is
# judged afresh. The file is read on every call (never held in the process), holds at most 500 entries and is replaced atomically.
function Get-CommitItemCacheFile { Join-Path (Get-StateDir) 'commit-items.json' }
function Read-CommitItemCache {
    $c = @{}
    try { $f = Get-CommitItemCacheFile; if (Test-Path $f) { $j = Get-Content -Raw $f | ConvertFrom-Json -AsHashtable; if ($j -is [hashtable]) { $c = $j } } } catch { $c = @{} }
    return $c
}
function Save-CommitItemCache($Cache) {
    try {
        $keys = @($Cache.Keys); if ($keys.Count -gt 500) { foreach ($k in ($keys | Select-Object -First ($keys.Count - 500))) { $Cache.Remove($k) } }
        $f = Get-CommitItemCacheFile; $tmp = "$f.$PID.tmp"
        $Cache | ConvertTo-Json -Compress | Set-Content $tmp; Move-Item -Force $tmp $f
    } catch { }
}
function Test-CommitItemSettled([int]$Item, [string]$Sha) {
    $s = try { (Get-PromoState $Item).state } catch [FactoryReject] { $null }
    if (-not $s -or -not (Test-Has $s 'merge') -or -not (Test-Has $s.merge 'sha') -or "$($s.merge.sha)".ToLower() -ne $Sha) { return $false }
    $aw = if ((Test-Has $s.merge 'awaiting') -and $s.merge.awaiting) { "$($s.merge.awaiting)" } else { '' }
    return ($aw -eq 'release-queue' -or (-not $aw -and (Test-Has $s.merge 'done_at') -and $s.merge.done_at))
}
function Find-CommitItem([string]$Sha, [string]$Message) {
    $c0 = Get-Cfg; $key = "$($c0.owner)/$($c0.repo)@$Sha"
    $cache = Read-CommitItemCache
    if ($cache.ContainsKey($key)) {
        if (Test-CommitItemSettled ([int]$cache[$key]) $Sha) { return @{ item = [int]$cache[$key]; gap = '' } }
        $cache.Remove($key); Save-CommitItemCache $cache
    }
    # a commit that is no work item's merge is remembered for ten minutes (the pull request is not read again at every judgment of the range)
    $nn = if ($null -ne $script:PromoStore) { $script:PromoStore.none[$key] } else { $null }
    if ($nn -and ((Get-UtcNow) - (ConvertFrom-Iso $nn)).TotalMinutes -lt 10) { return @{ item = 0; gap = '' } }
    $r = Find-CommitItemUncached $Sha $Message
    if ($r.item -gt 0 -and -not $r.gap) { $cache[$key] = $r.item; Save-CommitItemCache $cache }
    elseif ($r.item -eq 0 -and -not $r.gap -and $null -ne $script:PromoStore) { $script:PromoStore.none[$key] = ConvertTo-Iso (Get-UtcNow) }
    return $r
}
function Find-CommitItemUncached([string]$Sha, [string]$Message) {
    $first = ($Message -split "`n")[0]
    $pm = [regex]::Match($first, '\(#(\d+)\)\s*$'); if (-not $pm.Success) { return @{ item = 0; gap = '' } }
    $prNum = [int]$pm.Groups[1].Value
    $pr = Invoke-Gh -Path "$(Get-RepoPath)/pulls/$prNum" -Allow 404
    if ($pr -is [hashtable] -or -not $pr.merged -or "$($pr.merge_commit_sha)".ToLower() -ne $Sha) { return @{ item = 0; gap = '' } }
    $cands = @()
    $bm = [regex]::Match("$($pr.head.ref)", '/(\d+)-'); if ($bm.Success) { $cands += [int]$bm.Groups[1].Value }
    foreach ($x in [regex]::Matches("$($pr.title)", '(?<![0-9])#(\d+)(?![0-9])')) { $cands += [int]$x.Groups[1].Value }
    $bodyText = if ($pr.PSObject.Properties['body']) { "$($pr.body)" } else { '' }
    foreach ($x in [regex]::Matches($bodyText, '(?i)\b(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\s+#(\d+)')) { $cands += [int]$x.Groups[1].Value }
    foreach ($n in @($cands | Where-Object { $_ -gt 0 -and $_ -ne $prNum } | Select-Object -Unique)) {
        $s = try { (Get-State $n).state } catch [FactoryReject] { $null }
        if (-not $s) { continue }
        if ((Test-Has $s 'merge') -and (Test-Has $s.merge 'sha') -and "$($s.merge.sha)".ToLower() -eq $Sha) {
            $aw = if ((Test-Has $s.merge 'awaiting') -and $s.merge.awaiting) { "$($s.merge.awaiting)" } else { '' }
            $done = (Test-Has $s.merge 'done_at') -and $s.merge.done_at
            if ($aw -eq 'release-queue' -or (-not $aw -and $done)) { return @{ item = $n; gap = '' } }
            return @{ item = $n; gap = "#$n's merge ($($Sha.Substring(0,7))) is not settled yet$(if ($aw) { " (awaiting $aw)" })" }
        }
        $ownPr = ((Test-Has $s 'verdict') -and (Test-Has $s.verdict 'pr') -and [int]$s.verdict.pr -eq $prNum) -or ((Test-Has $s 'pr') -and $s.pr -is [System.Collections.IDictionary] -and (Test-Has $s.pr 'number') -and [int]$s.pr.number -eq $prNum)
        if ($ownPr) { return @{ item = $n; gap = "#$n's merge of PR #$prNum ($($Sha.Substring(0,7))) is not picked up by the gateway yet" } }
    }
    return @{ item = 0; gap = '' }
}

# what one item lacks for the promotion of its release (reasons; empty when it qualifies)
function Get-ItemPromotionGaps([int]$n, $Labels) {
    if ($null -ne $script:PromoMemo -and $script:PromoMemo.gaps.ContainsKey($n)) { return @($script:PromoMemo.gaps[$n]) }
    $r = @(Get-ItemPromotionGapsRead $n $Labels)
    if ($null -ne $script:PromoMemo) { $script:PromoMemo.gaps[$n] = $r }
    return @($r)
}
function Get-ItemPromotionGapsRead([int]$n, $Labels) {
    $c = Get-Cfg; $gaps = @()
    if (-not (Test-NameIn $c.autoMergeLabel @($Labels))) { $gaps += "#$n has no '$($c.autoMergeLabel)' label" }
    $hold = if (Test-NameIn 'Hold' @($Labels)) { 'Hold' } else { Get-HoldLabel @($Labels) }
    if ($hold) { $gaps += "#$n carries the '$hold' label" }
    $s = (Get-PromoState $n).state
    $passMerged = (Test-Has $s 'verdict') -and (Test-Has $s.verdict 'passed') -and $s.verdict.passed -and (Test-Has $s.verdict 'pr') -and (Test-Has $s 'merge') -and (Test-Has $s.merge 'pr') -and [int]$s.merge.pr -gt 0 -and [int]$s.merge.pr -eq [int]$s.verdict.pr -and -not ((Test-Has $s.merge 'by') -and "$($s.merge.by)" -eq 'person')
    if (-not $passMerged) { $gaps += "#$n has no pull request merged after a Functional Testing PASS verdict" }
    if (Test-NameIn $c.videoLabel @($Labels)) {
        # (a demo video once published stays: kept, so only an item still waiting for its video costs a read)
        $has = $null -ne $script:PromoStore -and $script:PromoStore.demo.ContainsKey("$n")
        if (-not $has) {
            $tv = Get-Date; $rel = Invoke-Gh -Path "$(Get-RepoPath)/releases/tags/demo-$n" -Allow 404; Add-PromoTime 'demo-release' $tv
            $has = $rel -isnot [hashtable] -and $rel.PSObject.Properties['assets'] -and @(@($rel.assets) |Where-Object { $_ -and "$($_.name)" -eq "demo-$n.mp4" }).Count -gt 0
            if ($has -and $null -ne $script:PromoStore) { $script:PromoStore.demo["$n"] = $true }
        }
        if (-not $has) { $gaps += "#$n has the '$($c.videoLabel)' label and no demo video published (release demo-$n, asset demo-$n.mp4)" }
    }
    return $gaps
}

# the tasks of the deployments the gateway created for a release and environment key, from the items' state (merge.promoted.<key>.task)
function Get-PromotedTasks([int[]]$Items, [string]$Key) {
    $tasks = @()
    foreach ($n in $Items) {
        $s0 = (Get-PromoState $n).state; $m = if (Test-Has $s0 'merge') { $s0.merge } else { $null }
        if ($m -and (Test-Has $m 'promoted') -and (Test-Has $m.promoted $Key) -and "$($m.promoted[$Key].task)") { $tasks += "$($m.promoted[$Key].task)" }
    }
    return @($tasks | Select-Object -Unique)
}

function Add-PromotionJournal([int[]]$Items, [string]$Event, [hashtable]$Fields, [string]$Key, [scriptblock]$Edit = $null) {
    foreach ($n in $Items) {
        # A line this pass or an earlier one wrote (or found written) for this event and key, with the item's state comment unchanged since, is written
        # again as nothing: Add-Journal would find its key. Skipped here without the lock and the three reads.
        $tok = "$Event|$Key"
        if (-not $Edit -and $null -ne $script:PromoStore) {
            $jn = $script:PromoStore.journaled["$n"]; $se = $script:PromoStore.states["$n"]
            if ($jn -and $se -and $jn.stamp -eq $se.stamp -and @($jn.tokens) -contains $tok) { continue }
        }
        try { [void](Use-ItemLock $n { $f = Get-State $n; if ($Edit) { & $Edit $f.state }; $did = Add-Journal $n $f $Event $Fields.Clone() $Key; if ($did -or $Edit) { Save-State $n $f }; Set-PromoCached $n $f $tok } 10); if ($null -ne $script:PromoMemo) { $script:PromoMemo.state.Remove($n) } }
        catch [FactoryReject] { if ($_.Exception.Code -notin 'item-busy', 'github-not-found', 'item-not-found') { throw } }
    }
}

# The pending interruptions (the Sign-off step) of a deployment task the gateway created: Proceed when $Gaps is empty, else Abort. Returns
# @{ answered; result; refused } (refused: the permission Octopus named, or the status).
function Resolve-SignOff([string]$Task, [string[]]$Gaps) {
    $sp = (Get-Cfg).octopus.space
    $list = Invoke-Octo "api/$sp/interruptions?regarding=$Task&pendingOnly=true&take=20"
    $done = 0; $result = if ($Gaps.Count) { 'Abort' } else { 'Proceed' }
    $pending = if ($list -and $list.PSObject.Properties['Items']) { @(@($list.Items) | Where-Object { $_ -and $_.PSObject.Properties['IsPending'] -and $_.IsPending }) } else { @() }
    foreach ($i in $pending) {
        $r = Invoke-OctoPost "api/$sp/interruptions/$($i.Id)/responsible" $null 'PUT'   # Octopus takes responsibility by PUT only (POST answers 405)
        if ($r.status -notin 200, 201) { return @{ answered = $done; result = $result; refused = $(if ($r.status -eq 403) { Get-OctoMissingPermission $r.body } else { "$($r.status)" }); status = $r.status } }
        $notes = if ($Gaps.Count) { "Aborted by the factory worker gateway: $($Gaps -join '; ')" } else { 'Signed off by the factory worker gateway: every gate of the release holds (docs/design/auto-promote.md).' }
        $r = Invoke-OctoPost "api/$sp/interruptions/$($i.Id)/submit" @{ Result = $result; Notes = $notes; Instructions = $null }
        if ($r.status -notin 200, 201) { return @{ answered = $done; result = $result; refused = $(if ($r.status -eq 403) { Get-OctoMissingPermission $r.body } else { "$($r.status)" }); status = $r.status } }
        $done++
    }
    return @{ answered = $done; result = $result; refused = $null }
}

# ---- the promotion pass's memo, deadline and timing (issues 428, 429) ----------------------------------------------------------------------------
# The releases of the queue are cumulative ranges, so one pass would judge the same ~50 items once per release (a state read, a label read and a demo
# video read each): the reads are kept for the length of one pass ($script:PromoMemo, null outside it) and, across passes, in the store below. The pass
# ends before its own deadline (budget_seconds of the verb; Test-PromoOver, checked between releases and between the expensive calls; what is not
# judged is judged first in the next pass), and it reports where its time went (promotion-timing: ms per release and per kind of call).
$script:PromoMemo = $null; $script:PromoRel = $null; $script:PromoRelT = $null; $script:PromoTestDelayMs = 0; $script:PromoDeadline = $null
$script:PromoStore = $null; $script:PromoChanged = @{}; $script:PromoDeferred = @()
function Test-PromoOver { return [bool]($script:PromoDeadline -and (Get-Date) -gt $script:PromoDeadline) }
function Add-PromoTime([string]$Kind, [datetime]$Since) {
    if ($null -eq $script:PromoMemo) { return }
    $ms = ((Get-Date) - $Since).TotalMilliseconds; $t = $script:PromoMemo.times
    if (-not $t.ContainsKey($Kind)) { $t[$Kind] = @{ ms = 0.0; n = 0 } }
    $t[$Kind].ms += $ms; $t[$Kind].n++
}
function Close-PromoRelease {
    if ($null -ne $script:PromoMemo -and $script:PromoRel) { $script:PromoMemo.releases[$script:PromoRel] = [int]((Get-Date) - $script:PromoRelT).TotalMilliseconds }
    $script:PromoRel = $null
}
# ---- the promotion's store (promote-judgments.json in the state directory, issue 429) --------------------------------------------------------
# What a pass learned and the next one may rely on, so a pass in which nothing changed reads next to nothing:
#   states     per item: the slim state (verdict, merge) with the update time ("stamp") of the state comment it was read from. A pass first asks
#              GitHub which state comments changed since the last pass (one listing of the repository's comments by update time, not one read per item);
#              the entries of those are dropped. At most 30 minutes old.
#   content    per release, target and commit range: the judgment of the release's content (Get-ReleaseContent), with the stamps of the items it names
#   journaled  per item: the journal events and keys written or found, with the stamp of the state then (Add-PromotionJournal)
#   none       commits that are no work item's merge; demo: items whose demo video is published
#   deferred   the releases the last pass had no time for: judged first by the next
# It is a cache: a missing or unreadable file is an empty store, and an entry is only used while what it was derived from is unchanged.
function Get-PromoStoreFile { Join-Path (Get-StateDir) 'promote-judgments.json' }
function Read-PromoStore {
    $st = $null
    try { $f = Get-PromoStoreFile; if (Test-Path $f) { $j = Get-Content -Raw $f | ConvertFrom-Json -AsHashtable -DateKind String; if ($j -is [hashtable] -and $j.v -eq 1) { $st = $j } } } catch { $st = $null }
    if (-not $st) { $st = @{ v = 1 } }
    foreach ($k in 'states', 'content', 'journaled', 'none', 'demo') { if (-not ($st[$k] -is [hashtable])) { $st[$k] = @{} } }
    $st.deferred = @(if ($st.ContainsKey('deferred') -and $null -ne $st.deferred) { @($st.deferred) | ForEach-Object { "$_" } })
    return $st
}
function Save-PromoStore {
    if ($null -eq $script:PromoStore) { return }
    try {
        $st = $script:PromoStore; $now = Get-UtcNow
        foreach ($k in @($st.states.Keys)) { if (($now - (ConvertFrom-Iso $st.states[$k].at)).TotalMinutes -ge 30) { $st.states.Remove($k) } }
        foreach ($k in @($st.content.Keys)) { if (($now - (ConvertFrom-Iso $st.content[$k].at)).TotalMinutes -ge 10) { $st.content.Remove($k) } }
        foreach ($k in @($st.none.Keys)) { if (($now - (ConvertFrom-Iso $st.none[$k])).TotalMinutes -ge 10) { $st.none.Remove($k) } }
        foreach ($k in @($st.journaled.Keys)) { if (-not $st.states.ContainsKey($k)) { $st.journaled.Remove($k) } }
        $st.deferred = @($script:PromoDeferred)
        $f = Get-PromoStoreFile; $tmp = "$f.$PID.tmp"
        $st | ConvertTo-Json -Depth 12 -Compress | Set-Content $tmp; Move-Item -Force $tmp $f
    } catch { [Console]::Error.WriteLine("promote: the store could not be saved: $($_.Exception.Message)") }
}
# The items whose state comment changed since the last pass: ONE listing of the repository's comments updated since then. Without a store, or when
# the listing fails, nothing is trusted (every state is read afresh); the store is rebuilt by this pass.
function Update-PromoStore {
    $st = $script:PromoStore; $script:PromoChanged = @{}
    $scanStart = Get-UtcNow
    try {
        if (-not $st.ContainsKey('watermark') -or -not $st.watermark) { throw 'no earlier pass' }
        $marker = Get-StateMarker; $page = 1; $t = Get-Date
        do {
            $chunk = @(Invoke-Gh -Path "$(Get-RepoPath)/issues/comments?since=$($st.watermark)&sort=updated&direction=asc&per_page=100&page=$page")
            foreach ($cm in $chunk) {
                if (-not "$($cm.body)".StartsWith($marker)) { continue }
                $m = [regex]::Match("$($cm.issue_url)", '/issues/(\d+)$'); if (-not $m.Success) { continue }
                $n = [int]$m.Groups[1].Value; $e = $st.states["$n"]
                if (-not $e -or $e.stamp -ne (Get-Stamp $cm.updated_at)) { $script:PromoChanged[$n] = $true; $st.states.Remove("$n") }
            }
            $page++
        } while ($chunk.Count -eq 100)
        Add-PromoTime 'comments-since' $t
    }
    catch { $st.states = @{}; $st.content = @{}; $st.journaled = @{}; $script:PromoChanged = @{} }
    $st.watermark = ConvertTo-Iso ($scanStart.AddMinutes(-2))
}
function Get-SlimPromoState($S) { $o = @{}; foreach ($k in 'verdict', 'merge') { if ((Test-Has $S $k)) { $o[$k] = $S[$k] } }; return $o }
# a state read or written by this pass: kept with its stamp, and the journal line (event|key) the state is known to carry
function Set-PromoCached([int]$n, $F, [string]$Token = '') {
    if ($null -eq $script:PromoStore) { return }
    if (-not $F.commentId -or -not $F.updatedAt) { $script:PromoStore.states.Remove("$n"); $script:PromoStore.journaled.Remove("$n"); return }
    $script:PromoStore.states["$n"] = @{ stamp = $F.updatedAt; at = (ConvertTo-Iso (Get-UtcNow)); state = (Get-SlimPromoState $F.state) }
    if ($Token) {
        # (an item carries the lines of every release that waits with it: the tokens known for this stamp are kept together)
        $jn = $script:PromoStore.journaled["$n"]
        $known = if ($jn -and $jn.stamp -eq $F.updatedAt) { @($jn.tokens) } else { @() }
        $script:PromoStore.journaled["$n"] = @{ stamp = $F.updatedAt; tokens = @(@($known) + @($Token) | Select-Object -Unique) }
    }
    elseif ($script:PromoStore.journaled.ContainsKey("$n") -and $script:PromoStore.journaled["$n"].stamp -ne $F.updatedAt) { $script:PromoStore.journaled.Remove("$n") }
}
# Octopus answers that do not change within a pass (the project, the environments), read once per pass
function Get-PromoOcto([string]$Path) {
    if ($null -eq $script:PromoMemo) { return (Invoke-Octo $Path) }
    if (-not $script:PromoMemo.octo.ContainsKey($Path)) { $t = Get-Date; $script:PromoMemo.octo[$Path] = Invoke-Octo $Path; Add-PromoTime 'octopus-lookup' $t }
    return $script:PromoMemo.octo[$Path]
}
function Get-PromoState([int]$n) {
    if ($null -eq $script:PromoMemo) { return (Get-State $n) }
    if (-not $script:PromoMemo.state.ContainsKey($n)) {
        $e = if ($null -ne $script:PromoStore) { $script:PromoStore.states["$n"] } else { $null }
        if ($e -and $e.stamp) { $script:PromoMemo.state[$n] = @{ commentId = 1; state = $e.state; updatedAt = $e.stamp }; $script:PromoMemo.hits++ }
        else {
            $t = Get-Date; $f = Get-State $n; $script:PromoMemo.state[$n] = $f; Add-PromoTime 'item-state' $t
            Set-PromoCached $n $f
        }
    }
    return $script:PromoMemo.state[$n]
}
function Get-PromoLabels([int]$n) {
    if ($null -eq $script:PromoMemo) { return @((Get-ItemInfo $n).labels) }
    if (-not $script:PromoMemo.labels.ContainsKey($n)) { $t = Get-Date; $script:PromoMemo.labels[$n] = @((Get-ItemInfo $n).labels); Add-PromoTime 'item-info' $t }
    return $script:PromoMemo.labels[$n]
}
# The Release Queue as the sweep sees it, from the board list (one paged read) and the items' states: the open or closed cards in a hold column
# (Release Queue) whose merge record awaits the release queue. $Labels: the labels of every card of the board (the gates read them from here, not from
# a read per item). $null when the budget ran out before the whole queue was read: a queue read in part would judge a release without some of its items.
function Get-PromoQueue($Board) {
    $c = Get-Cfg; $queue = @(); $labels = @{}
    foreach ($it in $Board) {
        $labels["$($it.number)"] = @($it.labels)
        if (-not (Test-NameIn $c.label $it.labels)) { continue }
        if (-not (Test-NameIn "$($it.column)" $c.columns.hold)) { continue }
        if (Test-NameIn $c.rivalLabel $it.labels) { continue }
        if (Test-PromoOver) { return $null }
        try { $s = (Get-PromoState ([int]$it.number)).state } catch [FactoryReject] { if ($_.Exception.Code -notin 'github-not-found', 'item-not-found') { throw }; continue }
        if ((Test-Has $s 'merge') -and (Test-Has $s.merge 'awaiting') -and "$($s.merge.awaiting)" -eq 'release-queue') {
            $queue += @{ item = [int]$it.number; release = $(if (Test-Has $s.merge 'release') { "$($s.merge.release)" } else { '' }); since = $(if (Test-Has $s.merge 'queued_at') { $s.merge.queued_at } else { $null }) }
        }
    }
    return @{ queue = $queue; labels = $labels }
}
# One pass over the board's Release Queue: the actions it took or waits on, and the timing of where its time went.
function Invoke-AutoPromote($Board) {
    $script:PromoMemo = @{ state = @{}; labels = @{}; gaps = @{}; envCommit = @{}; octo = @{}; times = @{}; releases = [ordered]@{}; hits = 0 }; $script:PromoRel = $null
    $script:PromoStore = Read-PromoStore; $script:PromoDeferred = @($script:PromoStore.deferred); $script:PromoChanged = @{}
    $t0 = Get-Date; $m = $script:PromoMemo; $out = @(); $incomplete = $false; $qn = 0
    try {
        Update-PromoStore
        $q = Get-PromoQueue $Board
        if ($null -eq $q) { $incomplete = $true }
        elseif (@($q.queue).Count -gt 0) { $qn = @($q.queue).Count; $out = @(Invoke-AutoPromoteInner $q.queue $q.labels) }
        else { $script:PromoDeferred = @() }
    }
    finally { Close-PromoRelease; Save-PromoStore; $script:PromoMemo = $null; $script:PromoStore = $null }
    $calls = [ordered]@{}; foreach ($k in ($m.times.Keys | Sort-Object)) { $calls[$k] = @{ ms = [int]$m.times[$k].ms; n = $m.times[$k].n } }
    if ($incomplete) { $out += @{ item = 0; action = 'promotion-deferred'; release = ''; reason = 'queue-not-read' } }
    $timing = @{ item = 0; action = 'promotion-timing'; total_ms = [int]((Get-Date) - $t0).TotalMilliseconds; queue = $qn; cache_hits = $m.hits; releases = $m.releases; calls = $calls }
    [Console]::Error.WriteLine("promotion timing: " + ($timing | ConvertTo-Json -Depth 5 -Compress))
    return @($out + $timing)
}
# The verb: the gateway's own loop calls it (gateway/AutoPromoter.cs), apart from the board sweep, with its own budget (budget_seconds, default 200;
# the gateway's cap for the call is longer), so a slow promotion never delays the sweep and a slow sweep never delays a promotion.
function Verb_auto_promote($a) {
    try { return (Invoke-AutoPromoteVerb $a) } finally { $script:PromoDeadline = $null; $script:PromoTestDelayMs = 0; $script:GhTrace = $null }
}
function Invoke-AutoPromoteVerb($a) {
    $c = Get-Cfg; $t0 = Get-Date
    $on = (Test-Has $c 'delivery') -and (Test-Has $c.delivery 'autoPromote') -and (Test-Has $c.delivery.autoPromote 'enabled') -and $c.delivery.autoPromote.enabled
    $gwEnv = if ($on -and (Test-Has $c.delivery.autoPromote 'gatewayEnvironment')) { "$($c.delivery.autoPromote.gatewayEnvironment)" } else { '' }
    if (-not $on -or -not $gwEnv -or "$env:FACTORY_ENVIRONMENT" -ine $gwEnv) { return @{ ok = $true; enabled = $false; actions = @(); errors = @(); elapsed_ms = 0 } }
    $budget = if ((Test-Has $a 'budget_seconds') -and [double]$a.budget_seconds -gt 0) { [double]$a.budget_seconds } else { 200 }
    $script:PromoDeadline = $t0.AddSeconds($budget)
    $script:PromoTestDelayMs = if (Test-Has $a 'test_promotion_delay_ms') { [int]$a.test_promotion_delay_ms } else { 0 }
    $actions = @(); $errors = @(); $g0 = $script:GhCalls; $o0 = $script:OctoCalls
    if ((Test-Has $a 'trace') -and $a.trace) { $script:GhTrace = [System.Collections.Generic.List[string]]::new() }   # (diagnosis: the requests of the pass are listed in github_paths)
    try { $actions += @(Invoke-AutoPromote (Get-BoardList)) }
    catch [FactoryReject] { $errors += @{ item = 0; code = $_.Exception.Code; reason = $_.Exception.Message } }
    catch { [Console]::Error.WriteLine("promote: $($_.Exception.Message) $($_.ScriptStackTrace -replace "`r?`n", " | ")"); $actions += @{ item = 0; action = 'promotion-error'; reason = "$($_.Exception.Message)" } }   # (the failure is reported and the next pass tries again)
    return @{ ok = $true; enabled = $true; github_calls = ($script:GhCalls - $g0); octopus_calls = ($script:OctoCalls - $o0); github_paths = $(if ($null -ne $script:GhTrace) { @($script:GhTrace) } else { $null }); actions = $actions; errors = $errors; elapsed_ms = [int]((Get-Date) - $t0).TotalMilliseconds; at = ConvertTo-Iso (Get-UtcNow) }
}
function Invoke-AutoPromoteInner($Queue, $Labels) {
    $c = Get-Cfg
    if (-not (Test-Has $c 'delivery') -or -not (Test-Has $c.delivery 'autoPromote') -or -not (Test-Has $c.delivery.autoPromote 'enabled') -or -not $c.delivery.autoPromote.enabled) { return @() }
    $ap = $c.delivery.autoPromote
    # only the gateway of the named environment promotes (the same image runs in tdd, uat and prod; tdd follows cmdemo1-ui's whole lifecycle)
    $gwEnv = if (Test-Has $ap 'gatewayEnvironment') { "$($ap.gatewayEnvironment)" } else { '' }
    # (fail closed: without a named gateway environment nobody promotes)
    if (-not $gwEnv -or "$env:FACTORY_ENVIRONMENT" -ine $gwEnv) { return @() }
    # the merge commit of every queued item, to judge each release by its whole content
    $bySha = @{}
    foreach ($q in @($Queue)) {
        try { $s0 = (Get-PromoState ([int]$q.item)).state; if ((Test-Has $s0 'merge') -and (Test-Has $s0.merge 'sha') -and $s0.merge.sha) { $bySha["$($s0.merge.sha)".ToLower()] = [int]$q.item } }
        catch [FactoryReject] { if ($_.Exception.Code -notin 'github-not-found', 'item-not-found') { throw } }
    }
    $o = $c.octopus; $chain = @('tdd', 'uat', 'prod')
    $targets = @(@($ap.environments) | ForEach-Object { "$_".ToLower() } | Where-Object { $chain.IndexOf($_) -ge 1 })
    $envName = { param($k) "$($o.environments.$k)" }
    $out = @()
    # the releases the last pass had no time for come first, in the order they were left (they keep their place); then the others as before
    $groups = @($Queue | Where-Object { $_.release } | Group-Object { "$($_.release)" })
    $prevDef = @($script:PromoDeferred); $newDef = @()
    $groups = @(@($prevDef | ForEach-Object { $d0 = $_; $groups | Where-Object { $_.Name -eq $d0 } }) + @($groups | Where-Object { $_.Name -notin $prevDef }))
    foreach ($g in $groups) {
        $ver = $g.Name; [int[]]$items = @($g.Group | ForEach-Object { [int]$_.item } | Sort-Object -Unique)
        # the sweep's budget (issue 426): the releases not judged in this sweep are judged first in the next (the commit cache makes that cheap)
        Close-PromoRelease; $script:PromoRel = $ver; $script:PromoRelT = Get-Date
        if (Test-PromoOver) { $out += @{ item = $items[0]; action = 'promotion-deferred'; release = $ver; reason = 'sweep-budget' }; $newDef += $ver; continue }
        if ($script:PromoTestDelayMs -gt 0) { Start-Sleep -Milliseconds $script:PromoTestDelayMs }   # (tests only: sync-board test_promotion_delay_ms, a slow promotion)
        $tr = Get-Date; $rd = Get-ReleaseDeployments $ver; Add-PromoTime 'release-deployments' $tr
        if (-not $rd) { $out += @{ item = $items[0]; action = 'promotion-waiting'; release = $ver; reason = 'release-not-in-octopus' }; continue }
        $tk = $null
        foreach ($t in $targets) { $d = $rd.deployments[(& $envName $t).ToLower()]; if (-not ($d -and $d.state -eq 'Success')) { $tk = $t; break } }
        if (-not $tk) { continue }   # promoted all the way: settle sees the prod deployment and finishes the items
        $tname = & $envName $tk; $pk = $chain[$chain.IndexOf($tk) - 1]; $pname = & $envName $pk
        $have = $rd.deployments[$tname.ToLower()]
        # the release's whole content relative to what runs in the target: every commit must be a queued item's merge commit; those items are
        # judged with the release's own items (an earlier release's unqualified change never rides along)
        $content = Get-ReleaseContent $rd $tname $bySha
        if ((Test-Has $content 'deferred') -and $content.deferred) { $out += @{ item = $items[0]; action = 'promotion-deferred'; release = $ver; reason = 'sweep-budget' }; $newDef += $ver; continue }
        if ($content.skip -and -not $have) { continue }   # not newer than what runs there: nothing to promote
        $own = $items
        $items = [int[]]@(@($own) + @($content.items) | Sort-Object -Unique)
        $gaps = @{}; $itemGaps = @()
        $gone = $false
        $deferred = $false
        foreach ($n in $items) { if (Test-PromoOver) { $deferred = $true; break }; try { $gaps[$n] = @(Get-ItemPromotionGaps $n $(if ($Labels.ContainsKey("$n")) { $Labels["$n"] } else { @(Get-PromoLabels $n) })) } catch [FactoryReject] { if ($_.Exception.Code -notin 'github-not-found', 'item-not-found') { throw }; $gone = $true; break }; $itemGaps += $gaps[$n] }
        if ($gone) { continue }   # an item of the release vanished since the sweep read it: judged again at the next sweep
        if ($deferred) { $out += @{ item = $items[0]; action = 'promotion-deferred'; release = $ver; reason = 'sweep-budget' }; $newDef += $ver; continue }
        $common = @(@($content.gaps)); $note = ''
        $pd = $rd.deployments[$pname.ToLower()]
        if (-not $pd) { $common += "it has no deployment to $pname" }
        elseif ($pd.state -ne 'Success') { $common += "its deployment to $pname is $(if ($pd.completed) { "$($pd.state)" } else { "still $($pd.state)" }) ($($pd.deployment))" }
        elseif ($itemGaps.Count -eq 0 -and $common.Count -eq 0 -and -not ($have -and -not $have.completed)) { $h = Test-EnvHealth $pk $ver $pd.completed_at $rd.release_id $own[0]; if ($h.gap) { $common += $h.gap }; $note = "$($h.note)" }
        if ($have -and -not $have.completed) {
            # following a deployment: only one the gateway created gets its Sign-off answered, with the gates read again just now
            $ours = (Get-PromotedTasks $items $tk) -contains $have.task
            if (-not $ours) { $out += @{ item = $items[0]; action = 'promotion-following'; release = $ver; environment = $tname; task = $have.task; state = $have.state; ours = $false }; continue }
            # The Sign-off asks again only what can get worse while it waits, and aborts only for a hard failure: an item gate (a Hold label, an item
            # that no longer qualifies), a commit of the release that is no factory work item's, the release superseded in the target, the previous
            # environment's app not Healthy now (one read). A waiting reason (a merge not settled, a range not readable) leaves it pending. Neither
            # the version running in the previous environment (a newer release there is fine: this one passed there) nor the reads window is asked.
            $hard = @($itemGaps) + @(if (Test-Has $content 'hard') { @($content.hard) })
            if ($content.skip) { $hard += "a release at least as new already runs in $tname" }
            $soft = @(@($common) | Where-Object { $_ -notin $hard })
            if ($hard.Count -eq 0 -and $soft.Count -eq 0) { $hn = Test-EnvHealthNow $pk; if ($hn) { $hard += $hn } }
            if ($hard.Count -eq 0 -and $soft.Count -gt 0) {
                # a waiting Sign-off blocks later deployments of the project to that environment: after delivery.autoPromote.signoffWaitHours (2)
                # it is aborted. Since when it waits is kept on the release's first item (merge.promoted.<env>.signoff_wait_since).
                $limitH = if (Test-Has $ap 'signoffWaitHours') { [double]$ap.signoffWaitHours } else { 2 }
                $key = $tk; $task0 = $have.task; $nowIso = ConvertTo-Iso (Get-UtcNow)
                $since = $null
                try { $s1 = (Get-State $own[0]).state; if ((Test-Has $s1 'merge') -and (Test-Has $s1.merge 'promoted') -and (Test-Has $s1.merge.promoted $key) -and (Test-Has $s1.merge.promoted[$key] 'signoff_wait_since') -and "$($s1.merge.promoted[$key].task)" -eq $task0) { $since = ConvertFrom-Iso $s1.merge.promoted[$key].signoff_wait_since } } catch [FactoryReject] { }
                if (-not $since) {
                    $n0 = [int]$own[0]
                    try { [void](Use-ItemLock $n0 { $f = Get-State $n0; $s = $f.state; if ((Test-Has $s 'merge') -and (Test-Has $s.merge 'promoted') -and (Test-Has $s.merge.promoted $key)) { $s.merge.promoted[$key].signoff_wait_since = $nowIso; Save-State $n0 $f } } 10) }
                    catch [FactoryReject] { if ($_.Exception.Code -notin 'item-busy', 'github-not-found', 'item-not-found') { throw } }
                    $since = ConvertFrom-Iso $nowIso
                }
                $waitedH = ((Get-UtcNow) - $since).TotalHours
                if ($waitedH -ge $limitH) { $hard = @("waited $([Math]::Round($waitedH, 1)) h at the Sign-off (limit $limitH h): $($soft -join '; ')") }
            }
            if ($hard.Count -eq 0 -and $soft.Count -gt 0) {
                $why = "the Sign-off of task $($have.task) waits: $($soft -join '; ')"
                Add-PromotionJournal $items 'promotion-waiting' @{ release = $ver; environment = $tname; reason = $why } "$ver|$tk|signoff-wait|$why"
                $out += @{ item = $items[0]; action = 'promotion-following'; release = $ver; environment = $tname; task = $have.task; state = $have.state; ours = $true; signoff = 'waiting' }
                continue
            }
            $all = $hard
            $so = Resolve-SignOff $have.task $all
            if ($so.refused) {
                $why = "Octopus refused to answer the Sign-off of task $($have.task) ($(if ($so.status -eq 403) { "403, missing permission $($so.refused)" } else { "status $($so.refused)" })); the owner grants it to the gateway's Octopus account"
                Add-PromotionJournal $items 'promotion-waiting' @{ release = $ver; environment = $tname; reason = $why } "$ver|$tk|signoff|$why"
            }
            elseif ($so.answered -gt 0) {
                Add-PromotionJournal $items 'promotion-signoff' @{ release = $ver; environment = $tname; task = $have.task; result = $so.result; reason = ($all -join '; ') } "$ver|$tk|$($have.task)|$($so.result)"
            }
            $out += @{ item = $items[0]; action = 'promotion-following'; release = $ver; environment = $tname; task = $have.task; state = $have.state; ours = $true; signoff = $so.result; answered = $so.answered; refused = $so.refused }
            continue
        }
        if ($have) { $common = @("its deployment to $tname ended $($have.state) ($($have.deployment)); the operator deploys it again, or the item goes back to Development") }
        if ($common.Count -eq 0 -and $itemGaps.Count -eq 0) {
            # read again just before: a deployment of this release to the target that appeared since (another sweep, a person) is never doubled
            $again = Get-ReleaseDeployments $ver
            if ($again -and $again.deployments.ContainsKey($tname.ToLower())) { $out += @{ item = $items[0]; action = 'promotion-following'; release = $ver; environment = $tname; task = $again.deployments[$tname.ToLower()].task }; continue }
            # the Executions API route this server advertises for an untenanted project (ExecutionsCreateApiDeploymentCreateV1); deployments/create/v1 is 404
            $createPath = "api/$($o.space)/deployments/create/untenanted/v1"
            $r = Invoke-OctoPost $createPath @{ SpaceIdOrName = "$($o.space)"; ProjectName = "$($o.project)"; ReleaseVersion = $ver; EnvironmentNames = @($tname) }
            if ($r.status -in 200, 201) {
                $dt = if ($r.body -and $r.body.PSObject.Properties['DeploymentServerTasks']) { @($r.body.DeploymentServerTasks)[0] } else { $null }
                if (-not $dt) { $out += @{ item = $items[0]; action = 'promotion-error'; release = $ver; environment = $tname; status = $r.status }; continue } $task = "$($dt.ServerTaskId)"; $depId = "$($dt.DeploymentId)"; $at = ConvertTo-Iso (Get-UtcNow); $key = $tk
                Add-PromotionJournal $items 'promoted' @{ release = $ver; environment = $tname; task = $task } "$ver|$tk" { param($s) if (Test-Has $s 'merge') { if (-not (Test-Has $s.merge 'promoted')) { $s.merge.promoted = @{} }; $s.merge.promoted[$key] = @{ task = $task; deployment = $depId; at = $at; release = $ver } } }.GetNewClosure()
                $out += @{ item = $items[0]; action = 'promoted'; release = $ver; environment = $tname; task = $task; items = $items; note = $note }
                continue
            }
            if ($r.status -eq 403) {
                $perm = Get-OctoMissingPermission $r.body
                $common += "Octopus refused to create the deployment (403$(if ($perm) { ", missing permission $perm" })); the owner grants it to the gateway's Octopus account"
            }
            else {
                # any other answer is a waiting reason on the items (journalled once per change) and a log line: the path, the status and
                # Octopus' ErrorMessage (first 200 characters), never the request or a key
                $em = if ($r.body -and $r.body.PSObject.Properties['ErrorMessage']) { "$($r.body.ErrorMessage)" } else { '' }
                if ($em.Length -gt 200) { $em = $em.Substring(0, 200) }
                $common += "Octopus answered $($r.status) to POST /$createPath$(if ($em) { ": $em" }); the gateway tries again at every sweep"
                $out += @{ item = $items[0]; action = 'promotion-error'; release = $ver; environment = $tname; status = $r.status; path = "/$createPath"; error = $em }
            }
        }
        foreach ($n in $items) {
            $why = @($common) + @($gaps[$n]) + @($items | Where-Object { $_ -ne $n -and $gaps[$_].Count } | ForEach-Object { "#$_, carried by this release, does not qualify" })
            $reason = $why -join '; '
            Add-PromotionJournal @($n) 'promotion-waiting' @{ release = $ver; environment = $tname; reason = $reason } "$ver|$tk|$reason"
        }
        $out += @{ item = $items[0]; action = 'promotion-waiting'; release = $ver; environment = $tname; items = $items; reasons = @($common) }
    }
    $script:PromoDeferred = @($newDef)
    return $out
}
