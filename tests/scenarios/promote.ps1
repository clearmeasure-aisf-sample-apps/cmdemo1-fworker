# Tier 1 scenarios of the automatic promotion (docs/design/auto-promote.md, plugin/scripts/lib/Verbs.Promote.ps1): suite 'promote'. The gateway
# promotes a release of cmdemo1-ui from tdd to uat, then prod, when every gate holds, answers the Sign-off of the deployments it created, and
# otherwise lets the release wait with one comment per item and reason. The fake Octopus deploys a release of /_fake/octo-release to tdd only.

$PromoteLabels = @('factory worker', 'Auto Merge')

# An item waiting in the Release Queue for release $Ver, as settle leaves it after a gateway merge of a PASS (or, with -NoPass, of a person's merge)
function Pm-Sha { ([guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')).Substring(0, 40) }
# What runs everywhere before the scenario's releases: release 3.0.0 at commit B, deployed to tdd, uat and prod; master history starts at B.
# Once per scenario (Pm-Item calls it unless -NoBase).
function Pm-Base {
    if ((Get-Variable -Scope Script -Name PmBaseFor -ValueOnly -ErrorAction SilentlyContinue) -eq $script:Current) { return }
    $script:PmBaseFor = $script:Current
    $b = Pm-Sha
    [void](Fake 'octo-release' @{ version = '3.0.0'; sha = $b; also = @('uat', 'prod') })
    [void](Fake 'promote-history' @{ shas = @($b) })
}
function Pm-Item([string]$Ver, [string[]]$Labels = $PromoteLabels, [string]$Tdd = 'Success', [switch]$NoPass, [switch]$NoRelease, [string]$Sha = '', [switch]$NoBase) {
    if (-not $NoBase) { Pm-Base }
    $sha = if ($Sha) { $Sha } else { Pm-Sha }
    [void](Fake 'promote-history' @{ shas = @($sha) })
    if (-not $NoRelease) { [void](Fake 'octo-release' @{ version = $Ver; sha = $sha; tdd = $Tdd }) }
    $n = New-Item-Fake "Promote $Ver" 'x' $Labels 'Release Queue'
    $now = (Get-Date).ToUniversalTime().ToString('o')
    $st = @{ schema = 1; item = $n; verdict = @{ passed = $true; pr = 7; processed = $true }; merge = @{ sha = $sha; pr = 7; merged_at = $now; green_at = $now; awaiting = 'release-queue'; release = $Ver; queued_at = $now } }
    if ($NoPass) { $st.merge.by = 'person'; $st.verdict.passed = $false }
    $body = "<!-- factory-state:factory-worker v1 -->`nstate`n`n``````json`n$($st | ConvertTo-Json -Depth 8)`n```````n"
    [void](Invoke-RestMethod "http://127.0.0.1:$($script:FakePort)/repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders/issues/$n/comments" -Method POST -Headers @{ Authorization = 'Bearer fake-operator' } -Body (@{ body = $body } | ConvertTo-Json) -ContentType 'application/json')
    return @{ n = $n; sha = $sha }
}
# A factory item in Done whose pull request was merged as a commit before the next release (a docs-only keeper item goes there straight from
# Functional Testing): its PR is merged as that commit, its state records the merge; -Awaiting leaves the merge unsettled
function Pm-DoneItem([string]$Awaiting = '') {
    Pm-Base
    $sha = Pm-Sha
    $n = New-Item-Fake 'Promote docs item' 'x' $PromoteLabels 'Done'
    $pr = (Fake 'merged-pr' @{ ref = "factory-worker/$n-docs"; title = "Docs change (#$n)"; sha = $sha; files = @('docs/x.md') }).number
    [void](Fake 'promote-history' @{ shas = @($sha) })
    $now = (Get-Date).ToUniversalTime().ToString('o')
    $st = @{ schema = 1; item = $n; verdict = @{ passed = $true; pr = $pr; processed = $true }; merge = @{ sha = $sha; pr = $pr; merged_at = $now; green_at = $now; docs_only = $true } }
    if ($Awaiting) { $st.merge.awaiting = $Awaiting } else { $st.merge.awaiting = $null; $st.merge.done_at = $now }
    $body = "<!-- factory-state:factory-worker v1 -->`nstate`n`n``````json`n$($st | ConvertTo-Json -Depth 8)`n```````n"
    [void](Invoke-RestMethod "http://127.0.0.1:$($script:FakePort)/repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders/issues/$n/comments" -Method POST -Headers @{ Authorization = 'Bearer fake-operator' } -Body (@{ body = $body } | ConvertTo-Json) -ContentType 'application/json')
    return @{ n = $n; sha = $sha; pr = $pr }
}
function Pm-Deps { Fake 'octo-deps' }
function Pm-Dep([string]$Ver, [string]$EnvName) { @((Pm-Deps).deployments | Where-Object { $_.release -eq $Ver -and $_.env -eq $EnvName }) }
function Pm-Events([int]$n, [string]$Event) { @((Get-FakeIssue $n).comments | Where-Object { $_.body -match "plant event=$Event " }) }
function Pm-Brief([int]$n) { (@((Get-FakeIssue $n).comments | ForEach-Object { ("$($_.body)" -replace '\s+', ' ').Substring(0, [Math]::Min(220, ("$($_.body)" -replace '\s+', ' ').Length)) }) -join ' | ') }

function Pm-Created([string]$Ver) {
    [void](Fake 'octo-forbid' @{ interruption = 'InterruptionSubmit' })
    $i = Pm-Item $Ver
    Check "the deployment of $Ver to uat is created" (Wait-Until -Seconds 40 { @(Pm-Dep $Ver 'uat').Count -eq 1 }) (Pm-Brief $i.n)
    return $i
}

$PromoteScenarios = [ordered]@{

'promote: every gate met: the release goes to uat, its Sign-off is answered, then to prod, each deployment created once, and the item is delivered' = {
    $i = Pm-Item '3.1.1'
    Check 'a deployment to uat is created' (Wait-Until -Seconds 40 { @(Pm-Dep '3.1.1' 'uat').Count -eq 1 }) (Pm-Brief $i.n)
    Check 'its Sign-off is answered Proceed by the gateway' (Wait-Until -Seconds 40 { @(Pm-Dep '3.1.1' 'uat')[0].result -eq 'Proceed' }) ((Pm-Deps) | ConvertTo-Json -Compress -Depth 4)
    Check 'then a deployment to prod' (Wait-Until -Seconds 40 { @(Pm-Dep '3.1.1' 'prod').Count -eq 1 -and @(Pm-Dep '3.1.1' 'prod')[0].result -eq 'Proceed' }) ((Pm-Deps) | ConvertTo-Json -Compress -Depth 4)
    Check 'the item is delivered to Prod and closed' (Wait-Until -Seconds 40 { $f = Get-FakeIssue $i.n; -not $f.open -and @($f.comments | Where-Object { $_.body -match 'Delivered to \*\*Prod\*\*' }).Count -eq 1 }) (Pm-Brief $i.n)
    Start-Sleep -Seconds 6
    Check 'exactly two deployments were created, one per environment' ((Pm-Deps).creates -eq 2) ((Pm-Deps) | ConvertTo-Json -Compress -Depth 4)
    Check 'the item journals each promotion once, with its task' (@(Pm-Events $i.n 'promoted').Count -eq 2 -and @(@(Pm-Events $i.n 'promoted') | Where-Object { $_.body -match 'to \*\*uat\*\* \(Octopus task `ServerTasks-P' }).Count -eq 1) (Pm-Brief $i.n)
    Check 'and each Sign-off once' (@(Pm-Events $i.n 'promotion-signoff').Count -eq 2) (Pm-Brief $i.n)
    Check 'no waiting comment but the health settle time' (@(@(Pm-Events $i.n 'promotion-waiting') | Where-Object { $_.body -notmatch 'not been Healthy long enough' }).Count -eq 0) (Pm-Brief $i.n)
}

'promote: an item merged without a PASS verdict holds the whole release back, explained once on each item' = {
    $a = Pm-Item '3.2.1'
    $b = Pm-Item '3.2.1' -NoRelease -NoPass
    Check 'each item gets one waiting comment' (Wait-Until -Seconds 40 { @(Pm-Events $a.n 'promotion-waiting').Count -ge 1 -and @(Pm-Events $b.n 'promotion-waiting').Count -ge 1 }) "$(Pm-Brief $a.n) /// $(Pm-Brief $b.n)"
    Start-Sleep -Seconds 8
    Check 'no deployment is created' ((Pm-Deps).creates -eq 0)
    Check 'once: later sweeps do not repeat it' (@(Pm-Events $a.n 'promotion-waiting').Count -eq 1 -and @(Pm-Events $b.n 'promotion-waiting').Count -eq 1)
    Check 'the item without the PASS is told so' ("$(@(Pm-Events $b.n 'promotion-waiting')[0].body)" -match 'merged after a Functional Testing PASS') (Pm-Brief $b.n)
    Check 'the other item is told which item holds it back' ("$(@(Pm-Events $a.n 'promotion-waiting')[0].body)" -match "#$($b.n), carried by this release, does not qualify") (Pm-Brief $a.n)
    Check 'both stay in the Release Queue' ((Get-FakeIssue $a.n).column -eq 'Release Queue' -and (Get-FakeIssue $b.n).column -eq 'Release Queue')
}

'promote: a video item waits for its demo video; once it is published the release is promoted' = {
    $i = Pm-Item '3.3.1' @('factory worker', 'Auto Merge', 'video')
    Check 'it waits, naming the demo video' (Wait-Until -Seconds 40 { @(@(Pm-Events $i.n 'promotion-waiting') | Where-Object { $_.body -match "demo-$($i.n)\.mp4" }).Count -eq 1 }) (Pm-Brief $i.n)
    Check 'no deployment meanwhile' ((Pm-Deps).creates -eq 0)
    [void](Fake 'gh-demo' @{ item = $i.n })
    Check 'with the video published it goes to uat' (Wait-Until -Seconds 40 { @(Pm-Dep '3.3.1' 'uat').Count -eq 1 }) (Pm-Brief $i.n)
}

'promote: no Auto Merge label, or a Hold label, keeps the release waiting' = {
    $a = Pm-Item '3.4.1' @('factory worker')
    $b = Pm-Item '3.4.2' @('factory worker', 'Auto Merge', 'Hold')
    Check 'the unlabelled item names the Auto Merge label' (Wait-Until -Seconds 40 { @(@(Pm-Events $a.n 'promotion-waiting') | Where-Object { $_.body -match "no 'Auto Merge' label" }).Count -ge 1 }) (Pm-Brief $a.n)
    Check 'the held item names the Hold label' (Wait-Until -Seconds 40 { @(@(Pm-Events $b.n 'promotion-waiting') | Where-Object { $_.body -match "carries the 'Hold' label" }).Count -eq 1 }) (Pm-Brief $b.n)
    Check 'no deployment' ((Pm-Deps).creates -eq 0)
}

'promote: a failed tdd deployment, then an unhealthy tdd app, keep the release from uat' = {
    $i = Pm-Item '3.5.1' -Tdd 'Failed'
    Check 'it waits, naming the failed tdd deployment' (Wait-Until -Seconds 40 { @(@(Pm-Events $i.n 'promotion-waiting') | Where-Object { $_.body -match 'deployment to tdd is Failed' }).Count -eq 1 }) (Pm-Brief $i.n)
    [void](Fake 'health' @{ status = 503 })
    [void](Fake 'octo-dep' @{ release = '3.5.1'; env = 'tdd'; state = 'Success' })
    Check 'then the non-Healthy tdd app' (Wait-Until -Seconds 40 { @(@(Pm-Events $i.n 'promotion-waiting') | Where-Object { $_.body -match 'tdd app answered Unhealthy on /_healthcheck/detailed' }).Count -eq 1 }) (Pm-Brief $i.n)
    [void](Fake 'health' @{ status = 200 })
    Start-Sleep -Seconds 10
    Check 'healthy again is not enough: a non-Healthy read since the deployment finished keeps it waiting' ((Pm-Deps).creates -eq 0)
    # a new tdd deployment of the release (finished now): the reads start again
    [void](Fake 'octo-dep' @{ release = '3.5.1'; env = 'tdd'; state = 'Success' })
    # a slow CI machine can take more than one sweep to notice the new deployment, and can journal the reason again: at least one, within 90 s
    Check 'first it waits for two Healthy reads apart, the later after the settle time' (Wait-Until -Seconds 90 { @(@(Pm-Events $i.n 'promotion-waiting') | Where-Object { $_.body -match 'not been Healthy long enough' }).Count -ge 1 }) (Pm-Brief $i.n)
    Check 'then it goes to uat' (Wait-Until -Seconds 40 { @(Pm-Dep '3.5.1' 'uat').Count -eq 1 }) (Pm-Brief $i.n)
}

'promote: the tdd app running another version keeps the release from uat' = {
    [void](Fake 'app-build' @{ version = '9.9.9' })
    $i = Pm-Item '3.5.2'
    Check 'it waits, naming the running version' (Wait-Until -Seconds 40 { @(@(Pm-Events $i.n 'promotion-waiting') | Where-Object { $_.body -match 'does not run release 3\.5\.2 yet \(/_build: 9\.9\.9\)' }).Count -eq 1 }) (Pm-Brief $i.n)
    Check 'no deployment' ((Pm-Deps).creates -eq 0)
    [void](Fake 'app-build' @{ version = '' })
    Check 'running it: it goes to uat' (Wait-Until -Seconds 40 { @(Pm-Dep '3.5.2' 'uat').Count -eq 1 }) (Pm-Brief $i.n)
}

'promote: a 403 of the deployment create waits and names the missing permission, once' = {
    [void](Fake 'octo-forbid' @{ create = 'DeploymentCreate' })
    $i = Pm-Item '3.6.1'
    Check 'the item is told the missing permission' (Wait-Until -Seconds 40 { @(@(Pm-Events $i.n 'promotion-waiting') | Where-Object { $_.body -match 'missing permission DeploymentCreate' }).Count -eq 1 }) (Pm-Brief $i.n)
    Start-Sleep -Seconds 8
    Check 'once, and nothing was created' (@(@(Pm-Events $i.n 'promotion-waiting') | Where-Object { $_.body -match 'DeploymentCreate' }).Count -eq 1 -and (Pm-Deps).creates -eq 0) (Pm-Brief $i.n)
    [void](Fake 'octo-forbid' @{})
    Check 'granted: it goes to uat' (Wait-Until -Seconds 40 { @(Pm-Dep '3.6.1' 'uat').Count -eq 1 }) (Pm-Brief $i.n)
}

'promote: a gate that fails while the Sign-off waits makes the gateway abort it; a 403 of the interruption names the permission' = {
    [void](Fake 'octo-forbid' @{ interruption = 'InterruptionSubmit' })
    $i = Pm-Item '3.7.1'
    Check 'the deployment to uat is created' (Wait-Until -Seconds 40 { @(Pm-Dep '3.7.1' 'uat').Count -eq 1 }) (Pm-Brief $i.n)
    Check 'the refused Sign-off names the permission' (Wait-Until -Seconds 40 { @(@(Pm-Events $i.n 'promotion-waiting') | Where-Object { $_.body -match 'missing permission InterruptionSubmit' }).Count -eq 1 }) (Pm-Brief $i.n)
    Check 'the Sign-off still waits' (@(Pm-Dep '3.7.1' 'uat')[0].pending)
    [void](Fake 'health' @{ status = 503 })
    [void](Fake 'octo-forbid' @{})
    Check 'the gateway aborts it' (Wait-Until -Seconds 40 { @(Pm-Dep '3.7.1' 'uat')[0].result -eq 'Abort' }) ((Pm-Deps) | ConvertTo-Json -Compress -Depth 4)
    Check 'and journals the abort with the reason' (Wait-Until -Seconds 20 { @(@(Pm-Events $i.n 'promotion-signoff') | Where-Object { $_.body -match 'Abort' -and $_.body -match 'answered Unhealthy' }).Count -eq 1 }) (Pm-Brief $i.n)
    [void](Fake 'health' @{ status = 200 })
    Check 'the failed uat deployment is explained, and not deployed again by the gateway' (Wait-Until -Seconds 40 { @(@(Pm-Events $i.n 'promotion-waiting') | Where-Object { $_.body -match 'deployment to uat ended Failed' }).Count -eq 1 }) (Pm-Brief $i.n)
    Check 'one deployment only' ((Pm-Deps).creates -eq 1)
}

'promote: a Sign-off of a deployment the gateway did not create is left alone' = {
    $sha = ([guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')).Substring(0, 40)
    [void](Fake 'octo-release' @{ version = '3.8.1'; sha = $sha })
    # a person's deployment to uat, created before the gateway's sweep could
    [void](Invoke-RestMethod "http://127.0.0.1:$($script:FakePort)/octopus/api/Spaces-355/deployments/create/untenanted/v1" -Method POST -Headers @{ 'X-Octopus-ApiKey' = ('API-' + 'PERSON') } -Body (@{ SpaceIdOrName = 'Spaces-355'; ProjectName = 'cmdemo1-ui'; ReleaseVersion = '3.8.1'; EnvironmentNames = @('uat') } | ConvertTo-Json) -ContentType 'application/json')
    $i = Pm-Item '3.8.1' -NoRelease -Sha $sha
    Start-Sleep -Seconds 8
    Check 'its Sign-off is not answered' (@(Pm-Dep '3.8.1' 'uat')[0].pending) ((Pm-Deps) | ConvertTo-Json -Compress -Depth 4)
    Check 'and no second deployment to uat' (@(Pm-Dep '3.8.1' 'uat').Count -eq 1)
}

'promote: a gateway restart while the uat deployment runs promotes nothing twice' = {
    [void](Fake 'octo-forbid' @{ interruption = 'InterruptionView' })
    $i = Pm-Item '3.9.1'
    Check 'the deployment to uat is created' (Wait-Until -Seconds 40 { @(Pm-Dep '3.9.1' 'uat').Count -eq 1 }) (Pm-Brief $i.n)
    Restart-Gateway
    Start-Sleep -Seconds 8
    Check 'after the restart: still one deployment to uat' ((Pm-Deps).creates -eq 1) ((Pm-Deps) | ConvertTo-Json -Compress -Depth 4)
    [void](Fake 'octo-forbid' @{})
    Check 'the restarted gateway answers the Sign-off of its own deployment (recorded on the item) and goes on to prod' (Wait-Until -Seconds 40 { @(Pm-Dep '3.9.1' 'prod').Count -eq 1 }) ((Pm-Deps) | ConvertTo-Json -Compress -Depth 4)
    Start-Sleep -Seconds 6
    Check 'two deployments in all' ((Pm-Deps).creates -eq 2)
    Check 'one promoted line per environment' (@(Pm-Events $i.n 'promoted').Count -eq 2) (Pm-Brief $i.n)
}

'promote: release N waits on an unqualified item; N+1, whose own item qualifies, carries that change and is not promoted either' = {
    $a = Pm-Item '3.10.1' -NoPass
    $b = Pm-Item '3.10.2'
    Check 'the item of N+1 is told that the item of N holds it back' (Wait-Until -Seconds 40 { @(@(Pm-Events $b.n 'promotion-waiting') | Where-Object { $_.body -match "release 3\.10\.2 .*#$($a.n), carried by this release, does not qualify" }).Count -eq 1 }) (Pm-Brief $b.n)
    Start-Sleep -Seconds 6
    Check 'no deployment of either release' ((Pm-Deps).creates -eq 0) ((Pm-Deps) | ConvertTo-Json -Compress -Depth 4)
}

'promote: a master commit in the release that is no queued item''s merge commit keeps the release waiting, naming the commit' = {
    Pm-Base
    $stray = Pm-Sha
    [void](Fake 'promote-history' @{ shas = @($stray) })
    $i = Pm-Item '3.11.1'
    Check 'the item is told the commit' (Wait-Until -Seconds 40 { @(@(Pm-Events $i.n 'promotion-waiting') | Where-Object { $_.body -match "commit $($stray.Substring(0,7)) in the release is not the merge commit of a factory work item" }).Count -eq 1 }) (Pm-Brief $i.n)
    Start-Sleep -Seconds 6
    Check 'no deployment' ((Pm-Deps).creates -eq 0)
}

'promote: when the commit running in the target is not known, the release waits (fail closed)' = {
    $i = Pm-Item '3.12.1' -NoBase
    Check 'the item is told the running commit is not known' (Wait-Until -Seconds 40 { @(@(Pm-Events $i.n 'promotion-waiting') | Where-Object { $_.body -match 'the commit running in uat is not known' }).Count -eq 1 }) (Pm-Brief $i.n)
    Start-Sleep -Seconds 6
    Check 'no deployment' ((Pm-Deps).creates -eq 0)
}


'promote: a docs-only item in Done whose gateway merge after a PASS is in the release counts as factory work: the release is promoted' = {
    $d = Pm-DoneItem
    $i = Pm-Item '3.13.1'
    Check 'the release goes to uat' (Wait-Until -Seconds 40 { @(Pm-Dep '3.13.1' 'uat').Count -eq 1 }) (Pm-Brief $i.n)
    Check 'no stranger commit was named' (@(@(Pm-Events $i.n 'promotion-waiting') | Where-Object { $_.body -match 'not the merge commit' }).Count -eq 0) (Pm-Brief $i.n)
}

'promote: a factory merge in the release that is not settled yet makes the release wait, then it is promoted' = {
    $d = Pm-DoneItem 'release-candidate'
    $i = Pm-Item '3.14.1'
    Check 'the release waits, naming the unsettled merge' (Wait-Until -Seconds 40 { @(@(Pm-Events $i.n 'promotion-waiting') | Where-Object { $_.body -match "#$($d.n)'s merge \($($d.sha.Substring(0,7))\) is not settled yet \(awaiting release-candidate\)" }).Count -eq 1 }) (Pm-Brief $i.n)
    Check 'no deployment meanwhile' ((Pm-Deps).creates -eq 0)
    Set-ItemState $d.n { param($st) $st.merge.awaiting = $null; $st.merge.done_at = (Get-Date).ToUniversalTime().ToString('o') }
    Check 'settled: the release goes to uat' (Wait-Until -Seconds 40 { @(Pm-Dep '3.14.1' 'uat').Count -eq 1 }) (Pm-Brief $i.n)
}

# The Sign-off of a created deployment: the gateway cannot answer it while the interruption calls are refused, so the scenario changes the world
# first, then lets it answer.

'promote: the health reads are kept in the item state, and a Sign-off after a gateway restart proceeds while tdd is Healthy' = {
    $i = Pm-Created '3.15.1'
    Check 'the reads are kept in the state comment of the item' ((Get-ItemState $i.n).ContainsKey('promote_health')) ((Get-ItemState $i.n) | ConvertTo-Json -Compress -Depth 6)
    Restart-Gateway
    [void](Fake 'octo-forbid' @{})
    Check 'Proceed' (Wait-Until -Seconds 40 { @(Pm-Dep '3.15.1' 'uat')[0].result -eq 'Proceed' }) ((Pm-Deps) | ConvertTo-Json -Compress -Depth 4)
}

'promote: a newer release running in tdd does not abort the Sign-off of the uat deployment' = {
    $i = Pm-Created '3.16.1'
    [void](Fake 'app-build' @{ version = '9.9.9' })
    [void](Fake 'octo-forbid' @{})
    Check 'Proceed' (Wait-Until -Seconds 40 { @(Pm-Dep '3.16.1' 'uat')[0].result -eq 'Proceed' }) ((Pm-Deps) | ConvertTo-Json -Compress -Depth 4)
}

'promote: a Hold label put on an item while the Sign-off waits aborts it' = {
    $i = Pm-Created '3.17.1'
    [void](Invoke-RestMethod "http://127.0.0.1:$($script:FakePort)/repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders/issues/$($i.n)/labels" -Method POST -Headers @{ Authorization = 'Bearer fake-operator' } -Body (@{ labels = @('Hold') } | ConvertTo-Json) -ContentType 'application/json')
    [void](Fake 'octo-forbid' @{})
    Check 'Abort' (Wait-Until -Seconds 40 { @(Pm-Dep '3.17.1' 'uat')[0].result -eq 'Abort' }) ((Pm-Deps) | ConvertTo-Json -Compress -Depth 4)
    Check 'journaled with the Hold label' (Wait-Until -Seconds 20 { @(@(Pm-Events $i.n 'promotion-signoff') | Where-Object { $_.body -match 'Abort' -and $_.body -match "'Hold' label" }).Count -eq 1 }) (Pm-Brief $i.n)
}

'promote: a waiting reason (a merge in the release not settled) leaves the Sign-off pending; settled, it proceeds' = {
    $d = Pm-DoneItem
    $i = Pm-Created '3.18.1'
    Set-ItemState $d.n { param($st) $st.merge.awaiting = 'release-candidate'; $st.merge.Remove('done_at') }
    [void](Fake 'octo-forbid' @{})
    Check 'the Sign-off wait is explained' (Wait-Until -Seconds 40 { @(@(Pm-Events $i.n 'promotion-waiting') | Where-Object { $_.body -match 'Sign-off of task .* waits: .*not settled yet' }).Count -eq 1 }) (Pm-Brief $i.n)
    Check 'and it stays pending' (@(Pm-Dep '3.18.1' 'uat')[0].pending) ((Pm-Deps) | ConvertTo-Json -Compress -Depth 4)
    Set-ItemState $d.n { param($st) $st.merge.awaiting = $null; $st.merge.done_at = (Get-Date).ToUniversalTime().ToString('o') }
    Check 'settled: Proceed' (Wait-Until -Seconds 40 { @(Pm-Dep '3.18.1' 'uat')[0].result -eq 'Proceed' }) ((Pm-Deps) | ConvertTo-Json -Compress -Depth 4)
}

'promote: with a newer release already in tdd, the older one is not deployed to uat; the newer one carries its items' = {
    $a = Pm-Item '3.19.1'
    $b = Pm-Item '3.19.2'
    Check 'the newer release goes to uat' (Wait-Until -Seconds 40 { @(Pm-Dep '3.19.2' 'uat').Count -eq 1 }) (Pm-Brief $b.n)
    Start-Sleep -Seconds 6
    Check 'the older one is never deployed to uat (no aborted deployment)' (@(Pm-Dep '3.19.1' 'uat').Count -eq 0) ((Pm-Deps) | ConvertTo-Json -Compress -Depth 4)
    Check 'the older release''s item is carried: journaled as promoted with the newer release' (Wait-Until -Seconds 20 { @(@(Pm-Events $a.n 'promoted') | Where-Object { $_.body -match 'release 3\.19\.2 to \*\*uat\*\*' }).Count -eq 1 }) (Pm-Brief $a.n)
}


'promote: a Sign-off that waits on a reason that never clears is aborted after signoffWaitHours (0.03 h in the suite), once [full]' = {
    $d = Pm-DoneItem
    $i = Pm-Created '3.20.1'
    Set-ItemState $d.n { param($st) $st.merge.awaiting = 'release-candidate'; $st.merge.Remove('done_at') }
    [void](Fake 'octo-forbid' @{})
    Check 'the Sign-off waits first' (Wait-Until -Seconds 120 { @(@(Pm-Events $i.n 'promotion-waiting') | Where-Object { $_.body -match 'Sign-off of task .* waits' }).Count -ge 1 }) (Pm-Brief $i.n)
    Check 'since when is recorded on the item' (Wait-Until -Seconds 120 { $u = (Get-ItemState $i.n).merge.promoted.uat; "$(if ($u -is [System.Collections.IDictionary]) { $u['signoff_wait_since'] } elseif ($u -and $u.PSObject.Properties['signoff_wait_since']) { $u.signoff_wait_since })" -ne '' }) ((Get-ItemState $i.n) | ConvertTo-Json -Compress -Depth 6)
    Check 'after the limit it is aborted' (Wait-Until -Seconds 480 { @(Pm-Dep '3.20.1' 'uat')[0].result -eq 'Abort' }) ((Pm-Deps) | ConvertTo-Json -Compress -Depth 4)
    Check 'journaled once with the wait and the reason' (Wait-Until -Seconds 20 { @(@(Pm-Events $i.n 'promotion-signoff') | Where-Object { $_.body -match 'Abort' -and $_.body -match 'waited .* h at the Sign-off .*not settled yet' }).Count -eq 1 }) (Pm-Brief $i.n)
}

}
