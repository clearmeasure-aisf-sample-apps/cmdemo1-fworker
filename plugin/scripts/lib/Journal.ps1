# The plant manager's journal on the work item. One helper writes every dispatch-side comment: a visible line, a hidden marker, a dedup ledger
# in the item's state comment (so redelivered webhooks and repeated sweeps never post twice), redaction, a length cap, and a per-item limit.
# Best-effort: a failed comment never blocks a dispatch. Design: docs/design/plant-manager.md section 8.

$script:JournalCap = 30
# Used only when config/instance.json has no secrets catalogue (worker-messaging.md 5.4); the catalogue is the source of truth.
$script:FallbackSecretPatterns = @('-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?(-----END [A-Z ]*PRIVATE KEY-----|$)', 'gh[pousr]_[A-Za-z0-9]{20,}', 'xox[baprs]-[A-Za-z0-9-]{10,}', 'AKIA[0-9A-Z]{16}',
    '(?i)(bearer|token|secret|password|api[_-]?key)(\s*[:=]\s*|\s+)[A-Za-z0-9._~+/=-]{12,}', '\b(w1|m1|i1|q1|o1)\.[A-Za-z0-9_.-]{20,}')

function Protect-JournalText([string]$Text) {
    if (-not $Text) { return '' }
    $t = $Text
    $secrets = try { (Get-Cfg).secrets } catch { $null }
    $list = if ($secrets) { @($secrets.patterns) + @($secrets.redactOnly) } else { @() }
    $list = @($list | Where-Object { $_ })
    if (-not $list) { $list = $script:FallbackSecretPatterns }
    foreach ($p in $list) { $t = [regex]::Replace($t, $p, '[redacted]') }
    return $t
}

