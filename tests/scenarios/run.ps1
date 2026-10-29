#!/usr/bin/env pwsh
# Tier 1: runs the scenarios against the fake GitHub.
# usage: pwsh tests/scenarios/run.ps1 [-Only <substring>] [-NoBuild] [-Suite <name>] [-ListSuites] [-ResultFile <json>]
#   no -Suite    every suite, one after the other, in this process (as before)
#   -Suite name  exactly one suite, with its own port slot (see Set-TestPorts in lib.ps1), its own HOME and TMPDIR; run-all.ps1 starts many of these at once
#   -Tier fast   skips the scenarios tagged [full] (lib.ps1 Get-ScenarioTier) and reports how many; the default here is full (everything), run-all.ps1 defaults to fast
#   -ListScenarios  prints suite, scenario and tier, tab separated; with -Suite only what that suite would run (after -Tier and -Only), with -Groups n -Group g only that group's share
#   -Groups n / -Group g  the scenarios of a suite split over n independent children (own fake, gateway, ports, HOME, TMPDIR), balanced by scenario-stats.json (owner: contiguous runs in suite order, cut only at its barriers, as its scenarios build on each other); the suites 'qc', 'messaging' and 'manager' default to 4 groups and 'owner' to 2, -Groups 1 runs one in one process; a child is started with -Group g (0-based); -GroupTimeoutMinutes (default 30) ends and fails groups that are still running
#   -ListSuites  prints the suite names, one per line, in slot order (slot = line number, from 0)
#   -Engine v2   the gateway under test runs the v2 engine (the tests.yml job "Tier 1 (v2)", for the suites and scenarios of v2-claims.txt): FACTORY_ENVIRONMENT is the CI-only name
#                'v2ci' (it replaces what a suite sets), the configuration's engine.v2Environments names it, and FACTORY_DB_CONNECTION is the SQL Server of FACTORY_TEST_SQL with a
#                database of its own for this process (the gateway migrates it into existence). FACTORY_TEST_SQL is required: without it the run exits 2. The default, v1, changes nothing.
#   Start guards (guard.ps1): refuses with exit 4 when another run.ps1 or run-all.ps1 is active on this machine, or when the temp filesystem has less than
#   -MinFreeDiskMb (default 2048) free; with less than -MinAvailableMb (default 1500) of memory a suite runs in half its groups. -Force starts anyway.
param([string] $Only = '', [switch] $NoBuild, [string] $Suite = '', [switch] $ListSuites, [switch] $ListScenarios, [string] $ResultFile = '', [ValidateSet('full', 'fast')][string] $Tier = 'full', [int] $Groups = 0, [int] $Group = -1, [double] $GroupTimeoutMinutes = 30, [switch] $Force, [int] $MinFreeDiskMb = 2048, [int] $MinAvailableMb = 1500, [ValidateSet('v1', 'v2')][string] $Engine = 'v1')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ($Engine -eq 'v2' -and -not $ListSuites -and -not $ListScenarios -and -not "$env:FACTORY_TEST_SQL".Trim()) { Write-Host '-Engine v2 needs a SQL Server: FACTORY_TEST_SQL (a connection string without a database) is not set'; exit 2 }
. (Join-Path $PSScriptRoot 'guard.ps1')
$lowMemory = $false
if (-not $ListSuites -and -not $ListScenarios) { $lowMemory = Assert-TestRunMayStart 'run.ps1' ([bool]$Force) $MinFreeDiskMb $MinAvailableMb; $env:FACTORY_TEST_RUN_HELD = "$PID" }
if (-not $NoBuild -and -not $ListSuites -and -not $ListScenarios) {
    foreach ($proj in 'tests/FakeGitHub', 'gateway') { Push-Location (Join-Path $root $proj); dotnet build -c Release -v q 2>&1 | Select-Object -Last 2 | ForEach-Object { Write-Host "  build $proj : $_" }; Pop-Location }
}
. (Join-Path $PSScriptRoot 'lib.ps1')
. (Join-Path $PSScriptRoot 'scenarios.ps1')
. (Join-Path $PSScriptRoot 'owner.ps1')
. (Join-Path $PSScriptRoot 'author.ps1')
. (Join-Path $PSScriptRoot 'messaging.ps1')   # worker messaging steps 1 to 6: suites 'messaging' and 'manager'
. (Join-Path $PSScriptRoot 'operator.ps1')    # the factory operator session: suite 'operator' (uses the helpers of messaging.ps1)
. (Join-Path $PSScriptRoot 'checks.ps1')      # why a check failed (pr-checks): its scenarios join the suites 'auto', 'owner' and 'manager'
foreach ($k in $CheckScenarios.Keys) { $Scenarios[$k] = $CheckScenarios[$k] }
foreach ($k in $CheckOwnerScenarios.Keys) { $OwnerScenarios[$k] = $CheckOwnerScenarios[$k] }
foreach ($k in $CheckManagerScenarios.Keys) { $ManagerScenarios[$k] = $CheckManagerScenarios[$k] }
. (Join-Path $PSScriptRoot 'cirule.ps1')      # one CI rule for flakes and pre-existing failures (issue 102): its scenarios join the suite 'auto'
foreach ($k in $CiRuleScenarios.Keys) { $Scenarios[$k] = $CiRuleScenarios[$k] }
foreach ($k in $CiRuleOwnerScenarios.Keys) { $OwnerScenarios[$k] = $CiRuleOwnerScenarios[$k] }   # (the gateway's merge after a PASS: the owner suite)
. (Join-Path $PSScriptRoot 'qc.ps1')
. (Join-Path $PSScriptRoot 'qc-dashboard.ps1')
. (Join-Path $PSScriptRoot 'qc-worker.ps1')   # S3: own gateway and workers, scenarios named "qc: worker ..."
. (Join-Path $PSScriptRoot 'qc-gate.ps1')     # S4: merge gate, approval trigger, sweep teardown, the race; scenarios named "qc: gate ..."
foreach ($k in $QcDashScenarios.Keys) { $QcScenarios[$k] = $QcDashScenarios[$k] }   # S2 scenarios run in the main QC suite, after the S1 ones
foreach ($k in $QcGateAutoScenarios.Keys) { $QcScenarios[$k] = $QcGateAutoScenarios[$k] }   # S4: the auto policy is unchanged
. (Join-Path $PSScriptRoot 'qc-pool.ps1')     # the rule-script worker pool; scenarios named "qc: runner pool ..."
foreach ($k in $QcPoolScenarios.Keys) { $QcScenarios[$k] = $QcPoolScenarios[$k] }
. (Join-Path $PSScriptRoot 'budget.ps1')      # the sweep throttle and the GitHub budget (GitHubBudget.cs): scenarios named "sweep throttle: ..." and "github budget: ..." join the owner suite
. (Join-Path $PSScriptRoot 'baseupdate.ps1')  # master moved: base.updated and the gateway's update branch; scenarios named "base update: ..." join the owner suite
. (Join-Path $PSScriptRoot 'runner-gate.ps1') # the gate of the rule scripts (gateway/RunnerGate.cs): scenarios named "runner gate: ..." join the owner suite
. (Join-Path $PSScriptRoot 'lease.ps1')       # owner leases kept by the plant (gateway/Lease.cs): scenarios named "lease: ..." join the manager suite
foreach ($k in $LeaseScenarios.Keys) { $ManagerScenarios[$k] = $LeaseScenarios[$k] }
. (Join-Path $PSScriptRoot 'leader.ps1')      # one acting gateway (Leader.cs): the leader lease, the hand-over, the gap; scenarios named "leader: ..." join the owner suite
. (Join-Path $PSScriptRoot 'runtime.ps1')     # cloud worker runtimes: w2, the nonce, git-credential; suite 'runtime' (uses the helpers of owner.ps1)
. (Join-Path $PSScriptRoot 'journal-fold.ps1') # the journal folds its repeats (Journal.ps1): scenarios named "journal fold: ..." join the owner suite
. (Join-Path $PSScriptRoot 'state-guard.ps1') # the state comment's rev check and 48,000-character budget (Board.ps1): scenarios named "state guard: ..." join the owner suite
. (Join-Path $PSScriptRoot 'manager-actions.ps1')   # the plant manager's flow verbs (gateway/Manager.cs): scenarios named "act: ..." join the manager suite
foreach ($k in $ManagerActionScenarios.Keys) { $ManagerScenarios[$k] = $ManagerActionScenarios[$k] }
. (Join-Path $PSScriptRoot 'mcp.ps1')               # the MCP server at /mcp (gateway/Mcp/): suite 'mcp' (uses the helpers of owner.ps1)
. (Join-Path $PSScriptRoot 'cursor.ps1')            # the api mode, Cursor Cloud Agents against the fake Cursor API: suite 'cursor'
. (Join-Path $PSScriptRoot 'copilot.ps1')           # the Copilot cloud agent against the fake agent tasks API: suite 'copilot'
. (Join-Path $PSScriptRoot 'copilot-manager.ps1')   # the plant manager on demand as a short Copilot task: suite 'copilot-manager'
. (Join-Path $PSScriptRoot 'envscope.ps1')           # the env: label: two gateways (tdd, uat) on one board; suite 'envscope'
. (Join-Path $PSScriptRoot 'keeper.ps1')            # the backlog keeper session (gateway/Keeper.cs): scenarios named "keeper: ..." join the manager suite
foreach ($k in $KeeperScenarios.Keys) { $ManagerScenarios[$k] = $KeeperScenarios[$k] }
. (Join-Path $PSScriptRoot 'octopus.ps1')           # the Octopus runbook runtime against the fake Octopus: suite 'octopus' (uses the helpers of owner.ps1 and runtime.ps1)
. (Join-Path $PSScriptRoot 'promote.ps1')           # the automatic promotion of a release to uat and prod (Verbs.Promote.ps1): suite 'promote'
. (Join-Path $PSScriptRoot 'repos.ps1')             # isolation of the home repository from a second repository in the fake (W1.9): suite 'repos'
# every barrier of the owner suite (owner.ps1) names one of its scenarios: a renamed one would move the cut of its groups without a word
foreach ($barrier0 in $OwnerBarriers) { if (-not $OwnerScenarios.Contains($barrier0) -and -not $OwnerScenarios.Contains("$barrier0 [full]")) { throw "owner.ps1 `$OwnerBarriers names no scenario of the owner suite: '$barrier0'" } }

