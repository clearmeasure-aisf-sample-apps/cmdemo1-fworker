# Session transcripts of runtime workers (gateway/Transcripts.cs): the ref store that the database's transcripts fall back to (RuleScriptTranscriptBackend).
# One parentless commit per save (the tree is master's, so nothing new is stored but the message) whose message is the transcript, under
# refs/factory/<instance>/transcripts/<yyyymmdd>/<name>. The gateway encrypts the message (AES-GCM, a key derived from its token secret) before it
# reaches this script: the repository is public, so what is stored here is ciphertext only.
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
