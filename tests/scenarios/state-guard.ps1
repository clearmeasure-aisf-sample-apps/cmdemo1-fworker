# The state comment's rev check and size budget (plugin/scripts/lib/Board.ps1 Save-State, Get-State; docs/design/delivery-speed.md 18.1.2 and 18.2, issue 291):
# a save is refused when the comment changed since it was read (the caller reads again and retries, or reports), and a state never goes over 48,000
# characters: promote_health, flow and lineage lose their oldest entries first and the trim is recorded. Runs in the owner suite; the rule script
# journal (ci-released: a read and a save of the state under the item lock) is called directly, on an item that is not factory work. The fake
# GitHub's control state-race makes the next single reads of a state comment find another process's write first.

function Sg-Release([int]$n, [string]$Key) { Pm-Server 'journal' @{ item = $n; event = 'ci-released'; key = $Key; fields = @{ release = $Key; reason = 'test' } } }
function Sg-StateComment([int]$n) { @((Get-FakeIssue $n).comments | Where-Object { "$($_.body)".StartsWith('<!-- factory-state:') })[0] }
# the item's state comment rewritten with $St (as another process, or an older gateway, would leave it)
function Sg-PutState([int]$n, $St) {
    $json = $St | ConvertTo-Json -Depth 12; $cm = Sg-StateComment $n
    $nb = "$($cm.body)" -replace '(?s)```json.*```', "``````json`n$json`n``````"
    Invoke-RestMethod "http://127.0.0.1:$($script:FakePort)/repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders/issues/comments/$($cm.id)" -Method PATCH -Headers @{ Authorization = "Bearer $env:GH_TOKEN" } -Body (@{ body = $nb } | ConvertTo-Json) -ContentType 'application/json' | Out-Null
}

$StateGuardScenarios = [ordered]@{}

$StateGuardScenarios['state guard: every save raises rev by one; a write that meets no change is as before'] = {
    $n = New-Item-Fake 'State rev' 'x' @()
    $r = Sg-Release $n 'a'; CheckExit 'first write' $r 0
    Check 'the state has rev 1' ((Pm-State $n).rev -eq 1) ((Pm-State $n) | ConvertTo-Json -Compress -Depth 5)
    $r = Sg-Release $n 'b'; CheckExit 'second write' $r 0
    $st = Pm-State $n
    Check 'rev 2 and both releases recorded' ($st.rev -eq 2 -and @($st.journal.ciReleased).Count -eq 2 -and $r.recorded -eq $true) ($st | ConvertTo-Json -Compress -Depth 5)
    $r = Sg-Release $n 'b'; CheckExit 'the same key again' $r 0
    Check 'a repeat writes nothing: rev stays 2' ((Pm-State $n).rev -eq 2 -and $r.recorded -eq $false) ((Pm-State $n).rev)
}

$StateGuardScenarios['state guard: a save that finds the comment changed since the read is refused; a retry on a fresh read succeeds; none is lost'] = {
    $n = New-Item-Fake 'State race' 'x' @()
    $r = Sg-Release $n 'a'; CheckExit 'first write' $r 0
    try {
        # one concurrent write: the first save is refused, the caller reads again and the second save goes through, on top of the other write
        Fake 'state-race' @{ times = 1 } | Out-Null
        $r = Sg-Release $n 'c'; CheckExit 'a write that meets one concurrent write' $r 0
        $st = Pm-State $n
        Check 'it was retried on the fresh read: both releases are in, rev is the other write plus this one' ($r.recorded -eq $true -and @($st.journal.ciReleased).Count -eq 2 -and $st.rev -eq 3) ($st | ConvertTo-Json -Compress -Depth 5)
        # a concurrent write at every attempt: the caller is told (exit 11, state-conflict) and nothing of its own was written
        Fake 'state-race' @{ times = 10 } | Out-Null
        $before = (Pm-State $n).rev
        $r = Sg-Release $n 'd'
        Fake 'state-race' @{ times = 0 } | Out-Null
        $st = Pm-State $n
        Check 'refused with exit 11 and the code state-conflict' ($r.exit -eq 11 -and "$($r.code)" -eq 'state-conflict') ($r | ConvertTo-Json -Compress)
        Check 'the other process''s state stands: no release d, three attempts raised rev by the three other writes only' (-not (@($st.journal.ciReleased) -match ' d$' ) -and @($st.journal.ciReleased).Count -eq 2 -and $st.rev -eq $before + 3) ($st | ConvertTo-Json -Compress -Depth 5)
        $r = Sg-Release $n 'd'; CheckExit 'once the other writes stop, the same call succeeds' $r 0
        Check 'and records the release' ($r.recorded -eq $true -and @((Pm-State $n).journal.ciReleased).Count -eq 3) ((Pm-State $n) | ConvertTo-Json -Compress -Depth 5)
    } finally { Fake 'state-race' @{ times = 0 } | Out-Null }
}

