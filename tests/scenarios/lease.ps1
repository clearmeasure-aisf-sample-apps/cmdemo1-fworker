# Owner leases kept by the plant (gateway/Lease.cs, docs/design/plant-manager.md 6b): the client's own acceptance, renewal by verb and by
# heartbeat, the item.check of an owner that went quiet, and the take-back. The scenarios join the manager suite (messaging on, test seams) and run
# on a second gateway each (Msg-OnGateway) that reads the suite's fake GitHub but does not sweep on its own: offers come from the plan seam, and
# its short schedule.* times are its own. The items of the renewal scenario are pinned to a session that never connects, so the suite's own gateway
# offers them to nobody; the take-back scenario's item is not pinned, so that it can be offered again.

function Ls-State([int]$n) {
    $sc = @((Get-FakeIssue $n).comments | Where-Object { $_.body -like '<!-- factory-state:*' })
    if ($sc.Count -eq 0) { return @{} }
    $mm = [regex]::Match($sc[0].body, '(?s)```json\s*(.*?)\s*```'); return ($mm.Groups[1].Value | ConvertFrom-Json -AsHashtable -DateKind String)
}
function Ls-Lease([int]$n) { $s = Ls-State $n; if ($s.ContainsKey('claim')) { return "$($s.claim.lease_until)" }; return '' }
function Ls-Taken($s) { if ($s.ContainsKey('claim') -and $s.claim.ContainsKey('taken_back')) { return "$($s.claim['taken_back'])" }; return '' }
function Ls-Status { Msg-Admin '/api/v1/status' }
function Ls-Owner([int]$n) { $o = (Ls-Status).plant.owners; if ($o -and $o.ContainsKey("$n")) { return "$($o["$n"])" }; return '' }
function Ls-Journal([int]$n, [string]$Event) { , @((Get-FakeIssue $n).comments | Where-Object { $_.body -match "<!-- plant event=$Event " }) }
function Ls-Config([string]$File) { Msg-Config $File { param($c) $c.messaging.allowedWorkers = @('alice', 'bob', 'carol') } }
# the second gateway: the fake GitHub, this suite's config with the three names, and the schedule times of the scenario
function Ls-Env([string]$File, [hashtable]$Times) { Msg-Merge @{ FACTORY_CONFIG = (Ls-Config $File); GITHUB_API_URL = "http://127.0.0.1:$($script:FakePort)" } $Times }
# its start-up sweep is over (it reads the whole fake board once): after it, nothing sweeps unless a verb asks for it
function Ls-Swept { [void](Wait-Until -Seconds 60 -Message 'the start-up sweep' { $null -ne (Ls-Status).lastSweep }) }

$LeaseScenarios = [ordered]@{}

