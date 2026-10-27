# The backlog keeper session (docs/design/backlog-keeper.md, gateway/Keeper.cs, plugin/scripts/lib/Verbs.Keeper.ps1): its credential and stream,
# the jobs a park or a thin backlog sends it, and its verbs with their guard rails. The scenarios join the manager suite (run.ps1) and run on a second
# gateway each (Msg-OnGateway) whose configuration turns the keeper on; it reads the suite's fake GitHub. Items are pinned to a session that never
# connects, so neither gateway offers them to anyone.

function Kp-Token([string]$Name = 'backlog-keeper') { "k1.$Name.1.$(Msg-Mac "keeper:${Name}:1")" }
function Kp-Config([string]$File, [hashtable]$Keeper) {
    Msg-Config $File { param($c) $c.messaging.allowedWorkers = @('alice', 'bob'); $c.keeper = (Msg-Merge @{ enabled = $true; allowedNames = @('backlog-keeper'); tokenGeneration = 1 } $Keeper) }
}
function Kp-Env([string]$File, [hashtable]$Keeper, [hashtable]$More = @{}) { Msg-Merge (Msg-Merge @{ FACTORY_CONFIG = (Kp-Config $File $Keeper); GITHUB_API_URL = "http://127.0.0.1:$($script:FakePort)" } $More) @{} }
function Kp-Open([string]$Name = 'backlog-keeper') { $s = Msg-Open "name=$Name&keeper=1&model=claude-opus-5-5&effort=high" (Kp-Token $Name); Msg-Read $s 8 { param($e) @($e | Where-Object { $_.data.event -eq 'keeper.registered' }).Count -gt 0 }; return $s }
# One verb of the keeper through the real client, against the second gateway
function Kp-F([string[]]$FactoryArgs) { Msg-WithEnv @{ FACTORY_KEEPER_TOKEN = (Kp-Token); FACTORY_URL = $script:MsgBase; FACTORY_WORKER_TOKEN = $null; FACTORY_SUPERVISOR_TOKEN = $null } { $script:KpOut = F 'backlog-keeper' $FactoryArgs }; return $script:KpOut }
# An admin verb call against the second gateway (the scripted worker's steps)
function Kp-Admin([string[]]$FactoryArgs) { Msg-WithEnv @{ FACTORY_URL = $script:MsgBase } { $script:KpOut = F 'w1' $FactoryArgs }; return $script:KpOut }
function Kp-Body([int]$n) { "$((Get-FakeIssue $n).body)" }
function Kp-State([int]$n) {
    $sc = @((Get-FakeIssue $n).comments | Where-Object { $_.body -like '<!-- factory-state:*' })
    if ($sc.Count -eq 0) { return @{} }
    $mm = [regex]::Match($sc[0].body, '(?s)```json\s*(.*?)\s*```'); return ($mm.Groups[1].Value | ConvertFrom-Json -AsHashtable -DateKind String)
}
function Kp-Journal([int]$n, [string]$Event) { , @((Get-FakeIssue $n).comments | Where-Object { $_.body -match "<!-- plant event=$Event " }) }
function Kp-Jobs($St) { @($St.events | Where-Object { $_.data.event -eq 'keeper.job' } | ForEach-Object { $_.data }) }

$KeeperScenarios = [ordered]@{}

