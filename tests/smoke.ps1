# No network, no secrets: parses every script and exercises the pure rule functions.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$fail = 0
function Check($name, $ok) { if ($ok) { "  ok   $name" } else { "  FAIL $name"; $script:fail++ } }
"parse"
Get-ChildItem (Join-Path $root 'plugin/scripts') -Recurse -Filter *.ps1 | ForEach-Object {
    $errs = $null; [void][System.Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$null, [ref]$errs)
    Check $_.Name ($errs.Count -eq 0); $errs | ForEach-Object { "       $($_.Message) line $($_.Extent.StartLineNumber)" }
}
$env:FACTORY_CONFIG = Join-Path $root 'config/instance.json'
. (Join-Path $root 'plugin/scripts/lib/Paths.ps1'); . (Join-Path $root 'plugin/scripts/lib/Config.ps1'); . (Join-Path $root 'plugin/scripts/lib/Models.ps1')   # the client's
foreach ($f in 'Core', 'Verbs.Item', 'Apps', 'Owner') { . (Join-Path $root "plugin/scripts/server/lib/$f.ps1") }   # the gateway's rule scripts (V3.3)
"rules"
try { Reject 10 'x' 'y' } catch { Check 'reject carries exit and code' ($_.Exception.Exit -eq 10 -and $_.Exception.Code -eq 'x') }
"model ladder"
$mc = Get-Content -Raw (Join-Path $root 'plugin/scripts/models.default.json') | ConvertFrom-Json -AsHashtable
$m0 = Resolve-ModelFor 'orchestrator' 0 $mc; $m1 = Resolve-ModelFor 'subagent' 1 $mc; $m2 = Resolve-ModelFor 'subagent' 2 $mc; $m9 = Resolve-ModelFor 'subagent' 9 $mc
Check 'default ladder starts on haiku 5.5 at xhigh' ($m0.model -eq 'claude-haiku-5-5' -and $m0.effort -eq 'xhigh')
Check 'tier 1 is sonnet 5.5 at medium' ($m1.model -eq 'claude-sonnet-5-5' -and $m1.effort -eq 'medium')
Check 'tier 2 is opus 5.5 at medium' ($m2.model -eq 'claude-opus-5-5' -and $m2.effort -eq 'medium')
Check 'a tier past the ladder is capped at the top rung' ($m9.model -eq 'claude-opus-5-5')
$cap = $mc | ConvertTo-Json -Depth 6 | ConvertFrom-Json -AsHashtable; $cap.subagent.maxTier = 1
Check 'a machine can cap the ladder' ((Resolve-ModelFor 'subagent' 2 $cap).model -eq 'claude-sonnet-5-5')
$st0 = $mc | ConvertTo-Json -Depth 6 | ConvertFrom-Json -AsHashtable; $st0.orchestrator.startTier = 1
Check 'a machine can raise the starting rung' ((Resolve-ModelFor 'orchestrator' 0 $st0).model -eq 'claude-sonnet-5-5')
$fx = $mc | ConvertTo-Json -Depth 6 | ConvertFrom-Json -AsHashtable; $fx.mode = 'fixed'
Check 'fixed mode ignores the tier' ((Resolve-ModelFor 'subagent' 2 $fx).model -eq 'claude-sonnet-5-5' -and (Resolve-ModelFor 'subagent' 2 $fx).mode -eq 'fixed')
$tp = Resolve-ModelFor 'subagent' 2 $mc 'testing'
Check 'testing profile is haiku 5.5 at low effort' ($tp.model -eq 'claude-haiku-5-5' -and $tp.effort -eq 'low' -and $tp.mode -eq 'profile')
Check 'testing profile never climbs, whatever the tier' ((Resolve-ModelFor 'orchestrator' 2 $mc 'testing').model -eq 'claude-haiku-5-5' -and -not $tp.escalates)
Check 'an unknown profile falls back to the ladder' ((Resolve-ModelFor 'subagent' 1 $mc 'nope').model -eq 'claude-sonnet-5-5')
Check 'the testing profile wins over fixed mode too' ((Resolve-ModelFor 'subagent' 0 $fx 'testing').effort -eq 'low')
# local progress of owned items (lib/Progress.ps1): which places belong to an item, and which files of the work directory never count
. (Join-Path $root 'plugin/scripts/lib/Progress.ps1')
Check 'progress: a branch or file names the item as a whole token' ((Test-NamesItem 'factory-worker/308-development' 308) -and (Test-NamesItem 'brief-308.md' 308) -and (Test-NamesItem '308' 308))
Check 'progress: a longer number or a hash does not name the item' (-not (Test-NamesItem 'factory-worker/1308-development' 308) -and -not (Test-NamesItem 'worktree-agent-a3081f' 308) -and -not (Test-NamesItem '3080.txt' 308))
Check 'progress: the files the client, the launcher and the stream write are never work' ($script:PgWorkSkip.IsMatch('events.log') -and $script:PgWorkSkip.IsMatch('launcher.log') -and $script:PgWorkSkip.IsMatch('session-ended') -and -not $script:PgWorkSkip.IsMatch('brief-308.md'))
$pgDir = Join-Path ([IO.Path]::GetTempPath()) "fw-smoke-progress-$PID"; New-Item -ItemType Directory -Force -Path (Join-Path $pgDir 'progress') | Out-Null
try {
    Set-Content -LiteralPath (Join-Path $pgDir 'progress/308.txt') -Value 'x'
    $pgState = New-ProgressState $pgDir @(); $pgState.owned[308] = [DateTime]::UtcNow.AddMinutes(-1); $pgState.repos = @()
    $pgRep = @(Get-ProgressReport $pgState)
    Check 'progress: a progress file is reported once as the item''s (source marker), then not again until it changes' ($pgRep.Count -eq 1 -and $pgRep[0].item -eq 308 -and $pgRep[0].source -eq 'marker' -and $(foreach ($e in $pgRep) { $pgState.sent[[int]$e.item] = [DateTime]::Parse($e.seen).ToUniversalTime() }; @(Get-ProgressReport $pgState).Count -eq 0))
    Check 'progress: recent work answers a check' ($null -ne (Get-RecentProgress $pgState 308) -and $null -eq (Get-RecentProgress $pgState 309))
    # a file dated in the future (a skewed mount, a restored archive) is no sign of work
    $pgFut = New-ProgressState $pgDir @(); $pgFut.owned[310] = [DateTime]::UtcNow.AddMinutes(-1); $pgFut.repos = @()
    $fut = Join-Path $pgDir 'progress/310.txt'; Set-Content -LiteralPath $fut -Value 'x'; (Get-Item -LiteralPath $fut).LastWriteTimeUtc = [DateTime]::UtcNow.AddHours(3)
    Check 'progress: a file dated in the future is not work' ($null -eq (Get-ItemProgress $pgFut 310 $false ([Diagnostics.Stopwatch]::StartNew())))
} finally { Remove-Item -Recurse -Force -LiteralPath $pgDir -ErrorAction SilentlyContinue }
"config"
# docs/design/cloud-worker-runtimes.md section 5: every runtime is off in the source defaults; one is switched on and sized only in
# config/deploy.overrides.json (a code-owner path). A default that starts executions would spend money from any build of this repository.
$rtCfg = (Get-Content -Raw $env:FACTORY_CONFIG | ConvertFrom-Json -AsHashtable).runtimes
Check 'runtimes: the source defaults exist' ($rtCfg -is [System.Collections.IDictionary] -and $rtCfg.caeJob -is [System.Collections.IDictionary])
Check 'runtimes: no routing rule in the source defaults' (@($rtCfg.routing).Count -eq 0)
Check 'runtimes: no environment block in the source defaults' ($rtCfg.environments -is [System.Collections.IDictionary] -and $rtCfg.environments.Count -eq 0)
foreach ($rk in @($rtCfg.Keys | Where-Object { $_ -ne 'environments' -and $rtCfg[$_] -is [System.Collections.IDictionary] })) {
    $rv = $rtCfg[$rk]
    Check "runtimes.${rk}: off in the source defaults (enabled false, maxConcurrent 0, maxExecutionsPerDay 0, no job named)" ($rv.enabled -eq $false -and $rv.maxConcurrent -eq 0 -and $rv.maxExecutionsPerDay -eq 0 -and "$($rv['jobResourceId'])" -eq '')
}
# The deployed settings: the overrides merge over the defaults as the Dockerfile merges them, and only tdd has a runtime switched on
# (runtimes.environments.<FACTORY_ENVIRONMENT> is the block of that environment; one without an entry has every runtime off).
$cfgDir = Join-Path $PSScriptRoot '..' 'config'; $merged = Join-Path ([IO.Path]::GetTempPath()) "fw-merged-$PID.json"
try {
    $null = & pwsh -NoProfile -File (Join-Path $cfgDir 'apply-overrides.ps1') -Base (Join-Path $cfgDir 'instance.json') -Overrides (Join-Path $cfgDir 'deploy.overrides.json') -Out $merged 2>&1
    Check 'config: the deploy overrides merge over the defaults' ($LASTEXITCODE -eq 0)
    $envs = if (Test-Path $merged) { (Get-Content -Raw $merged | ConvertFrom-Json -AsHashtable).runtimes.environments } else { @{} }
    $t = $envs.tdd
    # apply-overrides adds an environment's block as written, so its names are checked here: each key exists in the base runtimes block
    # (a misspelt setting would otherwise ship as its default), and environment names are lower case (FACTORY_ENVIRONMENT is matched lower-cased).
    $baseRt = (Get-Content -Raw $env:FACTORY_CONFIG | ConvertFrom-Json -AsHashtable).runtimes
    $badKeys = @(foreach ($en in @($envs.Keys)) { foreach ($k in @($envs[$en].Keys)) { if ($k -eq 'environments' -or -not $baseRt.Contains($k)) { "$en.$k" } elseif ($envs[$en][$k] -is [System.Collections.IDictionary]) { foreach ($k2 in @($envs[$en][$k].Keys)) { if (-not $baseRt[$k].Contains($k2)) { "$en.$k.$k2" } } } } })
    Check 'config: every environment names only settings of the base runtimes block' ($badKeys.Count -eq 0); if ($badKeys) { "       unknown: $($badKeys -join ', ')" }
    Check 'config: environment names are lower case' (@($envs.Keys | Where-Object { $_ -cne $_.ToLowerInvariant() }).Count -eq 0)
    Check 'config: tdd routes every phase to octopus (cmdemo1''s default runtime since 2026-10-22), uat and prod to copilot-cloud (inert), connected workers first' (@(@{ e = 'tdd'; r = 'octopus' }, @{ e = 'uat'; r = 'copilot-cloud' }, @{ e = 'prod'; r = 'copilot-cloud' } | Where-Object { $x = $_; $envs[$x.e].preferConnected -eq $true -and @($envs[$x.e].routing | Where-Object { $_.phase -eq '*' -and $_.runtime -eq $x.r }).Count -eq 1 }).Count -eq 3)
    Check 'config: tdd runs at most 2 executions at a time and 30 a day' ($t.caeJob.enabled -eq $true -and $t.caeJob.maxConcurrent -eq 2 -and $t.caeJob.maxExecutionsPerDay -eq 30)
    Check 'config: no runtime is switched on outside tdd' (@($envs.Keys | Where-Object { $_ -ne 'tdd' -and $envs[$_].ContainsKey('caeJob') -and $envs[$_].caeJob.enabled -eq $true }).Count -eq 0)
    $curDep = (Get-Content -Raw $merged | ConvertFrom-Json -AsHashtable).api.cursor
    Check 'config: the deployed api mode is written for tdd only, 3 agents at a time, 60 minutes, 30 launches a day, model auto with optimizeFor cost; off for the measured batch of W9.9c (#437 option A)' ($curDep.enabled -eq $false -and @($curDep.environments.Keys).Count -eq 1 -and $curDep.environments.ContainsKey('tdd') -and $curDep.maxConcurrent -eq 3 -and $curDep.maxMinutes -eq 60 -and $curDep.maxExecutionsPerDay -eq 30 -and $curDep.model -eq 'auto' -and $curDep.optimizeFor -eq 'cost')
    Check 'config: the api mode (Cursor Cloud Agents) is off in the source defaults, one agent at a time, 60 minutes' (((Get-Content -Raw $env:FACTORY_CONFIG | ConvertFrom-Json -AsHashtable).api.cursor | ForEach-Object { $_.enabled -eq $false -and $_.maxConcurrent -eq 1 -and $_.maxMinutes -eq 60 -and -not $_.ContainsKey('apiKey') }))
    $copDep = (Get-Content -Raw $merged | ConvertFrom-Json -AsHashtable).api.copilot
    $copSrc = (Get-Content -Raw $env:FACTORY_CONFIG | ConvertFrom-Json -AsHashtable).api.copilot
    Check 'config: the Copilot cloud agent is off in the source defaults (docs/design/copilot-cloud-agent.md)' ($copSrc.enabled -eq $false -and @($copSrc.environments.Keys).Count -eq 0)
    Check 'config: the deployed Copilot cloud agent is written for tdd only and off there for the measured batch of W9.9c (#437 option A); uat and prod are written ready but off, each with its own gateway URL; no model (Auto)' ($copDep.enabled -eq $true -and $copDep.environments.tdd.enabled -eq $false -and $copDep.environments.uat.enabled -eq $false -and $copDep.environments.prod.enabled -eq $false -and $copDep.environments.tdd.gatewayUrl -like 'https://ca-cmdemo1-tdd-fworker.*' -and $copDep.environments.uat.gatewayUrl -like 'https://ca-cmdemo1-uat-fworker-*' -and $copDep.environments.prod.gatewayUrl -like 'https://ca-cmdemo1-prod-fworker-*' -and "$($copDep.model)" -eq '' -and $copDep.maxConcurrent -eq 3 -and $copDep.maxMinutes -eq 60 -and $copDep.maxExecutionsPerDay -eq 30)
    Check 'config: the Copilot cloud agent uses Auto model selection: no model in the source defaults or the deployed overrides, so no model is sent and GitHub picks Auto (owner 2026-10-20)' (([string]((Get-Content -Raw $env:FACTORY_CONFIG | ConvertFrom-Json -AsHashtable).api.copilot.model)) -eq '' -and ([string]$copDep.model) -eq '')
    Check 'config: the Copilot cloud agent runs one task at a time, 60 minutes, 30 a day, and no token in the file' (((Get-Content -Raw $env:FACTORY_CONFIG | ConvertFrom-Json -AsHashtable).api.copilot | ForEach-Object { $_.maxConcurrent -eq 1 -and $_.maxMinutes -eq 60 -and $_.maxExecutionsPerDay -eq 30 -and -not $_.ContainsKey('token') }))
    Check 'config: the CI release of the Copilot agent''s held workflow runs is on wherever the mode is, a pass every 120 s (owner 2026-10-20: no file guard)' ($copSrc.ciRelease -eq $true -and $copSrc.ciReleaseSeconds -eq 120 -and $copDep.ciRelease -eq $true -and $copDep.ciReleaseSeconds -eq 120)
    # every gateway source, one file at a time: the registration moved from Program.cs to gateway/Composition/ (C5a, #278) and may move again.
    # Wanted: a loop that registers each RuntimeLoop with AddSingleton<IHostedService>; refused: any AddHostedService that makes a RuntimeLoop
    # (the lambda up to `new RuntimeLoop(` with no other Add* call between, or AddHostedService<RuntimeLoop>), anywhere in gateway/
    $gwSources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot '..' 'gateway') -Recurse -Filter '*.cs' -File | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } | ForEach-Object { Get-Content -Raw -LiteralPath $_.FullName })
    $rpSrc = (Get-Content -Raw $env:FACTORY_CONFIG | ConvertFrom-Json -AsHashtable).keeper.replenish
    Check 'replenish: holdWhenWaiting (0) and onlyWhenIdle (false) are in the source defaults, so behaviour is unchanged until a setting turns them on' ($rpSrc.holdWhenWaiting -eq 0 -and $rpSrc.onlyWhenIdle -eq $false)
    $loopEach = 'foreach \([^\n]*?\) (?:builder\.)?services\.AddSingleton<IHostedService>\(sp =>\s*\{(?:(?!\bAdd(?:Singleton|HostedService|Transient|Scoped)\b)[\s\S])*?new RuntimeLoop\('
    $loopHosted = 'AddHostedService(?:<RuntimeLoop>|\((?:(?!\bAdd(?:Singleton|HostedService|Transient|Scoped)\b)[\s\S])*?new RuntimeLoop\()'
    Check 'gateway: every runtime adapter gets its own poll loop (AddSingleton<IHostedService>; AddHostedService would keep only the first RuntimeLoop, so Copilot never relisted)' ($gwSources.Count -gt 0 -and @($gwSources | Where-Object { $_ -match $loopEach }).Count -ge 1 -and @($gwSources | Where-Object { $_ -match $loopHosted }).Count -eq 0)
    $mergedAll = Get-Content -Raw $merged | ConvertFrom-Json -AsHashtable
    Check 'config: the env: label scopes no gateway in the source defaults and only tdd when deployed (uat and prod are switched on by the owner, environment-test-factories.md 3)' (@((Get-Content -Raw $env:FACTORY_CONFIG | ConvertFrom-Json -AsHashtable).envScope).Count -eq 0 -and (@($mergedAll.envScope) -join ',') -eq 'tdd')
    Check 'config: the legacy credential kinds stay accepted in the source defaults (auth.legacyKinds)' ((Get-Content -Raw $env:FACTORY_CONFIG | ConvertFrom-Json -AsHashtable).auth.legacyKinds -eq $true)
} finally { Remove-Item -LiteralPath $merged -ErrorAction SilentlyContinue }