$LeaseScenarios['lease: the client accepts an owner offer itself (acceptedBy client); the session never starts, so after the lease the item is taken back and offered again in resume mode'] = {
    # not pinned: after the take-back the item must be offered to another session (the suite's clients do not accept by themselves)
    $n = New-Item-Fake 'Client accept, no start' 'x' @('factory worker')
    Msg-OnGateway (Ls-Env 'instance.lease-a.json' @{ FACTORY_TIMING_clientStartMinutes = '0.15'; FACTORY_TIMING_staleItemMinutes = '600' }) {
        param($g)
        Ls-Swept
        $c = Msg-Client 'alice' 'lease-a' '' @{ FACTORY_AUTO_ACCEPT = '1' } @('--capacity', '1')
        try {
            Check 'the client registers' (Msg-WaitEvent $c 'worker.registered' 30)
            [void](Msg-Post '/api/v1/_test/plan' @{ offers = @(@{ item = $n; kind = 'own-item'; column = 'Technical Design' }); pins = @{}; owned = @{}; keep = @() })
            Check 'the client prints the offer, then accepts it by itself: item.accepted, acceptedBy client' (Msg-WaitEvent $c 'item.accepted' 60) (Get-Content -Raw $c.out)
            $acc = Msg-First @(Msg-ClientEvents $c | Where-Object { $_.event -eq 'item.accepted' })
            Check 'the line names the item, the client and the lease' ($acc['item'] -eq $n -and $acc.acceptedBy -eq 'client' -and "$($acc.lease_until)" -ne '' -and "$($acc.next)" -match 'owner-session') ($acc | ConvertTo-Json -Compress)
            Check 'the plant owns the item for alice and records acceptedBy client' ((Ls-Owner $n) -eq 'alice' -and @((Ls-Status).lease.clientAccepted | Where-Object { $_.item -eq $n -and $_.acceptedBy -eq 'client' -and $_.worker -eq 'alice' }).Count -eq 1) ((Ls-Status).lease | ConvertTo-Json -Compress -Depth 5)
            Check 'the journal says the client accepted it' (Wait-Until -Seconds 30 { @((Ls-Journal $n 'assigned') | Where-Object { $_.body -match 'accepted by its client' }).Count -eq 1 })
        } finally { Mgr-Stop $c }
        # the session never calls a verb for the item: a worker without auto-accept is there to be offered it again
        $bob = Msg-Open 'name=bob&capacity=20&messaging=1' (Msg-Token 'bob'); Msg-Registered $bob
        try {
            Check 'after clientStartMinutes the lease is ended: taken_back in the state, lease-expired in the journal' (Wait-Until -Seconds 40 { $s = Ls-State $n; $s.ContainsKey('claim') -and "$(Ls-Taken $s)" -eq 'client-accepted-not-started' -and (Ls-Journal $n 'lease-expired').Count -eq 1 }) ((Ls-State $n) | ConvertTo-Json -Compress -Depth 6)
            Check 'the plant forgot the owner and counted the take-back' ((Ls-Owner $n) -eq '' -and (Ls-Status).lease.takenBack -ge 1) ((Ls-Status).lease | ConvertTo-Json -Compress -Depth 5)
            Msg-Read $bob 40 { param($e) @($e | Where-Object { $_.data.event -eq 'item.assigned' -and $_.data['item'] -eq $n }).Count -ge 1 }
            $re = Msg-First @(Msg-Of $bob 'item.assigned' | Where-Object { $_.data['item'] -eq $n })
            Check 'the next sweep offers it again, in resume mode' ($re -and $re.data.mode -eq 'resume') ($re | ConvertTo-Json -Compress -Depth 5)
            $r = Msg-Json (Msg-Verb 'claim-item' (Msg-ItemToken $n 'owner' 'alice') @{ item = $n; renew = $true })
            Check 'the old owner cannot renew the claim it lost' ($r.exit -eq 40 -and $r.code -eq 'claim-not-held') ($r | ConvertTo-Json -Compress)
        } finally { Msg-Close $bob }
    }
}