$KeeperScenarios['keeper: a worker-failure park sends the keeper a job; it amends a timing criterion keeping the original, with a follow-up and the park released; protected and held items are refused'] = {
    $body = "Make the list page fast.`n`n- The list page loads within 2 seconds for 100 work orders`n- Each work order must keep its creator after an edit`n- The page shows a total count of work orders`n"
    $n = New-Item-Fake 'Fast list page' $body @('factory worker', 'worker:ghost-kp')
    $held = New-Item-Fake 'Held by the owner' "x`n`n- One criterion that is long enough`n" @('factory worker', 'worker:ghost-kp', 'hold:owner')
    Msg-OnGateway (Kp-Env 'instance.kp-a.json' @{ maxNewItemsPerDay = 5 }) {
        param($g)
        $k = Kp-Open
        try {
            Check 'the keeper registers on its own stream' (@(Msg-Of $k 'keeper.registered').Count -eq 1) ($k.body)
            $r = Kp-Admin @('claim-item', '--item', $n, '--column', 'Technical Design'); CheckExit 'claim' $r 0
            $r = Kp-Admin @('complete', '--item', $n, '--column', 'Technical Design', '--succeeded', 'false', '--comment', 'criterion 1 missed: 2.05 s measured')
            Check 'the failure report parks the item' ($r.exit -eq 0 -and $r.parked -eq $true) ($r | ConvertTo-Json -Compress)
            Msg-Read $k 15 { param($e) @($e | Where-Object { $_.data.event -eq 'keeper.job' -and $_.data.item -eq $n }).Count -ge 1 }
            $job = Msg-First @(Kp-Jobs $k | Where-Object { $_.item -eq $n })
            Check 'the keeper gets one keeper.job with the park code, the item and its column' ($job -and $job.code -eq 'worker-failure' -and $job.detail -eq 'parked') ($job | ConvertTo-Json -Compress)
            $r = Kp-F @('amend-criteria', '--item', "$n", '--criterion', 'loads within 2 seconds', '--replacement', 'The list page loads within 2.3 seconds for 100 work orders', '--follow-up', '--unpark', '--policy', 'criterion-dispute', '--reason', 'measured 2.05 s against 2 s; baseline 2.6 s')
            CheckExit 'amend-criteria' $r 0
            $b = Kp-Body $n
            Check 'the criterion is rewritten in place and marked as amended' ($b -match '(?m)^- The list page loads within 2\.3 seconds for 100 work orders _\(amended by the backlog keeper, decision K-\d+-1;' -and $b -notmatch '(?m)^- The list page loads within 2 seconds') $b
            Check 'the decision block keeps the original word for word, the new text and the policy' ($b -match '<!-- keeper:decision K-\d+-1 -->' -and $b -match '> Original \(kept\): The list page loads within 2 seconds for 100 work orders' -and $b -match 'policy `criterion-dispute`') $b
            $f = [int]$r.follow_up
            Check 'a follow-up item keeps the original target, on the board in Technical Design' ($f -gt 0 -and (Get-FakeIssue $f).column -eq 'Technical Design' -and (Get-FakeIssue $f).labels -contains 'factory worker' -and (Kp-Body $f) -match 'loads within 2 seconds' -and (Kp-Body $f) -match "Depends on #$n") ((Get-FakeIssue $f) | ConvertTo-Json -Compress -Depth 3)
            Check 'the park is released and the item handed off to its column' ($r.unparked -eq $true -and $r.reoffer -eq $true -and -not (Kp-State $n).ContainsKey('park') -and (Kp-State $n).handoff.column -eq 'Technical Design') ($r | ConvertTo-Json -Compress -Depth 4)
            Check 'the item state records the amendment and one journal line names the policy' (@((Kp-State $n).amendments).Count -eq 1 -and @(Kp-Journal $n 'keeper-action' | Where-Object { $_.body -match 'backlog keeper \(\*\*backlog-keeper\*\*\)' -and $_.body -match 'policy `criterion-dispute`' }).Count -eq 1) (((Get-FakeIssue $n).comments | ForEach-Object { $_.body.Substring(0, [Math]::Min(140, $_.body.Length)) }) -join ' | ')
            $r = Kp-F @('amend-criteria', '--item', "$n", '--criterion', 'loads within 2.3 seconds', '--replacement', 'The list page loads within 3 seconds', '--policy', 'criterion-dispute')
            CheckExit 'a criterion is amended once' $r 10 'already-amended'
            $r = Kp-F @('amend-criteria', '--item', "$n", '--criterion', 'must keep its creator', '--replacement', 'Each work order keeps its creator', '--policy', 'ambiguous-wording')
            CheckExit 'a "must" criterion is never amended' $r 10 'protected-criterion'
            $r = Kp-F @('amend-criteria', '--item', "$n", '--criterion', 'no such criterion in this item', '--replacement', 'x y z', '--policy', 'ambiguous-wording')
            CheckExit 'a criterion that is not in the item' $r 10 'criterion-not-found'
            $r = Kp-F @('set-priority', '--item', "$held", '--level', 'top', '--policy', 'backlog-order')
            CheckExit 'an unknown policy code' $r 2 'unknown-policy'
            $r = Kp-F @('set-priority', '--item', "$held", '--level', 'top', '--policy', 'dependency-order')
            CheckExit 'an item labelled hold:owner refuses the keeper' $r 10 'owner-hold'
            foreach ($v in @(@('merge', '--item', "$n"), @('approve-pr', '--item', "$n"), @('validation-verdict', '--item', "$n", '--passed', 'true'), @('clear-hold', '--item', "$n"), @('claim-item', '--item', "$n"))) {
                $x = Msg-Json (Msg-Verb $v[0] (Kp-Token) @{ item = $n })
                Check "$($v[0]) is refused to the keeper" ($x.exit -eq 30 -and "$($x.reason)" -ceq "the keeper role cannot call '$($v[0])'") ($x | ConvertTo-Json -Compress)
            }
            $w = Msg-Json (Msg-Verb 'amend-criteria' (Msg-Token 'alice') @{ item = $n; criterion = 'shows a total count'; replacement = 'x'; policy = 'ambiguous-wording' })
            Check 'a worker credential cannot call the keeper''s verbs' ($w.exit -eq 30) ($w | ConvertTo-Json -Compress)
            $st = (Msg-Admin '/api/v1/status').keeper
            Check '/status names the keeper session and counts the day''s new item' ($st.session.name -eq 'backlog-keeper' -and $st.newItemsToday -eq 1 -and $st.maxNewItemsPerDay -eq 5) ($st | ConvertTo-Json -Compress -Depth 4)
        } finally { Msg-Close $k }
    }
}