function Get-JournalText([string]$Event, [hashtable]$F) {
    $col = if ($F.ContainsKey('column')) { "**$($F.column)**" } else { '' }
    $n = $F.item
    switch ($Event) {
        'matched' { return "matched $(if ($F.ContainsKey('trigger')) { $F.trigger } else { 'a trigger' }) to work item #$n in $col ($($F.reason)). Needs $(if ($F.ContainsKey('role') -and $F.role) { "a $($F.role) bot" } else { 'an owner session' })." }
        'offered' { return "offered to **$($F.worker)** ($($F.type)); waiting up to $($F.ackSeconds) s for it to accept." }
        'assigned' {
            if ($F.ContainsKey('role') -and $F.role) { return "**$($F.worker)** accepted the $($F.role) job for PR #$($F.pr)$(if ($F.ContainsKey('url') -and $F.url) { " ([session]($($F.url)))" })." }
            $m = if ($F.ContainsKey('model') -and $F.model) { " Model: $($F.model) ($($F.effort)), tier $($F.tier)." } else { '' }
            $link = if ($F.ContainsKey('url') -and $F.url) { " [session]($($F.url))" } else { '' }
            $by = if ($F.ContainsKey('by') -and $F.by -eq 'client') { ' (accepted by its client; the session starts on it next)' } else { '' }
            return "**$($F.worker)**$link accepted$($by): it owns this item from $col until it is merged (mode $($F.mode), attempt $($F.attempt)).$m Lease until $($F.lease)."
        }
        'routed' { return "told the owner (**$($F.worker)**): $($F.what)." }
        'reassigned' { return "$($F.reason); offering to the next worker." }
        'tier' { return "model tier raised to **$($F.name)** ($($F.reasons))." }
        'waiting' { return "waiting: $($F.reason)." }
        'pin-fallback' { return "the pinned worker **$($F.pin)** could not take the item for $($F.minutes) min ($($F.code)): the pin is set aside and it is offered to **$($F.worker)**. A new worker label pins it again." }
        'skipped' { return "not assigned: $($F.reason)." }
        'qc-offered' { return "a QC environment is offered for PR #$($F.pr) at head $($F.sha7); nothing runs until a person presses Start." }
        'qc-requested' { return "a person asked for a QC environment for PR #$($F.pr) at head $($F.sha7) (attempt $($F.attempt))." }
        'qc-assigned' { return "**$($F.worker)** accepted the QC environment job for PR #$($F.pr) (attempt $($F.attempt))." }
        'qc-ready' { return "QC environment for PR #$($F.pr) is ready on **$($F.worker)** (attempt $($F.attempt))$(if ($F.ContainsKey('url') -and $F.url) { ": $($F.url)" } else { '' }). Reachable only from networks that can reach that computer." }
        'qc-failed' { $fence = '```'; return "QC environment for PR #$($F.pr) failed (**$($F.code)**).`n$($fence)text`n$($F.reason)`n$fence" }
        'qc-reset' { return "QC environment for PR #$($F.pr) was reset by a person (attempt $($F.attempt))." }
        'qc-torn-down' { return "QC environment for PR #$($F.pr) torn down ($($F.reason))." }
        'qc-waiting' { return "QC environment for PR #$($F.pr) is waiting: $($F.reason)." }
        'question-escalated' { return "a question from **$($F.worker)** has waited $($F.minutes) min for the supervisor (supervisor: $(if ("$($F.supervisor)" -eq 'connected') { 'connected' } else { 'absent' })); a person is asked to look at the dashboard." }
        'merge-waiting' { return "PR #$($F.pr) has a PASS verdict and is not merged yet: $($F.reason) (``$($F.code)``). The gateway merges it by itself when that clears; no session has to call anything." }
        'base-updated' {
            if ($F.ContainsKey('conflict') -and $F.conflict) { return "the pull request has a merge conflict with master (``$($F.sha7)``); told the owner (**$($F.worker)**) to merge master into the branch and resolve it: $($F.behind) commit(s) behind$(if ($F.ContainsKey('overlap') -and $F.overlap) { ", files changed on both sides: $($F.overlap)" })." }
            return "master moved to ``$($F.sha7)``$(if ($F.ContainsKey('prs') -and $F.prs) { " (merged: $($F.prs))" }); told the owner (**$($F.worker)**) to merge master into the branch now: $($F.behind) commit(s) behind$(if ($F.ContainsKey('overlap') -and $F.overlap) { ", files changed on both sides: $($F.overlap)" })."
        }
        'branch-updated' { return "the gateway merged master into the branch of PR #$($F.pr) (GitHub's update branch: no conflict, the owner had not updated it within $($F.seconds) s)." }
        'lease-expired' { return "the gateway ended the lease of **$($F.worker)** ($($F.reason)); the item is offered again in resume mode." }
        'unparked' { if ($F.ContainsKey('closed') -and $F.closed) { return "park ``$($F.code)`` removed by $($F.by): $($F.reason). The item is closed: nothing is offered." }; return "park ``$($F.code)`` released early by $($F.by): $($F.reason). The next sweep takes it up again in $col." }
        'released' { return "ownership released by **$($F.worker)** ($($F.reason)); the next trigger assigns it again." }
        default { return "$Event" }
    }
}

