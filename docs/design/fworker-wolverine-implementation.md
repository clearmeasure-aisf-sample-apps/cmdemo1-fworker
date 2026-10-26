# Implementing ADR 0001: the sequence of work and its parallel lanes

Status: implementation plan, 2026-10-26. Nothing here is built. Asked for by the owner: "prepare for implementation. I need the
sequence of work, phased out including which tasks can be implemented by parallel subagents", then "Program.cs refactoring has to be
first and stabilized before enhancement plan commences." It turns:
- `gateway-composition.md` (stage C, first);
- the phases of `fworker-wolverine-refactor.md` (R0 to R9, waves W0 to W9);
- the speed rules of `delivery-speed.md`;
- the hooks of `system-gateway.md`;

into tasks: each one pull request, with what it comes after, the files it owns, and its size.

| View | Source | Rendered |
|---|---|---|
| Stage C and the waves | `fwi-waves.puml` | [fwi-waves.svg](diagrams/fwi-waves.svg) |

![Waves](diagrams/fwi-waves.svg)

## 1. How parallel subagents work on this repository

1. **One task, one subagent, one worktree, one pull request.** S is a day or two, M about a week, L two weeks or more of one session. A
   task that grows is split, not widened.
2. **At most four subagents at once, lane D included,** plus one reviewer per pull request. CI is the bottleneck: each pull request runs
   ten Tier 1 jobs for about 40 minutes.
3. **What a subagent can run:** `dotnet build` and pure dotnet tests only. The worktree guard refuses pwsh to subagents, and Tier 1
   allows one run per machine. So `tests/smoke.ps1`, the parity tests (which call the rule scripts), Tier 1 and the SQL-backed workflow
   tests run in CI. Every rule-script change is therefore first checked in CI, at about 40 minutes per round. Stage C adds a CI job that
   runs the smoke and parity tests in minutes.
4. **The main session is the standing integrator of the hot files.** A task that needs an addition to a hot file it does not own puts
   the change in its pull request only when its row lists that file; otherwise it asks the main session, which serialises it.
   - **Hot files:**
     - `gateway/Program.cs` (until stage C ends);
     - the composition files `gateway/Composition/*`;
     - `config/instance.json`, `config/deploy.overrides.json`;
     - `tests/scenarios/run.ps1` (`$SuiteDefs`), `tests/scenarios/lib.ps1`, the shared scenario files (`owner.ps1`, `octopus.ps1`,
       `leader.ps1`);
     - `tests/FakeGitHub/Program.cs` (also the fake Octopus);
     - `.github/workflows/tests.yml`;
     - the solution file and `gateway/Gateway.csproj`;
     - `plugin/scripts/lib/Board.ps1`, `Verbs.Sweep.ps1`;
     - from W6, the verb registry (`gateway/Verbs/*`; from W9 one route file per group);
     - from W2, the EF Core `DbContext` and its migrations, and the Wolverine registration (`gateway/Composition/Wolverine*.cs`).
   - Each wave's `.0` task creates the stubs the wave's tasks will fill (a registration file per seam, a table per new saga, a recurring
     job slot), so the parallel tasks add code in their own files.
   - A task that adds a Tier 1 suite owns its dot-source line and `$SuiteDefs` entry in `run.ps1` and its `suites:` line in
     `tests.yml`, in the same pull request (the "Every suite is in a job" step fails otherwise).
5. **Parallel pull requests are tested together on master.** The ruleset does not require branches to be up to date, and `tests.yml`
   does not run on master pushes. So the main session runs `gh workflow run Tests` on master after each batch of merges and before
   every gate.
6. **Merge windows.**
   - Every merge to master restarts the acting tdd gateway. No pull request merges during a measured batch, a soak or a shadow period,
     except fixes for that period.
   - Pull requests from outside the plan (operator-session fixes) avoid the hot files of the running wave; during stage C, `Program.cs`
     is frozen for them.
7. **Every pull request:** the pre-commit review by a reviewer subagent, Tier 1 green, auto-merge. Flaky jobs are re-run at most twice,
   then read.
8. **Kit-owned files stay untouched:** `build.yml` belongs to the demo-environment kit (only its tests job is local). New checks go into
   `tests.yml`. The Dockerfile copies every `*.csproj` before `dotnet restore`.
9. **Behaviour behind flags** (refactor plan rule 3), default v1. Switching a flag on in tdd is the main session's measured step, an
   owner-protected `deploy.overrides.json` change.
10. **Subagents follow the owner's standing rule** (Opus, low effort). Each brief gives the task's row, the files it owns and must not
   touch, its tests and its "done when".

## 2. Before the first task (W-1: decisions and prerequisites)

From the refactor plan's section 7, with who decides and which task needs it:

| Item | Who | Needed by |
|---|---|---|
| The test packages of the new .NET test projects (test SDK, test framework, assertions), pinned | owner | C0 (W1.0's projects reuse them) |
| Lift cmdemo1-system#314's hold on uat and prod promotion (an unbounded wait until then) | owner, cmdemo1 session | the stabilisation gate |
| Merge #253 (delivery speed) and #254 (Wolverine, plan, ADR 0001, system gateway, composition, this plan) | owner | C0 |
| Freeze `Program.cs` for pull requests from outside the plan during stage C | owner | C0 |
| The base (fworker) and, by W8, the incident rollback policy | owner | W0, W8 |
| cmdemo1-aisf stops acting on board 679 (kit watcher change) | owner, Platform Hub, cmdemo1 session | W0 |
| At least one replica for the acting gateway; `ui-auto-promote` paths (`src/`, `*.csproj`, `*.sln`); `FACTORY_V2_OFF` in cmdemo1-system | owner, done by the cmdemo1 session | W1 (paths), W3 (`FACTORY_V2_OFF`), W4 (replica) |
| Workflow changes (protected): C0 (smoke and parity job), W1.0, W2.0 (SQL Server in `tests.yml`), each new suite | owner | per task |
| The Dockerfile (`src/`) and every `deploy.overrides.json` flag switch (protected) | owner | W1.0, every wave |
| Security-sensitive changes (fworker's CLAUDE.md): stage C: revocations and `RevokeName` (C4a), the auth middleware (C6), the admin and operator filters (C7a), the `Auth` statics (C8); the token's repository claim (W1.8), the loopback route's authentication (W1.6), webhook secrets per source (W4.0), the alert webhook and alert rules (W8.2), revocations, revoke-worker and git-credential (W9) | owner review | per task |
| The new NuGet packages, pinned | owner | W2.0 |
| Azure SQL for the tdd gateway (provisioned, managed identity) | owner, cmdemo1 session | W2.0 |
| `projects_v2_item` delivery (organization or App webhook), or rely on the board reconciler | owner (admin) | W4 |
| The gateway's read access to cmdemo1-system; the `system.json` fields for policy, toolchain and WIP limit (kit and cmdemo1-system) | owner, Platform Hub | W1 (falls back without), W6 and W7 |
| Delivery-speed cross-repo pieces (merge queue ruleset, workorders pipeline, kit `release.yml`, uat settle, docs-only overflow) | owner, Platform Hub | W7, each optional |

## 3. Stage C: Program.cs becomes a composition root (first, then stabilised)

The steps of `gateway-composition.md` section 5, each behaviour-neutral. `Program.cs` is hot: steps that run together remove different
blocks and merge in sequence.

| Task | Content | Owns | After | Size |
|---|---|---|---|---|
| C-1 (defect) | board reading ignores other repositories: `Get-BoardList` and the `projects_v2_item` resolution read `repository.nameWithOwner` and drop other repositories' cards; the fake's board and node queries return `repository`; one scenario | `Board.ps1` (Get-BoardList), `Verbs.Sweep.ps1` (872-878), `tests/FakeGitHub/Program.cs` (board queries) | W-1, parallel with C0 | S |
| C0 | **the safety net:** the golden-write harness (the fake records writes per scenario; normalized diff), its baseline recorded from a CI artifact of master; the route-and-auth table test, pinning the eight `RawText` patterns `InProcess` needs, and one in-process call per pinned pattern; the start-up test (`ValidateOnBuild`, `ValidateScopes`, hosted services stop in time); a CI job for the smoke and parity tests | `tests/FakeGitHub/*`, `tests/scenarios/lib.ps1`, `tests/scenarios/golden/*`, a new `tests/Gateway.Tests` (test packages: section 2), `tests.yml` | W-1, C-1 (baseline) | M |
| C1 | the classes after `app.Run()` (`Runner`, `EventHub`, `PlantTicker`, `Sweeper`, `SweepGap`) to their own files | `Program.cs` (2496-2880), new files | C0 | S |
| C2 | the dashboard as an embedded resource served with `text/html; charset=utf-8` (not `wwwroot`: Tier 1 runs the build output in Production, `lib.ps1:42`); the connect prompt to an embedded template, its rendered text pinned by a test | `Program.cs` (2881-3294, route 769), `gateway/Templates/*` | C0 | S |
| C3 | configuration: `GatewayOptions`, `InstanceConfig` (one path), `TimingOptions`, the settings registered as singletons; only the static helpers (`Fail`, `ReadBody`, `Refuse`, `ParseCursor`, `FixedEquals`) to `HttpResultsX`; today's `GatewayLeader` registered as `ILeadership` | `Program.cs` (15-117, 450-483, helpers), `gateway/Configuration/*` | C1 | M |
| C4a | `RevocationService` (loader, status, `RevokeAsync`) | `Program.cs` (450-515, 925-946), `gateway/Identity/Revocation*` | C3 | M |
| C4b | `OperatorReleaseLoader` | `Program.cs` (1888-1915), `gateway/Operator/*` | C4a (it reads `revocationsOff`) | S |
| C4c | `TranscriptService` (save loop gated by the `ILeadership` registered in C3; the final flush stays an `ApplicationStopping` callback) | `Program.cs` (249-250), `Transcripts.cs` | C3 | S |
| C4d | `LeadershipService` and `SnapshotCoordinator` (snapshot restore, save, debounce, hand-over, leader start) | `Program.cs` (149-162, 280-432, 516-554), `gateway/Leadership/*` | C4a, C4b, C4c | L |
| C5a | `AddGateway*`, `IPlantHooks`; `InProcess` resolves endpoints through `EndpointDataSource` instead of being built after `Build()` | `Program.cs` (118-248, 252-279, 2488), `gateway/Composition/*`, `Plant.cs` (constructor), `Mcp/InProcess.cs` | C4d | M |
| C5b | prompt builders as services, rendered texts pinned by a test; `RuntimeRegistry` | `Program.cs` (171-185, 2256-2271, 2445-2487), `Runtimes/*` (constructors), `gateway/Templates/*` | C5a | M |
| C5c | `CopilotCiReleaseService` (2272-2292); `CopilotOwnerVerbs` (`OwnerVerb`, 2294-2299) and `CopilotSettleClient` (2356-2364) as constructor parameters; `CloudOwnerWatch` (2300-2353, 2365-2417) whose options carry the differences between the runtimes: interval (`CiReleaseSeconds` against `OwnerWatchSeconds`), master fixer and `OverBudget` (Copilot only), `LoadOwner`'s runtime test (`!= cursor-cloud` against `== cursor-cloud`), `Ending`'s head (`HeadOf` against null) | `Program.cs` (2272-2417), `gateway/Runtimes/Owner*` | C5b | M |
| C5d | `ManagerCopilotService` (2418-2444), `McpSessionSweeper` with cancellation (2493) | `Program.cs` (2418-2444, 2489-2493) | C5a | S |
| C6 | `GatewayAuthMiddleware` and `AdmissionService`; `ICallerContext` (scoped: `Who`, `Admitted`, `OperatorActive`, `By`, `SessionLinkOf` 561-575) and `PublicBase` (singleton: `seenBase`, `LinkBase`, `NoteBase`, 146-147 and 622-629; written by the middleware, read by `SessionLinkOf` and `Plant.SessionPage`) | `Program.cs` (146-147, 557, 561-653, 800-804), `gateway/Http/*` | C5a to C5d | M |
| C7a | endpoint modules: health, status (`StatusQuery`), metrics, dashboard, plugin, operator releases; the endpoint filters (guards only as endpoint filters, never middleware or ASP.NET authorization: `InProcess` calls `RequestDelegate` directly); `BuildInfo` (`buildVersion`, `started`) for `/_build` | `Program.cs` (their blocks), `gateway/Endpoints/{Health,Status,Metrics,Dashboard,Plugin,OperatorRelease}*`, `gateway/Http/Filters*` | C6 | M |
| C7b | endpoint modules: runtime hosts, Copilot, transcripts, webhook, demo, item admin, worker admin | `Program.cs` (their blocks), `gateway/Endpoints/{RuntimeHost,Copilot,Transcript,Webhook,Demo,ItemAdmin,WorkerAdmin}*` | C7a (filters) | M |
| C7c | endpoint modules: wait, events and SSE, presence, messaging, supervisor, test hooks | `Program.cs` (their blocks), `gateway/Endpoints/{Wait,EventStream,Presence,Messaging,Supervisor,TestHooks}*` | C7a (filters) | M |
| C7d | `VerbEndpoints` and `VerbPipeline` (seam S1's home) | `Program.cs` (1017-1235, 1451-1482), `gateway/Endpoints/Verb*`, `gateway/Verbs/*` | C7b, C7c | L |
| C8 | rules into policy classes (verb, credential, metrics access, presence, release record, derived text) with unit tests; statics (`Auth.*`, `GatewayMcp.Tools`, `QuotaBlock`) into injected services; the architecture test (Program.cs at most 60 lines, no behaviour) | `gateway/Policies/*`, `Auth.cs` (statics), `Mcp/*` (static), `tests/Gateway.Tests` | C7d | M |

Parallel: C-1 beside C0; C1 and C2; C4a and C4c together, then C4b after C4a, then C4d after all three; C5d beside C5b and C5c; C7b and C7c together (after C7a). C-1 does not touch Program.cs and ships now, ahead of stage C: board 679 links eight repositories (system-gateway.md 1). It and C0 both touch the fake, in different hunks; C-1 merges first, and C0's baseline is recorded after it.

**Stabilisation gate (owner's condition):** all of these, before W0 starts:
- Program.cs is the composition root and the architecture test passes;
- Tier 1, the golden writes and the route-and-auth table are unchanged;
- the refactored release runs in tdd for **two measured 8-item batches** with no regression against the 2026-10-25 baseline;
- the same release is promoted to uat and prod by `ui-auto-promote` and starts there (needs cmdemo1-system#314's hold lifted: section 2);
- **two weeks** pass without a fix to the refactored code.

## 4. The enhancement waves (after stage C)

With stage C done, `Program.cs` is no longer a hot file; areas live in their own modules and services, which the tasks below name.

### W0: R0, safety and quick wins (up to 4)

| Task | Content | Owns | After | Size |
|---|---|---|---|---|
| W0.0 | integrator: the flags framework `{engine, v2Environments}`, `FACTORY_V2_OFF` reader, W0's flags | `config/*`, `gateway/Configuration/Flags*` | stage C gate | S |
| W0.1 | fencing: lease epoch into `sync-board`; lease checks before every mutating step (merge, settle, promotion, Sign-off, `Save-State`); `MayAct` in `Plan` and in `RuntimeManager.Provision`; supersession awaits the rule script at most 45 s, then kills it | `gateway/Sweeper.cs`, `Leadership/*`, `RunnerPool.cs`, `RuntimeManager.cs` (Provision), `Verbs.Sweep.ps1`, `Verbs.Delivery.ps1`, `Verbs.Promote.ps1`, `Verbs.Merge.ps1`, `Board.ps1` (Save-State lease check) | W0.0 | M |
| W0.2 | the day budget counted from the task history (every Start and overflow run since 00:00 UTC) | `RuntimeManager.cs`, `IWorkerRuntime.cs`, `OctopusRunbookRuntime.cs`, the fake Octopus in `tests/FakeGitHub/Program.cs` | W0.1 | S |
| W0.3 | `Save-State` `rev` check; the 48,000-character state budget (trim `promote_health`, `flow`, lineage) | `Board.ps1` (Save-State, Get-State) | W0.1 | S |
| W0.4 | qualified refs: the `Depends on` parser and the keeper's cycle check (the board reading shipped in C-1) | `Verbs.Keeper.ps1`, `Verbs.Sweep.ps1` (419) | W0.6 | S |
| W0.5 | the exit-21 text: "stay connected while checks run" | `Verbs.Item.ps1` (499-500), `plugin/skills/factory-development`, `factory-owner-session` | W0.0 | S |
| W0.6 | replenish backpressure: the waiting count from the last plan into the sweep input; `onlyWhenIdle` | `Replenish.ps1`, `Plant.cs` (last plan's waiting count), the sweep input in `Sweeper.cs`, `Verbs.Sweep.ps1` (the `Invoke-Replenish` call, 779) | W0.1 | S |
| W0.7 | durable budgets: failures, launches, `idleFollowUps` into the item state; runtime health into the snapshot | `RuntimeManager.cs`, `Leadership/SnapshotCoordinator*`, `Owner.ps1` | W0.2 | M |
| W0.8 | flow metrics: one timestamp per stage (`Move-OwnerTo` and the stage hooks), exposed through the metrics route | `Metrics.ps1`, `Owner.ps1` (`Move-OwnerTo`), `gateway/Endpoints/Metrics*` | W0.7 | M |
| W0.9 | ranking comparer and plan on slot freed (queued item numbers, the hub delegate) | `Plant.cs` (Plan), `Plant.Runtimes.cs`, `RuntimeManager.cs`, `EventHub.cs` | W0.6, W0.7 | M |

Parallel start: W0.1 and W0.5; then W0.2, W0.3 and W0.6 after W0.1; then W0.4 after W0.6, W0.7 after W0.2; W0.8 and W0.9 as their "after" items merge.
**Gate:** Tier 1 green; a measured tdd batch no slower than the baseline; the golden-write baseline re-recorded from master (W0 changes writes on purpose: rev, flow times, durable budgets).

### W1: R1, structure and seams, no behaviour change (up to 4)

| Task | Content | Owns | After | Size |
|---|---|---|---|---|
| W1.0 | integrator: the solution, projects `src/Fworker.Contracts`, `Domain`, `Persistence`, `Integrations`, `Workflows` and their test projects, `Gateway.csproj` references, the Dockerfile, `tests.yml`; one empty registration file per seam | `*.sln`, `src/*/*.csproj`, `gateway/Gateway.csproj`, Dockerfile, `tests.yml`, `gateway/Composition/Seams*` | W0 gate | S |
| W1.1 | `ItemId` (parse, format, lowercase) and message base types; `ISystemCatalog` and its loader as a library (`system.json` through the API, `instance.json` fallback) | `src/Fworker.Contracts/*`, `src/Fworker.Integrations/Catalog/*` | W1.0 | M |
| W1.2 | the catalog's wiring and its mismatch report | `gateway/Composition/Seams*` (catalog), `gateway/Endpoints/Status*` (report) | W1.1 | S |
| W1.3 | Domain, first ports: the ranking comparer (ported without rewiring the call) and the journal text, with parity tests | `src/Fworker.Domain/{Ranking,JournalText}*`, `tests/Fworker.Domain.Tests` | W1.0 | M |
| W1.4 | S1: the engine branch inside `VerbPipeline` (v1 only); rule-script answers return their posted journal lines | `gateway/Verbs/*`, `factory.ps1`, `Journal.ps1` | W1.0 | M |
| W1.5 | S2 command port and S5 timers, v1 implementations | `gateway/Ports/{Commands,Timers}*`, call sites in `Plant.cs` and `RuntimeManager.cs` | W1.0 | M |
| W1.6 | S3 event ingress (also the verbs' `RequestSweep`) and S6 loopback route (authenticated; owner review) | `gateway/Ports/Ingress*`, `gateway/Endpoints/{Webhook,Loopback}*`, `gateway/Verbs/*` (the `RequestSweep` call) | W1.4 | M |
| W1.7 | S4 item state store | `gateway/Ports/ItemState*` | W1.4 | S |
| W1.8 | the token's repository claim (`i1`/`r1`, absent means home; owner review); launch fields (repository, clone URL, branch, toolchain) | `Auth.cs`, `Runtimes/*Runtime.cs` (launch only) | W1.1 | S |
| W1.9 | isolation scenarios with a second repository in the fake (webhook dropped, card ignored, `Depends on other#N` not home #N, same number no collision) | `tests/FakeGitHub/Program.cs`, a new `tests/scenarios/repos.ps1` with its `run.ps1` and `tests.yml` lines | W0.4, W1.0 | M |
| W1.10 | the verb inventory (73 verbs, owner and destination) | `docs/design/verb-inventory.md` | W1.0 | S |

Parallel start: W1.1, W1.3, W1.4, W1.5; then W1.6, W1.7, W1.8, W1.9, W1.2, W1.10 as slots free.
**Gate:** golden writes identical to the baseline recorded at the W0 gate; per-item event sequences unchanged on tdd.

### Lane D: Domain ports, beside W1 to W7 (one of the four slots)

One pull request each in `src/Fworker.Domain` and its tests, never a hot file.
- **Parity tests,** for ports of existing rule-script functions:
  - D2: the evidence composite;
  - D3: release choice and the content gate;
  - D4: the docs-only test.
- **Specification tests,** for rules v1 does not have:
  - D1: the admission rules beyond `Depends on`;
  - D5: the recovery policy table;
  - D6: file-overlap;
  - D7: host headroom and footprint.
- Plans as data belong to W6.1, not lane D.
- **Order by need:** D2 and D3 before W5; D4 and D5 before W6; D1, D6 and D7 before W7.

### W2: R2, the Wolverine host, inert (up to 3)

| Task | Content | Owns | After | Size |
|---|---|---|---|---|
| W2.0 | integrator: the packages; the connection-string flag; SQL Server as a service container in the Tier 1 jobs (owner jobs split once more); an image-start check without a connection string in `tests.yml`; the `DbContext` stub | `*.csproj`, `tests.yml`, `config/*`, `src/Fworker.Persistence/FactoryDbContext.cs` (stub) | W1 gate, W-1 packages and SQL | S |
| W2.1 | persistence: tables for history, correlation map, open-instance index, projection versions, the catalog's last good copy; migrations | `src/Fworker.Persistence/*` | W2.0 | M |
| W2.2 | host wiring: registered only with a connection string; Solo mode started when Acting and stopped on supersession; storage set up in the background; `/ready` independent; OpenTelemetry and the Azure Monitor exporter; the dead-letter admin page | `gateway/Composition/Wolverine*`, `gateway/Endpoints/Admin*` | W2.1 | M |
| W2.3 | shadow history: every returned journal line and every ingress event, best-effort | `gateway/Ports/*` (history sinks) | W2.2 | S |
| W2.4 | `Fworker.Workflow.Tests`: tracked sessions against SQL Server (CI only) | `tests/Fworker.Workflow.Tests/*` | W2.2 | S |

**Gate:** 7 days in tdd; a history row for every posted line; acting with the database stopped; the uat and prod images start without
one.

### W3: R3, durable side effects (up to 3)

| Task | Content | Owns | After | Size |
|---|---|---|---|---|
| W3.0 | integrator: `commands` flag; `FACTORY_V2_OFF` for commands; the integration command base (error classification, `IntegrationFailed`, idempotency keys); `GitHubBudget` keyed by (credential, resource); the GitHub client per repository and installation per owner (H6) | `src/Fworker.Integrations/{Commands,GitHub}/*`, `GitHubBudget.cs`, `config/*` | W2 gate | M |
| W3.1 | runtime start and stop and runbook runs through the outbox, with result messages back into `RuntimeManager` | `RuntimeManager.cs`, `OctopusRunbookRuntime.cs` | W3.0 | M |
| W3.2 | journal lines through the outbox handler that runs the `journal` verb; per-item partitioning; `ReserveLaunched` sends from the chain | `Plant.cs` (journal chain), `src/Fworker.Integrations/Commands/Journal*` | W3.0 | M |
| W3.3 | Copilot launch reservation and its reconciler | `CopilotCloudRuntime.cs`, `src/Fworker.Integrations/Commands/Copilot*` | W3.0 | S |
| W3.4 | kill and restart scenarios for commands | a new suite with its `run.ps1` and `tests.yml` lines | W3.1, W3.2 | S |

**Gate:** no lost or duplicated line, start or stop in a tdd batch with a kill.

### W4: R4, durable ingress and the board reconciler (up to 3)

| Task | Content | Owns | After | Size |
|---|---|---|---|---|
| W4.0 | integrator: `ingress` flag; a webhook secret per source (owner review); the recurring-job slots | `config/*`, `gateway/Endpoints/Webhook*`, `gateway/Composition/Wolverine*` | W3 gate | S |
| W4.1 | ingress to the inbox: logical and business-key deduplication; correlation-map writers from verbs and runtimes | `gateway/Ports/Ingress*`, `src/Fworker.Integrations/Correlation/*` | W4.0 | M |
| W4.2 | C# Octopus watcher: `ReleaseCreated`, deployment events, `SignOffRequested`, commit resolved | `src/Fworker.Integrations/Octopus/Watcher*` | W4.0 | M |
| W4.3 | board reconciler and catalog re-read as recurring jobs (cards keyed by repository; cmdemo1-system pushes before v1's filter) | `src/Fworker.Workflows/Reconcilers/*` | W4.1 | M |

**Gate:** every board-relevant change of a tdd batch has its event in the history.

### W5: R5, DeliveryTrainSaga (up to 3)

| Task | Content | Owns | After | Size |
|---|---|---|---|---|
| W5.0 | integrator: `delivery` flag; `ItemMerged(commit, gates, dependencies)` from v1 through S6; the train's and delivered-to tables | `Verbs.Merge.ps1`, `Verbs.Delivery.ps1` (emit only), `config/*`, `src/Fworker.Persistence/*` (tables) | W4 gate, D2, D3 | S |
| W5.1 | `DeliveryTrainSaga` per deployable and environment: candidates, evidence, newest qualifying release, Sign-off answers, due timers, supersession, the delivered-to table, the `dependencies-delivered` slot (off), policy and promoter per deployable from the catalog (H9) | `src/Fworker.Workflows/Delivery/*` | W5.0 | L |
| W5.2 | shadow comparison and its report | `src/Fworker.Workflows/Delivery/Shadow*`, `gateway/Endpoints/Admin*` | W5.1 | S |
| W5.3 | the switch: authoritative only with no Sign-off pending, or adoption of pending gateway deployments; v1 promotion off in the same release | `Verbs.Promote.ps1` (off switch), `src/Fworker.Workflows/Delivery/Switch*` | W5.2 | S |

**Gate:** zero shadow differences over 3 releases; one batch to prod by v2.

### W6: R6, the docs plan (up to 4)

| Task | Content | Owns | After | Size |
|---|---|---|---|---|
| W6.0 | integrator: `intake.v2Plans` flag; the `engine:v2` label; the per-repository WIP intake rule; the saga tables | `config/*`, `Verbs.Sweep.ps1` (intake), `src/Fworker.Persistence/*` (tables) | W5 gate, D4, D5 | S |
| W6.1 | `WorkItemSaga` core: plans as data, steps, generation and arm ids, `NotFound`, `IRevisioned`, partitioning; decision verbs by `InvokeAsync` through S1 | `src/Fworker.Workflows/WorkItem/*`, `gateway/Verbs/*` (v2 branch) | W6.0 | L |
| W6.2 | C# direct merge (squash with the PASS head, mergeability wait, branch update, out-of-date retries) | `src/Fworker.Integrations/GitHub/Merge*` | W6.0 | M |
| W6.3 | projections: board, labels and state comment, versioned; echo suppression | `src/Fworker.Integrations/GitHub/Projection*` | W6.0 | M |
| W6.4a | v1 skips v2 items in the rule scripts: `Test-ItemInScope` and its callers; v2 counted in intake WIP (`Verbs.Sweep.ps1:804`) and the replenisher's `targetOpen` | `Board.ps1`, `Verbs.Sweep.ps1`, `Qc.ps1`, `Replenish.ps1` (scope and counts) | W6.0 | M |
| W6.4b | v1 skips v2 items in C#: `BaseUpdate.cs`, the plant's offers, leases and expire-claim (`Plant*.cs`, `Lease.cs`, `Intake.cs`), `Plant.PassMerge` | those files | W6.0 | M |
| W6.4c | v1 forwards actions on v2 items to the saga through S1: `Manager.cs`, the operator's unpark and resettle, keeper verbs, `CopilotCiRelease`, copilot-settle and copilot-owner (`CopilotCloudRuntime.cs`, `CursorCloudRuntime.cs`), runtime adoption (`RuntimeManager.cs`, `OctopusRunbookRuntime.cs`) | those files, `gateway/Endpoints/ItemAdmin*` | W6.1, W6.4b | M |
| W6.5 | intake labelling at every entry point (replenish marker plus Auto Merge) | `Verbs.Keeper.ps1`, `Replenish.ps1` (label), intake | W6.4a | S |
| W6.6 | the docs plan's steps; the plant offers v2 items only to the separate runtime's sessions; the `FACTORY_V2_OFF` freeze | `src/Fworker.Workflows/WorkItem/Plans/Docs*`, `Plant.cs` (offer filter) | W6.1, W6.2, W6.3, W6.4a, W6.4b, W6.4c, W6.5 | M |
| W6.7 | views: dashboard, status and digest read the history for v2 items, grouped by repository | `gateway/Endpoints/{Dashboard,Status}*`, `StatusQuery*` | W6.1 | S |

Parallel start: W6.1, W6.2, W6.3, W6.4a; then W6.4b, W6.5, W6.7; then W6.4c; W6.6 last.
**Gate:** 8 docs items to Done on v2; v1 unaffected on its own slots.

### W7: R7, the feature plan (up to 4)

| Task | Content | Owns | After | Size |
|---|---|---|---|---|
| W7.0 | integrator: `scheduler` and `merge` flags; the owner's decisions on overflow and the uat settle; the scheduler's and merge train's tables | `config/*`, `src/Fworker.Persistence/*` (tables) | W6 gate, D1, D6, D7 | S |
| W7.1 | full `CapacityScheduler`: ranking, admission chain, ledger ownership, slot freed on `SessionEnded`, the `host-full` outcome | `src/Fworker.Workflows/Capacity/*` | W7.0 | L |
| W7.2 | `MergeTrainSaga` (direct, train, queue) per repository; v1's merge moved onto R6's C# direct merge | `src/Fworker.Workflows/Merge/*`, `Verbs.Merge.ps1` (delegation) | W7.0 | L |
| W7.3 | the feature plan: Functional Testing with CI alongside (watcher in Functional Testing; red CI as a fix-forward round), the file-overlap hold in the invoke, the 5.1 answer texts (skills branch on the answer) | `src/Fworker.Workflows/WorkItem/Plans/Feature*`, `plugin/skills/*` | W7.0 | L |
| W7.4 | host metrics and capability labels, both sides: `reconcile-hosts.sh`, `start-worker.sh` (counting `fw-*`, headroom, `host-full`), Tentacle tags (applied by the cmdemo1 session); the gateway's parsing of the new report fields | `deploy/octopus/*`, `OctopusRunbookRuntime.cs` (report parsing) | W7.0 | M |
| W7.5 | v1 asks the scheduler through S6; slot release through the scheduler | `RuntimeManager.cs`, `Plant.Runtimes.cs` | W7.1 | M |
| W7.6 | smoke evidence: the Smoke test runbook and `[Category("Smoke")]` in cmdemo1-workorders (a pull request there) | `deploy/octopus/*` (runbook), cmdemo1-workorders tests | W7.4 | M |
| W7.7 | `dependencies-delivered` rule on | `src/Fworker.Workflows/Delivery/*` | W7.1 | S |
| W7.8 | docs-only overflow (`overflowItems`, `MayOverflow`), if the owner agreed | `OctopusRunbookRuntime.cs`, `RuntimeManager.cs` | W7.4, W7.5 | S |
| W7.9 | intake puts every new Auto Merge item on v2 | intake | W7.3 | S |
| W7.10 | approved cross-repo pieces, each its own pull request: the merge queue ruleset with `merge_group`, the workorders pipeline changes with fworker's docs-only signal (`Get-MergeDocsOnly`), the kit's `release.yml` | other repositories, `Verbs.Delivery.ps1` (docs-only signal) | W7.2 | per piece |

Parallel start: W7.1, W7.2, W7.3, W7.4; then W7.5, W7.6, W7.7, W7.9; then W7.8, W7.10.
**Gate:** an 8-item code batch to prod on v2 against the baseline.

### W8: R8, incidents (up to 3)

| Task | Content | Owns | After | Size |
|---|---|---|---|---|
| W8.0 | integrator: `incidents` flag; the rollback policy; the incident tables | `config/*`, `src/Fworker.Persistence/*` (tables) | W7 gate | S |
| W8.1 | `IncidentSaga` per (deployable, environment), the hotfix plan, the train hold that lets the incident's own work through | `src/Fworker.Workflows/Incident/*`, `WorkItem/Plans/Hotfix*`, `Delivery/*` (hold) | W8.0 | L |
| W8.2 | alert ingress: the Azure Monitor webhook route; alert rules and the factory's out-of-band alert (applied by the cmdemo1 session; owner review) | `gateway/Endpoints/Alerts*`, cmdemo1-system (rules) | W8.0 | M |
| W8.3 | rollback through the DeliveryTrain with the migration precondition; `Undelivered` | `src/Fworker.Workflows/Delivery/Rollback*` | W8.1 | M |

**Gate:** a staged incident in the app's tdd, end to end.

### W9: R9, retire v1 (up to 4, then one)

| Task | Content (verb groups of the refactor plan, R9) | Owns | After | Size |
|---|---|---|---|---|
| W9.0 | integrator: the remaining tables; the flag that moves QC-gated items to v2; one route registration file per verb group (`gateway/Verbs/Routes/{Item,Merge,Delivery,ReadModels,Keeper,Services,Board}.cs`, stubs) | `src/Fworker.Persistence/*`, `config/*` | W8 gate | S |
| W9.1 | QC environments and the QC gate (`qc-*`, `Qc.cs`, `Qc.ps1`) as a saga per (repository, pull request) | `src/Fworker.Workflows/Qc/*`, `Qc.cs` | W9.0 | L |
| W9.2 | the item verbs (`get-item` … `expire-claim`) | `src/Fworker.Workflows/WorkItem/*`, `gateway/Verbs/Routes/Item.cs` | W9.0 | M |
| W9.3 | the merge train verbs (`approve-pr`, `merge`, `clear-hold`, `base-watch`, `base-update-branch`) | `src/Fworker.Workflows/Merge/*`, `BaseUpdate.cs`, `gateway/Verbs/Routes/Merge.cs` | W9.0 | M |
| W9.4 | the delivery train verbs (`settle`, `publish-demo`, `ci-release`, `copilot-settle`, `copilot-owner`, `private-build`) | `src/Fworker.Workflows/Delivery/*`, `Runtimes/Copilot*`, `gateway/Verbs/Routes/Delivery.cs` | W9.0 | M |
| W9.5 | read models (`pr-checks`, `wait-for-checks`, `inspect-facts`, `get-delivery`, `model-tier`, `check-secrets`) | `src/Fworker.Workflows/ReadModels/*`, `gateway/Verbs/Routes/ReadModels.cs` | W9.0 | M |
| W9.6 | keeper and manager commands (`backlog`, `amend-criteria`, `split-item`, `create-item`, `set-priority`, `link-dependency`, `manager-action`) | `src/Fworker.Workflows/Keeper/*`, `Manager.cs`, `gateway/Verbs/Routes/Keeper.cs` | W9.0 | M |
| W9.7 | services (owner review): `revocations`, `revoke-worker`, `git-credential`, `operator-release(s)`, `metrics` | `gateway/Identity/*`, `gateway/Operator/*`, `gateway/Verbs/Routes/Services.cs` | W9.0 | M |
| W9.8 | transcripts to the database; `sync-board` and `ingest-event` into the reconciler and ingress; `journal` into history and projection | `Transcripts.cs`, `src/Fworker.Workflows/Reconcilers/*`, `gateway/Verbs/Routes/Board.cs` | W9.0 | M |
| W9.9 | QC-gated items on v2 | intake, `config/*` | W9.1 | S |
| W9.10 | Balanced mode, Wolverine leader election, the session mailbox | `gateway/Composition/Wolverine*`, `src/Fworker.Persistence/*` (mailbox) | W9.1 to W9.9 | M |
| W9.11 | drain `engine:v1` items; remove the sweep engine, server rule scripts, lease, snapshot, `RunnerPool` (one subagent, last) | many | W9.10 | L |

**Gate:** no v1 item left; Tier 1 green with every capability on v2.

## 5. Critical path and calendar

```
W-1 approvals -> Stage C (C-1 and C0 .. C8) -> stabilisation (2 batches, uat/prod promotion, 2 quiet weeks)
  -> W0 -> W1 -> W2 (+7-day soak) -> W3 -> W4 -> W5 (+3 shadow releases) -> W6 -> W7 -> W8 -> W9
     Lane D (Domain ports) beside W1..W7, landing before W5, W6 and W7
```

From the task sizes, along each wave's longest chain:

| Stage or wave | Longest chain | Weeks |
|---|---|---|
| Stage C | C0, C1, C3, C4a, C4b, C4d, C5a, C5b, C5c, C6, C7a, C7b/c, C7d, C8 | about 12 |
| Stabilisation | 2 measured batches, promotion, 2 quiet weeks | 2 to 3 |
| W0 | W0.0, W0.1, W0.2, W0.7, W0.8 or W0.9 | about 4 |
| W1 | W1.0, W1.4, W1.6, then W1.9 | about 3 |
| W2 | W2.0, W2.1, W2.2, W2.3, plus 1 week soak | about 4 |
| W3 | W3.0, W3.1, W3.4 | about 3 |
| W4 | W4.0, W4.1, W4.3 | about 2.5 |
| W5 | W5.0, W5.1, W5.2, W5.3, plus 3 shadow releases | about 4 |
| W6 | W6.0, W6.1, W6.4c, W6.6 | about 5 |
| W7 | W7.0, W7.1, W7.5, W7.8 | about 4 to 5 |
| W8 | W8.0, W8.1, W8.3 | about 4 |
| W9 | W9.0, W9.1, W9.9, W9.10, W9.11 | about 6 |

**About 53 to 56 weeks serial: roughly a year,** of which stage C and its stabilisation are about 3 months. The measured gates (soak,
shadow, batches, stabilisation) are about 6 to 7 of those weeks; the rest is coding and CI.

**Shortening it:**
- Next-wave development may start during a soak or shadow period on branches that do not merge until the gate passes. That overlap
  saves up to about 1 to 1.5 months (the gate time it overlaps is about 4 to 6 weeks).
- More than four parallel subagents mainly waits on CI.

**Unbounded waits outside fworker:** the lifting of cmdemo1-system#314 (the stabilisation gate), the Platform Hub watcher change and the cmdemo1 changes (before W0), Azure SQL (before W2), and the
cross-repo approvals (W7).

## 6. What the main session does in each wave

1. Brief and start the wave's integrator, then the parallel tasks as their "after" items merge (at most four at once, lane D included).
2. Review each pull request with a reviewer subagent; watch CI; re-run flaky jobs at most twice; merge on green inside the merge window.
3. Integrate hot-file additions in order; run `gh workflow run Tests` on master after each batch of merges.
4. At the wave's end, switch its flag on in tdd, run the measured batch, record the gate's result in the item history and the handoff.
5. If a gate fails, roll back with `FACTORY_V2_OFF` (through the cmdemo1 session), then fix forward.
