#!/usr/bin/env pwsh
# rule.ps1 --server <verb>  < args.json     the rule scripts the gateway still runs (gateway/Runner.cs, the gateway's own pwsh; never a worker: the plugin a worker downloads leaves this directory out).
# What is left is the gateway's own housekeeping, which an inert gateway (an environment the engine does not act in) still runs (gateway/Verbs/VerbRouters.cs InertVerbs.Housekeeping):
#   - the revoked credentials (revocations, revoke-worker) and the operator's release record (operator-releases, operator-release);
#   - the transcript ref store (transcript-save, -load, -prune): the durable store where the engine does not act, and the read-only load of a transcript written before the move to
#     factory.Transcripts where it acts (gateway/TranscriptBackends.cs, until 2026-11-13).
# docs/design/verb-inventory.json says which rule each one is.
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

$ServerVerbs = @('transcript-save', 'transcript-load', 'transcript-prune', 'revocations', 'revoke-worker', 'operator-releases', 'operator-release')
$NoLockVerbs = @('transcript-save', 'transcript-load', 'transcript-prune')

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
    foreach ($f in 'Core', 'Verbs.Item', 'Apps', 'Owner', 'OperatorRelease', 'Transcripts') { . (Join-Path $here "lib/$f.ps1") }
    $ErrorActionPreference = 'Stop'
    try {
        if ($verb -notin $ServerVerbs) { Reject 2 'unknown-verb' "unknown verb '$verb'" }
        $raw = [Console]::In.ReadToEnd()
        $a = if ($raw.Trim()) { $raw | ConvertFrom-Json -AsHashtable -DateKind String } else { @{} }
        # the AI session the gateway runs a verb for (Program.cs SessionLinkOf) is no argument of a verb: none left here writes a comment that would carry it
        if ($a -is [System.Collections.IDictionary] -and $a.ContainsKey('_session')) { $a.Remove('_session') }
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