# Posts one journal comment. $St is the caller's state holder: the ledger is updated in it, and saved here only with -Save.
function Add-Journal([int]$Item, $St, [string]$Event, [hashtable]$Fields = @{}, [string]$Key = '', [switch]$Save) {
    try {
        $own = $false
        if ($null -eq $St) { $St = Get-State $Item; $own = $true }
        $s = $St.state
        if (-not (Test-Has $s 'journal')) { $s.journal = @{ keys = @(); count = 0 } }
        $k = "$Event|$Key"
        if (@($s.journal.keys) -contains $k) { return $false }
        # qc-ready, qc-failed and question-escalated may take a few slots beyond the cap: they are the lines a person looks for
        $extra = $Event -in 'qc-ready', 'qc-failed', 'question-escalated'
        if ([int]$s.journal.count -ge $script:JournalCap + $(if ($extra) { 4 } else { 1 })) { return $false }
        $Fields.item = $Item
        $text = if ([int]$s.journal.count -eq $script:JournalCap -and -not $extra) { "journal full ($script:JournalCap entries); later events are not posted here, see the gateway log." } else { Get-JournalText $Event $Fields }
        $text = Protect-JournalText $text
        if ($text.Length -gt 1500) { $text = $text.Substring(0, 1500) + ' [truncated]' }
        $name = (Get-Cfg).name
        $body = "🏭 Plant manager: $text`n`n<!-- plant event=$Event key=$Key -->"
        Invoke-Gh -Method POST -Path "$(Get-RepoPath)/issues/$Item/comments" -Body @{ body = $body } | Out-Null
        $s.journal.keys = @(@($s.journal.keys) + $k | Select-Object -Last 200)
        $s.journal.count = [int]$s.journal.count + 1
        if ($Save -or $own) { Save-State $Item $St }
        return $true
    } catch { [Console]::Error.WriteLine("journal $Event for #$Item failed: $($_.Exception.Message)"); return $false }
}

# For a caller that holds no item lock (the sweep): the ledger entries Add-Journal put into the caller's holder are written under the item lock into
# a state read there, never as a save of the holder itself. The holder was read before whatever a verb recorded meanwhile (an approval, a claim):
# saving it would undo that (seen on slow runners: an inspector's approval lost to the sweep that was posting the QC offer).
function Save-JournalKeys([int]$Item, [string[]]$Keys) {
    Use-ItemLock $Item {
        $f = Get-State $Item; $fs = $f.state; $added = 0
        if (-not (Test-Has $fs 'journal')) { $fs.journal = @{ keys = @(); count = 0 } }
        foreach ($k in $Keys) { if (@($fs.journal.keys) -notcontains $k) { $fs.journal.keys = @(@($fs.journal.keys) + $k | Select-Object -Last 200); $fs.journal.count = [int]$fs.journal.count + 1; $added++ } }
        if ($added -gt 0) { Save-State $Item $f }
    }
}
function Get-JournalKeys($State) { if (Test-Has $State 'journal') { return @($State.journal.keys) }; return @() }

# Admin verb: the gateway journals the decisions it makes itself (matched, offered, reassigned, waiting, skipped).
function Verb_journal($a) {
    $n = Get-ItemNumber $a; $ev = "$(Need $a 'event')"
    if ($ev -notin 'matched', 'offered', 'assigned', 'routed', 'reassigned', 'tier', 'waiting', 'skipped', 'released', 'qc-offered', 'qc-requested', 'qc-assigned', 'qc-ready', 'qc-failed', 'qc-reset', 'qc-torn-down', 'qc-waiting', 'question-escalated', 'base-updated', 'branch-updated', 'pin-fallback') { Reject 2 'usage' "unknown journal event '$ev'" }
    $f = @{}; if (Test-Has $a 'fields') { foreach ($k in $a.fields.Keys) { $f[$k] = $a.fields[$k] } }
    # a 'waiting' note is rate-limited: at most one per 30 minutes per item
    if ($ev -eq 'waiting') {
        $st = Get-State $n; $last = if ((Test-Has $st.state 'journal') -and (Test-Has $st.state.journal 'waiting_at')) { ConvertFrom-Iso $st.state.journal.waiting_at } else { $null }
        if ($last -and ((Get-UtcNow) - $last).TotalMinutes -lt 30) { return @{ ok = $true; id = $n; posted = $false; reason = 'rate-limited' } }
        $key = [string](Get-UtcNow).Ticks
        $posted = Add-Journal $n $st 'waiting' $f $key
        if ($posted) { $st.state.journal.waiting_at = ConvertTo-Iso (Get-UtcNow); Save-State $n $st }
        return @{ ok = $true; id = $n; posted = $posted }
    }
    $key = if (Test-Has $a 'key') { "$($a.key)" } else { [string](Get-UtcNow).Ticks }
    $posted = Add-Journal $n $null $ev $f $key
    return @{ ok = $true; id = $n; posted = $posted }
}
