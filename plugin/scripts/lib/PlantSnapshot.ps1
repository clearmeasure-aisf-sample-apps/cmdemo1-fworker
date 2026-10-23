# Server side of the plant snapshot (gateway/PlantSnapshot.cs, enhancement 5). The plant (offers, assignments, owners, leases) lives in the gateway's
# memory and the gateway keeps nothing on its own disk, so on SIGTERM it writes the plant to GitHub and reads it back at the next start. The snapshot
# is the message of one commit without a parent (the tree is master's, so nothing new is stored but the message), held by the single ref
# refs/factory/<instance>/plant-snapshot. Commits, trees and refs are what GitHub really stores for any repository; a ref that pointed at a blob
# would rest on a behaviour the API does not document. Admin verbs, called by the gateway only (its stop and its start), never through the verb route.
function Get-PlantSnapshotRefPath { "factory/$((Get-Cfg).name)/plant-snapshot" }

# plant-snapshot-save: { snapshot: <object>, tree, guard_epoch } -> the ref points at a new commit whose message is the snapshot's JSON.
# tree: the tree of an earlier save (the gateway keeps it: two reads fewer per save). guard_epoch (issue 94): a superseded gateway's final snapshot
# is not written over the snapshot of a later epoch (exit 11 snapshot-newer).
function Verb_plant_snapshot_save($a) {
    if (-not (Test-Has $a 'snapshot') -or $a.snapshot -isnot [System.Collections.IDictionary]) { Reject 2 'usage' 'snapshot is an object' }
    $json = $a.snapshot | ConvertTo-Json -Depth 12 -Compress
    if ($json.Length -gt 900000) { Reject 2 'too-large' "the snapshot is $($json.Length) characters; at most 900000" }
    $c = Get-Cfg; $repo = Get-RepoPath
    if (Test-Has $a 'guard_epoch') {
        $cur = Verb_plant_snapshot_load @{}
        if ($cur.found -and $cur.snapshot -is [System.Collections.IDictionary] -and $cur.snapshot.Contains('epoch') -and [long]$cur.snapshot.epoch -gt [long]$a.guard_epoch) {
            Reject 11 'snapshot-newer' "the snapshot ref holds epoch $($cur.snapshot.epoch), later than $($a.guard_epoch)"
        }
    }
    $tree = if ((Test-Has $a 'tree') -and "$($a.tree)" -match '^[0-9a-f]{7,64}$') { "$($a.tree)" } else {
        $head = (Invoke-Gh -Path "$repo/git/ref/heads/$($c.defaultBranch)").object.sha
        (Invoke-Gh -Path "$repo/git/commits/$head").tree.sha
    }
    $commit = (Invoke-Gh -Method POST -Path "$repo/git/commits" -Body @{ message = $json; tree = $tree; parents = @() }).sha
    $path = Get-PlantSnapshotRefPath
    $r = Invoke-Gh -Method PATCH -Path "$repo/git/refs/$path" -Body @{ sha = $commit; force = $true } -Allow 404, 422
    if ($r -is [hashtable] -and $r.ContainsKey('status')) {
        # no ref yet (GitHub answers 422 "Reference does not exist" to an update of a missing ref): create it
        $n = Invoke-Gh -Method POST -Path "$repo/git/refs" -Body @{ ref = "refs/$path"; sha = $commit } -Allow 422
        if ($n -is [hashtable] -and $n.ContainsKey('status')) { Reject 20 'github-error' 'GitHub refused the plant snapshot ref' @{ detail = "$($n.body.message)"; retryAfterSeconds = 30 } }
    }
    return @{ ok = $true; ref = "refs/$path"; commit = $commit; length = $json.Length; tree = $tree }
}

# plant-snapshot-load: the snapshot the ref holds, or found false when there is none. A message that is not JSON is found true with snapshot null.
function Verb_plant_snapshot_load($a) {
    $repo = Get-RepoPath; $path = Get-PlantSnapshotRefPath
    $r = Invoke-Gh -Path "$repo/git/ref/$path" -Allow 404
    if ($r -is [hashtable] -and $r.ContainsKey('status')) { return @{ ok = $true; found = $false } }
    $sha = "$($r.object.sha)"
    if ($sha -notmatch '^[0-9a-f]{7,64}$') { return @{ ok = $true; found = $false } }
    $msg = "$((Invoke-Gh -Path "$repo/git/commits/$sha").message)"
    $snap = $null; try { $snap = $msg | ConvertFrom-Json -AsHashtable -DateKind String } catch { }
    return @{ ok = $true; found = $true; commit = $sha; snapshot = $snap }
}