# ---- the suites. ORDER IS THE PORT SLOT: append new suites at the end, never reorder (a slot is 100 ports from 6100, see lib.ps1). ----------
# Env names the environment a suite needs (set before it, removed after it); Timing sets FACTORY_TIMING_* for the suite; Extra is merged into the gateway config.
$script:OwnerSecret = ('owner-test-secret-' + '0123456789abcdef')   # a fixture, composed so the secret scan does not read it as a credential
# PR QC environment (owner mode, qc roles): the main gateway has the click rate limit lifted, one has the default limits, one is behind a known proxy.
$qcBase = @{ allowLoopbackForTests = $true; probeTimeoutSeconds = 2; maxEnvironments = 50 }
# The qc suite runs its gateway with a pool of rule-script workers (runner.pool, gateway/RunnerPool.cs); the other suites, owner among them, keep one process per call, so both paths stay tested.
$qcOpen = @{ qc = ($qcBase + @{ rateLimitPerIp = 100000; rateLimitPerPr = 100000 }); runner = @{ pool = $(if ($env:TEST_RUNNER_POOL) { [int]$env:TEST_RUNNER_POOL } else { 3 }) } }
# S4: the merge gate needs mergePolicy peer-approved; the teardown timers each need their own short timing.* values (applied by Get-Cfg and the gateway from FACTORY_TIMING_*)
$msgPool = @{ pool = $(if ($env:TEST_RUNNER_POOL) { [int]$env:TEST_RUNNER_POOL } else { 3 }) }
$gateQc = @{ qc = ($qcBase + @{ rateLimitPerIp = 100000; rateLimitPerPr = 100000; minFreeMb = 1 }) }
function New-Gate([hashtable]$Qc = @{}) { @{ qc = ($gateQc.qc + $Qc) } }
$SuiteDefs = @(
    # auto and merge-queue test the session's own merge verb, so the gateway's merge after a PASS is off there (owner.ps1 tests that one)
    @{ Name = 'auto'; Policy = 'auto'; Set = $Scenarios; Extra = @{ gatewayMerge = $false } }
    @{ Name = 'peer-approved'; Policy = 'peer-approved'; Set = $ApprovalScenarios }
    @{ Name = 'merge-queue'; Policy = 'auto'; Set = $QueueScenarios; Extra = @{ mergeQueueDepth = 1; gatewayMerge = $false } }
    @{ Name = 'owner'; Policy = 'auto'; Set = $OwnerScenarios; Extra = @{ ownerSessions = $true; baseUpdate = @{ mode = 'every-move'; autoUpdateDelaySeconds = 6; quietSeconds = 3; sweepCheckSeconds = 3 }; schedule = @{ hosts = @{ 'lab-1' = @{ owned = 1; builds = 1 } } } }; Env = 'owner'; Timing = @{ baseUpdateDebounceSeconds = 4; passMergeGraceSeconds = 8; staleItemMinutes = 600; continueMinutes = 0 } }
    @{ Name = 'qc'; Policy = 'auto'; Set = $QcScenarios; Extra = $qcOpen; Env = 'qc'; ClientHost = $true }   # ClientHost: F runs the client in one long-lived pwsh (lib.ps1)
    @{ Name = 'qc-worker'; Policy = 'auto'; Set = $QcWorkerScenarios; Extra = (New-Gate); Env = 'qc' }
    @{ Name = 'qc-gate'; Policy = 'peer-approved'; Set = $QcGateScenarios; Extra = (New-Gate); Env = 'qc' }
    @{ Name = 'qc-gate-idle'; Policy = 'auto'; Set = $QcGateIdleScenarios; Extra = (New-Gate); Env = 'qc'; Timing = @{ qcIdleMinutes = 0.1 } }
    @{ Name = 'qc-gate-lifetime'; Policy = 'auto'; Set = $QcGateLifetimeScenarios; Extra = (New-Gate); Env = 'qc'; Timing = @{ qcLifetimeMinutes = 0.1 } }
    @{ Name = 'qc-gate-grace'; Policy = 'auto'; Set = $QcGateGraceScenarios; Extra = (New-Gate); Env = 'qc'; Timing = @{ qcGraceMinutes = 0.25 } }
    @{ Name = 'qc-gate-lease'; Policy = 'auto'; Set = $QcGateLeaseScenarios; Extra = (New-Gate); Env = 'qc'; Timing = @{ qcLeaseMinutes = 0.3; qcMaxRelaunches = 2 } }
    @{ Name = 'qc-gate-waiting'; Policy = 'auto'; Set = $QcGateWaitingScenarios; Extra = (New-Gate @{ waitingDelaySeconds = 3 }); Env = 'qc'; Timing = @{ qcWaitingRepeatMinutes = 0.15 } }
    @{ Name = 'qc-gate-race'; Policy = 'auto'; Set = $QcGateRaceScenarios; Extra = (New-Gate @{ testReconcileDelayMs = 3000 }); Env = 'qc'; Timing = @{} }
    @{ Name = 'qc-rate'; Policy = 'auto'; Set = $QcRateScenarios; Extra = @{ qc = ($qcBase + @{ rateLimitPerIp = 10; rateLimitPerPr = 20 }) }; Env = 'qc' }
    @{ Name = 'qc-proxy'; Policy = 'auto'; Set = $QcProxyScenarios; Extra = @{ qc = ($qcBase + @{ rateLimitPerIp = 10; rateLimitPerPr = 20; trustProxy = $true; knownProxies = @('127.0.0.1') }) }; Env = 'qc' }
    @{ Name = 'author'; Policy = 'auto'; Set = $AuthorScenarios; Extra = @{ stateAuthors = @('factory-bot') }; Env = 'author' }
    # messaging and manager run on the fast path of qc: the suite's gateway has the rule-script pool (the second gateways of the scenarios do not, see
    # Msg-StartGateway), F runs the client in one long-lived pwsh, and each suite is split over 4 groups. The owner suite keeps one process per call.
    # the limits of the main gateway are lifted, so one scenario's sends never count against another's; the scenarios that test the limits start a
    # second gateway of their own (Msg-OnGateway). "dave" is on no list: it is the name that is refused.
    @{ Name = 'messaging'; Policy = 'auto'; Set = $MessagingScenarios; Extra = @{ messaging = @{ enabled = $true; allowedWorkers = @('alice', 'bob', 'carol', 'erin', 'frank', 'gina', 'hank', 'ivan', 'judy', 'kate', 'liam', 'racer', 'poller'); testHooks = $true }; runner = $msgPool }; Env = 'owner'; ClientHost = $true
        Timing = @{ ratePerMinute = 100000; pairPer10Min = 100000; perSenderQueueCap = 1000; recipientQueueCap = 1000; broadcastPer10Min = 100000; staleItemMinutes = 600; continueMinutes = 0 } }
    @{ Name = 'manager'; Policy = 'auto'; Set = $ManagerScenarios; Extra = @{ ownerSessions = $true; messaging = @{ enabled = $true; testHooks = $true }; manager = @{ enabled = $true; allowedNames = @('supervisor', 'supervisor2'); allowedAskers = @('alice', 'bob', 'carol', 'erin', 'frank', 'gina', 'ivan', 'judy', 'kate'); tokenGeneration = 1; turnBudgetPerHour = 100000; turnBudgetPerDay = 100000; stepBudgetPerHour = 100000 }; runner = $msgPool }; Env = 'owner'; ClientHost = $true
        # the delivery and step budgets are lifted like the other limits (the budget scenarios set their own); an owner the sweep stops listing is dropped after 5 s
        # (an owner that a scenario leaves behind on the suite's gateway is not reported as quiet, and a client that an earlier scenario left running, which
        # reconnects to the next gateway with the cursor of the last one, is not reported as a restart: the scenarios of those conditions set their own times)
        Timing = @{ mgrTakeoverSeconds = 6; ratePerMinute = 100000; pairPer10Min = 100000; broadcastPer10Min = 100000; mgrOwnedGraceSeconds = 5; mgrQuietMinutes = 600; mgrRestartMinutes = 0.0001; staleItemMinutes = 600; continueMinutes = 0 } }
    # the factory operator session (operator.ps1): one process per rule-script call, so a verb call is in flight long enough to be seen by release-check
    @{ Name = 'operator'; Policy = 'auto'; Set = $OperatorScenarios; Extra = @{ ownerSessions = $true; messaging = @{ enabled = $true; allowedWorkers = @('alice', 'bob'); testHooks = $true }; manager = @{ enabled = $true; allowedNames = @('supervisor'); allowedAskers = @('alice', 'bob'); tokenGeneration = 1; turnBudgetPerHour = 100000; turnBudgetPerDay = 100000; stepBudgetPerHour = 100000 }
            operator = @{ enabled = $true; allowedNames = @('operator-session', 'operator-two'); tokenGeneration = 1; requiredModelTier = 'premium'; requiredEffort = 'low'; maxSelfMergesPerDay = 2 } }; Env = 'owner'
        Timing = @{ mgrTakeoverSeconds = 4; ratePerMinute = 100000; pairPer10Min = 100000; broadcastPer10Min = 100000; opSelfAnswerMinutes = 0.1; staleItemMinutes = 600; continueMinutes = 0 } }
    # cloud worker runtimes (runtime.ps1): the w2 credential, the one-time nonce (minted through the messaging test seam) and git-credential
    # and the adapter against the fake ARM: items opt in by the runtime:cae-job label (no routing rule), a 15 s window for runtime workers (8 s for local ones)
    @{ Name = 'runtime'; Policy = 'auto'; Set = $RuntimeScenarios; Env = 'runtime'; Timing = @{ staleItemMinutes = 600; continueMinutes = 0; sessionEndGraceSeconds = 3 }
        Extra = @{ ownerSessions = $true; messaging = @{ enabled = $true; allowedWorkers = @('*'); testHooks = $true }
            runtimes = @{ preferConnected = $true; routing = @(); caeJob = @{ enabled = $true; maxConcurrent = 3; maxExecutionsPerDay = 1000; ackSeconds = 15; replicaTimeoutSeconds = 3600; maxFailuresPerPhase = 2; unhealthyAfterFailures = 2; listSeconds = 1
                jobResourceId = '/subscriptions/s1/resourceGroups/rg-test/providers/Microsoft.App/jobs/caj-test-claude'; armEndpoint = '__FAKE_URL__'; apiVersion = '2024-03-01'; managedIdentityClientId = ''; image = ''; containerName = 'fwrunner'; cpu = 4; memory = '8Gi'; gatewayUrl = 'http://ca-test-fworker'
                agent = 'codex'; jobs = @{ codex = '/subscriptions/s1/resourceGroups/rg-test/providers/Microsoft.App/jobs/caj-test' } } } } }
    # the MCP server (mcp.ps1, docs/design/agents-roles-launch.md 6): owner mode, messaging on for every name
    @{ Name = 'mcp'; Policy = 'auto'; Set = $McpScenarios; Env = 'owner'; Timing = @{ staleItemMinutes = 600 }
        Extra = @{ ownerSessions = $true; messaging = @{ enabled = $true; allowedWorkers = @('*'); testHooks = $true } } }
    # the api mode (cursor.ps1, agents-roles-launch.md 10): the fake Cursor API, a 5-minute deadline (the deadline itself: suite cursor-deadline), a 20 s window for the agent
    @{ Name = 'cursor'; Policy = 'auto'; Set = $CursorScenarios; Env = 'cursor'; Timing = @{ staleItemMinutes = 600; sessionEndGraceSeconds = 3; noBuildMinutes = 0.1 }
        Extra = @{ ownerSessions = $true; messaging = @{ enabled = $true; allowedWorkers = @('*'); testHooks = $true }
            api = @{ cursor = @{ enabled = $true; maxConcurrent = 2; maxMinutes = 5; maxExecutionsPerDay = 1000; ackSeconds = 60; listSeconds = 1; idleGraceSeconds = 2; usageRetrySeconds = 0.5; usageWaitSeconds = 20; ownerWatchSeconds = 3; quotaRetryMinutes = 0.3; model = 'auto'; apiUrl = '__FAKE_URL__'; gatewayUrl = 'http://gw.test'; repository = 'https://github.com/clearmeasure-aisf-sample-apps/cmdemo1-workorders' } }
            # cmdemo1's default runtime in tdd: an item without a runtime: label goes to a Cursor agent
            runtimes = @{ preferConnected = $true; routing = @(@{ phase = '*'; runtime = 'cursor-cloud' }) } } }
    # the Copilot cloud agent (copilot.ps1, docs/design/copilot-cloud-agent.md): the fake agent tasks API, a 5-minute deadline (the deadline itself: suite copilot-deadline), a 300 s window (an offer that lapses stops the task and the next launch settles the item on: a scenario that plays no session must not race it)
    @{ Name = 'copilot'; Policy = 'auto'; Set = $CopilotScenarios; Env = 'copilot'; DefaultEnvLabel = 'env:tdd'; Timing = @{ staleItemMinutes = 600; sessionEndGraceSeconds = 3; noBuildMinutes = 0.1 }
        Extra = @{ ownerSessions = $true; messaging = @{ enabled = $true; allowedWorkers = @('*'); testHooks = $true }
            api = @{ copilot = @{ enabled = $true; maxConcurrent = 2; maxMinutes = 5; maxExecutionsPerDay = 1000; ackSeconds = 300; listSeconds = 1; ciReleaseSeconds = 10; quotaRetryMinutes = 0.3; maxCreditsPerTask = 10000000000; apiUrl = '__FAKE_URL__'; gatewayUrl = 'http://gw.test'; repository = 'clearmeasure-aisf-sample-apps/cmdemo1-workorders' } }
            # cmdemo1's default runtime: an item without a runtime: label goes to the Copilot cloud agent
            runtimes = @{ preferConnected = $true; routing = @(@{ phase = '*'; runtime = 'copilot-cloud' }) } } }
    # the env: label (envscope.ps1, environment-test-factories.md 3): the suite's gateway is tdd with scoping on, its scenario starts a uat one
    @{ Name = 'envscope'; Policy = 'auto'; Set = $EnvScopeScenarios; Env = 'envscope'; Timing = @{ staleItemMinutes = 600 } }
    # the plant manager on demand (copilot-manager.ps1, docs/design/agents-roles-launch.md): the fake agent tasks API, a manager that is enabled with a 15 s cooldown,
    # a cap of 3 tasks a day and a 1-minute deadline; a question is late after 3 s
    @{ Name = 'copilot-manager'; Policy = 'auto'; Set = $CopilotManagerScenarios; Env = 'copilot'; DefaultEnvLabel = 'env:tdd'; Timing = @{ staleItemMinutes = 600; sessionEndGraceSeconds = 3; ratePerMinute = 100000; pairPer10Min = 100000 }
        Extra = @{ ownerSessions = $true; messaging = @{ enabled = $true; allowedWorkers = @('*'); testHooks = $true }
            manager = @{ enabled = $true; allowedNames = @('supervisor'); allowedAskers = @('alice', 'bob'); answerSlaSeconds = 3; turnBudgetPerHour = 100; turnBudgetPerDay = 1000; stepBudgetPerHour = 1000 }
            api = @{ copilot = @{ enabled = $true; maxConcurrent = 2; maxMinutes = 5; maxExecutionsPerDay = 1000; ackSeconds = 20; listSeconds = 1; ciReleaseSeconds = 600; apiUrl = '__FAKE_URL__'; gatewayUrl = 'http://gw.test'; repository = 'clearmeasure-aisf-sample-apps/cmdemo1-workorders'
                manager = @{ enabled = $true; maxPerDay = 3; cooldownMinutes = 0.25; maxMinutes = 1 } } }
            runtimes = @{ preferConnected = $true; routing = @(@{ phase = '*'; runtime = 'copilot-cloud' }) } } }
    # the deadline of a Copilot task (copilot.ps1): the copilot suite with a 0.25-minute api.copilot.maxMinutes, for that one scenario (a 300 s window, so the deadline, not a lapsed offer, ends the task)
    @{ Name = 'copilot-deadline'; Policy = 'auto'; Set = $CopilotDeadlineScenarios; Env = 'copilot'; DefaultEnvLabel = 'env:tdd'; Timing = @{ staleItemMinutes = 600; sessionEndGraceSeconds = 3 }
        Extra = @{ ownerSessions = $true; messaging = @{ enabled = $true; allowedWorkers = @('*'); testHooks = $true }
            api = @{ copilot = @{ enabled = $true; maxConcurrent = 2; maxMinutes = 0.25; maxExecutionsPerDay = 1000; ackSeconds = 300; listSeconds = 1; ciReleaseSeconds = 10; maxCreditsPerTask = 10000000000; apiUrl = '__FAKE_URL__'; gatewayUrl = 'http://gw.test'; repository = 'clearmeasure-aisf-sample-apps/cmdemo1-workorders' } }
            # cmdemo1's default runtime: an item without a runtime: label goes to the Copilot cloud agent
            runtimes = @{ preferConnected = $true; routing = @(@{ phase = '*'; runtime = 'copilot-cloud' }) } } }
    # the deadline of a Cursor agent (cursor.ps1): a 0.25-minute api.cursor.maxMinutes and a 300 s window, so the deadline, not a lapsed offer, ends it
    @{ Name = 'cursor-deadline'; Policy = 'auto'; Set = $CursorDeadlineScenarios; Env = 'cursor'; Timing = @{ staleItemMinutes = 600; sessionEndGraceSeconds = 3 }
        Extra = @{ ownerSessions = $true; messaging = @{ enabled = $true; allowedWorkers = @('*'); testHooks = $true }
            api = @{ cursor = @{ enabled = $true; maxConcurrent = 2; maxMinutes = 0.25; maxExecutionsPerDay = 1000; ackSeconds = 300; listSeconds = 1; idleGraceSeconds = 2; usageRetrySeconds = 0.5; usageWaitSeconds = 20; model = 'auto'; apiUrl = '__FAKE_URL__'; gatewayUrl = 'http://gw.test'; repository = 'https://github.com/clearmeasure-aisf-sample-apps/cmdemo1-workorders' } } } }
    # the Octopus runbook runtime (octopus.ps1, docs/design/octopus-runbook-runtime.md): the fake Octopus, items opt in by the runtime:octopus label,
    # one worker per host (two healthy hosts), a 30 s window, reports older than 2 s past a start end a container they do not list; qc may overflow (one at a time)
    @{ Name = 'octopus'; Policy = 'auto'; Set = $OctopusScenarios; Env = 'octopus'; Timing = @{ staleItemMinutes = 600; continueMinutes = 0; sessionEndGraceSeconds = 3 }
        Extra = @{ ownerSessions = $true; messaging = @{ enabled = $true; allowedWorkers = @('*'); testHooks = $true }
            runtimes = @{ preferConnected = $true; routing = @(); octopus = @{ enabled = $true; serverUrl = '__FAKE_URL__'; spaceId = 'Spaces-355'; project = 'cmdemo1-fworker-runtime'; environment = 'tdd'; hostTag = 'fworker-host'
                agent = 'codex'; perHostCapacity = 1; overflowRoles = @('qc'); overflowMax = 1; maxConcurrent = 10; maxExecutionsPerDay = 1000; maxFailuresPerPhase = 2; unhealthyAfterFailures = 100; ackSeconds = 30; listSeconds = 1; reportGraceSeconds = 2 } } } }
    # the automatic promotion (promote.ps1, docs/design/auto-promote.md): on, for any gateway environment; tdd and uat health on the fake's /_healthcheck
    @{ Name = 'promote'; Policy = 'auto'; Set = $PromoteScenarios; Env = 'promote'; Timing = @{ staleItemMinutes = 600; promoteSeconds = 3 }
        Extra = @{ delivery = @{ autoPromote = @{ enabled = $true; environments = @('uat', 'prod'); gatewayEnvironment = 'tdd'; signoffWaitHours = 0.05 } }
            octopus = @{ url = '__FAKE_URL__/octopus'; space = 'Spaces-355'; project = 'cmdemo1-ui'; prodUrl = 'https://ca-cmdemo1-prod-ui.example'; tddUrl = '__FAKE_URL__'; uatUrl = '__FAKE_URL__'; healthPath = '/_healthcheck/detailed'; health = @{ spacingMinutes = 0.05; settleMinutes = 0.1; retries = 2; retrySeconds = 0.5 }
                environments = @{ tdd = 'tdd'; uat = 'uat'; prod = 'prod' } } } }
    # isolation (repos.ps1, docs/design/system-gateway.md H12): v1 serves the home repository only; a second repository's issues are linked to the shared board of the fake
    @{ Name = 'repos'; Policy = 'auto'; Set = $ReposScenarios; Timing = @{ staleItemMinutes = 600 } }
)