# ---- prompts (docs/design/agents-roles-launch.md 5.6): the rules gateway/Mcp/Prompts.cs applies, plus the body's tool names ------------------
'prompts'
$promptDir = Join-Path $PSScriptRoot '..' 'prompts'
$rtText = Get-Content -Raw (Join-Path $PSScriptRoot '..' 'gateway' 'Mcp' 'RoleTools.cs')
$roleSets = @{}
foreach ($m in [regex]::Matches($rtText, '\["([a-z]+)"\]\s*=\s*\[([^\]]*)\]')) { $roleSets[$m.Groups[1].Value] = @([regex]::Matches($m.Groups[2].Value, '"([a-z_]+)"') | ForEach-Object { $_.Groups[1].Value }) }
$allTools = @($roleSets.Values | ForEach-Object { $_ }) | Sort-Object -Unique
Check 'prompts: the role tool sets are read from RoleTools.cs' ($roleSets.ContainsKey('worker') -and $roleSets['worker'] -contains 'get_prompt')
$pfiles = @(Get-ChildItem -Path $promptDir -Recurse -Filter '*.md' | Where-Object { $_.Name -ne 'README.md' })
$seen = @{}
foreach ($pf in $pfiles) {
    $rel = [IO.Path]::GetRelativePath($promptDir, $pf.FullName).Replace('\', '/')
    $txt = (Get-Content -Raw $pf.FullName).Replace("`r`n", "`n")
    $ok = $txt -match '(?s)^---\n(.*?)\n---\n(.*)$'
    Check "prompts: $rel has front matter" $ok
    if (-not $ok) { continue }
    $head = $Matches[1]; $body = $Matches[2]
    $fm = @{}; foreach ($l in $head -split "`n") { if ($l -match '^([a-z]+):\s*(.*)$') { $fm[$Matches[1]] = $Matches[2].Trim() } }
    Check "prompts: $rel has role, phase, version, tools and includes" (@('role', 'phase', 'version', 'tools', 'includes' | Where-Object { -not $fm.ContainsKey($_) }).Count -eq 0)
    Check "prompts: $rel version is a positive number" ("$($fm.version)" -match '^[1-9][0-9]*$')
    $tools = @(if ($fm.tools -match '^\[(.*)\]$') { $Matches[1] -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ } })
    $incs = @(if ($fm.includes -match '^\[(.*)\]$') { $Matches[1] -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ } })
    $set = $(if ($fm.role -eq 'bootstrap') { 'worker' } else { $fm.role })
    Check "prompts: $rel names a known role" ($fm.role -in 'common', 'bootstrap' -or $roleSets.ContainsKey($fm.role))
    $outside = @($tools | Where-Object { -not ($roleSets.ContainsKey($set) -and $roleSets[$set] -contains $_) })
    Check "prompts: $rel tools are all in the $set role's set" ($outside.Count -eq 0); if ($outside) { "       outside: $($outside -join ', ')" }
    $named = @([regex]::Matches($body, '`([a-z_]+)`') | ForEach-Object { $_.Groups[1].Value } | Where-Object { $allTools -contains $_ } | Sort-Object -Unique)
    $undeclared = @($named | Where-Object { $tools -notcontains $_ })
    Check "prompts: $rel declares every tool its body names" ($undeclared.Count -eq 0); if ($undeclared) { "       undeclared: $($undeclared -join ', ')" }
    Check "prompts: $rel includes exist" (@($incs | Where-Object { -not (Test-Path (Join-Path $promptDir $_)) }).Count -eq 0)
    Check "prompts: $rel names no factory.ps1 and holds no shell variable" (-not $body.Contains('factory.ps1') -and $body -notmatch '\$[A-Za-z_][A-Za-z0-9_]*')
    $key = "$($fm.role)/$($fm.phase)"; Check "prompts: $rel is the only prompt of $key" (-not $seen.ContainsKey($key)); $seen[$key] = $rel
}
foreach ($need in 'worker/technical-design', 'worker/development', 'worker/functional-testing', 'bootstrap/api', 'bootstrap/api-copilot') { Check "prompts: there is a prompt for $need" ($seen.ContainsKey($need)) }
# The declared differences of v2 (issue #468): tests/scenarios/v2-differences.ps1, the pure functions the Tier 1 harness (lib.ps1) uses for every suite
'v2 declared differences'
. (Join-Path $root 'tests/scenarios/v2-differences.ps1')
$vd = Join-Path ([IO.Path]::GetTempPath()) "v2diff-smoke-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
$vdGolden = Join-Path $vd 'golden'
New-Item -ItemType Directory -Force -Path (Join-Path $vdGolden 's1'), (Join-Path $vdGolden 's2') | Out-Null
function Write-VdFile([string]$Path, [string[]]$Lines) { [IO.File]::WriteAllText($Path, (($Lines -join "`n") + "`n"), [Text.UTF8Encoding]::new($false)) }
# the errors of a declared-differences file (its lines) against the golden files of this test, as one string ('' when there is none)
function Get-VdErrors([string[]]$Lines) {
    Write-VdFile (Join-Path $vd 'd.txt') $Lines
    $r = New-V2EffectiveGolden $vdGolden (Join-Path $vd 'd.txt') (Join-Path $vd "out-$([guid]::NewGuid().ToString('N').Substring(0, 6))")
    return ($r.errors -join ' | ')
}
# the real file is true to the real golden files (a pull request that re-records the baseline or edits the file is checked here, on v1 too)
$vdReal = New-V2EffectiveGolden (Join-Path $root 'tests/scenarios/golden') (Join-Path $root 'tests/scenarios/golden/v2-differences.txt') (Join-Path $vd 'real')
Check 'v2 differences: tests/scenarios/golden/v2-differences.txt is valid and true to the golden files' ($vdReal.errors.Count -eq 0)
$vdA = '= rule POST /repos/{repo}/issues/{#1}/comments 201 {"body":"a"}'
$vdL = '= rule PUT /repos/{repo}/issues/{#1}/labels 200 -'
$vdB = '= rule POST /repos/{repo}/issues/{#2}/comments 201 {"body":"b"}'
$vdS = '+ gateway STATE x #{#1}'
$vdT = '~ rule POST /repos/{repo}/issues/{#1}/comments 201 {"body":"late"}'
$vdL2 = '= rule PUT /repos/{repo}/issues/{#1}/labels 204 -'
$vdX = '= rule POST /repos/{repo}/issues/{#1}/comments 201 {"body":"extra"}'
$vdS2 = '+ gateway STATE y #{#1}'
$vdT2 = '~ rule POST /repos/{repo}/issues/{#1}/comments 201 {"body":"tail2"}'
Write-VdFile (Join-Path $vdGolden 's1/a-1.writes.txt') @('# suite: s1', $vdA, $vdL, $vdB, $vdS, $vdT)
Write-VdFile (Join-Path $vdGolden 's2/b-2.writes.txt') @('# suite: s2', $vdA)
$vdWhy = '# why: v2 answers 204 on purpose; issue: #468'
Write-VdFile (Join-Path $vd 'ok.txt') @('# a comment before the first block', '', '@ s1/a-1', "- $vdL", "+ $vdL2", "+ $vdX", "- $vdB", "+ $vdT2", "- $vdS", "+ $vdS2", $vdWhy, '')
$vdOk = New-V2EffectiveGolden $vdGolden (Join-Path $vd 'ok.txt') (Join-Path $vd 'effective')
$vdEff = @([IO.File]::ReadAllLines((Join-Path $vdOk.dir 's1/a-1.writes.txt')) | Where-Object { -not $_.StartsWith('# ') })
Check 'v2 differences: a block applies a change in place, an addition after the last line of its section, a removal and a settled-line change' ($vdOk.errors.Count -eq 0 -and $vdOk.blocks -eq 1 -and $vdOk.dir -ne $vdGolden -and ($vdEff -join "`n") -ceq (@($vdA, $vdL2, $vdX, $vdS2, $vdT, $vdT2) -join "`n"))
Check 'v2 differences: a golden line no block names stays as it is, so a caused line v2 writes differently and no block declares still fails the comparison' ($vdEff -contains $vdA -and $vdEff -contains $vdT -and $vdEff -notcontains $vdL -and $vdEff -notcontains $vdB)
Check 'v2 differences: a scenario with no block compares with its golden file unchanged' ([IO.File]::ReadAllText((Join-Path $vdOk.dir 's2/b-2.writes.txt')) -ceq [IO.File]::ReadAllText((Join-Path $vdGolden 's2/b-2.writes.txt')))
Check 'v2 differences: the golden directory itself is not changed by applying a block' ([IO.File]::ReadAllLines((Join-Path $vdGolden 's1/a-1.writes.txt')) -contains $vdB)
Write-VdFile (Join-Path $vd 'empty.txt') @('# only comments')
$vdNone = New-V2EffectiveGolden $vdGolden (Join-Path $vd 'empty.txt') (Join-Path $vd 'none')
Check 'v2 differences: with no block the golden directory itself is used and nothing is copied' ($vdNone.errors.Count -eq 0 -and $vdNone.blocks -eq 0 -and $vdNone.dir -eq $vdGolden -and -not (Test-Path (Join-Path $vd 'none')))
Check 'v2 differences: a block without # why: is invalid' ((Get-VdErrors @('@ s1/a-1', "- $vdL", "+ $vdL2")) -like "*no '# why:*")
$vdBad = New-V2EffectiveGolden $vdGolden (Join-Path $vd 'd.txt') (Join-Path $vd 'bad')
Check 'v2 differences: a block with an error is applied to nothing (its scenario compares with the golden file as it is)' ($vdBad.errors.Count -gt 0 -and $vdBad.blocks -eq 0 -and $vdBad.dir -eq $vdGolden)
Check 'v2 differences: a - line that is not in the golden file is invalid' ((Get-VdErrors @('@ s1/a-1', '- = rule POST /nowhere 201 -', $vdWhy)) -like "*the '-' line is not in the golden file*")
Check 'v2 differences: a block for a scenario with no golden file is invalid' ((Get-VdErrors @('@ s1/zz-9', "+ $vdX", $vdWhy)) -like '*there is no golden file*')
Check 'v2 differences: a block that declares nothing is invalid' ((Get-VdErrors @('@ s1/a-1', $vdWhy)) -like '*declares no*')
Check 'v2 differences: a + line equal to its - line declares no difference' ((Get-VdErrors @('@ s1/a-1', "- $vdL", "+ $vdL", $vdWhy)) -like '*declares no difference*')
Check 'v2 differences: a line that is not understood is invalid' ((Get-VdErrors @('@ s1/a-1', 'foo', $vdWhy)) -like '*not understood*')
Check 'v2 differences: a - or + line that holds no golden line is invalid' ((Get-VdErrors @('@ s1/a-1', '- hello', $vdWhy)) -like '*does not hold a golden line*')
Check 'v2 differences: a - or + line outside a block is invalid' ((Get-VdErrors @("+ $vdX")) -like '*outside a block*')
Check 'v2 differences: a name that is not <suite>/<slug> is invalid' ((Get-VdErrors @('@ S1/a', "+ $vdX", $vdWhy)) -like '*is not <suite>/<slug>*')
Check 'v2 differences: a scenario with two blocks is invalid' ((Get-VdErrors @('@ s1/a-1', "+ $vdX", $vdWhy, '', '@ s1/a-1', "+ $vdX", $vdWhy)) -like '*has a block already*')
Remove-Item -Recurse -Force -Path $vd -ErrorAction SilentlyContinue
if ($fail) { "FAILED: $fail"; exit 1 } else { 'all passed' }