# plant-snapshot-await: { epoch, wait_seconds, until } (issue 94). A starting gateway that superseded a live one waits for that one's final snapshot
# (epoch = the predecessor's, final true) for at most wait_seconds, reading the ref conditionally every 3 s (an unchanged ref answers 304). It
# stops early once until (the predecessor's lease) has passed by 10 s: that process is gone and writes nothing more. Answers the final snapshot,
# else the latest one of that epoch (final false), else found false.
function Verb_plant_snapshot_await($a) {
    $epoch = [long](Need $a 'epoch')
    $wait = if (Test-Has $a 'wait_seconds') { [Math]::Clamp([double]$a.wait_seconds, 0, 600) } else { 90 }
    $until = if (Test-Has $a 'until') { ConvertFrom-Iso $a.until } else { $null }
    $repo = Get-RepoPath; $path = Get-PlantSnapshotRefPath
    $start = Get-UtcNow; $deadline = $start.AddSeconds($wait); $etag = ''; $lastSha = ''; $latest = $null; $reads = 0
    while ($true) {
        $hdr = @{}; if ($etag) { $hdr['If-None-Match'] = $etag }
        $r = Invoke-GhRaw -Path "$repo/git/ref/$path" -Headers $hdr; $reads++
        if ($r.status -ge 200 -and $r.status -lt 300) {
            $etag = Get-LeaderHeader $r.headers 'etag'
            $sha = "$($r.body.object.sha)"
            if ($sha -match '^[0-9a-f]{7,64}$' -and $sha -ne $lastSha) {
                $lastSha = $sha
                $snap = $null; try { $snap = "$((Invoke-Gh -Path "$repo/git/commits/$sha").message)" | ConvertFrom-Json -AsHashtable -DateKind String } catch { }
                if ($snap -is [System.Collections.IDictionary] -and $snap.Contains('epoch') -and [long]$snap.epoch -eq $epoch) {
                    $latest = $snap
                    if ($snap.Contains('final') -and $snap.final -eq $true) { return @{ ok = $true; found = $true; final = $true; snapshot = $snap; waitedSeconds = [int]((Get-UtcNow) - $start).TotalSeconds; reads = $reads } }
                }
            }
        }
        $now = Get-UtcNow
        if ($now -ge $deadline -or ($until -and $now -gt $until.AddSeconds(10))) { break }
        Start-Sleep -Seconds 3
    }
    return @{ ok = $true; found = ($null -ne $latest); final = $false; snapshot = $latest; waitedSeconds = [int]((Get-UtcNow) - $start).TotalSeconds; reads = $reads }
}

# plant-snapshot-clear: removes the ref once the start has read it (a snapshot is restored once). No ref is no error.
function Verb_plant_snapshot_clear($a) {
    $r = Invoke-Gh -Method DELETE -Path "$(Get-RepoPath)/git/refs/$(Get-PlantSnapshotRefPath)" -Allow 404, 422
    return @{ ok = $true; removed = -not ($r -is [hashtable] -and $r.ContainsKey('status')) }
}

# Session transcripts of runtime workers (gateway/Transcripts.cs): the same storage as the plant snapshot, one parentless commit per save whose
# message is the transcript, under refs/factory/<instance>/transcripts/<yyyymmdd>/<name>. The gateway encrypts the message (AES-GCM, a key derived
# from its token secret) before it reaches this script: the repository is public, so what is stored here is ciphertext only.
function Get-TranscriptRefRoot { "factory/$((Get-Cfg).name)/transcripts" }
function Test-TranscriptName([string]$Name) { $Name -match '^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$' }
function Get-TranscriptRefs {
    $r = Invoke-Gh -Path "$(Get-RepoPath)/git/matching-refs/$(Get-TranscriptRefRoot)/" -Allow 404
    if ($r -is [hashtable] -and $r.ContainsKey('status')) { return @() }
    return @(@($r) | ForEach-Object { "$($_.ref)" } | Where-Object { $_ -match '^refs/factory/[^/]+/transcripts/\d{8}/[A-Za-z0-9._-]+$' })
}

