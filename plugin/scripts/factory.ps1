#!/usr/bin/env pwsh
# factory.ps1 <verb> [--flag value ...]        client: sends the verb to the gateway (no GitHub or Octopus secret on this machine)
# factory.ps1 --server <verb>  < args.json     server: runs the rule script (gateway only; holds GH_TOKEN, OCTOPUS_API_KEY)
# factory.ps1 subscribe --own --name N [--spawn]  client: register as a worker; assignments arrive as item.assigned (token saved, not printed)
# factory.ps1 wait-event --item N | worker-token --name N
# factory.ps1 subscribe [--spawn]              client: prints one normalized event per line; with --spawn starts a headless worker per offer
#
# Exit codes: 0 ok, 2 usage, 10 gate refused, 11 stale, 12 not found, 20 retryable external, 21 accepted/pending, 30 config/secret, 40 claim held.
# Plain $args, not a param block: an advanced script would bind --pr to -ProgressAction.
$Rest = [string[]]$args
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot

$ServerVerbs = @('get-item', 'append-body', 'comment', 'claim-item', 'record-review', 'move-next', 'complete', 'open-pr', 'pr-checks',
    'wait-for-checks', 'validation-verdict', 'inspect-facts', 'approve-pr', 'clear-hold', 'merge', 'get-delivery', 'model-tier', 'accept', 'release', 'my-items', 'journal', 'settle', 'sync-board', 'ingest-event', 'check-secrets',
    'qc-sync', 'qc-link', 'qc-view', 'qc-request', 'qc-reset', 'qc-teardown', 'qc-list', 'qc-job', 'qc-progress', 'qc-report', 'qc-fail', 'qc-stopped', 'qc-transition', 'qc-approval')
$NoLockVerbs = @('inspect-facts', 'get-item', 'pr-checks', 'wait-for-checks', 'get-delivery', 'sync-board', 'ingest-event', 'check-secrets', 'qc-sync', 'qc-list', 'qc-view', 'qc-job')

function Write-Out($obj, [int]$code) {
    $obj.exit = $code
    [Console]::Out.WriteLine(($obj | ConvertTo-Json -Depth 14 -Compress))
    exit $code
}

# ---------------------------------------------------------------- server mode
if ($Rest.Count -ge 2 -and $Rest[0] -eq '--server') {
    $verb = $Rest[1]
    foreach ($f in 'Core', 'Board', 'Prs', 'Verbs.Item', 'Apps', 'Verbs.Pr', 'Verbs.Inspect', 'Verbs.Merge', 'Verbs.Delivery', 'Verbs.Sweep', 'Models', 'Journal', 'Owner', 'Qc') { . (Join-Path $here "lib/$f.ps1") }
    $ErrorActionPreference = 'Stop'
    try {
        if ($verb -notin $ServerVerbs) { Reject 2 'unknown-verb' "unknown verb '$verb'" }
        $raw = [Console]::In.ReadToEnd()
        $a = if ($raw.Trim()) { $raw | ConvertFrom-Json -AsHashtable -DateKind String } else { @{} }
        $fn = 'Verb_' + ($verb -replace '-', '_')
        $r = if ((Test-Has $a 'item') -and $verb -notin $NoLockVerbs) { Use-ItemLock ([int]$a.item) { & $fn $a } } else { & $fn $a }
        $r.verb = $verb
        if (-not $r.ContainsKey('ok')) { $r.ok = $true }
        Write-Out $r 0
    } catch [FactoryReject] {
        $e = $_.Exception
        # A refused gate call is one data point for the model ladder: repeated refusals on one item mean the session is looping.
        if ($e.Exit -eq 10 -and $verb -in 'complete', 'record-review', 'validation-verdict', 'approve-pr', 'merge', 'open-pr', 'append-body' -and (Test-Has $a 'item')) { try { Add-Refusal ([int]$a.item) } catch { } }
        $o = @{ ok = $false; verb = $verb; code = $e.Code; reason = $e.Message }
        foreach ($k in $e.Extra.Keys) { $o[$k] = $e.Extra[$k] }
        Write-Out $o $e.Exit
    } catch {
        [Console]::Error.WriteLine("internal error in $verb : $($_.Exception.Message)`n$($_.ScriptStackTrace)")
        Write-Out @{ ok = $false; verb = $verb; code = 'internal-error'; reason = "$($_.Exception.Message)"; retryAfterSeconds = 30 } 20
    }
}

# ---------------------------------------------------------------- client mode
function Get-ClientConfig {
    $cfg = @{ url = $env:FACTORY_URL; token = $env:FACTORY_TOKEN }
    $f = Join-Path $HOME '.config/factory-worker/client.json'
    if ((-not $cfg.url -or -not $cfg.token) -and (Test-Path $f)) {
        $j = Get-Content -Raw $f | ConvertFrom-Json -AsHashtable
        if (-not $cfg.url) { $cfg.url = $j.url }; if (-not $cfg.token) { $cfg.token = $j.token }
    }
    if (-not $cfg.url -or -not $cfg.token) { Write-Out @{ ok = $false; code = 'client-not-configured'; reason = 'set FACTORY_URL and FACTORY_TOKEN, or write ~/.config/factory-worker/client.json' } 30 }
    $cfg.url = $cfg.url.TrimEnd('/')
    return $cfg
}

