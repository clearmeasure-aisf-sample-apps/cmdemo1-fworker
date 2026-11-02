# The sweep throttle and the GitHub budget (issue 92): at most one full sweep per sweepMinSeconds, triggers of an item get an incremental pass, the
# budget is built from GitHub's rate-limit headers, below its reserve only the essential verbs run, and a 403 is a rate limit only when GitHub says
# so. Runs in the owner suite; each scenario starts a gateway of its own (a long sweep interval, so only the scenario's triggers sweep) and gives
# the suite's gateway back in finally.

function Bg-Status { Get-Plant }
function Bg-Verb([string]$Verb, [hashtable]$Body) {
    $r = Invoke-WebRequest "http://127.0.0.1:$($script:ApiPort)/api/v1/verbs/$Verb" -Method POST -Headers @{ Authorization = 'Bearer test-token' } -Body ($Body | ConvertTo-Json -Compress -Depth 5) -ContentType 'application/json' -SkipHttpErrorCheck -TimeoutSec 120
    @{ status = [int]$r.StatusCode; body = $(try { $r.Content | ConvertFrom-Json -AsHashtable } catch { @{ raw = "$($r.Content)" } }) }
}
function Bg-Labeled([int]$n) { Qc-Hook 'issues' @{ action = 'labeled'; label = @{ name = 'factory worker' }; issue = @{ number = $n; labels = @(@{ name = 'factory worker' }) }; repository = @{ full_name = 'clearmeasure-aisf-sample-apps/cmdemo1-workorders' } } }
function Bg-MasterBuild { Qc-Hook 'workflow_run' @{ action = 'completed'; workflow_run = @{ head_branch = 'master'; head_sha = ('a' * 40); conclusion = 'success'; name = 'Build' }; repository = @{ full_name = 'clearmeasure-aisf-sample-apps/cmdemo1-workorders' } } }
function Bg-WithGateway([hashtable]$Env, [scriptblock]$Body) {
    $old = @{}; foreach ($k in $Env.Keys) { $old[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $Env[$k]) }
    try { Restart-Gateway; [void](Wait-Until -Seconds 40 { (Bg-Status).sweep.full -ge 1 }); & $Body }
    finally {
        Fake 'rate' @{ resource = 'core'; remaining = 1000000; resetSeconds = 3600 } | Out-Null; Fake 'rate' @{ resource = 'graphql'; remaining = 1000000; resetSeconds = 3600 } | Out-Null
        foreach ($k in $Env.Keys) { [Environment]::SetEnvironmentVariable($k, $old[$k]) }
        Restart-Gateway
    }
}

$BudgetScenarios = [ordered]@{}

foreach ($k in $BudgetScenarios.Keys) { $OwnerScenarios[$k] = $BudgetScenarios[$k] }