# ---- which scenarios run: Run-Suite runs them and -ListScenarios -Suite prints them, through the same functions, so the two cannot differ ----------
function Get-OnlyNames($Set) { @($Set.Keys | Where-Object { -not $Only -or $_ -match $Only }) }
function Get-TierNames([string[]]$Names) { @(foreach ($n in @($Names)) { if ($n -and ($Tier -eq 'full' -or (Get-ScenarioTier $n) -eq 'fast')) { $n } }) }
# The share of group $Index (from 0) of $Count of a suite's scenarios, kept in suite order; every scenario is in exactly one group. Seconds per scenario
# come from scenario-stats.json (run-all.ps1 -Tier full rewrites it); one it does not know counts as 30 s, about the mean seen in CI for the long
# suites (owner 29, auto 46, manager 19 s per scenario). owner: contiguous runs, cut only where a scenario of $OwnerBarriers (owner.ps1) starts, so
# that the biggest group is as small as the barriers allow; its scenarios lean on workers and state that earlier ones left behind, so a balanced pick from
# anywhere in the suite breaks them. The others: longest scenario first onto the least loaded group.
function Select-GroupShare([string]$SuiteName, [string[]]$RunNames, [int]$Count, [int]$Index) {
    if (-not $RunNames) { return @() }
    $st = @{}; try { $st = (Get-Content -Raw (Join-Path $PSScriptRoot 'scenario-stats.json') | ConvertFrom-Json -AsHashtable)[$SuiteName] } catch { }
    if ($SuiteName -eq 'owner') {
        $secs = @($RunNames | ForEach-Object { $k = $_ -replace '\s\[full\]\s*$', ''; $(if ($st -and $st.ContainsKey($k)) { [double]$st[$k].seconds } else { 30.0 }) })
        $total = ($secs | Measure-Object -Sum).Sum; if (-not ($total -gt 0)) { $secs = @($secs | ForEach-Object { 1.0 }); $total = [double]$secs.Count }
        # the runs between two barriers (the first run starts one whatever the list says), then the fewest seconds the biggest group needs: binary
        # search on the limit, packing the runs left to right into groups of at most the limit (the optimal cut of contiguous runs into $Count groups)
        $segStart = [System.Collections.Generic.List[int]]::new(); $segSecs = [System.Collections.Generic.List[double]]::new()
        for ($i = 0; $i -lt $RunNames.Count; $i++) {
            $isBarrier = $OwnerBarriers.Contains($RunNames[$i]) -or $OwnerBarriers.Contains(($RunNames[$i] -replace '\s\[full\]\s*$', ''))
            if ($i -eq 0 -or $isBarrier) { $segStart.Add($i); $segSecs.Add(0.0) }
            $segSecs[$segSecs.Count - 1] += $secs[$i]
        }
        $lo = [double](($segSecs | Measure-Object -Maximum).Maximum); $hi = [double]$total
        while ($hi - $lo -gt 0.5) {
            $mid = ($lo + $hi) / 2; $need = 1; $acc = 0.0
            foreach ($sg in $segSecs) { if ($acc -gt 0 -and $acc + $sg -gt $mid) { $need++; $acc = 0.0 }; $acc += $sg }
            if ($need -le $Count) { $hi = $mid } else { $lo = $mid }
        }
        $cuts = @(0); $acc = 0.0   # the index each group starts at
        for ($sgi = 0; $sgi -lt $segStart.Count; $sgi++) { if ($acc -gt 0 -and $acc + $segSecs[$sgi] -gt $hi) { $cuts += $segStart[$sgi]; $acc = 0.0 }; $acc += $segSecs[$sgi] }
        while ($cuts.Count -lt $Count) { $cuts += $RunNames.Count }   # groups the runs do not fill are empty
        $cuts += $RunNames.Count
        return @(for ($i = $cuts[$Index]; $i -lt $cuts[$Index + 1]; $i++) { $RunNames[$i] })
    }
    $load = New-Object double[] $Count; $mine = @{}
    foreach ($n in ($RunNames | Sort-Object { $k = $_ -replace '\s\[full\]\s*$', ''; if ($st -and $st.ContainsKey($k)) { -[double]$st[$k].seconds } else { -30.0 } })) {
        $k = $n -replace '\s\[full\]\s*$', ''; $sec = $(if ($st -and $st.ContainsKey($k)) { [double]$st[$k].seconds } else { 30.0 }); $gi = 0; for ($i = 1; $i -lt $Count; $i++) { if ($load[$i] -lt $load[$gi]) { $gi = $i } }
        $load[$gi] += $sec; if ($gi -eq $Index) { $mine[$n] = $true }
    }
    return @($RunNames | Where-Object { $mine.ContainsKey($_) })
}

