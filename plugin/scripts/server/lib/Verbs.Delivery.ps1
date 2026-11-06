# Delivery: Octopus reads (GET only), release-candidate match, and settle (the post-merge state machine, no model needed).

# Issue 114: re-arms the settle step of an item that has a merge record but no wait and is not done: an older gateway erased the wait when it parked
# the item master-broken, master-green-timeout or late (262 and 280 were left closed in Functional Testing, 272 and 276 were validated again).
# The wait is set from where the record says the item was; such an item is settled again and never handed to a worker. Works on the state in
# hand, the caller saves it. Returns the wait it set, or '' (no merge record, a wait already set, or done; -Force re-arms a done one too).
function Restore-Settle($s, [switch]$Force) {
    if (-not (Test-Has $s 'merge') -or -not (Test-Has $s.merge 'sha') -or -not $s.merge.sha) { return '' }
    $m = $s.merge
    if ((Test-Has $m 'awaiting') -and $m.awaiting) { return '' }
    if ((Test-Has $m 'done_at') -and $m.done_at) { if (-not $Force) { return '' }; $m.Remove('done_at') }
    $t = (Get-Cfg).timing; $now = Get-UtcNow
    if ((Test-Has $m 'release') -and $m.release) { $m.awaiting = 'release-queue' }
    elseif ((Test-Has $m 'green_at') -and $m.green_at) { $m.awaiting = 'release-candidate'; $m.rc_deadline = ConvertTo-Iso ($now.AddMinutes($t.releaseCandidateTimeoutMinutes)) }
    elseif ((Get-Park $s) -and "$($s.park.reason)" -eq 'master-green-timeout') { $m.awaiting = 'master-timeout' }
    else { $m.awaiting = 'master-broken' }   # polled: it goes on as soon as master is green for it (Find-GreenMaster), with no re-run
    $m.rearmed_at = ConvertTo-Iso $now
    return "$($m.awaiting)"
}