# transcript-save: { name, day (yyyymmdd), data (fwt1:<base64>), tree } -> the ref of that session points at a new commit with data as its message
function Verb_transcript_save($a) {
    $name = "$(Need $a 'name')"; $day = "$(Need $a 'day')"; $data = "$(Need $a 'data')"
    if (-not (Test-TranscriptName $name) -or $day -notmatch '^\d{8}$') { Reject 2 'usage' 'name is a session name, day is yyyymmdd' }
    if ($data -notmatch '^fwt1:[A-Za-z0-9+/=]+$') { Reject 2 'usage' 'data is the encrypted transcript (fwt1:<base64>)' }
    if ($data.Length -gt 900000) { Reject 2 'too-large' "the transcript is $($data.Length) characters; at most 900000" }
    $c = Get-Cfg; $repo = Get-RepoPath
    $tree = if ((Test-Has $a 'tree') -and "$($a.tree)" -match '^[0-9a-f]{7,64}$') { "$($a.tree)" } else {
        $head = (Invoke-Gh -Path "$repo/git/ref/heads/$($c.defaultBranch)").object.sha
        (Invoke-Gh -Path "$repo/git/commits/$head").tree.sha
    }
    $commit = (Invoke-Gh -Method POST -Path "$repo/git/commits" -Body @{ message = $data; tree = $tree; parents = @() }).sha
    $path = "$(Get-TranscriptRefRoot)/$day/$name"
    $r = Invoke-Gh -Method PATCH -Path "$repo/git/refs/$path" -Body @{ sha = $commit; force = $true } -Allow 404, 422
    if ($r -is [hashtable] -and $r.ContainsKey('status')) {
        $n = Invoke-Gh -Method POST -Path "$repo/git/refs" -Body @{ ref = "refs/$path"; sha = $commit } -Allow 422
        if ($n -is [hashtable] -and $n.ContainsKey('status')) { Reject 20 'github-error' 'GitHub refused the transcript ref' @{ detail = "$($n.body.message)"; retryAfterSeconds = 30 } }
    }
    return @{ ok = $true; ref = "refs/$path"; commit = $commit; tree = $tree }
}

# transcript-load: { name } -> the newest stored transcript of that session (found false when there is none)
function Verb_transcript_load($a) {
    $name = "$(Need $a 'name')"
    if (-not (Test-TranscriptName $name)) { Reject 2 'usage' 'name is a session name' }
    $hit = @(Get-TranscriptRefs | Where-Object { $_.EndsWith("/$name", [StringComparison]::Ordinal) } | Sort-Object -Descending) | Select-Object -First 1
    if (-not $hit) { return @{ ok = $true; found = $false } }
    $repo = Get-RepoPath
    $r = Invoke-Gh -Path "$repo/git/ref/$($hit.Substring(5))" -Allow 404
    if ($r -is [hashtable] -and $r.ContainsKey('status')) { return @{ ok = $true; found = $false } }
    $sha = "$($r.object.sha)"
    if ($sha -notmatch '^[0-9a-f]{7,64}$') { return @{ ok = $true; found = $false } }
    return @{ ok = $true; found = $true; data = "$((Invoke-Gh -Path "$repo/git/commits/$sha").message)" }
}

# transcript-prune: { days } -> removes the transcript refs of the days before the last `days` (retention)
function Verb_transcript_prune($a) {
    $days = if (Test-Has $a 'days') { [Math]::Clamp([int]$a.days, 1, 365) } else { 14 }
    $cut = (Get-UtcNow).AddDays(-$days).ToString('yyyyMMdd'); $repo = Get-RepoPath; $removed = 0
    foreach ($ref in Get-TranscriptRefs) {
        if ($ref -match '/transcripts/(\d{8})/[^/]+$' -and $Matches[1] -lt $cut) {
            $d = Invoke-Gh -Method DELETE -Path "$repo/git/$ref" -Allow 404, 422
            if (-not ($d -is [hashtable] -and $d.ContainsKey('status'))) { $removed++ }
        }
    }
    return @{ ok = $true; removed = $removed }
}