if ($ListSuites) { $SuiteDefs | ForEach-Object { $_.Name }; exit 0 }
# -ListScenarios alone: every scenario of every suite, whatever -Tier and -Only say (run-all.ps1 reads it); with -Suite see below
if ($ListScenarios -and -not $Suite) { foreach ($d0 in $SuiteDefs) { foreach ($k0 in $d0.Set.Keys) { "$($d0.Name)`t$k0`t$(Get-ScenarioTier $k0)" } }; exit 0 }
if ($Suite -and -not @($SuiteDefs | Where-Object { $_.Name -eq $Suite }).Count) { Write-Host "unknown suite '$Suite'; known: $(($SuiteDefs | ForEach-Object { $_.Name }) -join ', ')"; exit 2 }

# ---- qc runs as independent groups: this process starts them, waits (at most -GroupTimeoutMinutes), and adds up their results ---------------
if ($Groups -le 0) { $Groups = $(if ($Suite -in 'qc', 'messaging', 'manager') { 4 } elseif ($Suite -eq 'owner') { 2 } else { 1 }); if ($lowMemory -and $Groups -gt 1) { $Groups = [int]($Groups / 2); Write-Host "memory is short: $Groups groups at once" } }
if ($Groups -gt 10) { Write-Host "-Groups ${Groups}: at most 10 (each group has a port range of 100 inside the 1000 of its suite)"; exit 2 }
if ($Group -ge $Groups) { Write-Host "-Group $Group is outside -Groups $Groups (groups are numbered from 0)"; exit 2 }
# -ListScenarios -Suite s [-Groups n -Group g] [-Tier] [-Only]: the scenarios that run would run there (suite, scenario and tier, tab separated), and
# nothing else; without -Group the whole suite. A guard can prove with it that every scenario of a suite runs in exactly one of its groups.
if ($ListScenarios) {
    $d0 = @($SuiteDefs | Where-Object { $_.Name -eq $Suite })[0]
    $n0 = @(Get-TierNames @(Get-OnlyNames $d0.Set))
    if ($Groups -gt 1 -and $Group -ge 0) { $n0 = @(Select-GroupShare $Suite $n0 $Groups $Group) }
    foreach ($k0 in $n0) { "$Suite`t$k0`t$(Get-ScenarioTier $k0)" }
    exit 0
}
if ($Groups -gt 1 -and $Suite -and $Group -lt 0) {
    # Group port ranges lie above every suite slot (6100 + 100 * slot, up to 8999): 10000 + 1000 * slot + 100 * group. TEST_PORT_BASE moves the first group; TEST_FAKE_PORT and TEST_API_PORT name one port each, so they cannot apply to several groups and are not passed on.
    # The ranges of slots 23 and up (33000 and up) lie in Linux's default ephemeral range (32768-60999), where an outgoing connection can take a port
    # before its group listens on it: CI (.github/workflows/tests.yml) moves the ephemeral range to 40000-60999 first; a local run of such a group can collide.
    $base0 = $(if ($env:TEST_PORT_BASE) { [int]$env:TEST_PORT_BASE } else { 10000 + 1000 * @($SuiteDefs | ForEach-Object { $_.Name }).IndexOf($Suite) })
    $gDir = Join-Path ([IO.Path]::GetTempPath()) "factory-groups-$([guid]::NewGuid().ToString('N').Substring(0,6))"; New-Item -ItemType Directory -Force -Path $gDir | Out-Null
    $swAll = [Diagnostics.Stopwatch]::StartNew(); $kids = @(); $tot = 0; $fails = @(); $sc = @(); $skip = [ordered]@{}; $vc = 0; $vs = 0.0; $gv = 0; $cc = 0; $bad = $false; $quar = @()
    $oB = $env:TEST_PORT_BASE; $oF = $env:TEST_FAKE_PORT; $oA = $env:TEST_API_PORT
    try {
        try {
            Remove-Item Env:TEST_FAKE_PORT, Env:TEST_API_PORT -ErrorAction SilentlyContinue
            foreach ($g in 0..($Groups - 1)) {
                $a2 = @('-NoProfile', '-File', $PSCommandPath, '-NoBuild', '-Suite', $Suite, '-Tier', $Tier, '-Groups', $Groups, '-Group', $g, '-ResultFile', (Join-Path $gDir "g$g.json")); if ($Only) { $a2 += @('-Only', $Only) }; if ($Engine -eq 'v2') { $a2 += @('-Engine', 'v2') }
                $env:TEST_PORT_BASE = "$($base0 + 100 * $g)"
                $kids += ,@{ g = $g; log = (Join-Path $gDir "g$g.log"); err = (Join-Path $gDir "g$g.err"); json = (Join-Path $gDir "g$g.json"); timedOut = $false; proc = (Start-Process pwsh -ArgumentList $a2 -PassThru -RedirectStandardOutput (Join-Path $gDir "g$g.log") -RedirectStandardError (Join-Path $gDir "g$g.err")) }
            }
        } finally { foreach ($e in @(@('TEST_PORT_BASE', $oB), @('TEST_FAKE_PORT', $oF), @('TEST_API_PORT', $oA))) { if ($e[1]) { Set-Item "Env:$($e[0])" $e[1] } else { Remove-Item "Env:$($e[0])" -ErrorAction SilentlyContinue } } }
        $deadline = [DateTime]::UtcNow.AddMinutes($GroupTimeoutMinutes)
        while (@($kids | Where-Object { -not $_.proc.HasExited }).Count -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 250 }
        foreach ($k in $kids) { if (-not $k.proc.HasExited) { $k.timedOut = $true; try { $k.proc.Kill($true) } catch { } }; [void]$k.proc.WaitForExit(10000) }
        foreach ($k in $kids) {
            Write-Host "=== group $($k.g) of $Groups ==="; if (Test-Path $k.log) { Get-Content $k.log | ForEach-Object { Write-Host $_ } }
            if ((Test-Path $k.err) -and (Get-Item $k.err).Length -gt 0) { Write-Host "--- group $($k.g) stderr ---"; Get-Content $k.err | ForEach-Object { Write-Host $_ } }
            if ($k.timedOut) { $bad = $true; $fails += "group $($k.g) timed out (after $GroupTimeoutMinutes minutes)"; continue }
            $code = $k.proc.ExitCode; if ($code -ne 0) { $bad = $true; $fails += "group $($k.g) exited $code" }
            $r = $null; try { if (Test-Path $k.json) { $r = Get-Content -Raw $k.json | ConvertFrom-Json -AsHashtable } } catch { }
            if (-not $r -or -not $r.ContainsKey('checks')) { $bad = $true; $fails += "group $($k.g) produced no readable result file"; continue }
            $tot += $r.checks; $fails += @($r.failures); $sc += @($r.scenarios); $vc += $r.verbCalls; $vs += $r.verbSeconds; $gv += $r.gatewayVerbLines; if ($r.ContainsKey('clientCrashes')) { $cc += [int]$r.clientCrashes }; if ($r.ContainsKey('quarantined')) { $quar += @($r.quarantined | Where-Object { $_ }) }
            foreach ($x in @($r.skipped)) { if ($x) { $skip["$($x.suite)`t$($x.name)"] = $x } }
        }
        if ($tot -eq 0) { $bad = $true; $fails += 'no checks ran' }
        Write-Host ""; Write-Host "$tot checks, $($fails.Count) failed"; $fails | ForEach-Object { Write-Host "  FAILED: $_" }
        if ($quar.Count) { Write-Host "quarantined failures: $($quar.Count) (reported, not counted as failed)"; $quar | ForEach-Object { Write-Host "  QUARANTINED: $_" } }
        if ($Tier -eq 'fast') { Write-Host "skipped as full-tier: $($skip.Count) scenarios" }
        Write-Host ("verbs: {0} client calls, {1:N1}s in them; groups: {2}; suite took {3:N0}s" -f $vc, $vs, $Groups, $swAll.Elapsed.TotalSeconds)
        if ($cc) { Write-Host "client crashes: $cc client calls ended with pwsh exit 139 (segmentation fault): machine load or the runtime, not a product answer" }
        if ($ResultFile) { [ordered]@{ suite = $Suite; checks = $tot; failed = $fails.Count; quarantined = @($quar); clientCrashes = $cc; failures = @($fails); seconds = [math]::Round($swAll.Elapsed.TotalSeconds, 1); verbCalls = $vc; verbSeconds = [math]::Round($vs, 1); tier = $Tier; skipped = @($skip.Values); scenarios = @($sc); gatewayVerbLines = $gv; groups = $Groups; portBase = $base0 } | ConvertTo-Json -Depth 4 | Set-Content -Path $ResultFile }
    } finally {
        foreach ($k in $kids) { try { if (-not $k.proc.HasExited) { $k.proc.Kill($true) } } catch { } }
        Remove-Item -Recurse -Force $gDir -ErrorAction SilentlyContinue
    }
    exit ([int]($bad -or $fails.Count -gt 0))
}