function ConvertFrom-Flags([string[]]$Tokens) {
    $a = @{}
    for ($i = 0; $i -lt $Tokens.Count; $i++) {
        $t = $Tokens[$i]
        if ($t -notmatch '^--(.+)$') { Write-Out @{ ok = $false; code = 'usage'; reason = "unexpected argument '$t'" } 2 }
        $k = $Matches[1]
        $hasVal = ($i + 1 -lt $Tokens.Count) -and ($Tokens[$i + 1] -notmatch '^--')
        $v = if ($hasVal) { $Tokens[++$i] } else { 'true' }
        if ($k -match '^(.+)-file$') {
            if (-not (Test-Path -LiteralPath $v)) { Write-Out @{ ok = $false; code = 'usage'; reason = "file not found: $v" } 2 }
            $a[($Matches[1] -replace '-', '_')] = Get-Content -Raw -LiteralPath $v
        } else {
            $a[$k -replace '-', '_'] = if ($v -ceq 'true') { $true } elseif ($v -ceq 'false') { $false } else { $v }
        }
    }
    return $a
}

$verb = if ($Rest.Count -gt 0) { $Rest[0] } else { '' }
$flagTokens = if ($Rest.Count -gt 1) { $Rest[1..($Rest.Count - 1)] } else { @() }
if (-not $verb) { Write-Out @{ ok = $false; code = 'usage'; reason = 'usage: factory.ps1 <verb> [--flag value]' } 2 }

if ($verb -eq 'model-for') {
    . (Join-Path $here 'lib/Models.ps1')
    $m = ConvertFrom-Flags $flagTokens
    $role = if ($m.ContainsKey('role')) { "$($m.role)" } else { 'subagent' }
    if ($role -notin 'orchestrator', 'subagent') { Write-Out @{ ok = $false; code = 'usage'; reason = 'role must be orchestrator or subagent' } 2 }
    $tier = 0; $why = @(); $note = ''
    $profile = if ($m.ContainsKey('profile')) { "$($m.profile)" } elseif ($env:FACTORY_MODEL_PROFILE) { $env:FACTORY_MODEL_PROFILE } else { '' }
    if ($m.ContainsKey('tier')) { $tier = [int]$m.tier }
    elseif ($m.ContainsKey('item')) {
        try {
            $cc = Get-ClientConfig
            $resp = Invoke-WebRequest -Uri "$($cc.url)/api/v1/verbs/model-tier" -Method POST -Body (@{ item = $m.item } | ConvertTo-Json -Compress) -ContentType 'application/json' -Headers @{ Authorization = "Bearer $($cc.token)" } -SkipHttpErrorCheck -TimeoutSec 60
            $j = $resp.Content | ConvertFrom-Json -AsHashtable
            if ($j.ok) { $tier = [int]$j.tier; $why = @($j.reasons); if (-not $profile -and $j.profile) { $profile = "$($j.profile)" } } else { $note = "gateway refused: $($j.code)" }
        } catch { $note = 'gateway unreachable; using the starting tier' }
    }
    $res = Resolve-ModelFor $role $tier (Get-ModelsConfig) $profile
    $res.reasons = $why; if ($note) { $res.note = $note }
    Write-Out $res 0
}
if ($verb -eq 'subscribe') {
    . (Join-Path $here 'lib/Client.Subscribe.ps1'); $sf = ConvertFrom-Flags $flagTokens
    if ($sf.ContainsKey('own')) { . (Join-Path $here 'lib/Client.Own.ps1'); Invoke-SubscribeOwn $sf } else { Invoke-Subscribe $sf }
}
if ($verb -eq 'worker-token') { . (Join-Path $here 'lib/Client.Own.ps1'); Invoke-WorkerToken (ConvertFrom-Flags $flagTokens) }
if ($verb -eq 'plugin-sync') { . (Join-Path $here 'lib/Client.Plugin.ps1'); Invoke-PluginSync (Get-ClientConfig) }
if ($verb -eq 'wait-event') { . (Join-Path $here 'lib/Client.Own.ps1'); Invoke-WaitEvent (ConvertFrom-Flags $flagTokens) (Get-ClientConfig) }

$a = ConvertFrom-Flags $flagTokens
if (-not $a.ContainsKey('holder')) { $a.holder = if ($env:FACTORY_HOLDER) { $env:FACTORY_HOLDER } else { "$($env:USER)@$([Net.Dns]::GetHostName())" } }
$cfg = Get-ClientConfig
. (Join-Path $here 'lib/Client.Own.ps1')
$callRole = if ($a.ContainsKey('role')) { "$($a.role)" } elseif ($verb -like 'qc-*') { 'qc' } else { '' }
$callToken = if ($verb -eq 'my-items') { Get-WorkerCredential $cfg $a.holder } else { Select-ItemToken $cfg $(if ($a.ContainsKey('item')) { $a.item } else { $null }) $callRole }
$resp = $null
try {
    $resp = Invoke-WebRequest -Uri "$($cfg.url)/api/v1/verbs/$verb" -Method POST -Body ($a | ConvertTo-Json -Depth 10 -Compress) -ContentType 'application/json' `
        -Headers @{ Authorization = "Bearer $callToken" } -SkipHttpErrorCheck -TimeoutSec 170
} catch { Write-Out @{ ok = $false; code = 'gateway-unreachable'; reason = $_.Exception.Message; retryAfterSeconds = 15 } 20 }
$body = $resp.Content
$code = 0
try { $j = $body | ConvertFrom-Json -AsHashtable; if ($j.ContainsKey('exit')) { $code = [int]$j.exit } } catch { $j = $null }
if (-not $j) {
    $code = switch ([int]$resp.StatusCode) { 401 { 30 } 403 { 30 } 404 { 12 } 503 { 20 } default { 20 } }
    [Console]::Out.WriteLine((@{ ok = $false; code = 'gateway-error'; reason = "HTTP $([int]$resp.StatusCode)"; exit = $code } | ConvertTo-Json -Compress)); exit $code
}
[Console]::Out.WriteLine($body.Trim())
exit $code
