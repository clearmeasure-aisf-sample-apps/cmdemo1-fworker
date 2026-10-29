# Implementing ADR 0001: the sequence of work and its parallel lanes

Status: implementation plan, 2026-10-26, revised the same day. Nothing here is built. Asked for by the owner: "prepare for
implementation. I need the sequence of work, phased out including which tasks can be implemented by parallel subagents", then
"Program.cs refactoring has to be first and stabilized before enhancement plan commences." It turns:
- `gateway-composition.md` (stage C, first);
- the phases of `fworker-wolverine-refactor.md` (R0 to R9, waves W0 to W9);
- the speed rules of `delivery-speed.md`;
- the hooks of `system-gateway.md`;

into tasks: each one pull request, with what it comes after, the files it owns, and its size.

**Revision of 2026-10-26** (the owner's decisions of that day; ADR 0001, "Revision 2026-10-26"):
1. uat and prod do not matter for this plan: if it works in tdd, it is accepted.
2. Green pull requests merge and deploy without approval stops.

So the gates are tdd observation windows in hours, not soaks in days or promotions to uat and prod. The v2 engine is built beside
v1 as new projects, held to the same Tier 1 suites and golden-write oracle, and tdd switches engines **as a whole**, once; there is no
per-item engine. Rollback is a revert or the `FACTORY_V2_OFF` kill switch. Appendix A lists the board's tasks whose scope changed.

| View | Source | Rendered |
|---|---|---|
| Stage C and the waves | `fwi-waves.puml` | [fwi-waves.svg](diagrams/fwi-waves.svg) |

![Waves](diagrams/fwi-waves.svg)

## 1. How parallel subagents work on this repository

1. **One task, one subagent, one worktree, one pull request.** An S or M task is one task cycle of about 50 minutes (section 5); an
   L task is two cycles, and is split into two pull requests where its content allows. A task that grows is split, not widened.
2. **Two to three subagents at once, lane D included, at most four,** plus one reviewer per pull request. Every merge is a release to
   tdd, so merges are serial: the merge, the release and its tdd check take about 10 minutes each.
3. **What a subagent can run:** `dotnet build` and pure dotnet tests only. The worktree guard refuses pwsh to subagents, and Tier 1
   allows one run per machine. So `tests/smoke.ps1`, the parity tests (which call the rule scripts), Tier 1 and the SQL-backed workflow
   tests run in CI. Every rule-script change is therefore first checked in CI. Stage C added a CI job that runs the smoke and parity
   tests in minutes.
4. **The main session is the standing integrator of the hot files.** A task that needs an addition to a hot file it does not own puts
   the change in its pull request only when its row lists that file; otherwise it asks the main session, which serialises it.
   - **Hot files:**
     - `gateway/Program.cs` (until stage C ends);
     - the composition files `gateway/Composition/*`;
     - `config/instance.json`, `config/deploy.overrides.json`;
     - `tests/scenarios/run.ps1` (`$SuiteDefs`), `tests/scenarios/lib.ps1`, the shared scenario files (`owner.ps1`, `octopus.ps1`,
       `leader.ps1`), and from W2 the golden-write declared-differences file of the v2 Tier 1 job;
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
6. **Merge windows and tdd observation windows.**
   - Every merge to master restarts the acting tdd gateway. A **tdd observation window** is a stated number of hours from a release
     with no merge to master, except a fix for the window (which restarts it). In every window the main session checks, after the
     release that opens it and at its end: `/_build` shows the release, `/ready` answers 200, `/api/v1/status` shows the leader acting
     and a successful restore (v1: `plantSnapshot.restore.how`; v2: the Wolverine node's leadership and the sagas resumed), and no
     unplanned restart. Each gate adds its own comparison.
   - Subagents keep working during a window; their pull requests wait for its end to merge.
   - Pull requests from outside the plan (operator-session fixes) avoid the hot files of the running wave; during stage C, `Program.cs`
     is frozen for them.
7. **Every pull request:** the pre-commit review by a reviewer subagent, Tier 1 green, then merge and release to tdd with no approval
   stop (owner, 2026-10-26). Flaky jobs are re-run at most twice, then read. The tasks the earlier plan marked "owner review" (section 2)
   get a reviewer brief with a security focus, and the main session records each in the handoff for the owner to read afterwards.
8. **Kit-owned files stay untouched:** `build.yml` belongs to the demo-environment kit (only its tests job is local). New checks go into
   `tests.yml`. The Dockerfile copies every `*.csproj` before `dotnet restore`.
9. **One engine switch per environment** (refactor plan rule 3), default v1. tdd goes to v2 shadow mode at W2 and to v2 at the cutover
   (W9.9), each a `deploy.overrides.json` change that merges on green like any other pull request. There are no per-capability flags.
10. **Subagents follow the owner's standing rule** (Opus, low effort). Each brief gives the task's row, the files it owns and must not
   touch, its tests and its "done when".

## 2. Before the first task (W-1: decisions and prerequisites)

From the refactor plan's section 7. Since 2026-10-26 nothing inside this repository waits for an approval; what remains are actions
outside it:

| Item | Who | Needed by |
|---|---|---|
| Freeze `Program.cs` for pull requests from outside the plan during stage C | owner (in force) | the rest of stage C |
| The base: this plan proceeds on fworker; ADR 0001 stays Proposed until the owner accepts it | owner | W1.0 |
| cmdemo1-aisf stops acting on board 679 (kit watcher change) | owner, Platform Hub, cmdemo1 session | W0 |
| Azure SQL for the tdd gateway (provisioned, managed identity) | owner, cmdemo1 session | W2.0 |
| At least one replica for the tdd gateway (Balanced mode, timers and recurring jobs from W2) | cmdemo1 session | W2.2 |
| `FACTORY_V2_OFF` in cmdemo1-system's infrastructure code, unset | cmdemo1 session | W2.3 (it also stops shadow mode); drilled at W9.9 |
| `projects_v2_item` delivery (organization or App webhook); without it the board reconciler is the source | owner (admin) | W4, not a stop |
| The gateway's read access to cmdemo1-system; the `system.json` fields for policy, toolchain and WIP limit (kit and cmdemo1-system) | owner, Platform Hub | W1 (falls back without), W6 and W7 |
| Delivery-speed cross-repo pieces (merge queue ruleset, workorders pipeline, kit `release.yml`, docs-only overflow) | owner, Platform Hub | W7.10, each optional, off the critical path |
| The incident rollback policy in the app's prod; until answered, the default of `wolverine-factory.md` 5.5: automatic in the app's tdd and uat, a person's Sign-off in its prod | owner | W8.0, not a stop |

**Removed by the decisions of 2026-10-26:**
- the lifting of cmdemo1-system#314's hold on uat and prod promotion, and `ui-auto-promote`'s application paths (`src/`, `*.csproj`,
  `*.sln`): fworker's uat and prod are out of scope;
- per-pull-request owner approvals: the workflow changes (smoke and parity job, W1.0, SQL Server in `tests.yml`, each new suite), the
  Dockerfile (`src/`), the `deploy.overrides.json` switches, the pinned NuGet packages (W2.0) and the security-sensitive tasks (stage
  C: C4a, C6, C7a, C8; W1.8 the token's repository claim, W4.0 webhook secrets per source, W8.2 the alert webhook and rules, W9.7
  revocations, revoke-worker and git-credential). They merge on green after the reviewer subagent (section 1, rule 7);
- the merges of #253 and #254, which are done.

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

**Stabilisation gate (owner's condition, revised 2026-10-26):** all of these, before W0 starts:
1. the architecture test (C8) is green: `Program.cs` is the composition root, at most 60 lines, no behaviour;
2. the golden writes are unchanged against C0's baseline, with Tier 1 and the route-and-auth table green;
3. **one measured 8-item batch** runs through tdd (from the first worker start to deployed in the app's tdd) with no regression against
   the delivery-speed baseline of 2026-10-25 (batch #706 to #721, `delivery-speed.md` 1): no stage median more than 5 minutes slower
   than the baseline's (Technical Design 5, Development 16, Functional Testing 10, merge 8, master CI and candidate 12, tdd 11) and no
   item parked for a gateway fault;
4. after each release of stage C and of the gate: `/_build` shows the new version, `/ready` answers 200, the leader is acting and the
   plant restore succeeded (`/api/v1/status`: `leader.role`, `plantSnapshot.restore.how`);
5. **a quiet tdd observation window of 4 hours** from the last release (section 1, rule 6): no merge and no fix to the refactored code,
   no unplanned restart, no sweep error, no 5xx from a route other than its designed refusals. The measured batch runs inside it.

**Why 4 hours:** the longest timer the acting gateway runs is the build slot's maximum TTL (`buildSlotMaxTtlSeconds` 7,200), which
expires twice in 4 hours; the full sweep (180 s) runs about 80 times and the lease renewal and host reconcile (5 min each) about 48
times. An 8-item batch on two slots reaches the app's tdd in about 3.5 hours (2026-10-25: about 44 minutes of slot time per item, so
8 x 44 / 2 is about 3 hours of starts, plus about 30 minutes from merge to tdd), so the window is the batch plus a margin, and the gate
takes about 4 to 4.5 hours, not 8. Not seen in it: the day budget's reset at 00:00 UTC and the 24-hour message TTL, neither of which
stage C changes; Tier 1 covers both. (`caeJob.replicaTimeoutSeconds`, 14,400, belongs to the Container Apps job runtime, which is off.)

## 4. The enhancement waves (after stage C)

With stage C done, `Program.cs` is no longer a hot file; areas live in their own modules and services, which the tasks below name.

**How v2 is built and switched.** The Wolverine engine (v2) is built as the new projects of the refactor plan's section 3, beside v1,
inside the same gateway host. Its CI is the same Tier 1 suites run with the engine set to v2 (a second Tier 1 job from W2.4) and the
same golden-write oracle; where v2 writes differently on purpose, the difference is a line in a reviewed declared-differences file.
The engine is chosen **per environment**, once, at start: `engine: { "v2Environments": [], "shadowEnvironments": [] }` in
`config/instance.json`, read for `FACTORY_ENVIRONMENT`. tdd joins `shadowEnvironments` at W2 (v2 reads the board, its commands go to a
recording sink, v1 acts) and `v2Environments` at the cutover W9.9 (v2 acts, v1 does not run). `FACTORY_V2_OFF=1` (cmdemo1-system) puts
the environment back on v1 with no shadow, in a new revision and without an image change; reverting the switch's pull request does the
same through a release.

**What stays from the side-by-side design, and why:**
- **S1, the verb router** (`VerbPipeline`): the verb endpoints call one engine interface, composed per environment. It keeps the
  agents' contract in one place and lets Tier 1 run either engine.
- **S2, the command port:** every external side effect of v2 is one integration command. Behind it are the outbox handlers, or the
  recording sink that shadow mode and the golden-write comparison read.
- **S3, the event ingress:** webhooks and the verbs' events enter through one interface, which in shadow mode also feeds v2.
- **The state comment as v2's projection, in the format v1 reads:** this is how a revert or the kill switch hands every open item back
  to v1 with nothing lost. It stays until v1's code is removed (W9.11).

**What went, because it existed only for v1 and v2 side by side on one environment:** the `engine:v2` label and the intake labelling;
v1 skipping or forwarding v2 items (W6.4a to c, W6.5); the separate runtime and offer filter for v2 items; S4 as a seam in v1's code,
S5 (timers) and S6 (the loopback route); v1's side effects through the outbox; v1 asking v2's scheduler (W7.5); moving v1's merge onto
C# (in W7.2); the per-capability flags (`commands`, `ingress`, `delivery`, `intake.v2Plans`, `scheduler`, `merge`, `incidents`); Solo
mode on the lease holder; the expand-then-contract steps for uat's and prod's lag; v1's promotion off-switch (W5.3); the per-item
moves to v2 (W7.9, W9.9's QC items) and the drain of `engine:v1` items in W9.11.

### W0: R0, safety and quick wins on v1 (2 to 3 at once)

v1 acts in tdd until the cutover, and releases now restart it several times an hour, so the fixes for a sweep acting after its
successor and for blind state writes stay. The durable budgets (W0.7) are v2's by design and go to W6.1.

| Task | Content | Owns | After | Size |
|---|---|---|---|---|
| W0.0 | integrator: the engine switch `engine: { v2Environments, shadowEnvironments }` read for `FACTORY_ENVIRONMENT`; the `FACTORY_V2_OFF` reader | `config/*`, `gateway/Configuration/Engine*` | stabilisation gate | S |
| W0.1 | fencing: lease epoch into `sync-board`; lease checks before every mutating step (merge, settle, promotion, Sign-off, `Save-State`); `MayAct` in `Plan` and in `RuntimeManager.Provision`; supersession awaits the rule script at most 45 s, then kills it | `gateway/Sweeper.cs`, `Leadership/*`, `RunnerPool.cs`, `RuntimeManager.cs` (Provision), `Verbs.Sweep.ps1`, `Verbs.Delivery.ps1`, `Verbs.Promote.ps1`, `Verbs.Merge.ps1`, `Board.ps1` (Save-State lease check) | W0.0 | M |
| W0.2 | the day budget counted from the task history (every Start and overflow run since 00:00 UTC) | `RuntimeManager.cs`, `IWorkerRuntime.cs`, `OctopusRunbookRuntime.cs`, the fake Octopus in `tests/FakeGitHub/Program.cs` | W0.1 | S |
| W0.3 | `Save-State` `rev` check; the 48,000-character state budget (trim `promote_health`, `flow`, lineage) | `Board.ps1` (Save-State, Get-State) | W0.1 | S |
| W0.4 | qualified refs: the `Depends on` parser and the keeper's cycle check (the board reading shipped in C-1) | `Verbs.Keeper.ps1`, `Verbs.Sweep.ps1` (419) | W0.6 | S |
| W0.5 | the exit-21 text: "stay connected while checks run" | `Verbs.Item.ps1` (499-500), `plugin/skills/factory-development`, `factory-owner-session` | W0.0 | S |
| W0.6 | replenish backpressure: the waiting count from the last plan into the sweep input; `onlyWhenIdle` | `Replenish.ps1`, `Plant.cs` (last plan's waiting count), the sweep input in `Sweeper.cs`, `Verbs.Sweep.ps1` (the `Invoke-Replenish` call, 779) | W0.1 | S |
| W0.8 | flow metrics: one timestamp per stage (`Move-OwnerTo` and the stage hooks), exposed through the metrics route; the measure of every later batch | `Metrics.ps1`, `Owner.ps1` (`Move-OwnerTo`), `gateway/Endpoints/Metrics*` | W0.1 | M |
| W0.9 | ranking comparer and plan on slot freed (queued item numbers, the hub delegate) | `Plant.cs` (Plan), `Plant.Runtimes.cs`, `RuntimeManager.cs`, `EventHub.cs` | W0.6 | M |

Parallel start: W0.1 and W0.5; then W0.2, W0.3, W0.6 and W0.8 after W0.1; then W0.4 and W0.9 after W0.6.
**Gate:** Tier 1 green; the golden-write baseline re-recorded from master (W0 changes writes on purpose: rev, flow times); a **2-hour
tdd observation window** with board activity, in which every item that moved has its flow timestamps and no stage median is more than
5 minutes slower than the baseline. Two hours: four full slot-TTL cycles (1,800 s) and about 40 sweeps, enough to see the fencing and
the `rev` check through at least one release hand-over, which opens the window.

### W1: R1, structure and seams, no behaviour change (2 to 3 at once)

| Task | Content | Owns | After | Size |
|---|---|---|---|---|
| W1.0 | integrator: the solution, projects `src/Fworker.Contracts`, `Domain`, `Persistence`, `Integrations`, `Workflows` and their test projects, `Gateway.csproj` references, the Dockerfile, `tests.yml`; one empty registration file per seam (S1 to S3) | `*.sln`, `src/*/*.csproj`, `gateway/Gateway.csproj`, Dockerfile, `tests.yml`, `gateway/Composition/Seams*` | W0 gate | S |
| W1.1 | `ItemId` (parse, format, lowercase) and message base types; `ISystemCatalog` and its loader as a library (`system.json` through the API, `instance.json` fallback) | `src/Fworker.Contracts/*`, `src/Fworker.Integrations/Catalog/*` | W1.0 | M |
| W1.2 | the catalog's wiring and its mismatch report | `gateway/Composition/Seams*` (catalog), `gateway/Endpoints/Status*` (report) | W1.1 | S |
| W1.3 | Domain, first ports: the ranking comparer (ported without rewiring the call) and the journal text, with parity tests | `src/Fworker.Domain/{Ranking,JournalText}*`, `tests/Fworker.Domain.Tests` | W1.0 | M |
| W1.4 | S1: `VerbPipeline` calls one engine interface composed for the environment (only v1 behind it now) | `gateway/Verbs/*` | W1.0 | M |
| W1.5 | S2: the command port as integration-command contracts, with a recording sink for shadow mode and tests; v1 is not rewired onto it | `src/Fworker.Contracts/Commands*`, `src/Fworker.Integrations/Commands/Recording*` | W1.0 | S |
| W1.6 | S3: one event ingress for the webhook routes and the verbs' `RequestSweep`, able to feed a second (shadow) engine | `gateway/Ports/Ingress*`, `gateway/Endpoints/Webhook*`, `gateway/Verbs/*` (the `RequestSweep` call) | W1.4 | M |
| W1.8 | the token's repository claim (`i1`/`r1`, absent means home; security-focused review); launch fields (repository, clone URL, branch, toolchain) | `Auth.cs`, `Runtimes/*Runtime.cs` (launch only) | W1.1 | S |
| W1.9 | isolation scenarios with a second repository in the fake (webhook dropped, card ignored, `Depends on other#N` not home #N, same number no collision) | `tests/FakeGitHub/Program.cs`, a new `tests/scenarios/repos.ps1` with its `run.ps1` and `tests.yml` lines | W0.4, W1.0 | M |
| W1.10 | the verb inventory (73 verbs, owner and destination) | `docs/design/verb-inventory.md` | W1.0 | S |

Parallel start: W1.1, W1.3, W1.4, W1.5; then W1.6, W1.8, W1.9, W1.2, W1.10 as slots free.
**Gate:** golden writes identical to the baseline recorded at the W0 gate (CI); the release check of section 1, rule 6. No window: W1
changes no behaviour.

### Lane D: Domain ports, beside W1 to W7 (one of the slots)

One pull request each in `src/Fworker.Domain` and its tests, never a hot file. Unchanged by the revision.
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

### W2: R2, the Wolverine host, in shadow mode (2 to 3 at once)

| Task | Content | Owns | After | Size |
|---|---|---|---|---|
| W2.0 | integrator: the packages, pinned; the connection-string setting; SQL Server as a service container in the Tier 1 jobs (owner jobs split once more); an image-start check without a connection string in `tests.yml` (the kill switch's case); the `DbContext` stub | `*.csproj`, `tests.yml`, `config/*`, `src/Fworker.Persistence/FactoryDbContext.cs` (stub) | W1 gate, Azure SQL | S |
| W2.1 | persistence: tables for history, correlation map, open-instance index, projection versions, the session mailbox, the catalog's last good copy; migrations | `src/Fworker.Persistence/*` | W2.0 | M |
| W2.2 | host wiring: registered when the environment is in `shadowEnvironments` or `v2Environments` and a connection string is set; **Balanced mode with Wolverine's leader election** and the session mailbox from the start; storage set up in the background; `/ready` independent of the database; OpenTelemetry and the Azure Monitor exporter; the dead-letter admin page | `gateway/Composition/Wolverine*`, `gateway/Endpoints/Admin*`, `src/Fworker.Persistence/*` (mailbox) | W2.1 | L |
| W2.3 | shadow mode: v2 reads S3's events, decides, writes its own history, and sends its commands to S2's recording sink; a comparison of the recorded commands with v1's actual writes, as a report | `gateway/Composition/Shadow*`, `src/Fworker.Integrations/Commands/Recording*`, `gateway/Endpoints/Admin*` (report) | W2.2 | M |
| W2.4 | `Fworker.Workflow.Tests` (tracked sessions against SQL Server, CI only) and the **v2 Tier 1 job**: Tier 1 with the engine set to v2 for the suites v2 claims (a list that grows per wave), and the golden-write diff against the declared-differences file | `tests/Fworker.Workflow.Tests/*`, `tests.yml`, `tests/scenarios/golden/v2-differences*` | W2.2 | M |

**Gate:** a **2-hour tdd observation window** with tdd in `shadowEnvironments`: the release check; v2's node elected leader and its
storage ready; a history row for every ingress event; a 10-minute stop of the database inside the window, during which v1 acts and
`/ready` stays 200. Two hours replaces the 7-day soak: shadow mode writes nothing to GitHub, so what can go wrong is in the process
(storage setup, leader election, the durability agent's cycles, which run every few seconds) and shows within minutes; the window also
spans the release that opened it.

### W3: R3, v2's durable side effects (2 to 3 at once)

| Task | Content | Owns | After | Size |
|---|---|---|---|---|
| W3.0 | integrator: the integration command base (error classification, `IntegrationFailed`, idempotency keys); `GitHubBudget` keyed by (credential, resource); the GitHub client per repository and installation per owner (H6) | `src/Fworker.Integrations/{Commands,GitHub}/*`, `GitHubBudget.cs` | W2 gate | M |
| W3.1 | runtime start and stop and runbook runs as v2 integration commands, with result messages; `RuntimeManager` behind an interface v2 calls; v1's path unchanged | `src/Fworker.Integrations/Commands/Runtime*`, `RuntimeManager.cs` (interface), `OctopusRunbookRuntime.cs` | W3.0 | M |
| W3.2 | the journal as a v2 integration command: `Add-Journal`'s ledger in C# (dedup keys, the 30-line cap, folds by comment id, the `waiting_at` rate limit), golden-identical; per-item partitioning; the launched line's order (#252) | `src/Fworker.Integrations/Commands/Journal*`, `src/Fworker.Domain/JournalText*` | W3.0 | L |
| W3.3 | Copilot launch reservation and its reconciler | `CopilotCloudRuntime.cs`, `src/Fworker.Integrations/Commands/Copilot*` | W3.0 | S |
| W3.4 | kill and restart scenarios for commands, on the v2 engine | a new suite with its `run.ps1` and `tests.yml` lines | W3.1, W3.2 | S |

**Gate:** W3.4 and the journal-fold and octopus suites green on v2 (no lost or duplicated line, start or stop across a kill); a
**2-hour tdd observation window** in shadow mode in which every command v2 records has its counterpart in v1's writes, or a declared
difference.

### W4: R4, durable ingress and the board reconciler (2 to 3 at once)

| Task | Content | Owns | After | Size |
|---|---|---|---|---|
| W4.0 | integrator: a webhook secret per source (security-focused review); the recurring-job slots | `config/*`, `gateway/Endpoints/Webhook*`, `gateway/Composition/Wolverine*` | W3 gate | S |
| W4.1 | ingress to the inbox: logical and business-key deduplication; correlation-map writers from verbs and runtimes | `gateway/Ports/Ingress*`, `src/Fworker.Integrations/Correlation/*` | W4.0 | M |
| W4.2 | C# Octopus watcher: `ReleaseCreated`, deployment events, `SignOffRequested`, commit resolved | `src/Fworker.Integrations/Octopus/Watcher*` | W4.0 | M |
| W4.3 | board reconciler and catalog re-read as recurring jobs (cards keyed by repository; cmdemo1-system pushes before v1's filter) | `src/Fworker.Workflows/Reconcilers/*` | W4.1 | M |

**Gate:** a **2-hour tdd observation window** in shadow mode with board activity (the main session starts a 4-item docs batch through
the replenisher if the board is idle): every board-relevant change in it has its event in v2's history.

### W5: R5, DeliveryTrainSaga (2 to 3 at once)

| Task | Content | Owns | After | Size |
|---|---|---|---|---|
| W5.0 | integrator: `ItemMerged(commit, gates, dependencies)` derived from the merge webhook and the reconciler (v1 emits nothing); the train's and delivered-to tables | `src/Fworker.Workflows/Delivery/ItemMerged*`, `src/Fworker.Persistence/*` (tables) | W4 gate, D2, D3 | S |
| W5.1 | `DeliveryTrainSaga` per deployable and environment: candidates, evidence, newest qualifying release, Sign-off answers, due timers, supersession, the delivered-to table, the `dependencies-delivered` slot (off), policy and promoter per deployable from the catalog (H9) | `src/Fworker.Workflows/Delivery/*` | W5.0 | L |
| W5.2 | the shadow run against the board: the train's decisions compared with v1's promotions and Sign-off answers, and its report | `src/Fworker.Workflows/Delivery/Shadow*`, `gateway/Endpoints/Admin*` | W5.1 | S |

**W5.1 revision notes:**
- Hand-written handlers instead of a Wolverine Saga are the accepted design (#491).
- The item "command or timer lost between save and outbox" moved to W9.9 (#361).

**Gate:** a **tdd shadow run of 4 hours or 3 app releases, whichever ends later**, with zero unexplained differences between the
train's decisions and v1's. It replaces the 3 shadow releases; there is no batch to prod, and no switch while v1 acts: the train acts
from the cutover (W9.9), which adopts any pending Sign-off.

### W6: R6, the docs plan (2 to 3 at once)

| Task | Content | Owns | After | Size |
|---|---|---|---|---|
| W6.0 | integrator: the per-repository WIP intake rule; the saga tables | `src/Fworker.Persistence/*` (tables), `src/Fworker.Workflows/Intake*` | W5 gate, D4, D5 | S |
| W6.1 | `WorkItemSaga` core: plans as data, steps, generation and arm ids, `NotFound`, `IRevisioned`, partitioning; the budgets (failures, launches, `idleFollowUps`) in the saga's state; decision verbs by `InvokeAsync` through S1 | `src/Fworker.Workflows/WorkItem/*`, `gateway/Verbs/*` (the v2 engine) | W6.0 | L |
| W6.2 | C# direct merge (squash with the PASS head, mergeability wait, branch update, out-of-date retries) | `src/Fworker.Integrations/GitHub/Merge*` | W6.0 | M |
| W6.3 | projections: board, labels and state comment, versioned, the state comment **in the format v1's `Get-State` reads** (the rollback path); echo suppression | `src/Fworker.Integrations/GitHub/Projection*` | W6.0 | M |
| W6.6 | the docs plan's steps | `src/Fworker.Workflows/WorkItem/Plans/Docs*` | W6.1, W6.2, W6.3 | M |
| W6.7 | views: dashboard, status and digest read the history on the v2 engine, grouped by repository | `gateway/Endpoints/{Dashboard,Status}*`, `StatusQuery*` | W6.1 | S |

Parallel start: W6.1, W6.2, W6.3; then W6.7; W6.6 last.
**Gate:** the docs-plan suites green in the v2 Tier 1 job, golden writes unchanged but for declared differences (CI). No window: v2
does not act in tdd yet.

**Revision of 2026-10-28: the hybrid composition for W6.0 and W6.1 (owner decision).** The revision of 2026-10-26 above removed the
per-item routing (`intake.v2Plans`, the `engine:v2` label, v1 skipping v2 items). For the docs plan the owner chose the composition
back, as the way to put the saga to work before the cutover without a second engine switch: **v1's plant still offers the sessions
and runs every verb v2 does not own yet; v2's `WorkItemSaga` decides the docs-only items it has started; v1's sweep leaves those items
alone.** It acts only where `Engine.V2Acts`, which tdd (shadow) is not, so nothing of it exists in tdd, uat or prod today. What W6.0
and W6.1 built, and what is left:
- **Tables:** `factory.WorkItems` (state as a JSON document with a revision, the `IRevisioned` of the design; the open count per
  repository is read from its columns) and the existing `OpenInstances` index, written in the same transaction.
- **The saga** (`Fworker.Workflows.WorkItem`): plans as data (`feature`, `docs`, `hotfix`, `redeliver`), the steps as classes,
  generation and arm ids, `NotFound` ending quietly, the budgets (failures and launches per phase, idle follow-ups) in the state,
  leases as scheduled messages, the settle window, decisions published as `WorkItemDecided` with a sequence number, a redelivery after
  a crash between the save and the publish publishing again, per-item partitioning of the host's local queues. The docs-only test is
  `IDocsOnlyPort`, whose adapter `DomainDocsOnlyPort` calls D4's `DocsOnly` with the settings of `config/instance.json`; the failures
  of a phase are counted against D5's recovery table (`RecoveryPolicy`, the lost-session row).
- **Intake:** `IntakeRule` (the rule of `Invoke-Intake` as a pure function, with a WIP limit per repository and the choice of engine
  by `intake.v2Plans`), `intake.repositoryWipLimits`, `intake.v2Plans` (empty), `intake.v2Label` (`engine:v2`). v1's own intake in
  `Verbs.Sweep.ps1` is unchanged.
- **The hand-off in the gateway:** `V2Ownership` reads the saga's table before each sweep and `SweepV2Filter` drops what the sweep
  says about those items (offers, triggers, holds, parks, owners) before the plant sees it. The rule script itself is not changed.
- **Left for the integrator and W6.2 to W6.7:** passing the owned items into `sync-board` so the script also stops reading them
  (`Verbs.Sweep.ps1`); writing the `engine:v2` label (a projection, W6.3). (The feeder, the decision verbs and the demotion and
  mechanical rows of D5's table were left to W6.6 here; W6.6 below has them. The fix-forward row, CI red, stays with W7.)

**W6.6, the docs plan's steps (#335, built on W6.1 to W6.3).** Revision 2026-10-26 of the task stands: the steps only, no offer
filter, no separate runtime, no per-item freeze. What the hybrid composition needs on top, all wired only where `Engine.V2Acts`:
- **The steps** (`Fworker.Workflows.WorkItem`, `Plans/Docs*`): the docs plan has its own step classes (`Steps.For(plan, kind)`),
  each applying what v1's verb scripts apply to a docs-only item. *Design:* `ReviewRecorded` counts rounds (`timing.maxReviewRounds`,
  and `docsOnlyMaxReviewRounds` once the latest design recorded the marker; `Verb_record_review`), and the design is left only after
  a REVIEW: COMPLETE (`reviewVerdictColumns`). *Develop:* the same review, and a recorded pull request (`no-open-pr`); a pull request
  with files that are not documentation switches the plan (W6.1). *Verify:* left only by a validation verdict; a PASS covers the head
  it names (`head-mismatch`, `pass-requires-prNumber`) and moves the item to Merge; a FAIL is D5's FailVerdict row (back to
  Development twice, then `verdict-failed`). *Merge:* the step asks the gateway to merge (`MergeRequested`, the C# merge of W6.2)
  and decides each answer (`MergeAttempted`): merged completes the item; blocked and transient answers ask again after
  `DocsPlanRules.MergeRetry` (60 s, a scheduled `MergeRetryDue`; each answer renews the lease; 60 calls park the item `merge-blocked`); a head
  that moved sends the item back to Verify for a new verdict; an out-of-date branch is D5's MechanicalFailure row (the owner
  merges master, 4 times, then `mechanical-exhausted`); any other refusal parks it `merge-failed: <code>`. The command is part of
  the saga's state (`MergeRequest`, `MergeRetryAt`), so a crash between the save and the publish publishes it again (idempotent on
  the command's key), and a renewal does not. An item that switches to a plan the engine does not carry (`intake.v2Plans`) is
  **handed back**: its instance ends with the decision `handed-back` (not `completed`), it is no longer owned, and v1's sweep takes
  it up from the state comment and the board, which v1's verbs kept in step all along.
- **The gateway** (`Ports/DocsPlanPort.cs`, `Verbs/V2VerbFeed.cs`): `DocsMergePort` does the checks of the script that are not
  GitHub's (the Auto Merge label, a hold) and then the merge command; a fault is a transient answer, never an exception and never a
  recorded refusal. `DocsProjection` moves the card (the design column for a started item, Done for a merged one) by the
  decision's sequence number; a fault is tried again three times and then left (nothing is recorded as failed, the next decision
  moves the card; an item that never gets its Done is read by v1 like one a person merged). `V2Ownership` also makes the **offers**:
  an open, unparked saga item on Design, Develop or Verify is offered to the plant in that step's column, until a session holds it
  (`Plant.HasOwner`); v1's own offers for owned items are still dropped, so the saga is what says that an item waits for a session
  and v1's plant and sessions do the rest. `V2VerbFeed` (S1) runs the verb as v1 always did and then tells the saga what happened
  (`append-body` of the design, `record-review`, `complete`, `validation-verdict`): the script's answer is the caller's, unchanged.
  `DocsIntakeJob` (recurring slot `docs-intake`, every minute) is the feeder: the next Todo item of the home repository whose body
  carries the docs-only marker, by the intake rule, starts its saga; it exists only when `intake.enabled` and `intake.v2Plans`
  names `docs` (empty in `config/instance.json`, so nothing starts a saga until the owner says so).
- **`FACTORY_V2_OFF`:** unchanged and per environment, as the revision says: the environment goes back to v1 with no host, no
  ownership, no feed and no saga service; v1's sweep reads every item again. There is no per-item freeze: an item the saga
  owned when the switch was thrown has its state comment, card and pull request as v1's verbs left them; one the saga merged and
  whose card had not moved yet is read by v1 as an item a person merged.
- **Declared differences from the golden writes** (for the v2 Tier 1 job of W2.4; blocks of `v2-differences.txt` are added with
  that job, because a block must quote the golden lines of a scenario it can compare): for a docs item the saga carries, (1) the
  merge is the gateway's own call, so the `PUT .../pulls/N/merge` line has the source `gateway` instead of `rule` (same body:
  `merge_method` and the PASS `sha`); (2) the card moves to the design column at the start and to Done at the merge by the gateway's
  GraphQL calls (source `gateway`), and Done follows the merge at once, with no wait for master CI, no `merge` record in the state
  comment (`awaiting: master-green`, `done_at`) and none of the comments of that wait (the docs plan ends at the merge,
  `wolverine-factory.md` 3.1); (3) no comment on the merge itself: the saga's history (`ItemHistory`, `workitem.decided`) is the
  record. Every write of the verbs a session calls is the script's and is unchanged.
- **Not in W6.6, on purpose:** the feature plan's steps (W7: the saga hands those items back), CI red and the fix-forward row (the
  verbs that run first gate a red head; the merge waits for GitHub's required checks), the `engine:v2` label, the state comment
  projection of the saga's own transitions (v1's verbs write it), and the unpark and park verbs through the saga (they have
  endpoints of their own).

**W6.7 as built (#336):** `V2ItemViews` (`gateway/Endpoints/StatusV2Views.cs`) reads the `WorkItems` rows and the newest `workitem.decided` rows of the
item history (`SqlWorkItemViewSource`, read only) and gives three views, grouped by repository: `v2Items` in `/api/v1/status` (counts per
repository: open, settling, completed in the last 7 days, parked, lease overdue), in `/api/v1/dashboard` (the items, open first, each with its
five newest decisions; a new panel on the page) and beside `digest` in `/api/v1/digest` (the parked and lease-overdue items by repository, codes
and numbers only, no text a person wrote; the digest's own schema is untouched). It is registered only where `Engine.V2Acts` and a database are
set, so every other environment, and every v1 answer, is byte for byte what it was. A read is tried 3 times (8 seconds each), shared by concurrent
requests, kept for 5 seconds; when it still fails the last good answer is served marked `stale` with the error type, and a failure is never kept.
The patrol digest the supervisor receives by push (`DigestBuilder`) is unchanged: v2 items have no owner session in the plant for it to patrol.

### W7: R7, the feature plan (2 to 3 at once)

| Task | Content | Owns | After | Size |
|---|---|---|---|---|
| W7.0 | integrator: the scheduler's and merge train's tables; the owner's decisions on overflow and the uat settle, if given (defaults otherwise: no overflow, today's settle) | `config/*`, `src/Fworker.Persistence/*` (tables) | W6 gate, D1, D6, D7 | S |
| W7.1 | full `CapacityScheduler`: ranking, admission chain, ledger ownership, slot freed on `SessionEnded`, the `host-full` outcome | `src/Fworker.Workflows/Capacity/*` | W7.0 | L |
| W7.2 | `MergeTrainSaga` (direct, train; queue deferred by the owner, see "W7.2 as built") per repository, for the v2 engine; v1 keeps `Verbs.Merge.ps1` until W9.11 | `src/Fworker.Workflows/Merge/*` | W7.0 | M |
| W7.3 | the feature plan: Functional Testing with CI alongside (watcher in Functional Testing; red CI as a fix-forward round), the file-overlap hold in the invoke, the 5.1 answer texts (skills branch on the answer) | `src/Fworker.Workflows/WorkItem/Plans/Feature*`, `plugin/skills/*` | W7.0 | L |
| W7.4 | host metrics and capability labels, both sides: `reconcile-hosts.sh`, `start-worker.sh` (counting `fw-*`, headroom, `host-full`), Tentacle tags (applied by the cmdemo1 session); the gateway's parsing of the new report fields | `deploy/octopus/*`, `OctopusRunbookRuntime.cs` (report parsing) | W7.0 | M |
| W7.6 | smoke evidence: the Smoke test runbook and `[Category("Smoke")]` in cmdemo1-workorders (a pull request there) | `deploy/octopus/*` (runbook), cmdemo1-workorders tests | W7.4 | M |
| W7.7 | `dependencies-delivered` rule on | `src/Fworker.Workflows/Delivery/*` | W7.1 | S |
| W7.8 | docs-only overflow (`overflowItems`, `MayOverflow`) in the v2 scheduler, if the owner agreed | `src/Fworker.Workflows/Capacity/Overflow*`, `OctopusRunbookRuntime.cs` | W7.1, W7.4 | S |
| W7.10 | approved cross-repo pieces, each its own pull request: the merge queue ruleset with `merge_group`, the workorders pipeline changes with fworker's docs-only signal (`Get-MergeDocsOnly`), the kit's `release.yml` | other repositories, `Verbs.Delivery.ps1` (docs-only signal) | W7.2 | per piece |

Parallel start: W7.1, W7.2, W7.3, W7.4; then W7.6, W7.7; then W7.8, W7.10.
**Gate:** the feature-plan suites green in the v2 Tier 1 job with golden writes (CI). No window.

**W7.2 as built (#339), the direct and train strategies only.** Owner decisions of 2026-10-28: hybrid composition (v1's plant offers the sessions and runs the verbs v2 does not own; the sagas decide the v2 items; v1's sweep
leaves them alone), tdd only, and the W7 scope cut: **the merge queue strategy (`queue`) and W7.10 are deferred**, so the train has two strategies. Wired only where `Engine.V2Acts` (in `WorkItemWiring.Attach`, which replaces the
direct merge handler of W6.6 there); v1 and the shadow run register nothing, and v1 keeps `Verbs.Merge.ps1`.
- **Where it sits** (`Fworker.Workflows.Merge`): the work item saga's contract is unchanged. `MergeRequested` is routed to the `MergeTrainSaga` of the item's repository as `ReadyToMerge`; the train answers either `MergeGranted`
  (the turn: the gateway's merge port runs W6.2's `MergeDirect`/`MergePullRequest`, after the label and hold checks of W6.6) or, when it is not the item's turn, a blocked `merge-train` wait (`MergeAttempted`, transient) that the
  saga records as `merge-blocked` and asks again after (its `MergeRetryDue`; `timing.mergeTrainWaitSeconds` sets the pause, 0 is the saga's minute). The port's answer comes back as `MergeTrainAnswered`; the train settles its line by
  it and hands it on to the saga under the same key (`merge-attempt|arm|attempt`), so a repeated answer is applied once. The train is hand-written like the delivery train (a function of the saved state, a revision on the row of
  the new `MergeTrains` table, one durable sequential queue `merge-train`; the turns run on their own queue `merge-train-grants`), and every decision is a `MergeTrainDecided` in the item history (`merge.train.decided`).
- **`direct` (the default, `mergeStrategy` in `config/instance.json`):** every request is let through at once, exactly what W6.6 did; the update of a branch that is behind is asked on the saga's first attempt. The line holds only the
  requests whose answer is outstanding.
- **`train`:** one item at a time per repository. The item first in line gets the turn; the others wait. A head that waits for its own checks or for master (`checks-pending`, `mergeability-unknown`, `branch-updated`, `merge-async`,
  `merge-queue`) keeps the first place; a head blocked for a reason of its own (no Auto Merge label, a hold, a draft, a fault of the call) goes to the back; a head that holds the first place longer than
  `timing.mergeTrainHeadPatienceSeconds` (900) with others waiting goes to the back; an item that has not asked for `timing.mergeTrainEntryExpirySeconds` (300) loses its place (parked, merged by a person: `ItemMerged` also releases it).
  The branch update of a behind branch is asked on the first turn an item gets, so a branch is updated at most once per merge ahead of it. A new arm (validated again) takes the last place.
- **Transient faults:** a blocked answer, and a failure the port calls transient, leave the item in the line (or send it to the back) and reach the saga unchanged; only a merge, a head that moved and a refusal for good take the item out.
  The merge command's ledger keeps the permanent outcomes by key, as before.
- **Not here (documented gaps):** the merge queue strategy and its ruleset (`queue` in the configuration runs `direct` with a warning); the queue-depth count for code changes (`mergeQueueDepth`, `QueueBusy` stay 0 and the port merges as
  a docs-only change: the feature plan, W7.3, brings the count; it is built, see "W7.3 as built"); a waiting item learns that its turn has come at its next ask (a minute by default), not at once; every ask is one of the saga's 60 merge calls, so an item can wait
  about an hour in a line; the retry is a Wolverine scheduled message, which the gateway's host finds only when its durability agent polls (`wolverine.scheduledJobSeconds`, 4 hours by default, floor 60 seconds: the database
  is let sleep), the same as every scheduled message of the saga since W6.1, so a line of the train strategy needs that setting lowered before the owner turns it on; the decisions of a request whose first delivery died between the save and the publish are not repeated (the grant and the wait are); `peer-approved` stays on v1 (W6.2).
- **Off on tdd** until the owner names the train: `config/deploy.overrides.json` does not choose a strategy, and the docs plan itself starts no saga while `intake.v2Plans` is empty.

**Revision of 2026-10-28: W7 scope (owner decision).** In the hybrid composition (W6 above): no W7.6 (smoke evidence), no W7.8 (docs-only overflow stays off), W7.2 with the direct and train strategies
only (the merge queue and W7.10 wait), tdd only. W7.0 is folded into W7.1.

**W7.0 and W7.1 as built (#337, #338):** the `CapacityScheduler` (`Fworker.Workflows.Capacity`) is the slot ledger of the sessions the v2 engine starts. It exists only where `Engine.V2Acts` (tdd is in the shadow
run, so nothing of it runs in tdd, uat or prod today), and nothing sends it a request yet: the feature plan (W7.3) and the docs plan's steps do.
- **Table and settings (W7.0, no flag):** `factory.CapacityLedgers` (one row, the key `factory`: the ledger as a JSON document with a revision). Settings in `config/instance.json`: `schedule.fileOverlap`
  (`enabled` false, `maxHoldMinutes` 120) and `schedule.overlapIgnore`; `scheduler` (`slotMaxMinutes` 240, `hostReportMaxMinutes` 10, `safetyWakeMinutes` 10, `toolchains` per runtime, unrestricted when absent);
  `runtimes.octopus` (`perHostBurst` 0: no burst, `reserveMb` 2048, `minDiskMb` 4096, `hostFullSeconds` 30, `containerCpus` 2, `minFootprintSamples` 10). The merge train's table is W7.2's.
- **Messages in:** `RequestSlot` (rank facts, footprint, toolchains, phase), `ClearItem` (the design's Clear: merged, parked, closed), `SessionEnded` (frees the slot; a verdict does not), `StartRefused`,
  `HostReported`, `FootprintChanged`, and its own timer `CapacityWake`. **Out:** `SlotGranted`, `Held` (the waiting code, once per change), `SlotExpired`. One sequential durable queue: the ledger has one writer.
- **Every input is followed by a plan pass:** the waiting requests are ranked (pinned, resume, v1's priority and intake order, deeper column, older, item), then each goes through D1's chain (file-overlap hold,
  runtime off, toolchain, runtime capacity and day budget, generated last) and, for a runtime with hosts, D7's host choice (guaranteed slots before burst, most headroom first). An item entering Development is
  compared by D6 with the items in flight; a holder is never held; the hold keeps its clock while the item waits.
- **Ledger ownership:** a slot is held from the grant to `SessionEnded`, a refused start, or `slotMaxMinutes` (a lost end signal must not hold capacity for ever; the day's execution stays used). The plant's
  manager still runs the sessions v1 offers: what it runs and has started today (`RuntimeManager.Load`) and the containers a host report shows beyond the ledger's slots count against the same limits, so neither
  engine oversubscribes a runtime. A host not reported for `hostReportMaxMinutes` gives no burst, for three times that no slot.
- **host-full:** a wait, not a failure. The slot is freed, the day's execution is not used, only that host is blocked for `hostFullSeconds`, the request waits again with its rank, and the timer plans it again when
  the block ends. Any other refusal frees the slot, drops the request and leaves the failure to the item's saga.
- **Left for W7.3, W7.4 and the integrator:** the senders (the item saga's `RequestSlot` and `ClearItem`, the session-end signal, the start command's `StartRefused`, the host report's `HostReported` from
  W7.4's report fields; **W7.3 below built the first three, the session-end signal of a lost session, and the `FootprintChanged`**); host capability labels (the toolchain rule checks the runtime's list only); a reaper for items left in flight when no `ClearItem` comes; when the saga starts its sessions through
  `RuntimeManager`, the executions of ledger items must be subtracted from `RuntimeManager.Load` (they would count twice).

**W7.3 as built (#340), the feature plan.** Owner decisions of 2026-10-28 stand: hybrid composition, W7 scope cut (no W7.6, W7.8 off, merge queue and W7.10 deferred), tdd only. Wired only where `Engine.V2Acts` **and** the engine carries the plan
(`intake.v2Plans` names `feature`, and it is empty in `config/instance.json`, so no saga of this plan starts until the owner says so; the docs plan is untouched, with or without it). With no `FeaturePlanRules` in the policy the feature plan's
steps are the core's, as W6.1 made them.
- **The steps** (`Fworker.Workflows.WorkItem`, `Plans/Feature*`; `Steps.For(plan, kind, policy)` picks them): *Design* is the docs plan's review gate and keeps the design's `Files:` line as the footprint (`DesignRecorded.Files`, D6); the docs-only marker
  still switches the item to the docs plan. *Develop* is left with a COMPLETE review and a recorded pull request, whether CI is green or still running (v1 refuses a red head before that); the pull request's changed files replace the footprint
  (`FootprintChanged` to the scheduler); a new head after the PASS sends the item back to Verify. *Verify* is Functional Testing with CI alongside (delivery-speed.md 5): the verdicts are the docs plan's (a FAIL is the quality demotion, `maxDemotions`).
  *Merge* is "the merge still needs both" (5.4): the step asks to merge (`MergeRequested`, now with the item's `Plan`) only when the checks of the head the PASS covers are green; until then it waits with no merge call (decision `merge-blocked
  checks-pending`) and a pending observation renews its lease. *Deliver* is the core's.
- **CI alongside** (`ChecksObserved`, `FeatureFlow.Checks`): the saga hears what CI says of the head under test. Red on the head, in Verify or Merge, before or after the PASS, is a **fix-forward round** (D5's `CiRed` row, `fixForwardMaxRounds` 4, never a
  demotion): the PASS is void, the item re-enters Development (`checks-red`, `fix-forward`, `step-entered`), the owner session is sent the `checks.failed` trigger v1's sweep sends (`FeatureNotice`, the plant's `Route`), and Functional Testing runs again on the
  next head; past the rounds the item is parked `fix-forward-exhausted`; green checks end the rounds. A red in Development is recorded and costs nothing; the checks of another head are stale. The **watcher** is the recurring job `feature-checks`
  (every minute, the leader only, `FeatureChecksJob`): for every feature item in Verify or Merge it reads the head's checks through the rule script's own verbs (`pr-checks`, and for a failed head `wait-for-checks`, so the CI rule of issue 102, the one
  re-run, stays the script's), tells the saga a head it does not know yet (`PullRequestChanged`) and the checks (`ChecksObserved`; green and red once per head, pending every minute as a sign of life). A read that fails is tried again at the next run and never
  reported as a state. The 5.1 answer texts: the gateway adds `ftWithPendingChecks` to a development `complete` of a feature item the saga owns (`V2VerbFeed`), the script then moves the item on to Functional Testing instead of parking it
  (`continue`, `next_column`, `checks: "pending"`, `ci: "alongside"`), and the skills (`factory-owner-session` step 3 and 4, `factory-development` step 6, `factory-functional-testing`) branch on the answer; the card follows the saga
  (`DocsProjection`: the release queue column for a merged feature item, Done after the settle window).
- **The scheduler** (`StepContext.AskSlot`): entering Design, Develop or Verify the saga records a request (`SlotWanted`, kept in the state's `Outbox` like the merge call, so a redelivery publishes it again); the gateway adds the item's labels (priority,
  `worker:` pin) and the runtime (`SlotRequestPort`; `scheduler.runtime`, else octopus) and sends the scheduler's `RequestSlot`. The scheduler's word comes back as inputs of the saga (`WorkItemRouting`): `SlotGranted` starts the step's lease (**the lease
  does not run while the item waits for a slot**), `Held` (a file overlap, a full or a disabled runtime) stops it and is decided once per change (`held`), `SlotExpired` is a lost session; each carries the scheduler's time and an older word that arrives later is
  stale. **The offers of the plant (`V2Ownership`) skip an item that waits for its slot**, so a hold, a full runtime or a runtime that is off keeps a session from being offered. The slot goes back (`SessionEnded`) when the step needs no session (Merge,
  Deliver), when a lease ran out, and when the item is parked, handed back or done; `ClearItem` leaves the scheduler at the merge, a park, a completion, a hand-back and a switch to the docs plan. The plant's journal line "session-gone" ends the slot and tells the saga
  (`SessionLost`): it asks again as an owned item (rank 2), without counting a failure (the lease and the launches keep the budgets). `scheduler.featureSlots` (true) turns the slots of the feature plan off while its steps stay.
- **The file-overlap hold in the invoke:** an owner session goes from Design to Development with no plan pass, so the item that holds a design slot and asks for a later phase is compared with the items in flight at that request
  (`CapacityScheduler.CarryOn`): held, its slot is given up (its execution of the day is given back) and it waits as any request does; otherwise the same session goes on and the item is a holder from then on.
- **The merge line** (W7.2) is connected both ways: a feature item that leaves the Merge step without a merge (head moved, fix-forward, park, hand-back) gives its place back (`MergeTrainLeft`); the merge of a code change counts in `mergeQueueDepth`
  (`MergeQueueCount`: the saga's own merged feature items that no environment has seen yet, `DocsOnly` is false for the plan), and a count that cannot be read is a transient answer.
- **Intake:** where the engine carries the plan, `DocsIntakeJob` starts the next Todo item on it (a docs-only marker still starts the docs plan). `delivery.finalEnvironment` (prod) names the environment whose delivery starts the settle window; a tdd-only
  factory sets it to tdd.
- **Not here (documented gaps):** the plant starts the sessions, on a runtime and host of its own choosing; the slot says whether a session may be offered and counts against the runtime's limits, but the plant does not honor its runtime or host, and what the plant's
  manager runs counts beside it (a ledger session is counted twice until the saga starts sessions itself); a v1 owner session that already holds the item when a hold begins is not stopped (the hold keeps the next offer and the ledger honest); `StartRefused`
  and `HostReported` have no sender (W7.4); the master-defect ruling of the CI rule is the script's `wait-for-checks` only; `MergeFactsRead` (a merge with no release built, settled as docs) has no sender; the merge queue depth does not count merged items
  v1 owns; the new `Outbox` and slot fields of `factory.WorkItems.StateJson` are optional (a state saved before them reads as none), no table was added.

**W7.7 as built (#344): the `dependencies-delivered` rule on.** Owner decisions of 2026-10-28 (hybrid composition, tdd only, the W7 scope cut) apply. The slot of W5.1 was a policy flag nobody set; now the configuration sets it, and only
where the engine acts.
- **Setting:** `delivery.dependenciesDelivered` in `config/instance.json` (true). `DeliveryShadowSettings.From(cfg, engineActs)` gives it to the policy of the home repository's deployable only when `engineActs` (`Engine.V2Acts`) is true:
  in the shadow run (tdd) it stays off whatever the file says, because v1 has no such rule and a train that held an item would differ from v1's promotions for no defect. uat and prod run v1 and register no train. The deployed image
  needs no override.
- **The rule (`DeliveryTrain`, `TrainRun.DependencyGaps`):** a candidate whose `Depends on` items (the qualified ones `ItemMerged` carries) are not delivered to the train's environment is held: its gap
  (`<item> depends on <dependency>, which is not yet delivered to <Environment>`) holds the release that carries it, so the release is not promoted, the decision is `waiting` with that reason, and the train goes on with
  the release before it as it does for any other gap of an item. The health is read only when every other gate holds, so the evidence window of a release starts when the item is let go.
  Delivered means: in the same release (same repository); a row of the delivered-to table for that environment (a dependency of another repository is delivered by that repository's train, per environment: delivered to
  tdd is not delivered to uat); or, for a dependency in a repository the catalog has no deployable for, merged (the merged-items table), which "settles without a release" (system-gateway.md H5; the gap then reads `which is not yet merged`).
- **Released on delivery:** `DeliveredTo` is routed (`DeliveryTrainRouter.Route(DeliveredTo)`) to the trains of that environment that act and have the rule on, as `TrainDependencyDelivered(train, item, environment)`. The train looks
  again only when one of its candidates waits for that item (otherwise the input is dropped before any read), at once and not at the ten-minute safety timer. The router's handler records the delivered-to row itself before it wakes
  anything (the gateway's own handler records it too, the two run in either order), and records nothing where no train is woken (every train in the shadow run).
- **Faults:** a failed read of the delivered-to or merged-items table (`DbException`) is a transient fault like a failed read of GitHub or the app: the input is saved, nothing of that look is decided or kept, no gap is invented, and
  the train looks again after `TransientRetry` (15 s). An item is never held for a read that failed.
- **Not here:** the train is wired only in the shadow run until the cutover (W9.9), so where the engine acts the rule has no train to run in yet; the executor of `PromoteRelease` and `AnswerSignOff` is W9.9's. No backfill of the delivered-to table for
  releases before W5.0/W5.2 (an item merged and delivered earlier has no row, so a dependent of it waits until the owner removes the `Depends on` line); a dependency closed without a merge is never delivered and holds its dependents (the owner edits the
  `Depends on` line, or `link-dependency --remove`); a docs-only dependency in a repository that has a deployable is delivered with the next release that carries its commit, not at once.

### W9: R9, the rest of v2, the tdd cutover, then v1 removed (2 to 3 at once, then one)

W9 now comes before W8: the cutover needs every verb on v2, and incidents are a new capability that only v2 has.

| Task | Content (verb groups of the refactor plan, R9) | Owns | After | Size |
|---|---|---|---|---|
| W9.0 | integrator: the remaining tables; one route registration file per verb group (`gateway/Verbs/Routes/{Item,Merge,Delivery,ReadModels,Keeper,Services,Board}.cs`, stubs) | `src/Fworker.Persistence/*`, `gateway/Verbs/Routes/*` (stubs) | W7 gate | S |
| W9.1 | QC environments and the QC gate (`qc-*`, `Qc.cs`, `Qc.ps1`) as a saga per (repository, pull request) | `src/Fworker.Workflows/Qc/*`, `Qc.cs` | W9.0 | L |
| W9.2 | the item verbs (`get-item` … `expire-claim`) | `src/Fworker.Workflows/WorkItem/*`, `gateway/Verbs/Routes/Item.cs` | W9.0 | M |
| W9.3 | the merge train verbs (`approve-pr`, `merge`, `clear-hold`, `base-watch`, `base-update-branch`) | `src/Fworker.Workflows/Merge/*`, `BaseUpdate.cs`, `gateway/Verbs/Routes/Merge.cs` | W9.0 | M |
| W9.4 | the delivery train verbs (`settle`, `publish-demo`, `ci-release`, `copilot-settle`, `copilot-owner`, `private-build`) | `src/Fworker.Workflows/Delivery/*`, `Runtimes/Copilot*`, `gateway/Verbs/Routes/Delivery.cs` | W9.0 | M |
| W9.5 | read models (`pr-checks`, `wait-for-checks`, `inspect-facts`, `get-delivery`, `model-tier`, `check-secrets`) | `src/Fworker.Workflows/ReadModels/*`, `gateway/Verbs/Routes/ReadModels.cs` | W9.0 | M |
| W9.6 | keeper and manager commands (`backlog`, `amend-criteria`, `split-item`, `create-item`, `set-priority`, `link-dependency`, `manager-action`) | `src/Fworker.Workflows/Keeper/*`, `Manager.cs`, `gateway/Verbs/Routes/Keeper.cs` | W9.0 | M |
| W9.7 | services (security-focused review): `revocations`, `revoke-worker`, `git-credential`, `operator-release(s)`, `metrics` | `gateway/Identity/*`, `gateway/Operator/*`, `gateway/Verbs/Routes/Services.cs` | W9.0 | M |
| W9.8 | transcripts to the database; `sync-board` and `ingest-event` into the reconciler and ingress; `journal` into history and projection | `Transcripts.cs`, `src/Fworker.Workflows/Reconcilers/*`, `gateway/Verbs/Routes/Board.cs` | W9.0 | M |
| W9.9 | **the tdd cutover:** on its first start as v2, the engine adopts every open item from its state comment and claim refs (idempotent) and every pending Sign-off; tdd moves from `shadowEnvironments` to `v2Environments` in `deploy.overrides.json`; the kill-switch drill | `src/Fworker.Workflows/Adoption*`, `config/deploy.overrides.json` | W9.1 to W9.8, every Tier 1 suite green on v2 | M |
| W9.11 | remove v1: the sweep engine, the server rule scripts, the lease, the snapshot, `RunnerPool`, shadow mode and the v1 side of S1 (one subagent, last) | many | the cutover gate | L |

Parallel start: W9.1 to W9.8 after W9.0, three at a time; W9.9 when they are merged; W9.11 after the cutover gate, beside W8.

**W9.0 as built (#352).** No flag moves QC-gated items to v2 (revision of 2026-10-26): the verbs of QC join v2 with W9.1 and the cutover. The schema is DbUp's, so the tables are two new idempotent scripts
(`fworker-database.md`) and the model that maps them: `012_AddQcEnvironments.sql` (`factory.QcEnvironments`: one row per repository and pull request, the key `owner/repo!N`; the state as a JSON document with a
revision, beside the columns `Repository`, `ItemKey` and `Status` that "Waiting for PR QC" reads; W9.1) and `013_AddTranscripts.sql` (`factory.Transcripts`: one row per session name with the qualified item, the
kept lines as a JSON array, a revision; indexes by item and by start for the prune; W9.8). W9.2 to W9.7 need no table: the item verbs use `WorkItems`, the merge verbs `MergeTrains`, the delivery verbs
`DeliveryTrains`, `MergedItems` and `DeliveredTo`, the read models read GitHub and those tables, the keeper and manager commands are handlers without state, the revocations stay refs of the work-orders
repository (the durable truth of CLAUDE.md), and `journal` uses `ItemHistory` and `ProjectionVersions`. A table a later item finds missing is a new script `014_` onward, expand-only.
The route files are `gateway/Verbs/Routes/{Item,Merge,Delivery,ReadModels,Keeper,Services,Board}.cs`: each lists the verbs of its group (the inventory's owners; `auto-promote` is in Delivery, `metrics` in Services,
the transcript verbs in Board; the `qc-*` verbs and the five retired ones are in none) and has an empty `Register(V2VerbRoutes, IServiceProvider)` that its item fills with `routes.Add(Verbs, "verb", handler)`.
`V2VerbRoutes.Build` calls the seven only where the engine acts (`Engine.V2Acts`) and the composition puts a `V2VerbRouter` in front of the router only when a route exists, so v1 routing, the shadow run and every
environment are exactly what they were; a verb no group registered still runs on v1.
**Cutover gate** (the stabilisation gate's form, on v2): the full Tier 1 on v2 with golden writes; the kill-switch drill first (set
`FACTORY_V2_OFF`, v1 resumes every item from the state comments and acts for one sweep, unset it, v2 adopts them again); then **one
measured 8-item batch** on v2 with no regression against the 2026-10-25 baseline (same rule as stage C's gate); after each release
`/ready` 200, v2's node leading and every open saga resumed; a **4-hour quiet window** with the batch inside, for the reasons given in
section 3. After W9.11 merges: the release check and a **2-hour window**; rollback from then on is a revert of W9.11.

### W8: R8, incidents, after the cutover (2 to 3 at once)

| Task | Content | Owns | After | Size |
|---|---|---|---|---|
| W8.0 | integrator: the rollback policy (the default of section 2 until the owner answers); the incident tables | `config/*`, `src/Fworker.Persistence/*` (tables) | the cutover gate | S |
| W8.1 | `IncidentSaga` per (deployable, environment), the hotfix plan, the train hold that lets the incident's own work through | `src/Fworker.Workflows/Incident/*`, `WorkItem/Plans/Hotfix*`, `Delivery/*` (hold) | W8.0 | L |
| W8.2 | alert ingress: the Azure Monitor webhook route; alert rules and the factory's out-of-band alert (applied by the cmdemo1 session; security-focused review) | `gateway/Endpoints/Alerts*`, cmdemo1-system (rules) | W8.0 | M |
| W8.3 | rollback through the DeliveryTrain with the migration precondition; `Undelivered` | `src/Fworker.Workflows/Delivery/Rollback*` | W8.1 | M |

**Gate:** a staged incident in the app's tdd, end to end (about an hour).

## 5. Critical path and clock time

```
W-1 actions -> Stage C (C7b, C7c, C7d, C8 left) -> stabilisation gate (4 h window, measured batch inside)
  -> W0 (+2 h) -> W1 -> W2 (+2 h, shadow) -> W3 (+2 h) -> W4 (+2 h) -> W5 (+4 h or 3 app releases) -> W6 -> W7
  -> W9.0..W9.9 -> cutover gate (drill, 4 h window, measured batch inside)
  -> W8 beside W9.11 (+1 h staged incident; +2 h after the removal)
     Lane D (Domain ports) beside W1..W7, landing before W5, W6 and W7
```

**The observed throughput** (stage C, 2026-10-26): about 50 minutes per task from brief to live in tdd, with 2 to 3 tasks in
parallel: about 20 minutes of agent work, 10 of CI, 10 of merge and release with the tdd check, and about 10 of review, hand-over and
queueing. So:
- one **unit** is one 50-minute task cycle; S and M tasks are one unit, L tasks two;
- a wave's coding time is the larger of its longest chain (units x 50 minutes) and its throughput (all its units x 50 minutes / 2.5
  tasks in parallel);
- its clock time is the coding time plus its tdd window, which is serial because nothing merges during a window.

| Stage or wave | Tasks (units) | Longest chain (units) | Coding | tdd window | Clock time |
|---|---|---|---|---|---|
| Stage C, the rest | C7b, C7c, C7d (L), C8: 4 (5) | C7b, C7d, C8 (4) | 3.3 h | none | 3.3 h |
| Stabilisation gate | | | | 4 h with the batch inside, plus the release check | 4.5 h |
| W0 | 9 (9) | W0.0, W0.1, W0.6, W0.9 (4) | 3.3 h | 2 h | 5.3 h |
| W1 | 10 (10) | W1.0, W1.4, W1.6 (3) | 3.3 h (throughput) | none | 3.3 h |
| Lane D | 7 (7), beside W1 to W7 | each one (1) | 2.3 h added throughput | none | 2.3 h |
| W2 | 5 (6) | W2.0, W2.1, W2.2 (L), W2.3 (5) | 4.2 h | 2 h | 6.2 h |
| W3 | 5 (6) | W3.0, W3.2 (L), W3.4 (4) | 3.3 h | 2 h | 5.3 h |
| W4 | 4 (4) | W4.0, W4.1, W4.3 (3) | 2.5 h | 2 h | 4.5 h |
| W5 | 3 (4) | W5.0, W5.1 (L), W5.2 (4) | 3.3 h | 4 h or 3 app releases | 7.3 h |
| W6 | 6 (7) | W6.0, W6.1 (L), W6.6 (4) | 3.3 h | none | 3.3 h |
| W7 (W7.10 excluded) | 8 (10) | W7.0, W7.1 (L), W7.7 (4) | 3.3 h | none | 3.3 h |
| W9 to the cutover | W9.0 to W9.9: 10 (11) | W9.0, W9.1 (L), W9.9 (4) | 3.7 h (throughput) | drill 0.5 h, then 4 h with the batch inside | 8.2 h |
| After the cutover | W8: 4 (5); W9.11 (L): 1 (2), side by side | W8.0, W8.1 (L), W8.3 (4) | 3.3 h | staged incident 1 h (W9.11's 2 h window fits beside it) | 4.3 h |
| **Total** | **86 units** | | **about 39 h** | **about 22 h** | **about 61 h** |

**About 61 hours of clock time, in a range of 55 to 75.** Run around the clock that is about 2.5 days; at about 12 supervised hours a
day, about 5 days.
- **Shorter:** subagents work during each window and merge at its end, so up to about 30 minutes of the next wave's agent work and
  CI hides in each of the eight windows (about 4 hours).
- **Longer:** a second CI round on one task in five (about 20 minutes on the chain each time), and a window or a measured batch that
  fails once and is run again.

**Assumptions:**
- the 50-minute cycle and 10-minute CI hold for these waves. From W2, the SQL Server container and the v2 Tier 1 job add jobs that
  run in parallel, not in series; if a pull request's CI takes 40 minutes, as section 1 assumed before, add 30 minutes per unit on
  the chains (about 15 hours in all);
- three subagents and one reviewer at once; more than that waits on the serial merges (about 10 minutes each);
- an 8-item batch reaches the app's tdd in about 3.5 hours on two slots;
- the windows run while the board has work, or the main session starts a small docs batch;
- **not included:** the actions outside this repository (section 2: Azure SQL, the replica, `FACTORY_V2_OFF` in cmdemo1-system,
  cmdemo1-aisf off board 679), which are unbounded waits; the cross-repo pieces of W7.10, which are off the critical path; and any
  time the owner's decisions take.

## 6. What the main session does in each wave

1. Brief and start the wave's integrator, then the parallel tasks as their "after" items merge (two to three at once, lane D
   included).
2. Review each pull request with a reviewer subagent; watch CI; re-run flaky jobs at most twice; merge on green outside a window.
3. Integrate hot-file additions in order; run `gh workflow run Tests` on master after each batch of merges.
4. At the wave's end, open its tdd window, run its batch or comparison, and record the gate's result in the item history and the
   handoff.
5. If a gate fails: before the cutover v1 is acting, so fix forward (shadow mode can be stopped with `FACTORY_V2_OFF`); from the
   cutover, revert the switch's pull request or set `FACTORY_V2_OFF` through the cmdemo1 session, then fix forward.

## Appendix A. Board tasks whose scope changed (2026-10-26)

Issue number to new disposition, for the orchestrator to apply to the board's issues (project "cmdemo1-fworker delivery"). Not
listed means unchanged: C7b to C8 (#284 to #287), W0.1 to W0.6 (#289 to #294), W1.1 to W1.3 and W1.8 to W1.10 (#299 to #301, #306 to
#308), W2.0 (#309), W3.3 (#317), W4.1 to W4.3 (#320 to #322), W5.1 (#324), W6.2 and W6.7 (#329, #336), W7.1, W7.3, W7.4, W7.6, W7.7,
W7.10 (#338, #340, #341, #343, #344, #347), W8.1 to W8.3 (#349 to #351), W9.1 to W9.8 (#353 to #360), and lane D (#260, #364 to
#370). Where a row below says "after" changed, the issue's "Depends on" line changes with it. In every issue, "owner review" now
reads "security-focused review by the reviewer subagent; recorded in the handoff" (section 1, rule 7), and no task waits for an
owner approval.

**Epics (the wave issues):**
- #257 Stage C → reworded: the stabilisation gate is section 3's (architecture test, golden writes, one measured batch, the release
  checks, a 4-hour quiet tdd window); no uat or prod promotion, no two quiet weeks.
- #258 W0 → reworded: W0.7 removed; no per-fix flags; gate with a 2-hour tdd window instead of a measured batch.
- #259 W1 → reworded: seams S1 to S3 only (W1.7 removed; S5 and S6 dropped); W1.5 and W1.6 shrink.
- #261 W2 → reworded: "the Wolverine host, in shadow mode"; Balanced mode and the session mailbox from the start; gate a 2-hour tdd
  window instead of 7 days; no uat or prod image check.
- #262 W3 → reworded: v2's own integration commands, v1's side effects unchanged; gate on the v2 kill scenarios and a 2-hour shadow
  window.
- #263 W4 → reworded: no `ingress` flag; gate a 2-hour shadow window.
- #264 W5 → reworded: W5.3 merged into W9.9; gate a 4-hour (or 3 app releases) shadow run; no batch to prod.
- #265 W6 → reworded: W6.4a, W6.4b, W6.4c and W6.5 removed; gate the v2 Tier 1 job (CI), no tdd items on v2.
- #266 W7 → reworded: W7.5 and W7.9 removed; gate the v2 Tier 1 job (CI), no batch to prod.
- #267 W8 → reworded: runs after the cutover gate, beside W9.11.
- #268 W9 → reworded: "the rest of v2, the tdd cutover, then v1 removed"; W9.9 becomes the cutover; W9.10 merged into W2.2.

**Tasks:**

| Issue | Task | Disposition |
|---|---|---|
| #288 | W0.0 | reworded: the engine switch `engine: { v2Environments, shadowEnvironments }` per `FACTORY_ENVIRONMENT` and the `FACTORY_V2_OFF` reader; no per-capability flags |
| #295 | W0.7 | removed: the budgets (failures, launches, `idleFollowUps`) become `WorkItemSaga` state in W6.1 (#328); runtime health in the snapshot goes with the snapshot |
| #296 | W0.8 | reworded: after W0.1 (was W0.7) |
| #297 | W0.9 | reworded: after W0.6 only (W0.7 removed) |
| #298 | W1.0 | reworded: registration stubs for seams S1 to S3 only |
| #302 | W1.4 | reworded: S1, `VerbPipeline` calls one engine interface composed per environment; "rule-script answers return their posted journal lines" dropped |
| #303 | W1.5 | reworded, size S: S2 as integration-command contracts plus a recording sink (shadow mode, tests); no v1 implementation, S5 timers dropped |
| #304 | W1.6 | reworded: S3 event ingress only, able to feed a shadow engine; the S6 loopback route dropped |
| #305 | W1.7 | removed: no S4 seam in v1; the v1-readable state comment is v2's projection in W6.3 (#330) |
| #310 | W2.1 | reworded: adds the session mailbox table |
| #311 | W2.2 | reworded, size L: registered for `shadowEnvironments` or `v2Environments` with a connection string; Balanced mode with Wolverine's leader election and the session mailbox (absorbs W9.10, #362); no Solo mode on the lease holder |
| #312 | W2.3 | reworded, size M: shadow mode (v2 reads S3's events, commands to the recording sink) and its comparison report with v1's writes; replaces shadow history |
| #313 | W2.4 | reworded: adds the v2 Tier 1 job and the golden-write declared-differences file |
| #314 | W3.0 | reworded: no `commands` flag and no per-capability `FACTORY_V2_OFF`; the command base, `GitHubBudget`, the per-repository client stay |
| #315 | W3.1 | reworded: runtime start, stop and runbook runs as v2 integration commands; `RuntimeManager` behind an interface; v1's path unchanged |
| #316 | W3.2 | reworded, size L: the journal ledger in C# as a v2 integration command, golden-identical; no longer through the `journal` verb |
| #318 | W3.4 | reworded: the kill and restart scenarios run on the v2 engine |
| #319 | W4.0 | reworded: no `ingress` flag; webhook secrets per source and the recurring-job slots stay |
| #323 | W5.0 | reworded: no `delivery` flag; `ItemMerged` derived from the merge webhook and the reconciler, not emitted by v1 through S6; no change to `Verbs.Merge.ps1` or `Verbs.Delivery.ps1` |
| #325 | W5.2 | reworded: a shadow run in tdd against the board, 4 hours or 3 app releases |
| #326 | W5.3 | merged into W9.9 (#361): pending Sign-offs are adopted at the cutover; no separate v1 promotion off-switch |
| #327 | W6.0 | reworded: no `intake.v2Plans` flag and no `engine:v2` label; the WIP intake rule and the saga tables stay |
| #328 | W6.1 | reworded: also the budgets from W0.7 (#295) in the saga's state |
| #330 | W6.3 | reworded: the state comment projection keeps v1's format until W9.11 (the rollback path) |
| #331 | W6.4a | removed: no per-item engine |
| #332 | W6.4b | removed: no per-item engine |
| #333 | W6.4c | removed: no per-item engine |
| #334 | W6.5 | removed: no `engine:v2` label |
| #335 | W6.6 | reworded: the docs plan's steps only; no offer filter, no separate runtime, no per-item freeze; after W6.1 to W6.3 (built in the hybrid composition: the saga's items are offered to v1's plant from the saga's state) |
| #337 | W7.0 | reworded: no `scheduler` or `merge` flags; owner decisions have defaults and stop nothing |
| #339 | W7.2 | reworded, size M: `MergeTrainSaga` for the v2 engine only; v1's merge is not moved to C# |
| #342 | W7.5 | removed: v1 does not ask v2's scheduler |
| #345 | W7.8 | reworded: in the v2 scheduler; after W7.1 and W7.4 (W7.5 removed) |
| #346 | W7.9 | removed: no per-item move to v2 |
| #348 | W8.0 | reworded: no `incidents` flag; after the cutover gate |
| #352 | W9.0 | reworded: no flag moving QC-gated items to v2 |
| #361 | W9.9 | reworded: the tdd cutover (adoption of open items and pending Sign-offs, the `deploy.overrides.json` switch, the kill-switch drill); absorbs W5.3 (#326) |
| #362 | W9.10 | merged into W2.2 (#311) |
| #363 | W9.11 | reworded: remove v1 after the cutover gate (sweep engine, server rule scripts, lease, snapshot, `RunnerPool`, shadow mode); no drain of `engine:v1` items |