# ---- isolation: this process has its own ports, HOME (client configs, worker and token dirs, App tokens) and TMPDIR (every work dir) ----------
$slot = 0; if ($Suite) { for ($i = 0; $i -lt $SuiteDefs.Count; $i++) { if ($SuiteDefs[$i].Name -eq $Suite) { $slot = $i } } }
if (-not $env:TEST_PORT_BASE) { $env:TEST_PORT_BASE = "$(6100 + 100 * $slot)" }
Set-TestPorts ([int]$env:TEST_PORT_BASE)
# Less CPU per dotnet process (the fake, the gateway, every pwsh): no profile-guided tiering, no background GC thread. About 10% of the CPU of a suite, and CPU is what parallel suites compete for.
if (-not $env:DOTNET_TieredPGO) { $env:DOTNET_TieredPGO = '0' }; if (-not $env:DOTNET_gcConcurrent) { $env:DOTNET_gcConcurrent = '0' }
# No pwsh of the suite reads or writes a .NET multicore-JIT startup profile in the suite's HOME: a corrupt one makes every later pwsh exit 139 (issue 375).
# The runtime reads the value as hex (0x65535, 415029), which exceeds any processor count: multicore JIT is off.
if (-not $env:DOTNET_MultiCoreJitMinNumCpus) { $env:DOTNET_MultiCoreJitMinNumCpus = '65535' }
$label = if ($Suite) { $Suite } else { 'all' }
$script:SuiteRoot = Join-Path ([IO.Path]::GetTempPath()) "factory-suite-$label-$([guid]::NewGuid().ToString('N').Substring(0,6))"
foreach ($d in 'home', 'tmp') { New-Item -ItemType Directory -Force -Path (Join-Path $script:SuiteRoot $d) | Out-Null }
$env:HOME = Join-Path $script:SuiteRoot 'home'; $env:TMPDIR = Join-Path $script:SuiteRoot 'tmp'
Write-Host "suite $label : ports $($script:PortBase)-$($script:PortBase + 99) (fake $($script:FakePort), api $($script:ApiPort), hooks $($script:HookPort)), work $($script:SuiteRoot)"
# (variable names here must not collide, case-insensitively, with the globals of the scenario files: $L, $L2, ...)
# A suite needs its ports free (a leftover process of an earlier run would answer in place of this suite's own fake and gateway).
foreach ($port in $script:FakePort, $script:ApiPort, $script:HookPort) {
    $probeSock = $null; try { $probeSock = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $port); $probeSock.Start() } catch { Write-Host "port $port is already in use: another run, or a process outside the tests"; exit 3 } finally { if ($probeSock) { $probeSock.Stop() } }
}