$KeeperScenarios['keeper: split-item creates ordered children and closes the parent; create-item pins the next free worker at top priority; link-dependency; the day''s limit; a thin backlog sends backlog-low'] = {
    Msg-OnGateway (Kp-Env 'instance.kp-b.json' @{ maxNewItemsPerDay = 3; minTechnicalDesign = 3 } @{ FACTORY_TIMING_keeperBacklogLowMinutes = '0' }) {
        param($g)
        [void](Wait-Until -Seconds 60 { $null -ne (Msg-Admin '/api/v1/status').lastSweep })
        $k = Kp-Open
        $alice = Msg-Open 'name=alice&capacity=5&messaging=1' (Msg-Token 'alice'); Msg-Registered $alice
        try {
            Msg-Read $k 10 { param($e) @($e | Where-Object { $_.data.event -eq 'keeper.job' -and $_.data.code -eq 'backlog-low' }).Count -ge 1 }
            $low = Msg-First @(Kp-Jobs $k | Where-Object { $_.code -eq 'backlog-low' })
            Check 'a sweep with fewer than 3 items in Technical Design sends backlog-low' ($low -and $low.item -eq 0 -and "$($low.detail)" -match '^waiting-\d+-of-3$') ($low | ConvertTo-Json -Compress)
            $p = New-Item-Fake 'Migration and UI in one' "Both at once.`n`n- the table has a new column`n- the page shows it`n" @('factory worker', 'worker:ghost-kp', 'Auto Merge')
            $kids = @(@{ title = 'Add the column (expand)'; body = "From the design section 1.`n`n- the table has a new nullable column`n" }, @{ title = 'Show the column on the page'; body = "From the design section 2.`n`n- the page shows the new column`n" }) | ConvertTo-Json -Compress
            $r = Kp-F @('split-item', '--item', "$p", '--children-file', (TmpFile 'kp-kids.json' $kids), '--policy', 'oversized-item', '--reason', 'a migration and UI work in one item')
            CheckExit 'split-item' $r 0
            $c1 = [int]$r.created[0]; $c2 = [int]$r.created[1]
            Check 'two children on the board in Technical Design, the second depending on the first, with the parent''s Auto Merge' ((Get-FakeIssue $c1).column -eq 'Technical Design' -and (Get-FakeIssue $c2).column -eq 'Technical Design' -and (Kp-Body $c2) -match "(?m)^Depends on #$c1" -and (Get-FakeIssue $c2).labels -contains 'Auto Merge' -and -not ((Get-FakeIssue $c2).labels -contains 'worker:ghost-kp')) ((Get-FakeIssue $c2) | ConvertTo-Json -Compress -Depth 3)
            Check 'the parent is closed as split, its state lists the children' (-not (Get-FakeIssue $p).open -and @((Kp-State $p).split.children).Count -eq 2) ((Kp-State $p) | ConvertTo-Json -Compress -Depth 4)
            $r = Kp-F @('create-item', '--title', 'Fix the flaky acceptance shard', '--body-file', (TmpFile 'kp-flaky.md' "The shard fails on unrelated items.`n`n- the shard passes ten runs in a row`n"), '--priority', 'top', '--pin', 'next-free', '--policy', 'flaky-test')
            CheckExit 'create-item' $r 0
            $fx = [int]$r.item
            Check 'the fix item is at top priority and pinned to the next free worker (alice)' ((Get-FakeIssue $fx).labels -contains 'priority:top' -and (Get-FakeIssue $fx).labels -contains 'worker:alice') ((Get-FakeIssue $fx).labels -join ',')
            $r = Kp-F @('link-dependency', '--item', "$c1", '--depends-on', "$fx", '--policy', 'dependency-order')
            CheckExit 'link-dependency' $r 0
            Check 'the item names its dependency in its body' ((Kp-Body $c1) -match "(?m)^Depends on #$fx\s*$")
            $r = Kp-F @('link-dependency', '--item', "$fx", '--depends-on', "$c1", '--policy', 'dependency-order')
            CheckExit 'the other way round is a cycle' $r 10 'cycle'
            $r = Kp-F @('create-item', '--title', 'One item too many', '--body-file', (TmpFile 'kp-more.md' "Over the limit of the day.`n`n- something to check`n"), '--policy', 'backlog-low')
            CheckExit 'the fourth new item of the day is refused' $r 20 'daily-item-limit'
            Check 'and nothing was created' (@((Get-FakeState).issues | Where-Object { $_.title -eq 'One item too many' }).Count -eq 0)
            $bl = Kp-F @('backlog')
            Check 'backlog lists the factory items by column with their priority and pin' ($bl.exit -eq 0 -and @($bl.items | Where-Object { $_.item -eq $fx -and $_.priority -eq 'top' -and $_.pin -eq 'alice' }).Count -eq 1 -and $bl.counts['Technical Design'] -ge 3) ($bl | ConvertTo-Json -Compress -Depth 4)
        } finally { Msg-Close $k; Msg-Close $alice }
    }

}