$StateGuardScenarios['state guard: a state over the 48,000-character budget is trimmed (promote_health, flow, lineage, oldest first), stays valid, and the trim is recorded'] = {
    $n = New-Item-Fake 'State budget' 'x' @()
    $r = Sg-Release $n 'a'; CheckExit 'a state exists' $r 0
    $pad = 'p' * 150
    $st = Pm-State $n
    $st.promote_health = @{ 'tdd|1' = @(foreach ($i in 0..199) { @{ at = '2026-01-01T00:{0:00}:00Z' -f ($i % 60); seq = $i; status = 'Healthy'; pad = $pad } }) }
    $st.flow = @(foreach ($i in 0..199) { @{ seq = $i; stage = 'queued'; pad = $pad } })
    $st.lineage = @(foreach ($i in 0..199) { @{ seq = $i; session = "s$i"; pad = $pad } })
    Sg-PutState $n $st
    $big = "$((Sg-StateComment $n).body)".Length
    Check 'the fixture is over the budget (about 100,000 characters)' ($big -gt 90000) "$big"
    $r = Sg-Release $n 'b'; CheckExit 'a write of that state' $r 0
    $cm = Sg-StateComment $n; $len = "$($cm.body)".Length
    Check 'the comment is within 48,000 characters' ($len -le 48000 -and $len -gt 30000) "$len"
    $s2 = Pm-State $n
    Check 'it is still valid state: the marker, the other keys, the new release' ("$($cm.body)".StartsWith('<!-- factory-state:') -and $s2.item -eq $n -and @($s2.journal.ciReleased).Count -eq 2 -and $s2.rev -eq 2) ($s2.Keys -join ',')
    $ph = @($s2.promote_health['tdd|1']); $fl = @($s2.flow); $li = @($s2.lineage)
    Check 'the newest entries of the three are kept, the oldest are gone' ($ph[-1].seq -eq 199 -and $fl[-1].seq -eq 199 -and $li[-1].seq -eq 199 -and $ph[0].seq -gt 0 -and $fl[0].seq -gt 0 -and $li[0].seq -gt 0) "$($ph[0].seq) $($fl[0].seq) $($li[0].seq)"
    Check 'the entries kept are the newest in order, none from the middle' (($ph.seq -join ',') -eq ((($ph[0].seq)..199) -join ',') -and ($fl.seq -join ',') -eq ((($fl[0].seq)..199) -join ',') -and ($li.seq -join ',') -eq ((($li[0].seq)..199) -join ',')) "$($ph.Count) $($fl.Count) $($li.Count)"
    Check 'no collection was emptied while another kept old entries (counts within one of each other)' ((@($ph.Count, $fl.Count, $li.Count) | Measure-Object -Maximum -Minimum | ForEach-Object { $_.Maximum - $_.Minimum }) -le 1) "$($ph.Count) $($fl.Count) $($li.Count)"
    $t = @($s2.journal.trims)
    Check 'the trim is recorded in the state: when, how big it was, what was dropped from each' ($t.Count -eq 1 -and $t[0].before -gt 90000 -and $t[0].dropped.promote_health -gt 0 -and $t[0].dropped.flow -gt 0 -and $t[0].dropped.lineage -gt 0 -and $t[0].at) ($s2.journal | ConvertTo-Json -Compress -Depth 6)
    # a state within the budget is not touched
    $r = Sg-Release $n 'c'; CheckExit 'a later write' $r 0
    $s3 = Pm-State $n
    Check 'under the budget nothing more is dropped and no second trim is recorded' (@($s3.promote_health['tdd|1']).Count -eq $ph.Count -and @($s3.journal.trims).Count -eq 1 -and $s3.rev -eq 3) "$(@($s3.promote_health['tdd|1']).Count) $($ph.Count) $(@($s3.journal.trims).Count)"
}

foreach ($k in $StateGuardScenarios.Keys) { $OwnerScenarios[$k] = $StateGuardScenarios[$k] }