# -Engine v2: the environment name and the database of this process. The name is in no list of config/instance.json (Run-Suite adds it to engine.v2Environments of the test
# configuration only), so no deployed gateway can run v2 because of it. The database is new for every process (a suite, or one group of a suite): the gateway migrates it into
# existence, a run never meets the tables of another, and the CI job's SQL Server container goes with the job. The connection string carries the password: it is never printed.
$script:V2EnvName = 'v2ci'
$script:V2Connection = ''
if ($Engine -eq 'v2' -and $env:FACTORY_TEST_SQL) {
    $v2Db = 'fworker_v2ci_' + ($label.ToLowerInvariant() -replace '[^a-z0-9]', '_') + '_' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $script:V2Connection = $env:FACTORY_TEST_SQL.Trim().TrimEnd(';') + ';Initial Catalog=' + $v2Db   # (a keyword given twice takes its last value)
    Write-Host "engine v2: environment $($script:V2EnvName), database $v2Db on the server of FACTORY_TEST_SQL"
}

function Get-SuiteEnv([string]$Kind) {
    switch ($Kind) {
        # 8 s: a late accept is refused now (stale-assignment), and a loaded runner takes several seconds per client call
        # the plant snapshot (PlantSnapshot.cs) is off in the suites: its read at start would hold every first sweep back; its own scenario switches it on
        # the leader lease (Leader.cs) is off as well: a restarted suite gateway would wait for its predecessor's hand-over; its own scenarios switch it on
        'owner' { return @{ FACTORY_OWNER_MODE = '1'; FACTORY_PLANT_SNAPSHOT = '0'; FACTORY_LEADER_LEASE = '0'; FACTORY_TOKEN_SECRET = $script:OwnerSecret; FACTORY_ASSIGN_ACK_SECONDS = '8' } }
        # the runtime suite: owner mode and the bearer token of the fake ARM (taken by the gateway only for a loopback ARM endpoint)
        # the cursor suite: owner mode and the key of the fake Cursor API (CURSOR_API_KEY, as the deployed gateway reads it)
        'cursor' { return @{ FACTORY_OWNER_MODE = '1'; FACTORY_PLANT_SNAPSHOT = '0'; FACTORY_LEADER_LEASE = '0'; FACTORY_TOKEN_SECRET = $script:OwnerSecret; FACTORY_ASSIGN_ACK_SECONDS = '8'; CURSOR_API_KEY = 'fake-cursor' } }
        # the copilot suite: owner mode and the token of the fake agent tasks API (COPILOT_TASKS_TOKEN, as the deployed gateway reads it)
        'copilot' { return @{ FACTORY_ENVIRONMENT = 'tdd'; FACTORY_ENV_SCOPE = '1'; FACTORY_OWNER_MODE = '1'; FACTORY_PLANT_SNAPSHOT = '0'; FACTORY_LEADER_LEASE = '0'; FACTORY_TOKEN_SECRET = $script:OwnerSecret; FACTORY_ASSIGN_ACK_SECONDS = '8'; COPILOT_TASKS_TOKEN = 'fake-copilot' } }
        # the promote suite: the key that creates deployments, composed here at run time
        'promote' { return @{ FACTORY_ENVIRONMENT = 'tdd'; FACTORY_LEADER_LEASE = '0'; OCTOPUS_RUNBOOK_API_KEY = ('API-' + [guid]::NewGuid().ToString('N').ToUpperInvariant()) } }
        'envscope' { return @{ FACTORY_ENVIRONMENT = 'tdd'; FACTORY_ENV_SCOPE = '1'; FACTORY_LEADER_LEASE = '0' } }
        # the octopus suite: owner mode, the runbook key and the host token, both composed here at run time (no credential-like fixture in the repository)
        'octopus' { return @{ FACTORY_OWNER_MODE = '1'; FACTORY_PLANT_SNAPSHOT = '0'; FACTORY_TRANSCRIPT_SAVE_SECONDS = '2'; FACTORY_LEADER_LEASE = '0'; FACTORY_TOKEN_SECRET = $script:OwnerSecret; FACTORY_ASSIGN_ACK_SECONDS = '8'; OCTOPUS_RUNBOOK_API_KEY = ('API-' + [guid]::NewGuid().ToString('N').ToUpperInvariant()); FACTORY_OCTOPUS_HOST_TOKEN = [guid]::NewGuid().ToString('N'); FACTORY_TEST_OCTOPUS_ENTRA_TOKEN = ('eyJ0ZXN0.' + [guid]::NewGuid().ToString('N') + '.sig') } }
        'runtime' { return @{ FACTORY_OWNER_MODE = '1'; FACTORY_PLANT_SNAPSHOT = '0'; FACTORY_LEADER_LEASE = '0'; FACTORY_TOKEN_SECRET = $script:OwnerSecret; FACTORY_ASSIGN_ACK_SECONDS = '8'; FACTORY_TEST_ARM_TOKEN = 'fake-arm' } }
        'qc' {
            $adminTokens = Join-Path $script:SuiteRoot 'qc-admin-tokens'; New-Item -ItemType Directory -Force -Path $adminTokens | Out-Null
            return @{ FACTORY_OWNER_MODE = '1'; FACTORY_PLANT_SNAPSHOT = '0'; FACTORY_LEADER_LEASE = '0'; FACTORY_TOKEN_SECRET = $script:OwnerSecret; FACTORY_ASSIGN_ACK_SECONDS = '60'; FACTORY_PUBLIC_URL = "http://127.0.0.1:$($script:ApiPort)"; FACTORY_TOKEN_DIR = $adminTokens }
        }
        'author' {
            $pem = (& openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 2>$null) -join "`n"
            return @{ TEST_REAL_APPS = '1'; FACTORY_AUTHOR_APP = '1'; FACTORY_APP_AUTHOR_ID = '4242'; FACTORY_APP_AUTHOR_INSTALLATION = '7'; FACTORY_APP_AUTHOR_SLUG = 'factory-worker-author'; FACTORY_APP_AUTHOR_KEY = $pem; GH_TOKEN = 'unset' }
        }
    }
    return @{}
}