$KeeperScenarios['keeper: replenish creates one docs-only item per Done item on the board in Technical Design, never above targetOpen or maxPerDay, and shows its counts in /status'] = {
    $wl = @('factory worker', 'Auto Merge', 'env:tdd')
    $gh = "http://127.0.0.1:$($script:FakePort)/repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders"
    $mkDone = {
        param($title)
        $d = New-Item-Fake $title 'Done in production.' $wl 'Done'
        $state = @{ item = $d; merge = @{ sha = 'abc1234'; merged_at = (Get-Date).ToUniversalTime().ToString('o'); done_at = (Get-Date).ToUniversalTime().ToString('o'); awaiting = $null } }
        $body = "<!-- factory-state:factory-worker v1 -->`nstate`n`n``````json`n" + ($state | ConvertTo-Json -Compress -Depth 8) + "`n```````n"
        [void](Invoke-RestMethod "$gh/issues/$d/comments" -Method POST -Headers @{ Authorization = 'Bearer fake-operator' } -ContentType 'application/json' -Body (@{ body = $body } | ConvertTo-Json))
        [void](Invoke-RestMethod "$gh/issues/$d" -Method PATCH -Headers @{ Authorization = 'Bearer fake-operator' } -ContentType 'application/json' -Body (@{ state = 'closed' } | ConvertTo-Json))
        return $d
    }
    $cfg = Msg-Config 'instance.rp.json' { param($c) $c.envScope = @('tdd'); $c.keeper.replenish = @{ enabled = $true; environments = @('tdd'); maxPerDay = 2; targetOpen = 2 } }
    $made = { param($from) @((Get-FakeState).issues | Where-Object { "$($_.body)" -match '<!-- keeper-replenish -->' -and "$($_.body)" -match "work item #$from " }) }
    $d1 = & $mkDone 'Replenish done one'
    Msg-OnGateway @{ FACTORY_CONFIG = $cfg; GITHUB_API_URL = "http://127.0.0.1:$($script:FakePort)"; FACTORY_ENVIRONMENT = 'tdd'; FACTORY_SWEEP_SECONDS = '2'; FACTORY_SWEEP_MIN_SECONDS = '1' } {
        param($g)
        Check 'a Done item gets one new item' (Wait-Until -Seconds 60 { @(& $made $d1).Count -eq 1 }) ((Get-FakeState).issues | ForEach-Object { $_.title } | Out-String)
        $n1 = @(& $made $d1)[0]
        # replenish creates the issue first, then puts it on the board and in its column (Replenish.ps1): read until the card is placed, the
        # first read can come between the two (column null)
        $placed = Wait-Until -Seconds 30 { $x = @(& $made $d1)[0]; @('enhancement', 'Auto Merge', 'factory worker', 'env:tdd' | Where-Object { $x.labels -notcontains $_ }).Count -eq 0 -and $x.column -eq 'Technical Design' }
        $n1 = @(& $made $d1)[0]
        Check 'it carries enhancement, Auto Merge, factory worker and env:tdd and sits in Technical Design' $placed ($n1 | ConvertTo-Json -Compress -Depth 3)
        Check 'it says the gateway created it to keep activity going and only docs change' ("$($n1.body)" -match 'to keep activity going' -and "$($n1.body)" -match 'Only Markdown files under `docs/`' -and "$($n1.title)" -like 'Docs: *') "$($n1.title)"
        Check 'the finished item has a journal line naming the new item' (Wait-Until -Seconds 20 { @((Get-FakeIssue $d1).comments | Where-Object { $_.body -match 'plant event=replenish ' -and $_.body -match "#$($n1.number)" }).Count -eq 1 }) (((Get-FakeIssue $d1).comments | ForEach-Object { $_.body.Substring(0, [Math]::Min(80, $_.body.Length)) }) -join ' | ')
        Start-Sleep -Seconds 8
        Check 'later sweeps create no second item for the same Done item' (@(& $made $d1).Count -eq 1)
        $d2 = & $mkDone 'Replenish done two'
        Check 'a second Done item gets its own item with a different title' (Wait-Until -Seconds 60 { @(& $made $d2).Count -eq 1 }) ''
        Check 'titles are unique' (@(& $made $d2)[0].title -ne $n1.title)
        $d3 = & $mkDone 'Replenish done three'
        Start-Sleep -Seconds 10
        Check 'two open items are the target: a third Done item creates none' (@(& $made $d3).Count -eq 0)
        $n2 = @(& $made $d2)[0]
        [void](Invoke-RestMethod "$gh/issues/$($n2.number)" -Method PATCH -Headers @{ Authorization = 'Bearer fake-operator' } -ContentType 'application/json' -Body (@{ state = 'closed' } | ConvertTo-Json))
        Start-Sleep -Seconds 10
        Check 'below the target but at maxPerDay (2) nothing more is created' (@(& $made $d3).Count -eq 0 -and @((Get-FakeState).issues | Where-Object { "$($_.body)" -match '<!-- keeper-replenish -->' }).Count -eq 2)
        $st = Invoke-RestMethod (Msg-Url '/api/v1/status') -Headers @{ Authorization = 'Bearer test-token' }
        Check '/status shows the keeper counts' ($st.replenish.maxPerDay -eq 2 -and $st.replenish.targetOpen -eq 2 -and $st.replenish.createdSinceStart -eq 2 -and $st.replenish.createdToday -ge 2) ($st.replenish | ConvertTo-Json -Compress)
    }
}

