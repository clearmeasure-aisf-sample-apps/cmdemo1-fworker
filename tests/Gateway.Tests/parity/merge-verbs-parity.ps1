# W9.3 (#355): runs the REAL rule scripts of the merge train verbs (plugin/scripts/lib/Verbs.Merge.ps1, Verbs.Inspect.ps1, BaseUpdate.ps1) on the cases of
# MergeVerbParityTests.cs, with a scripted GitHub and a scripted item state in place of the network, and writes what each verb answered (the answer the
# gateway would hand to the caller, with its exit code) as JSON. The test then runs the same cases through the C# verbs (v2) and compares the two answers
# field by field. Nothing here touches the network or a secret.
#   pwsh -NoProfile -File merge-verbs-parity.ps1 -Cases cases.json -Out answers.json
param([Parameter(Mandatory)][string]$Cases, [Parameter(Mandatory)][string]$Out)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$env:FACTORY_CONFIG = Join-Path $root 'config/instance.json'
foreach ($f in 'Core', 'Board', 'Prs', 'Checks', 'Verbs.Item', 'Apps', 'Verbs.Pr', 'Verbs.Inspect', 'Verbs.Merge', 'Verbs.Delivery', 'Verbs.Promote', 'Verbs.Sweep', 'Models', 'Journal', 'Owner', 'Qc', 'OperatorRelease', 'BaseUpdate') { . (Join-Path $root "plugin/scripts/lib/$f.ps1") }

$script:Case = $null
$script:Calls = @()

# --- the scripted world ------------------------------------------------------------------------------------------------------------------------
function Start-Sleep { param($Seconds, $Milliseconds) }

function New-Pr($p) {
    [pscustomobject]@{ number = [int]$p.number; state = "$($p.state)"; merged = $false; head = [pscustomobject]@{ ref = "$($p.ref)"; sha = "$($p.sha)" }
        mergeable = $p.mergeable; mergeable_state = "$($p.mergeable_state)"; title = "x"; draft = $false }
}

# the HTTP layer under Invoke-Gh: the real Invoke-Gh keeps its own handling of statuses (-Allow, rejects)
function Invoke-GhRaw {
    param([string]$Method = 'GET', [string]$Path, $Body = $null, [string]$Accept = '')
    $g = $script:Case.github
    $p = $Path -replace '^repos/[^/]+/[^/]+/', ''
    $script:Calls += "$Method $p"
    $ok = { param($b) @{ status = 200; body = $b; headers = @{} } }
    if ($Method -eq 'GET' -and $p -match '^branches/') { return & $ok ([pscustomobject]@{ commit = [pscustomobject]@{ sha = "$($g.head)" } }) }
    if ($Method -eq 'GET' -and $p -match '^compare/([^./]+)\.\.\.(.+?)(\?per_page=1)?$') {
        $from = $Matches[1]; $to = $Matches[2]
        if ($Matches[3]) {   # behind_by of the default branch against a branch
            if ($g.ContainsKey('behindStatus') -and $g.behindStatus.ContainsKey($to)) { return @{ status = [int]$g.behindStatus[$to]; body = [pscustomobject]@{}; headers = @{} } }
            $n = if ($g.behind -and $g.behind.ContainsKey($to)) { [int]$g.behind[$to] } else { 0 }
            return & $ok ([pscustomobject]@{ behind_by = $n })
        }
        if ($null -eq $g.changed) { return @{ status = 404; body = [pscustomobject]@{}; headers = @{} } }
        return & $ok ([pscustomobject]@{ files = @($g.changed | ForEach-Object { [pscustomobject]@{ filename = "$_" } }) })
    }
    if ($Method -eq 'GET' -and $p -match '^pulls\?state=open') { return & $ok @($g.open | ForEach-Object { New-Pr $_ }) }
    if ($Method -eq 'GET' -and $p -match '^pulls/(\d+)/files') {
        $files = $g.files["$($Matches[1])"]; if ($page = [regex]::Match($p, '[?&]page=(\d+)').Groups[1].Value) { if ([int]$page -gt 1) { $files = @() } }
        return & $ok @($files | ForEach-Object { [pscustomobject]@{ filename = "$_"; additions = 1; deletions = 0; status = 'modified' } })
    }
    if ($Method -eq 'GET' -and $p -match '^pulls/(\d+)$') {
        $n = "$($Matches[1])"
        # the reads of one pull request answer in turn what the case scripted (a first answer with mergeable null, then the computed one)
        $seq = $g.pulls[$n]; $k = "reads$n"; if (-not $script:Case.ContainsKey($k)) { $script:Case[$k] = 0 }
        $i = [Math]::Min($script:Case[$k], @($seq).Count - 1); $script:Case[$k] = $script:Case[$k] + 1
        return & $ok (New-Pr @($seq)[$i])
    }
    if ($Method -eq 'PUT' -and $p -match '^pulls/(\d+)/update-branch$') {
        $s = [int]$g.update
        return @{ status = $s; body = $(if ($s -eq 202) { [pscustomobject]@{ message = 'Updating pull request branch.' } } else { [pscustomobject]@{ message = 'refused' } }); headers = @{} }
    }
    throw "the scripted GitHub has no answer for $Method $Path"
}

function Get-ItemInfo([int]$Item) {
    $i = $script:Case.items["$Item"]
    if (-not $i) { Reject 12 'item-not-found' "issue #$Item not found" }
    @{ number = $Item; nodeId = 'x'; open = [bool]$i.v1.open; title = 't'; body = ''; url = ''; createdAt = ''; labels = @($i.v1.labels); cardId = 'c'; column = "$($i.v1.column)" }
}

function Get-State([int]$Item) {
    $i = $script:Case.items["$Item"]
    if (-not $i) { return @{ commentId = $null; state = @{}; comments = @() } }
    @{ commentId = 1; state = $i.v1.state; comments = @() }
}

# the QC record of the case (Get-BackmergeHold reads it from the state: the case puts it there)

# --- the cases --------------------------------------------------------------------------------------------------------------------------------
$caseList = Get-Content -Raw $Cases | ConvertFrom-Json -AsHashtable -DateKind String
$answers = @()
foreach ($c in $caseList) {
    $script:Case = $c; $script:Calls = @()
    $verb = "$($c.verb)"; $a = $c.args
    $exit = 0
    try {
        $r = & ('Verb_' + ($verb -replace '-', '_')) $a
        $r.verb = $verb
        if (-not $r.ContainsKey('ok')) { $r.ok = $true }
        $r.exit = 0
    } catch [FactoryReject] {
        $e = $_.Exception
        $r = @{ ok = $false; verb = $verb; code = $e.Code; reason = $e.Message }
        foreach ($k in $e.Extra.Keys) { $r[$k] = $e.Extra[$k] }
        $r.exit = $e.Exit
    } catch {
        # (a fault of the harness or the script: the test shows it with the case)
        $r = @{ ok = $false; verb = $verb; code = 'script-fault'; reason = "$($_.Exception.Message) $($_.ScriptStackTrace)"; exit = 99 }
    }
    $answers += @{ name = "$($c.name)"; exit = [int]$r.exit; answer = $r; calls = @($script:Calls) }
}
$answers | ConvertTo-Json -Depth 12 | Set-Content -Path $Out -Encoding utf8