# -Engine v2: what /api/v1/status says of the engine and of the Wolverine host (its state is running once the migration has created the database and the node has started)
function Get-V2State {
    try {
        $h = (Invoke-WebRequest "http://127.0.0.1:$($script:ApiPort)/api/v1/status" -Headers @{ Authorization = 'Bearer test-token' } -TimeoutSec 3 -UseBasicParsing).Content | ConvertFrom-Json -AsHashtable
        $mode = ''; if ($h.ContainsKey('engine') -and $h['engine'] -is [System.Collections.IDictionary] -and $h['engine'].Contains('mode')) { $mode = "$($h['engine']['mode'])" }
        $state = ''; $text = '(no wolverine block)'
        if ($h.ContainsKey('wolverine') -and $h['wolverine'] -is [System.Collections.IDictionary]) { $text = $h['wolverine'] | ConvertTo-Json -Compress -Depth 4; if ($h['wolverine'].Contains('state')) { $state = "$($h['wolverine']['state'])" } }
        return @{ mode = $mode; state = $state; wolverine = $text }
    } catch { return @{ mode = ''; state = ''; wolverine = "(the status call failed: $($_.Exception.Message))" } }
}

$script:GwVerbs = 0
function Count-GatewayVerbs { foreach ($f in Get-ChildItem -Path $script:Work -Filter 'gateway*.out' -ErrorAction SilentlyContinue) { $script:GwVerbs += @(Select-String -Path $f.FullName -Pattern '"verb":"').Count } }
$script:Skipped = New-Object System.Collections.ArrayList
function Run-Suite([string]$Policy, $Set, [hashtable]$Extra = @{}) {
    $names = @(Get-OnlyNames $Set)
    $runNames = @(Get-TierNames $names)
    foreach ($n in $names) { if ($runNames -notcontains $n) { [void]$script:Skipped.Add(@{ suite = $script:SuiteName; name = $n }) } }
    if ($Groups -gt 1 -and $Group -ge 0) { $runNames = @(Select-GroupShare $script:SuiteName $runNames $Groups $Group) }   # this child's share
    if ($runNames.Count -eq 0) {
        # (not a failure: a fast-tier run can leave a group nothing to do; with all scenarios it means the cut left no barrier for this group)
        if ($Groups -gt 1 -and $Group -ge 0 -and -not $Only) { Write-Host "group $Group of $Groups of suite $($script:SuiteName) is empty (tier $Tier): no scenario runs in it" }
        return
    }
    $cfg = @{ mergePolicy = $Policy; humanApprovers = @('factory-worker-testperson[bot]') }; foreach ($k in $Extra.Keys) { $cfg[$k] = $Extra[$k] }
    if ($Engine -eq 'v2') { $cfg['engine'] = @{ v2Environments = @($script:V2EnvName); shadowEnvironments = @() } }   # (no suite sets an engine block of its own)
    Start-TestEnv $cfg
    if ($Engine -eq 'v2') {
        # /ready does not wait for the database (the host starts in the background: the migration, which creates the database, then the node), and a journal line before the host is
        # up is refused ("the Wolverine host is not up"); so no scenario starts before the status says the host runs, and that the engine is v2 (this fails, once, when it is not)
        $script:Current = "$($script:SuiteName) engine v2"
        $script:V2Seen = @{ mode = ''; state = ''; wolverine = '(not asked)' }
        [void](Wait-Until -Seconds 120 -Message 'the Wolverine host' { $script:V2Seen = Get-V2State; $script:V2Seen.state -eq 'running' })
        Check 'engine v2: the status names the engine v2 and the Wolverine host runs' ($script:V2Seen.mode -eq 'v2' -and $script:V2Seen.state -eq 'running') "(engine mode '$($script:V2Seen.mode)'; wolverine: $($script:V2Seen.wolverine))"
    }
    try {
        Write-Host "scenarios (merge policy: $Policy, tier: $Tier)"
        foreach ($name in $runNames) { Run-Scenario $name $Set[$name] }
    } finally { try { Start-Sleep -Seconds $(if ($Tier -eq 'fast') { 1 } else { 3 }); Assert-NoServerErrors $Policy } finally { Count-GatewayVerbs; Stop-TestEnv } }
}
function Invoke-SuiteDef($Def) {
    $script:SuiteName = $Def.Name
    $script:UseClientHost = $Def.ContainsKey('ClientHost') -and [bool]$Def.ClientHost
    $envKind = $(if ($Def.ContainsKey('Env')) { $Def.Env } else { '' }); $envs = Get-SuiteEnv $envKind; if ($Engine -eq 'v2') { $envs['FACTORY_ENVIRONMENT'] = $script:V2EnvName; $envs['FACTORY_DB_CONNECTION'] = $script:V2Connection };$script:DefaultEnvLabel = $(if ($Def.ContainsKey('DefaultEnvLabel')) { $Def.DefaultEnvLabel } else { '' }); $timing = if ($Def.ContainsKey('Timing')) { $Def.Timing } else { @{} }
    foreach ($k in $envs.Keys) { Set-Item "Env:$k" ([string]$envs[$k]) }
    foreach ($k in $timing.Keys) { Set-Item "Env:FACTORY_TIMING_$k" ([string]$timing[$k]) }
    try { Run-Suite $Def.Policy $Def.Set $(if ($Def.ContainsKey('Extra')) { $Def.Extra } else { @{} }) }
    finally {
        foreach ($k in $envs.Keys) { Remove-Item "Env:$k" -ErrorAction SilentlyContinue }
        foreach ($k in $timing.Keys) { Remove-Item "Env:FACTORY_TIMING_$k" -ErrorAction SilentlyContinue }
        if ($envKind -eq 'author') { $env:GH_TOKEN = 'fake-operator' }
    }
}

