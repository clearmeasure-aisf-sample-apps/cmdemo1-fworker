# Owner sessions: one worker session owns a work item from its first assignment to the merge. The claim is per item and spans columns; a bot job
# (the inspector) holds a separate lease on the same item and never the owner's claim. Design: docs/design/plant-manager.md sections 2, 4 and 7.

function Test-OwnerMode { $c = Get-Cfg; (Test-Has $c 'ownerSessions') -and [bool]$c.ownerSessions }
function Test-OwnedClaim($s) { (Test-Has $s 'claim') -and (Test-Has $s.claim 'owner') -and [bool]$s.claim.owner }
function Test-BotLive($s) { if (-not (Test-Has $s 'bot')) { return $false }; $u = ConvertFrom-Iso $s.bot.lease_until; return ($u -and $u -gt (Get-UtcNow)) }

# The owner finishes a stage and keeps the item: the claim moves to the new column with a fresh lease; reviews of the new column start empty.
function Move-OwnerTo([int]$n, $s, [string]$Column) {
    $t = (Get-Cfg).timing
    $s.claim.column = $Column; $s.claim.heartbeat = ConvertTo-Iso (Get-UtcNow); $s.claim.lease_until = ConvertTo-Iso ((Get-UtcNow).AddMinutes($t.leaseMinutes))
    if (-not (Test-Has $s 'dispatched_at')) { $s.dispatched_at = @{} }
    $s.dispatched_at[$Column] = ConvertTo-Iso (Get-UtcNow)
    if (Test-Has $s 'reviews') { $s.reviews.Remove($Column) }
}

# accept: the answer to a directed assignment. Own-item: the per-item claim (every claim rule applies). Bot job: a separate bot lease.
function Verb_accept($a) {
    $n = Get-ItemNumber $a; $c = Get-Cfg; $t = $c.timing
    $assignment = "$(Need $a 'assignment')"; $holder = "$(Need $a 'holder')"
    $kind = if (Test-Has $a 'kind') { "$($a.kind)" } else { 'own-item' }
    if ($kind -eq 'qc-env') { return Invoke-QcAccept $n $a $assignment $holder }
    if ($kind -eq 'bot-job') {
        $role = if (Test-Has $a 'role') { "$($a.role)" } else { 'inspector' }
        $info = Get-ItemInfo $n; Assert-Open $info
        $st = Get-State $n; $s = $st.state
        if (-not (Test-Has $s 'verdict') -or -not $s.verdict.passed -or $s.verdict.processed) { Reject 10 'no-pass-verdict' 'no unprocessed PASS verdict is recorded for this item' }
        if ((Test-BotLive $s) -and $s.bot.assignment -ne $assignment) { Reject 40 'bot-claimed' "another $role job holds this item until $($s.bot.lease_until)" }
        $s.bot = @{ holder = $holder; assignment = $assignment; role = $role; started = ConvertTo-Iso (Get-UtcNow); lease_until = ConvertTo-Iso ((Get-UtcNow).AddMinutes($t.leaseMinutes)) }
        [void](Add-Journal $n $st 'assigned' @{ worker = $holder; role = $role; pr = $s.verdict.pr; url = $(if (Test-Has $a 'url') { "$($a.url)" } else { '' }) } "bot|$assignment")
        Save-State $n $st
        return @{ ok = $true; id = $n; accepted = $true; kind = 'bot-job'; role = $role; pr = $s.verdict.pr; lease_until = $s.bot.lease_until }
    }
    $claimArgs = @{ item = $n; holder = $holder; owner = $true; worker = $holder; assignment = $assignment }
    if (Test-Has $a 'column') { $claimArgs.column = $a.column }
    $r = Verb_claim_item $claimArgs
    if (-not (Test-Has $r 'already') -or -not $r.already) {
        $mt = Verb_model_tier @{ item = $n }
        [void](Add-Journal $n $null 'assigned' @{ worker = $holder; column = $r.column; mode = $r.mode; attempt = $r.launches; lease = $r.lease_until
            model = $(if (Test-Has $a 'model') { "$($a.model)" } else { '' }); effort = $(if (Test-Has $a 'effort') { "$($a.effort)" } else { '' }); tier = $mt.tier
            url = $(if (Test-Has $a 'url') { "$($a.url)" } else { '' }) } "owner|$assignment" -Save)
    }
    $r.kind = 'own-item'; $r.assignment = $assignment
    return $r
}

