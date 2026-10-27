# Tier 1 isolation scenarios (W1.9, docs/design/system-gateway.md H12): v1 serves ONE repository, the home repository. The fake GitHub holds a
# second repository whose issues are linked to the same board (the 'repo' field of /_fake/issue, C-1 #372); these scenarios prove that nothing of
# that second repository reaches the home repository's work: its webhook is dropped, its card is ignored, a "Depends on other-org/other-repo#N" line
# of a home item is not a home dependency, and a verb naming its number is refused. Suite 'repos': the default gateway, no environment.
#
# Not here (it needs a repository-keyed fake, and the token's repository claim of W1.8): the same issue number in both repositories, and a token or
# a verb call that names another repository. They are follow-ups of #259.

$script:RepoHome = 'clearmeasure-aisf-sample-apps/cmdemo1-workorders'
$script:RepoOther = 'clearmeasure-aisf-sample-apps/other-repo'

# One verb over HTTP with the operator token; the parsed answer (ok, code, ...).
function Rp-Verb([string]$Verb, [hashtable]$Body) {
    $r = Invoke-WebRequest -Uri "$(Get-GatewayBase)/api/v1/verbs/$Verb" -Method POST -Body ($Body | ConvertTo-Json -Compress -Depth 10) -ContentType 'application/json' -Headers @{ Authorization = 'Bearer test-token' } -SkipHttpErrorCheck -TimeoutSec 120
    try { return ("$($r.Content)" | ConvertFrom-Json -AsHashtable) } catch { return @{ ok = $false; code = "http-$([int]$r.StatusCode)" } }
}
# An open issue of the second repository, linked to the board in $Column (none: no card), labelled for the factory like a home item.
function Rp-ForeignIssue([string]$Title, [string]$Body = 'x', [string]$Column = '', [string]$Repo = $script:RepoOther) {
    $b = @{ title = $Title; body = $Body; labels = @(@($L) + @($script:DefaultEnvLabel | Where-Object { $_ })); repo = $Repo }
    if ($Column) { $b.column = $Column }
    return [int](Fake 'issue' $b).number
}
# True when the sweep's answer names the item: as "item":N in an entry, as a number in one of its lists, or as a key of one of its maps.
function Rp-Mentions($Answer, [int]$n) {
    if (($Answer | ConvertTo-Json -Depth 12 -Compress) -match "`"item`":$n[,}]") { return $true }
    foreach ($k in 'unconfirmed', 'settling', 'holds', 'unparked') { if (@(Pv $Answer $k) -contains $n) { return $true } }
    foreach ($k in 'owners', 'pins', 'priorities') { $mp = Pv $Answer $k; if ($mp -is [System.Collections.IDictionary] -and $mp.Contains("$n")) { return $true } }
    return $false
}
# An open home issue without a card (the board guard puts it on the board).
function Rp-NoCardIssue([string]$Title) { return [int](Fake 'issue' @{ title = $Title; body = 'x'; labels = @(@($L) + @($script:DefaultEnvLabel | Where-Object { $_ })) }).number }

$ReposScenarios = [ordered]@{}

$ReposScenarios['repos: a webhook delivery for another repository is dropped and requests no sweep'] = {
    $n = New-Item-Fake 'Home item' 'x' $L
    $labeled = { param([int]$Num, [string]$Repo) @{ action = 'labeled'; label = @{ name = 'factory worker' }; issue = @{ number = $Num; labels = @(@{ name = 'factory worker' }) }; repository = @{ full_name = $Repo } } }
    # the control: a delivery of the home repository about the same number asks for a sweep (the counter moves), so a counter that stays proves a drop
    $r0 = [long](Get-Plant).sweep.requests
    Check 'a signed delivery of the home repository is accepted' ((Qc-Hook 'issues' (& $labeled $n $script:RepoHome)) -eq 202)
    Check 'and it asks for a sweep' ([long](Get-Plant).sweep.requests -gt $r0) ((Get-Plant).sweep | ConvertTo-Json -Compress)
    Start-Sleep -Seconds 2
    $r1 = [long](Get-Plant).sweep.requests
    $others = @(
        @{ ev = 'issues'; p = (& $labeled $n $script:RepoOther) }
        @{ ev = 'issues'; p = (& $labeled $n 'other-org/cmdemo1-workorders') }
        @{ ev = 'workflow_run'; p = @{ action = 'completed'; workflow_run = @{ head_branch = 'master'; head_sha = ('a' * 40); conclusion = 'success'; name = 'Build' }; repository = @{ full_name = $script:RepoOther } } }
        @{ ev = 'pull_request'; p = @{ action = 'opened'; number = $n; pull_request = @{ number = $n; head = @{ ref = 'x'; sha = ('b' * 40) }; base = @{ ref = 'master' } }; repository = @{ full_name = 'other-org/other-repo' } } }
    )
    foreach ($d in $others) { Check "a $($d.ev) delivery of another repository is accepted" ((Qc-Hook $d.ev $d.p) -eq 202) }
    Start-Sleep -Seconds 2
    $r2 = [long](Get-Plant).sweep.requests
    Check 'none of the deliveries of other repositories asked for a sweep' ($r2 -eq $r1) "requests $r1 -> $r2"
    # the rule script says why: dropped, no event
    $ev = Rp-Verb 'ingest-event' @{ event = 'issues'; delivery = 'rp-other'; payload = (& $labeled $n 'other-org/other-repo') }
    Check 'ingest-event answers dropped other-repository and makes no event' ($ev.ok -eq $true -and "$($ev.dropped)" -eq 'other-repository' -and @($ev.events).Count -eq 0) ($ev | ConvertTo-Json -Compress -Depth 6)
    $evH = Rp-Verb 'ingest-event' @{ event = 'issues'; delivery = 'rp-home'; payload = (& $labeled $n 'ClearMeasure-AISF-Sample-Apps/CMDemo1-WorkOrders') }
    Check 'the same delivery for the home repository (any case) makes its item.labeled event' ($evH.ok -eq $true -and @($evH.events | Where-Object { $_ -and $_.event -eq 'item.labeled' -and $_.item -eq $n }).Count -eq 1) ($evH | ConvertTo-Json -Compress -Depth 6)
}

$ReposScenarios['repos: a card of another repository on the shared board is ignored by the sweep, the intake and the board guard [full]'] = {
    # the intake: the foreign card is first in Todo, the home card second; one item is pulled, and it is the home one
    $fTodo = Rp-ForeignIssue 'Foreign Todo' 'x' 'Todo'
    $hTodo = New-Item-Fake 'Home Todo' 'x' $L 'Todo'
    $in = Qc-Server 'sync-board' @{ intake = @{ freeSlots = 2 } } @{ FACTORY_INTAKE = '1' }
    Check 'the intake pulls the home card from Todo' (@($in.actions | Where-Object { $_ -and $_.action -eq 'intake-pulled' -and $_.item -eq $hTodo }).Count -eq 1 -and (Get-FakeIssue $hTodo).column -eq 'Technical Design') ($in | ConvertTo-Json -Compress -Depth 6)
    Check 'and never the foreign card' ((Get-FakeIssue $fTodo).column -eq 'Todo' -and -not (Rp-Mentions @{ actions = $in.actions } $fTodo)) ($in.actions | ConvertTo-Json -Compress -Depth 6)
    # the sweep: a foreign card in an AI column is in no list of the answer
    $fTd = Rp-ForeignIssue 'Foreign Technical Design' 'x' 'Technical Design'
    $sw = F 'w1' @('sync-board'); $swj = $sw | ConvertTo-Json -Depth 12 -Compress
    Check 'the sweep names neither foreign card' (-not (Rp-Mentions $sw $fTd) -and -not (Rp-Mentions $sw $fTodo)) $swj
    # the board guard: a labelled issue without a card is put on the board; the foreign one without a card is not
    $fNone = Rp-ForeignIssue 'Foreign without a card'
    $hNone = Rp-NoCardIssue 'Home without a card'
    Check 'the board guard places the home issue in the intake column' (Wait-Until -Seconds 40 { (Get-FakeIssue $hNone).column -eq 'Technical Design' }) "column $((Get-FakeIssue $hNone).column)"
    Start-Sleep -Seconds 4
    Check 'the board guard leaves the foreign issue without a card' ($null -eq (Get-FakeIssue $fNone).column) "column $((Get-FakeIssue $fNone).column)"
    foreach ($f in $fTodo, $fTd, $fNone) {
        $fi = Get-FakeIssue $f
        Check "nothing was written on the foreign issue #$f" (@($fi.comments | Where-Object { $_ }).Count -eq 0) ($fi.comments | ConvertTo-Json -Compress -Depth 4)
    }
    Check 'the foreign cards stayed in their columns' ((Get-FakeIssue $fTodo).column -eq 'Todo' -and (Get-FakeIssue $fTd).column -eq 'Technical Design')
}

$ReposScenarios['repos: a home item that depends on other-org/other-repo#N is not held, and the foreign ref stays on it'] = {
    $blocker = New-Item-Fake 'Open home item' 'x' $L
    # the same number as the open home item, but of another repository: not a home dependency. The control depends on the home item itself.
    $line = "Depends on other-org/other-repo#$blocker"
    $free = New-Item-Fake 'Depends on a foreign ref' "Builds on the work of another repository.`n`n$line`n" $L
    $ctl = New-Item-Fake 'Depends on the home item' "Builds on the open item.`n`nDepends on #$blocker`n" $L
    $script:RpSweep = $null
    Check 'the control is held on the home item (so the sweep offers items and reads their dependencies)' (Wait-Until -Seconds 60 {
        $script:RpSweep = F 'w1' @('sync-board')
        @($script:RpSweep.held | Where-Object { $_ -and $_.item -eq $ctl -and $_.code -eq 'depends-on' }).Count -eq 1 }) ($script:RpSweep | ConvertTo-Json -Compress -Depth 6)
    $sw = $script:RpSweep
    Check 'the item with the foreign ref is not held' (@($sw.held | Where-Object { $_ -and $_.item -eq $free }).Count -eq 0) ($sw.held | ConvertTo-Json -Compress -Depth 4)
    Check 'and is offered in the same sweep' (@($sw.offers | Where-Object { $_ -and $_.item -eq $free }).Count -eq 1) ($sw.offers | ConvertTo-Json -Compress -Depth 4)
    Check 'the control waits for the home item only' (@(@($sw.held | Where-Object { $_ -and $_.item -eq $ctl })[0].on).Count -eq 1 -and @(@($sw.held | Where-Object { $_ -and $_.item -eq $ctl })[0].on)[0] -eq $blocker) ($sw.held | ConvertTo-Json -Compress -Depth 4)
    $gi = Rp-Verb 'get-item' @{ item = $free }
    Check 'the foreign ref is shown: get-item carries the qualified line in the body' ($gi.ok -eq $true -and "$($gi.body)" -match "(?m)^Depends on other-org/other-repo#$blocker\s*$") ($gi | ConvertTo-Json -Compress -Depth 4)
    Check 'and it was not turned into a home ref' ("$($gi.body)" -notmatch "(?m)^Depends on #$blocker")
}

$ReposScenarios['repos: a verb call naming a card of another repository is refused and writes nothing [full]'] = {
    $f = Rp-ForeignIssue 'Foreign card' 'x' 'Technical Design'
    $inst = "$((Get-Content -Raw (Join-Path $script:Work 'instance.test.json') | ConvertFrom-Json).name)"
    $repo = "http://127.0.0.1:$($script:FakePort)/repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders"
    $r = Rp-Verb 'get-item' @{ item = $f }
    Check 'get-item of the foreign number: item-not-found' ($r.ok -ne $true -and $r.code -eq 'item-not-found') ($r | ConvertTo-Json -Compress -Depth 4)
    $r = Rp-Verb 'claim-item' @{ item = $f; column = 'Technical Design'; holder = 'w-rp' }
    Check 'claim-item of the foreign number: item-not-found' ($r.ok -ne $true -and $r.code -eq 'item-not-found') ($r | ConvertTo-Json -Compress -Depth 4)
    $r = Rp-Verb 'merge' @{ item = $f; pr = 1 }
    Check 'merge for the foreign number is refused' ($r.ok -ne $true) ($r | ConvertTo-Json -Compress -Depth 4)
    $fi = Get-FakeIssue $f
    Check 'no comment, and the card did not move' (@($fi.comments | Where-Object { $_ }).Count -eq 0 -and $fi.column -eq 'Technical Design') ($fi | ConvertTo-Json -Compress -Depth 4)
    $refs = @((Invoke-RestMethod "$repo/git/matching-refs/factory/$inst/claims/" -Headers @{ Authorization = 'Bearer fake-operator' }) | ForEach-Object { $_ } | ForEach-Object { "$($_.ref)" })
    Check 'no claim ref was made for the foreign number' ("refs/factory/$inst/claims/$f" -notin $refs) ($refs -join ',')
    # the home item of the control is claimed as usual
    $h = New-Item-Fake 'Home control' 'x' $L
    $c = Rp-Verb 'claim-item' @{ item = $h; column = 'Technical Design'; holder = 'w-rp' }
    Check 'the same claim for a home item succeeds' ($c.ok -eq $true) ($c | ConvertTo-Json -Compress -Depth 4)
}