$sw = [Diagnostics.Stopwatch]::StartNew()
foreach ($def in $SuiteDefs) { if ($Suite -and $def.Name -ne $Suite) { continue }; Invoke-SuiteDef $def }
$sw.Stop()
Write-Host ""
Write-Host "$($script:Checks) checks, $($script:Failures.Count) failed"
$script:Failures | ForEach-Object { Write-Host "  FAILED: $_" }
if ($script:Quarantined.Count) { Write-Host "quarantined failures: $($script:Quarantined.Count) (reported, not counted as failed)"; $script:Quarantined | ForEach-Object { Write-Host "  QUARANTINED: $_" } }
if ($Tier -eq 'fast') { Write-Host "skipped as full-tier: $($script:Skipped.Count) scenarios" }
if ($script:ClientCrashes) { Write-Host "client crashes: $($script:ClientCrashes) client calls ended with pwsh exit 139 (segmentation fault). That is the machine (load, memory) or the runtime, not an answer of the product: run the failed scenarios again on a quiet machine before reading them as defects." }
if ($script:VerbCalls) { Write-Host ("verbs: {0} client calls, {1:N1}s in them ({2:N2}s per call); gateway logged {3} verb lines; suite took {4:N0}s" -f $script:VerbCalls, $script:VerbSeconds, ($script:VerbSeconds / $script:VerbCalls), $script:GwVerbs, $sw.Elapsed.TotalSeconds) }
if ($ResultFile) {
    [ordered]@{ suite = $label; slot = $slot; checks = $script:Checks; failed = $script:Failures.Count; quarantined = @($script:Quarantined); failures = @($script:Failures); seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1)
        verbCalls = $script:VerbCalls; verbSeconds = [math]::Round($script:VerbSeconds, 1); clientCrashes = $script:ClientCrashes; tier = $Tier; skipped = @($script:Skipped); scenarios = @($script:ScenarioStats); gatewayVerbLines = $script:GwVerbs; portBase = $script:PortBase } | ConvertTo-Json -Depth 4 | Set-Content -Path $ResultFile
}
exit ([int]($script:Failures.Count -gt 0))