# release: the owner (or an admin) gives the item up. The next trigger assigns it again and a new owner resumes from the item state.
function Verb_release($a) {
    $n = Get-ItemNumber $a; $holder = "$(Need $a 'holder')"
    $reason = if (Test-Has $a 'reason') { "$($a.reason)" } else { 'released' }
    $st = Get-State $n; $s = $st.state
    if (-not (Test-Has $s 'claim')) { return @{ ok = $true; id = $n; released = $false; reason = 'no claim' } }
    if ($s.claim.holder -ne $holder -and -not ((Test-Has $a 'force') -and $a.force)) { Reject 40 'claim-not-held' "the claim is held by $($s.claim.holder)" }
    $s.Remove('claim'); [void](Add-Journal $n $st 'released' @{ worker = $holder; reason = $reason } "rel|$([string](Get-UtcNow).Ticks)")
    Save-State $n $st; Remove-ClaimRef $n
    return @{ ok = $true; id = $n; released = $true }
}

# my-items: everything one worker owns, so a restarted or compacted session can rebuild its picture.
function Verb_my_items($a) {
    $c = Get-Cfg; $name = "$(Need $a 'holder')"; $out = @()
    foreach ($it in (Get-BoardList | Where-Object { $_.open -and (Test-NameIn $c.label $_.labels) -and (Test-NameIn "$($_.column)" $c.columns.ai) })) {
        $s = (Get-State $it.number).state
        if ((Test-Has $s 'claim') -and ($s.claim.holder -eq $name -or ((Test-Has $s.claim 'worker') -and $s.claim.worker -eq $name))) {
            $out += @{ item = $it.number; column = $it.column; role = 'owner'; lease_until = $s.claim.lease_until; live = (Test-ClaimLive $s) }
        } elseif ((Test-Has $s 'bot') -and $s.bot.holder -eq $name) { $out += @{ item = $it.number; column = $it.column; role = $s.bot.role; lease_until = $s.bot.lease_until; live = (Test-BotLive $s) } }
    }
    return @{ ok = $true; holder = $name; items = $out }
}

# ---- revoked worker names (gateway/Auth.cs). The gateway keeps one generation per name and nothing on its own disk, so the generation lives in
# GitHub as a ref beside the claim refs: refs/factory/<instance>/revoked/<name>/<generation>. Admin verbs, called by the gateway only. ----
function Get-RevokedRefPath { "factory/$((Get-Cfg).name)/revoked" }

# revocations: every revoked name with its highest generation; read once when the gateway starts.
function Verb_revocations($a) {
    $out = @{}; $prefix = "refs/$(Get-RevokedRefPath)/"
    for ($page = 1; $page -le 50; $page++) {
        $rows = @(Invoke-Gh -Path "$(Get-RepoPath)/git/matching-refs/$(Get-RevokedRefPath)/?per_page=100&page=$page")
        foreach ($r in $rows) {
            if ("$($r.ref)".StartsWith($prefix, [StringComparison]::Ordinal) -and "$($r.ref)".Substring($prefix.Length) -match '^([A-Za-z0-9_-]{1,40})/(\d{1,9})$') {
                $n = $Matches[1]; $g = [int]$Matches[2]
                if (-not $out.ContainsKey($n) -or $out[$n] -lt $g) { $out[$n] = $g }
            }
        }
        if ($rows.Count -lt 100) { break }
    }
    return @{ ok = $true; revoked = $out }
}

# revoke-worker: records generation G for one name (idempotent).
function Verb_revoke_worker($a) {
    $name = "$(Need $a 'name')"; $gen = [int](Need $a 'generation')
    if ($name -notmatch '^[A-Za-z0-9_-]{1,40}$' -or $gen -lt 1) { Reject 2 'usage' 'name is a worker name and generation is 1 or more' }
    $c = Get-Cfg
    $head = (Invoke-Gh -Path "$(Get-RepoPath)/git/ref/heads/$($c.defaultBranch)").object.sha
    # older generations stay (the reader takes the highest); only "already exists" is an acceptable 422
    $r = Invoke-Gh -Method POST -Path "$(Get-RepoPath)/git/refs" -Body @{ ref = "refs/$(Get-RevokedRefPath)/$name/$gen"; sha = $head } -Allow 422
    if ($r -is [hashtable] -and $r.ContainsKey('status') -and $r.status -eq 422 -and "$($r.body.message)" -notmatch '(?i)already exists') { Reject 20 'github-error' 'GitHub refused the revocation ref' @{ detail = "$($r.body.message)"; retryAfterSeconds = 30 } }
    return @{ ok = $true; name = $name; generation = $gen }
}
