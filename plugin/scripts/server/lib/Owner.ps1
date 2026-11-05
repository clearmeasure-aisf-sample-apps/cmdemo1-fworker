# Owner sessions: one worker session owns a work item from its first assignment to the merge. The claim is per item and spans columns; a bot job
# (the inspector) holds a separate lease on the same item and never the owner's claim. Design: docs/design/plant-manager.md sections 2, 4 and 7.

function Test-OwnerMode { $c = Get-Cfg; (Test-Has $c 'ownerSessions') -and [bool]$c.ownerSessions }
function Test-OwnedClaim($s) { (Test-Has $s 'claim') -and (Test-Has $s.claim 'owner') -and [bool]$s.claim.owner }

# The owner finishes a stage and keeps the item: the claim moves to the new column with a fresh lease; reviews of the new column start empty.
# Flow metrics (W0.8, delivery-speed.md 13): the state's `flow` is one small entry per stage the item entered, oldest first: @{ c = column; at = ISO }.
# Only the last FlowKeep entries are kept (about 3 rounds of the board's 5 columns), so it stays small inside the state budget; Limit-StateBudget
# trims it oldest-first as well when the whole state is over the budget. An entry for the column the item is already in is not added again
# (a claim taken again in the same stage is not a new stage). The `metrics` verb turns the entries into the time spent per stage.
$script:FlowKeep = 15
function Add-FlowStamp($s, [string]$Column) {
    if (-not $Column) { return }
    $prev = @(if (Test-Has $s 'flow') { $s.flow })
    if ($prev.Count -gt 0 -and "$($prev[-1].c)" -eq $Column) { return }
    $s.flow = @($prev + @(@{ c = $Column; at = (ConvertTo-Iso (Get-UtcNow)) }) | Select-Object -Last $script:FlowKeep)
}

function Move-OwnerTo([int]$n, $s, [string]$Column) {
    $t = (Get-Cfg).timing
    Add-FlowStamp $s $Column
    $s.claim.column = $Column; $s.claim.heartbeat = ConvertTo-Iso (Get-UtcNow); $s.claim.lease_until = ConvertTo-Iso ((Get-UtcNow).AddMinutes($t.leaseMinutes))
    if (-not (Test-Has $s 'dispatched_at')) { $s.dispatched_at = @{} }
    $s.dispatched_at[$Column] = ConvertTo-Iso (Get-UtcNow)
    if (Test-Has $s 'reviews') { $s.reviews.Remove($Column) }
    # a stage finished is progress: the run of session take-backs (issue 91) starts again, and a take-back mark of an earlier session goes
    foreach ($k in 'session_launches', 'taken_back') { if (Test-Has $s.claim $k) { $s.claim.Remove($k) } }
}

# ---- revoked worker names (gateway/Auth.cs). The gateway keeps one generation per name and nothing on its own disk, so the generation lives in
# GitHub as a ref beside the claim refs: refs/factory/<instance>/revoked/<name>/<generation>. Admin verbs, called by the gateway only. ----
function Get-RevokedRefPath { "factory/$((Get-Cfg).name)/revoked" }

# Every ref under a prefix of the revoked tree, all pages: the read goes on until a page comes back empty, so neither a page smaller than the one
# asked for nor any number of refs ends it early (a revoked name that is not read would be let in again after a restart).
function Get-RevokedRefs([string]$Sub = '') {
    # GitHub's matching-refs may ignore the page parameter and answer the same refs again: the loop ends on an empty page, on a page that
    # brings nothing new, and after 200 pages at the latest. Without that a gateway never finishes starting.
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal); $out = @()
    for ($page = 1; $page -le 200; $page++) {
        $rows = @(Invoke-Gh -Path "$(Get-RepoPath)/git/matching-refs/$(Get-RevokedRefPath)/$Sub`?per_page=100&page=$page")
        if ($rows.Count -eq 0) { break }
        $new = 0
        foreach ($r in $rows) { if ($seen.Add("$($r.ref)")) { $out += "$($r.ref)"; $new++ } }
        if ($new -eq 0) { break }
    }
    return , @($out)
}

# revocations: every revoked name with its highest generation; read once when the gateway starts.
function Verb_revocations($a) {
    $out = @{}; $prefix = "refs/$(Get-RevokedRefPath)/"
    foreach ($ref in (Get-RevokedRefs)) {
        if ($ref.StartsWith($prefix, [StringComparison]::Ordinal) -and $ref.Substring($prefix.Length) -match '^([A-Za-z0-9_-]{1,40})/(\d{1,9})$') {
            $n = $Matches[1]; $g = [int]$Matches[2]
            if (-not $out.ContainsKey($n) -or $out[$n] -lt $g) { $out[$n] = $g }
        }
    }
    return @{ ok = $true; revoked = $out }
}

# revoke-worker: records generation G for one name (idempotent) and keeps one ref per name: the new generation is written, read back, and only
# then are the name's lower generations removed. The reader takes the highest, so a removal that fails costs nothing but a leftover ref (the
# next revoke of the name removes it); a lower generation is never removed before the higher one is seen on GitHub.
function Verb_revoke_worker($a) {
    $name = "$(Need $a 'name')"; $gen = [int](Need $a 'generation')
    if ($name -notmatch '^[A-Za-z0-9_-]{1,40}$' -or $gen -lt 1) { Reject 2 'usage' 'name is a worker name and generation is 1 or more' }
    $c = Get-Cfg
    $head = (Invoke-Gh -Path "$(Get-RepoPath)/git/ref/heads/$($c.defaultBranch)").object.sha
    # only "already exists" is an acceptable 422
    $r = Invoke-Gh -Method POST -Path "$(Get-RepoPath)/git/refs" -Body @{ ref = "refs/$(Get-RevokedRefPath)/$name/$gen"; sha = $head } -Allow 422
    if ($r -is [hashtable] -and $r.ContainsKey('status') -and $r.status -eq 422 -and "$($r.body.message)" -notmatch '(?i)already exists') { Reject 20 'github-error' 'GitHub refused the revocation ref' @{ detail = "$($r.body.message)"; retryAfterSeconds = 30 } }
    $prefix = "refs/$(Get-RevokedRefPath)/$name/"; $gens = @()
    foreach ($ref in (Get-RevokedRefs "$name/")) { if ($ref.StartsWith($prefix, [StringComparison]::Ordinal) -and $ref.Substring($prefix.Length) -match '^\d{1,9}$') { $gens += [int]$ref.Substring($prefix.Length) } }
    if ($gens -notcontains $gen) { Reject 20 'not-recorded' "the revocation ref of generation $gen is not on GitHub after the write" @{ retryAfterSeconds = 15 } }
    $removed = @(); $left = @()
    foreach ($g in @($gens | Where-Object { $_ -lt $gen } | Sort-Object)) {
        try { Invoke-Gh -Method DELETE -Path "$(Get-RepoPath)/git/refs/$(Get-RevokedRefPath)/$name/$g" | Out-Null; $removed += $g }
        catch { $left += $g; [Console]::Error.WriteLine("revoke-worker: generation $g of '$name' was not removed: $($_.Exception.Message)") }
    }
    return @{ ok = $true; name = $name; generation = $gen; removed = @($removed); left = @($left) }
}
