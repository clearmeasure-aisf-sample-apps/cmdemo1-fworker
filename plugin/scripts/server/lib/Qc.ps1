# PR QC environment, gateway side (rule scripts). Design: docs/design/pr-qc-environment.md.
# The QC record per pull request lives in the work item's state comment (key 'qc'), changed only at transitions and only by these verbs.
# Lease, heartbeat, URL, client address, probe result and log tail live in gateway memory (gateway/Qc.cs), never here and never in a comment.
# Every string a worker supplies passes ConvertTo-QcText before it is persisted: Get-State finds the JSON with a regex on the code fence, so a backtick would corrupt it.

$script:QcLiveStates = @('requested', 'reset', 'assigned', 'building', 'running', 'ready', 'stale')
$script:QcFailCodes = @('build-failed', 'start-failed', 'health-timeout', 'no-reachable-url', 'unreachable-url', 'no-dotnet', 'port-busy', 'no-memory', 'profile-incomplete', 'trust-refused', 'app-down', 'no-worker')
$script:QcTeardownReasons = @('merged', 'closed', 'head-changed', 'idle', 'lifetime', 'person', 'left-validation', 'label-changed', 'worker-stopped', 'drafted')
$script:QcMoves = @{
    'assigned' = @('requested', 'reset', 'assigned'); 'building' = @('assigned'); 'running' = @('building'); 'ready' = @('building', 'running', 'ready')
    'failed' = @('requested', 'reset', 'assigned', 'building', 'running', 'ready', 'stale'); 'stale' = @('assigned', 'building', 'running', 'ready')
    'torn-down' = @('requested', 'reset', 'assigned', 'building', 'running', 'ready', 'stale', 'failed')
}

# The merge gate (design 3.6): for an item without the Auto Merge label under mergePolicy peer-approved, merge needs a QC run for the current head
# (qc.requireRun) and a GitHub approval of the current head by a person in humanApprovers (qc.requireApproval). Both default to true under
# peer-approved and false otherwise; a null in the config means "default". Read from the raw config so a merge never trips the QC config guards.
function Get-QcGate {
    $c = Get-Cfg; $peer = ("$($c.mergePolicy)" -eq 'peer-approved')
    $q = if (Test-Has $c 'qc') { $c.qc } else { @{} }
    $run = if (Test-Has $q 'requireRun') { [bool]$q.requireRun } else { $peer }
    $appr = if (Test-Has $q 'requireApproval') { [bool]$q.requireApproval } else { $peer }
    return @{ requireRun = $run; requireApproval = $appr }
}
# True when a non-auto-merge item can be merged through the gate at all: owner mode (QC exists), peer-approved, and at least one requirement on.
function Test-QcGateActive {
    if (-not (Test-QcEnabled)) { return $false }
    if ("$((Get-Cfg).mergePolicy)" -ne 'peer-approved') { return $false }
    $g = Get-QcGate
    return ($g.requireRun -or $g.requireApproval)
}

function Test-QcEnabled { ($env:FACTORY_OWNER_MODE -eq '1') -and ("$env:FACTORY_TOKEN_SECRET".Length -ge 16) }

# ---- records ----------------------------------------------------------------------------------------------------------
function Get-QcRec($s, [int]$Pr) { if ((Test-Has $s 'qc') -and $s.qc.Contains("$Pr")) { return $s.qc["$Pr"] }; return $null }

