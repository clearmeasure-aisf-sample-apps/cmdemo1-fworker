#!/usr/bin/env pwsh
# rule.ps1 --server <verb>  < args.json     the rule scripts the gateway still runs (gateway/Runner.cs, the gateway's own pwsh; never a worker: the plugin a worker downloads leaves this directory out).
# Two kinds of verb are left (V3.3; docs/design/verb-inventory.json says which rule each one is):
#   - the fallbacks of the verbs v2 answers, where a v2 route cannot answer: the hybrid merge (merge, approve-pr, clear-hold, base-watch, base-update-branch for an item the saga does not own, and the
#     merge policy other than auto), the transcript ref store (transcript-*), ingest-event without the translator and journal without the ledger;
#   - the gateway's own housekeeping, which an inert gateway (an environment the engine does not act in) still runs: the revoked credentials (revocations, revoke-worker) and the operator's release
#     record (operator-releases, operator-release).
# Every other verb is v2's alone, and this script answers it unknown-verb (exit 2). The client is plugin/scripts/factory.ps1, which has no server mode.
#
# Exit codes: 0 ok, 2 usage, 10 gate refused, 11 stale, 12 not found, 20 retryable external, 21 accepted/pending, 30 config/secret, 40 claim held.
$Rest = [string[]]$args
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
. (Join-Path $here '../lib/Paths.ps1')
. (Join-Path $here '../lib/Config.ps1')
# test-only (Tier 1 golden writes, tests/scenarios/golden/README.md): the cause id of the harness's call a rule-script run serves (set below; lib/Core.ps1 Get-FactoryCause reads it)
$script:FactoryCause = $null

$ServerVerbs = @('merge', 'approve-pr', 'clear-hold', 'base-watch', 'base-update-branch', 'transcript-save', 'transcript-load', 'transcript-prune', 'ingest-event', 'journal', 'revocations', 'revoke-worker', 'operator-releases', 'operator-release')
$NoLockVerbs = @('ingest-event', 'base-watch', 'base-update-branch', 'transcript-save', 'transcript-load', 'transcript-prune')

function Write-Out($obj, [int]$code) {
    $obj.exit = $code
    [Console]::Out.WriteLine(($obj | ConvertTo-Json -Depth 14 -Compress))
    exit $code
}

# the rate limit this call saw on GitHub, for the gateway's shared budget (gateway/GitHubBudget.cs, issue 92); the gateway takes the field out of
# the answer before anyone else reads it
function Add-GhReport($o) { try { $g = Get-GhRateReport; if ($g -and $o -is [System.Collections.IDictionary]) { $o['_github'] = $g } } catch { } }
if ($Rest.Count -ge 2 -and $Rest[0] -eq '--server') {
    $verb = $Rest[1]
    foreach ($f in 'Core', 'Board', 'Prs', 'Checks', 'Verbs.Item', 'Apps', 'Verbs.Pr', 'Verbs.Inspect', 'Verbs.Merge', 'Verbs.Delivery', 'Verbs.Sweep', 'Models', 'Journal', 'Owner', 'Qc', 'OperatorRelease', 'BaseUpdate', 'Transcripts') { . (Join-Path $here "lib/$f.ps1") }
    $ErrorActionPreference = 'Stop'
    try {
        if ($verb -notin $ServerVerbs) { Reject 2 'unknown-verb' "unknown verb '$verb'" }
        $raw = [Console]::In.ReadToEnd()
        $a = if ($raw.Trim()) { $raw | ConvertFrom-Json -AsHashtable -DateKind String } else { @{} }
        # the AI session the gateway runs this verb for (Program.cs SessionLinkOf): the footer of every comment written for it (Board.ps1)
        $script:FactorySession = $null
        if ($a -is [System.Collections.IDictionary] -and $a.ContainsKey('_session')) { $script:FactorySession = $a['_session']; $a.Remove('_session') }
        # test-only (Tier 1 golden writes, tests/scenarios/golden/README.md), inert unless FACTORY_TEST_CAUSE_HEADER=1: the cause id of the harness's
        # call whose request the gateway served when it started this run (gateway/TestCause.cs), sent as X-Fake-Cause with every write
        $script:FactoryCause = $null
        if ($env:FACTORY_TEST_CAUSE_HEADER -eq '1' -and $a -is [System.Collections.IDictionary] -and $a.ContainsKey('_cause')) { $script:FactoryCause = "$($a['_cause'])"; $a.Remove('_cause') }
        $fn = 'Verb_' + ($verb -replace '-', '_')
        $r = if ((Test-Has $a 'item') -and $verb -notin $NoLockVerbs) { Use-ItemLock ([int]$a.item) { & $fn $a } } else { & $fn $a }
        $r.verb = $verb
        if (-not $r.ContainsKey('ok')) { $r.ok = $true }
        Add-GhReport $r
        Write-Out $r 0
    } catch [FactoryReject] {
        $e = $_.Exception
        # A refused gate call is one data point for the model ladder: repeated refusals on one item mean the session is looping.
        if ($e.Exit -eq 10 -and $verb -in 'complete', 'record-review', 'validation-verdict', 'approve-pr', 'merge', 'open-pr', 'append-body' -and (Test-Has $a 'item')) { try { Add-Refusal ([int]$a.item) } catch { } }
        $o = @{ ok = $false; verb = $verb; code = $e.Code; reason = $e.Message }
        foreach ($k in $e.Extra.Keys) { $o[$k] = $e.Extra[$k] }
        Add-GhReport $o
        Write-Out $o $e.Exit
    } catch {
        [Console]::Error.WriteLine("internal error in $verb : $($_.Exception.Message)`n$($_.ScriptStackTrace)")
        $o = @{ ok = $false; verb = $verb; code = 'internal-error'; reason = "$($_.Exception.Message)"; retryAfterSeconds = 30 }; Add-GhReport $o
        Write-Out $o 20
    }
}
if (-not ($Rest.Count -ge 2 -and $Rest[0] -eq '--server')) { Write-Out @{ ok = $false; code = 'usage'; reason = 'usage: rule.ps1 --server <verb>  < args.json' } 2 }