# W0.6 (delivery-speed.md 6): replenish backpressure. The gateway hands the last plan's waiting count to sync-board (replenish_waiting); the scenario plays
# the gateway's part with explicit sync-board calls on the second gateway, whose own sweeps are not in owner mode and so pass no count at all.
$KeeperScenarios['keeper: replenish backpressure holds while items wait or without a plan, releases below the limit, and onlyWhenIdle also needs a free session'] = {
    $wl = @('factory worker', 'Auto Merge', 'env:tdd')
    $gh = "http://127.0.0.1:$($script:FakePort)/repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders"
    $mkDone = {
        param($title)
        $d = New-Item-Fake $title 'Done in production.' $wl 'Done'
        $state = @{ item = $d; merge = @{ sha = 'abc1234'; merged_at = (Get-Date).ToUniversalTime().ToString('o'); done_at = (Get-Date).ToUniversalTime().ToString('o'); awaiting = $null } }
        $body = "<!-- factory-state:factory-worker v1 -->`nstate`n`n``````json`n" + ($state | ConvertTo-Json -Compress -Depth 8) + "`n```````n"
        [void](Invoke-RestMethod "$gh/issues/$d/comments" -Method POST -Headers @{ Authorization = 'Bearer fake-operator' } -ContentType 'application/json' -Body (@{ body = $body } | ConvertTo-Json))
        [void](Invoke-RestMethod "$gh/issues/$d" -Method PATCH -Headers @{ Authorization = 'Bearer fake-operator' } -ContentType 'application/json' -Body (@{ state = 'closed' } | ConvertTo-Json))
        return $d
    }
    $made = { @((Get-FakeState).issues | Where-Object { "$($_.body)" -match '<!-- keeper-replenish -->' }) }
    $cfg = Msg-Config 'instance.rpbp.json' { param($c) $c.envScope = @('tdd'); $c.keeper.replenish = @{ enabled = $true; environments = @('tdd'); maxPerDay = 5; targetOpen = 5; holdWhenWaiting = 2; onlyWhenIdle = $false } }
    $d1 = & $mkDone 'Backpressure done one'
    Msg-OnGateway @{ FACTORY_CONFIG = $cfg; GITHUB_API_URL = "http://127.0.0.1:$($script:FakePort)"; FACTORY_ENVIRONMENT = 'tdd'; FACTORY_SWEEP_SECONDS = '2'; FACTORY_SWEEP_MIN_SECONDS = '1' } {
        param($g)
        Start-Sleep -Seconds 8
        Check 'without a plan counted (the gateway''s own sweeps pass none) nothing is created' (@(& $made).Count -eq 0) ((Get-FakeState).issues | ForEach-Object { $_.title } | Out-String)
        $r = Kp-Admin @('sync-board', '--replenish_waiting', '3'); CheckExit 'a sweep with 3 waiting' $r 0
        Check '3 waiting is at or above holdWhenWaiting (2): held with the reason waiting, the count in the answer' ($r.replenish.skipped -eq 'waiting' -and $r.replenish.waiting -eq 3 -and @($r.replenish.created).Count -eq 0) ($r.replenish | ConvertTo-Json -Compress)
        Check 'and no item was created' (@(& $made).Count -eq 0)
        $r = Kp-Admin @('sync-board', '--replenish_waiting', '2'); Check '2 waiting is still at the limit: held' ($r.replenish.skipped -eq 'waiting' -and @(& $made).Count -eq 0) ($r.replenish | ConvertTo-Json -Compress)
        $r = Kp-Admin @('sync-board', '--replenish_waiting', '1')
        Check '1 waiting is below the limit: released, one item created for the Done item' (@($r.replenish.created).Count -eq 1 -and @(& $made).Count -eq 1) ($r.replenish | ConvertTo-Json -Compress)
    }
    $cfg2 = Msg-Config 'instance.rpidle.json' { param($c) $c.envScope = @('tdd'); $c.keeper.replenish = @{ enabled = $true; environments = @('tdd'); maxPerDay = 5; targetOpen = 5; holdWhenWaiting = 0; onlyWhenIdle = $true } }
    $d2 = & $mkDone 'Backpressure done two'
    Msg-OnGateway @{ FACTORY_CONFIG = $cfg2; GITHUB_API_URL = "http://127.0.0.1:$($script:FakePort)"; FACTORY_ENVIRONMENT = 'tdd'; FACTORY_SWEEP_SECONDS = '2'; FACTORY_SWEEP_MIN_SECONDS = '1' } {
        param($g)
        $before = @(& $made).Count
        $r = Kp-Admin @('sync-board', '--replenish_waiting', '1', '--free_workers', '2')
        Check 'onlyWhenIdle: an item waits, so the plant is not idle, even with a free session' ($r.replenish.skipped -eq 'not-idle' -and @(& $made).Count -eq $before) ($r.replenish | ConvertTo-Json -Compress)
        $r = Kp-Admin @('sync-board', '--replenish_waiting', '0', '--free_workers', '0')
        Check 'onlyWhenIdle: nothing waits but no session could take a new item: not idle' ($r.replenish.skipped -eq 'not-idle' -and @(& $made).Count -eq $before) ($r.replenish | ConvertTo-Json -Compress)
        $r = Kp-Admin @('sync-board', '--replenish_waiting', '0', '--free_workers', '1')
        Check 'onlyWhenIdle: nothing waits and a session is free: released, one item created' (@($r.replenish.created).Count -eq 1 -and @(& $made).Count -eq $before + 1) ($r.replenish | ConvertTo-Json -Compress)
    }
}