$LeaseScenarios['lease: a verb and a heartbeat that names the item renew its lease; a quiet owner gets item.check, and an item whose check nobody answers is taken back'] = {
    $n1 = New-Item-Fake 'Lease by verb, check answered' 'x' @('factory worker', 'worker:ghost-lease')
    $n2 = New-Item-Fake 'Lease by heartbeat, check unanswered' 'x' @('factory worker', 'worker:ghost-lease')
    Msg-OnGateway (Ls-Env 'instance.lease-b.json' @{ FACTORY_TIMING_leaseRenewMinutes = '0'; FACTORY_TIMING_staleItemMinutes = '0.25'; FACTORY_TIMING_staleConfirmMinutes = '0.4' }) {
        param($g)
        Ls-Swept
        $carol = Msg-Open 'name=carol&capacity=5&messaging=1' (Msg-Token 'carol'); Msg-Registered $carol
        try {
            [void](Msg-Post '/api/v1/_test/plan' @{ offers = @(@{ item = $n1; kind = 'own-item'; column = 'Technical Design' }, @{ item = $n2; kind = 'own-item'; column = 'Technical Design' }); pins = @{}; owned = @{}; keep = @() })
            Msg-Read $carol 20 { param($e) @($e | Where-Object { $_.data.event -eq 'item.assigned' }).Count -ge 2 }
            $tok = @{}
            $evs = Msg-Of $carol 'item.assigned'; foreach ($ev in $evs) { $d = $ev.data; $ik = "$($d['item'])"; $tok[$ik] = "$($d['token'])" }
            foreach ($i in $n1, $n2) { $r = Msg-Json (Msg-Verb 'accept' $tok["$i"] @{ item = $i }); Check "carol accepts #$i" ($r.exit -eq 0) ($r | ConvertTo-Json -Compress) }
            $l1 = Ls-Lease $n1; $l2 = Ls-Lease $n2
            Start-Sleep -Seconds 1.2
            [void](Msg-Verb 'get-item' $tok["$n1"] @{ item = $n1 })
            Check 'a verb for the item renews its lease (the gateway calls claim-item --renew)' (Wait-Until -Seconds 30 { (Ls-Lease $n1) -gt $l1 }) "$l1 -> $(Ls-Lease $n1)"
            Check 'the other item was not renewed by it' ((Ls-Lease $n2) -eq $l2)
            $p = Msg-Post '/api/v1/presence' @{ summary = "working on #$n2 (design)" } (Msg-Token 'carol')
            Check 'a heartbeat whose summary names the item renews its lease' ($p.status -eq 200 -and (Wait-Until -Seconds 30 { (Ls-Lease $n2) -gt $l2 })) "$l2 -> $(Ls-Lease $n2)"
            Check 'the renewals are counted' ((Ls-Status).lease.renewals -ge 2) ((Ls-Status).lease | ConvertTo-Json -Compress -Depth 5)
            # quiet from here: heartbeats go on with the same summary, no verb. Both items get one item.check.
            $checks = { $x = Msg-Of $carol 'item.check'; $x }
            $end = (Get-Date).AddSeconds(45)
            while ((Get-Date) -lt $end -and @((& $checks) | Where-Object { $_.data['item'] -in $n1, $n2 } | ForEach-Object { $_.data['item'] } | Select-Object -Unique).Count -lt 2) { [void](Msg-Post '/api/v1/presence' @{} (Msg-Token 'carol')); Msg-Read $carol 3 }
            $ck = @((& $checks) | Where-Object { $_.data['item'] -in $n1, $n2 })
            Check 'after staleItemMinutes without a verb and with the summary unchanged, each item gets item.check' (@($ck | ForEach-Object { $_.data['item'] } | Select-Object -Unique).Count -eq 2 -and "$($ck[0].data.reason)" -match 'claim-item') ($ck | ConvertTo-Json -Compress -Depth 5)
            # the owner answers for n1 with a verb; n2 stays silent
            [void](Msg-Verb 'claim-item' $tok["$n1"] @{ item = $n1; renew = $true })
            Check 'one item.check per quiet spell' (@((& $checks) | Where-Object { $_.data['item'] -eq $n1 }).Count -eq 1)
            Msg-Read $carol 40 { param($e) @($e | Where-Object { $_.data.event -eq 'item.taken_back' }).Count -ge 1 }
            $tb = Msg-Of $carol 'item.taken_back'
            Check 'the unanswered item is taken back; the answered one is not' ($tb.Count -eq 1 -and $tb[0].data['item'] -eq $n2) ($tb | ConvertTo-Json -Compress -Depth 5)
            Check 'its lease is ended in the state and the journal says why' (Wait-Until -Seconds 30 { "$(Ls-Taken (Ls-State $n2))" -eq 'no-answer-to-item-check' -and (Ls-Journal $n2 'lease-expired').Count -eq 1 }) ((Ls-State $n2) | ConvertTo-Json -Compress -Depth 6)
            Check 'the plant still names carol for the answered item only' ((Ls-Owner $n1) -eq 'carol' -and (Ls-Owner $n2) -eq '') ((Ls-Status).plant.owners | ConvertTo-Json -Compress)
        } finally { Msg-Close $carol }
    }
}
