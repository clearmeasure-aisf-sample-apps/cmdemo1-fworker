# One CI rule for flakes and pre-existing failures (issue 102, plugin/scripts/lib/Prs.ps1): only the gates (ciGates) decide; a red head is re-run
# once when its failures are checks other than the gates, known flaky or failing on master too; a failure master has as well costs no fix-forward
# round and no demotion and is reported once; a park that ends (expiry or unpark) starts the fix-forward rounds and demotions from 0. The
# scenarios join the suite 'auto' (the session's own verbs and the sweep; the gateway's merge after a PASS is off there).

# The checks of a head as one Build workflow run (runId) plus secret-scan: $Fail are the jobs that failed (and with them "Build result").
function Ci-SetHead([string]$Sha, [string[]]$Fail, [long]$Run, [string[]]$Pass = @()) {
    foreach ($f in $Fail) { Fake 'checks' @{ sha = $Sha; name = $f; conclusion = 'failure'; runId = $Run } | Out-Null }
    foreach ($p in $Pass) { Fake 'checks' @{ sha = $Sha; name = $p; conclusion = 'success'; runId = $Run } | Out-Null }
    Fake 'checks' @{ sha = $Sha; name = 'Build result'; conclusion = $(if ($Fail.Count -gt 0) { 'failure' } else { 'success' }); runId = $Run } | Out-Null
    Fake 'checks' @{ sha = $Sha; name = 'secret-scan'; conclusion = 'success'; runId = $Run + 1 } | Out-Null
}
function Ci-State([int]$n) {
    $c = @((Get-FakeIssue $n).comments | Where-Object { "$($_.body)".StartsWith('<!-- factory-state:') }) | Select-Object -First 1
    if (-not $c) { return @{} }
    $m = [regex]::Match("$($c.body)", '```json\s*([\s\S]*?)```'); if (-not $m.Success) { return @{} }
    return ($m.Groups[1].Value | ConvertFrom-Json -AsHashtable)
}
function Ci-Comments([int]$n, [string]$Like) { , @((Get-FakeIssue $n).comments | Where-Object { "$($_.body)" -like $Like }) }
# An item through Technical Design and an open pull request in Development; returns @{ n; pr; sha }.
function Ci-Item([string]$Title) {
    $n = New-Item-Fake $Title 'x' $L2; Do-TechnicalDesign $n
    $pr = Do-Development $n @('docs/ci.md')
    return @{ n = $n; pr = $pr; sha = (Get-Head $pr) }
}

$CiRuleScenarios = [ordered]@{}

$CiRuleScenarios['ci rule: a check that is not a gate failing alone leaves the head green; the column completes'] = {
    $i = Ci-Item 'Non-gate red'
    Fake 'checks' @{ sha = $i.sha; name = 'QDNETC'; conclusion = 'failure'; app = 'qodana' } | Out-Null
    $r = Do-DevComplete $i.n; CheckExit 'complete is accepted although a non-gate check failed' $r 0
    Check 'the item moved on to Functional Testing' ((Get-FakeIssue $i.n).column -eq 'Functional Testing') "column $((Get-FakeIssue $i.n).column)"
    $g = F 'w1' @('pr-checks', '--item', $i.n, '--brief')
    Check 'pr-checks says success and still lists the failed non-gate check' ($g.status -eq 'success' -and @($g.failed_checks) -contains 'QDNETC') ($g | ConvertTo-Json -Compress -Depth 4)
}

# The merge path (the owner suite: the gateway merges a PASS by itself). A PASS whose head turns red on a check master fails too is re-run once,
# then waits as a master defect without a demotion; once master passes the check again the gateway updates the branch and merges the new head.
$CiRuleOwnerScenarios = [ordered]@{}
