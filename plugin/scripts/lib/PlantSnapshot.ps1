# Server side of the plant snapshot (gateway/PlantSnapshot.cs, enhancement 5). The plant (offers, assignments, owners, leases) lives in the gateway's
# memory and the gateway keeps nothing on its own disk, so on SIGTERM it writes the plant to GitHub and reads it back at the next start. The snapshot
# is the message of one commit without a parent (the tree is master's, so nothing new is stored but the message), held by the single ref
# refs/factory/<instance>/plant-snapshot. Commits, trees and refs are what GitHub really stores for any repository; a ref that pointed at a blob
# would rest on a behaviour the API does not document. Admin verbs, called by the gateway only (its stop and its start), never through the verb route.
function Get-PlantSnapshotRefPath { "factory/$((Get-Cfg).name)/plant-snapshot" }

# plant-snapshot-save: { snapshot: <object> } -> the ref points at a new commit whose message is the snapshot's JSON.
function Verb_plant_snapshot_save($a) {
    if (-not (Test-Has $a 'snapshot') -or $a.snapshot -isnot [System.Collections.IDictionary]) { Reject 2 'usage' 'snapshot is an object' }
    $json = $a.snapshot | ConvertTo-Json -Depth 12 -Compress
    if ($json.Length -gt 900000) { Reject 2 'too-large' "the snapshot is $($json.Length) characters; at most 900000" }
    $c = Get-Cfg; $repo = Get-RepoPath
    $head = (Invoke-Gh -Path "$repo/git/ref/heads/$($c.defaultBranch)").object.sha
    $tree = (Invoke-Gh -Path "$repo/git/commits/$head").tree.sha
    $commit = (Invoke-Gh -Method POST -Path "$repo/git/commits" -Body @{ message = $json; tree = $tree; parents = @() }).sha
    $path = Get-PlantSnapshotRefPath
    $r = Invoke-Gh -Method PATCH -Path "$repo/git/refs/$path" -Body @{ sha = $commit; force = $true } -Allow 404, 422
    if ($r -is [hashtable] -and $r.ContainsKey('status')) {
        # no ref yet (GitHub answers 422 "Reference does not exist" to an update of a missing ref): create it
        $n = Invoke-Gh -Method POST -Path "$repo/git/refs" -Body @{ ref = "refs/$path"; sha = $commit } -Allow 422
        if ($n -is [hashtable] -and $n.ContainsKey('status')) { Reject 20 'github-error' 'GitHub refused the plant snapshot ref' @{ detail = "$($n.body.message)"; retryAfterSeconds = 30 } }
    }
    return @{ ok = $true; ref = "refs/$path"; commit = $commit; length = $json.Length }
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

# plant-snapshot-clear: removes the ref once the start has read it (a snapshot is restored once). No ref is no error.
function Verb_plant_snapshot_clear($a) {
    $r = Invoke-Gh -Method DELETE -Path "$(Get-RepoPath)/git/refs/$(Get-PlantSnapshotRefPath)" -Allow 404, 422
    return @{ ok = $true; removed = -not ($r -is [hashtable] -and $r.ContainsKey('status')) }
}
