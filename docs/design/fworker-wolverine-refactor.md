# Refactoring fworker onto Wolverine and sagas, release by release

Status: plan only, 2026-10-25. Nothing here is built. Asked for by the owner: "How would the existing fworker repo be refactored and
enhanced for this new design with Wolverine and sagas? Make a phased refactoring plan that keeps fworker releases stable." The target
design is `wolverine-factory.md` (sections 2 to 4). This plan takes the fworker gateway as the base (its decision 2, second option)
and uses Wolverine directly: the gateway has no messaging seam to keep and no downstream hosts. The delivery-speed rules
(`delivery-speed.md`, draft PR #253, to be merged before R0) are built inside the phases below, once.

| View | Source | Rendered |
|---|---|---|
| Target projects | `fwr-projects.puml` | [fwr-projects.svg](diagrams/fwr-projects.svg) |
| One process, two engines, six seams | `fwr-seams.puml` | [fwr-seams.svg](diagrams/fwr-seams.svg) |
| Phases and gates | `fwr-phases.puml` | [fwr-phases.svg](diagrams/fwr-phases.svg) |

## 1. Starting point (origin/master, 2026-10-25)

- **Gateway:** one project, `gateway/Gateway.csproj` (assembly `FactoryWorker.Gateway`, which the Dockerfile's entry point and
  `architecture.yml` name), 14,460 lines of C#: `Program.cs` 3,294 (routes, sweeper, dashboard, verbs), `Plant.cs` 2,267,
  `Messaging.cs` 1,506, `Qc.cs` 875, `Runtimes/` 2,604 (Octopus, Copilot, Cursor, CAE), `Auth`, `Lease`, `Leader`, `PlantSnapshot`,
  `Transcripts`, `BaseUpdate`, `ReleaseQueue`, `Manager`, `Keeper`. Packages: Azure.Identity and ModelContextProtocol.AspNetCore; no
  database, no OpenTelemetry exporter (an `ActivitySource` only).
- **Rule scripts:** 73 server verbs (`factory.ps1 --server <verb>`, run by `RunnerPool` in pwsh child processes), about 6,200 to
  6,800 lines of server-side PowerShell. The rest of `plugin/` (`Client.*.ps1`, skills, launcher) is what workers download.
- **The journal is a rule script:** `Plant.Journal` runs the `journal` verb under the item lock, and `Add-Journal` (called from about
  32 places in the rule scripts) keeps a ledger in the state comment: dedup keys, the 30-line cap, folds by comment id, the
  `waiting_at` rate limit.
- **State:** GitHub (state comments, claim refs, board, snapshot and lease refs, transcripts in the same ref store) and process memory.
- **Events in:** a repository webhook (issues, workflow_run, pull_request, pull_request_review; `create-webhook.sh`). The code accepts
  `projects_v2_item`, but nothing delivers it, so card moves are seen by the sweep.
- **Tests:** `tests/smoke.ps1` (pure rule functions); Tier 1: 29 suites in 30 scenario files, 5,743 checks against `tests/FakeGitHub`,
  the required check of every pull request (`tests.yml`, nine Tier 1 jobs plus "Tests result"; the owner jobs are near their limit).
- **Release:** a green Build of master becomes image and Octopus release `cmdemo1-fworker`. Lifecycle Lifecycles-720 deploys tdd
  automatically; uat and prod are optional phases, promoted by cmdemo1-system's `ui-auto-promote.yml` one environment per 10-minute run
  or by a person (held now, cmdemo1-system#314). uat and prod run releases about 20 behind tdd (last: Releases-50899, 2026-10-21).
  **tdd is the only acting gateway**: it delivers the app to tdd, uat and prod; the uat and prod gateways are inert on the board
  (github-token "unset").

## 2. Rules that keep every release stable

1. **Tier 1 is the contract.** The scenario suites drive the gateway only from outside, so they stay valid while the inside changes.
   Every pull request keeps them green; a phase adds scenarios, never weakens one. From R0 a **golden-write harness** records the fake
   GitHub's writes per scenario (times, ids, session links normalized), so a seam can be shown to change nothing.
2. **Branch by abstraction.** A seam first goes in front of v1 with v1 behind it, then v2 is added behind it, switched on, and v1 is
   removed only at the end.
3. **Flags.** Each capability gets a key in `config/instance.json` in the existing environment-list style,
   `{ "engine": "v1", "v2Environments": [] }`, with tdd's value in `config/deploy.overrides.json` (an owner-protected path: such a pull
   request needs the owner, and `ui-auto-promote` holds it 60 minutes per environment). Flags are baked into the image, so a flag
   change is a release. **The fast rollback** is an environment variable, `FACTORY_V2_OFF=1`, kept in cmdemo1-system's infrastructure
   code (so a system deploy does not reset it) and set by the cmdemo1 session: a new revision in minutes, no image change. It switches
   `commands` and `ingress` back to v1 at once. Work already on v2 is **frozen, not handed to v1**: before v2 stops acting, each
   saga parks its own `engine:v2` items, posts their journal line and notes the operator (v1's park path refuses them, being out of
   scope); `delivery` returns to v1 only when no v2 Sign-off is pending (until then the train keeps answering its own Sign-offs and
   starts no new promotion). From R7, `scheduler` and `merge` return too: v1 falls back to its own slot ledger and to the C# direct
   merge, both kept until R9 for this purpose.
4. **Expand, then contract, with the lag in mind.** Migrations are additive. A column or table (Wolverine's included) is dropped only
   when every environment's running release and its previously deployed release no longer read it; uat and prod run many releases
   behind and promotion skips releases.
5. **The database is required only where a capability is on v2.** Wolverine is registered only when a connection string is
   configured; storage setup and migrations run in a background task with retries; `/ready` never waits on the database (the 49-minute
   `/ready` outage of 1.0.58). An environment with every capability on v1 runs as today, with or without a database. From R3 in an
   environment with `commands` on v2, the database is required there and the health check says so.
6. **One owner per item and per shared capability.** The item owner is the `engine:v2` label (section 5, R6). It is checked at the rule
   scripts' choke point `Test-ItemInScope` and at every C# path listed in R6. Rule scripts run in pwsh child processes, so they reach
   v2 through a loopback route (seam S6), never an in-process call.
7. **Agents see compatible answers.** The verbs' request and answer shapes stay the same. Changes to what agents are told (R0's
   "stay connected while checks run", R7's 5.1 texts) are explicit, measured steps. Every session downloads the same skills, so a
   skill branches on the **answer** it gets (exit 21 or `continue: true`), never on the engine.
8. **One acting Wolverine node.** Until R9, Wolverine runs in Solo mode on the lease holder only: its listeners and agents start when
   the gateway becomes Acting and stop on supersession before the final snapshot, so a replaced process cannot promote or answer a
   Sign-off (`delivery-speed.md` 18.1.1). Balanced mode and Wolverine's own leader election come with R9. While a new process is
   Starting (up to `leaderHandoverSeconds`, 90), v2 decision verbs answer exit 20 with `retryAfterSeconds`, as other verbs do when
   they cannot act yet.
9. **The usual path:** small pull requests, the pre-commit review, auto-merge on green, tdd measured by flow metrics.
10. **Declared deviations from `wolverine-factory.md`:** Solo mode on the lease holder until R9 (its sections 2 and 4.8 assume
   Balanced), the session mailbox only at R9 (its M1 has it first; one node needs none), and v1's own side effects through the outbox
   in R3 (its M1 keeps v1 unchanged).

## 3. Target structure

![Projects](diagrams/fwr-projects.svg)

| Project | Content | Comes from |
|---|---|---|
| `src/Fworker.Contracts` | message records with stable `[MessageIdentity]` names, ids | new |
| `src/Fworker.Domain` | pure rules: ranking, admission, recovery table, evidence composite, release choice, content gate, docs-only test, journal text | the rule scripts' pure functions, with parity tests |
| `src/Fworker.Persistence` | EF Core `DbContext`: saga rows, item history, correlation map, open-instance index, session mailbox, projection versions; migrations | new |
| `src/Fworker.Integrations` | GitHub and Octopus clients, runtimes, error classification, rate limits, integration command handlers (they write the correlation map and projection versions) | `gateway/Runtimes/*`, the rule scripts' GitHub calls |
| `src/Fworker.Workflows` | `WorkItemSaga` and plans, `CapacityScheduler`, `MergeTrainSaga`, `DeliveryTrainSaga`, `IncidentSaga`, reconcilers | new, replacing `Verbs.*` |
| `gateway/Gateway.csproj` (name and assembly kept) | host: API, MCP, verb router, webhooks, dashboard, Wolverine wiring; v1 until R9 | today's gateway |
| `tests/Fworker.Domain.Tests`, `tests/Fworker.Workflow.Tests` | unit and parity tests; Wolverine tracked sessions against SQL Server | new |
| `tests/scenarios`, `tests/FakeGitHub` | Tier 1 with the golden-write harness; the fake gains merge groups and Octopus events | today's |

The Dockerfile copies `src/` too (a protected path), and cmdemo1-system's `ui-auto-promote` learns `src/`, `*.csproj` and `*.sln` as
application paths, so such releases are not held as infrastructure changes.

## 4. The six seams

![Seams](diagrams/fwr-seams.svg)

| Seam | Today | Behind it in v2 |
|---|---|---|
| **S1 verb router** | `Program.cs` runs the verb's rule script through `RunnerPool` | per verb and per item: the rule script, or the item's saga by `InvokeAsync` (decision verbs) or a published event (event verbs) |
| **S2 command port** | runtime start and stop (`RuntimeManager`, which consumes the result), runbook runs, journal lines (the `journal` verb) | integration commands through the outbox; results come back as messages (`SessionStarted(execution, url)`, `StartFailed(reason)`) to `RuntimeManager`; journal lines go to a handler **that runs the `journal` verb** (same lock and ledger); the launched line's reserved place (`Plant.ReserveLaunched`) stays in the plant's per-item chain, which sends when the link fires |
| **S3 event ingress** | webhook routes (Program.cs:972-984) and verbs queue `RequestSweep` | also published to the inbox, deduplicated on the delivery id, resolved through the correlation map |
| **S4 item state store** | `Save-State` / `Get-State` on the state comment | the saga row, with the state comment written as a versioned projection for people and v1 readers |
| **S5 timers** | timestamps compared on each sweep | scheduled messages |
| **S6 loopback** | none | an authenticated local route the rule scripts call to ask v2 (slot requests, merge and delivery facts) while they still run v1 work |

Rule-script answers also return the journal lines they posted (as `_github` is returned today), so the gateway can record every line,
from wherever it was written, in the history.

## 5. Phases

![Phases](diagrams/fwr-phases.svg)

Sizes are rough (S about a week of one session, M two, L three or more).

### R0. Prerequisites and safety (M)

- **Prerequisites** (owner and system):
  - cmdemo1-aisf stops acting on board 679 (a kit watcher change through Platform Hub);
  - the acting gateway keeps at least one replica (cmdemo1's `containerapps.bicep`; tdd scales to zero today), needed before R5's
    timers;
  - draft PR #253 merged.
- **v1 safety fixes**, each behind its own flag: `delivery-speed.md` 18.1.1 (lease checks inside running sweeps, `Plan` and
  `Provision`), 18.1.3 (budgets into the item state), 18.1.4 (the day budget), the `rev` check of 18.1.2.
- **Delivery-speed quick wins:**
  - replenish backpressure, the ranking, plan on slot freed;
  - for the exit-21 texts, only "stay connected while checks run", because v1 follows `awaiting_checks` in Development only
    (Verbs.Sweep.ps1:687-697); 5.1 as a whole ships with section 5 in R7.
- **Flow metrics** as `delivery-speed.md` 13 and 18.2 define them: one timestamp per stage in the state comment, not read from the
  capped journal.
- **The golden-write harness** for Tier 1 (rule 1).
- These are deliberate behaviour changes, measured on a tdd batch.
- **Gate:** Tier 1 green; a tdd batch no slower than the 2026-10-25 baseline.

### R1. Structure and seams, all routed to v1 (M; no behaviour change)

- The projects of section 3 (names of the host kept); `Fworker.Domain` gets its first ports (ranking, recovery table, journal text)
  with parity tests that run the rule scripts' pure functions on the same inputs.
- S1 to S6, each with only its v1 implementation; rule-script answers return their posted journal lines.
- The verb inventory of R9 is written here, with an owner for each of the 73 verbs.
- **Gate:** Tier 1's golden writes are identical before and after; on tdd, each item's sequence of journal event kinds and state keys is
  unchanged.

### R2. The Wolverine host, inert (M)

- **Infrastructure:** an Azure SQL database for the acting gateway (tdd), provisioned rather than serverless (Wolverine polls every few
  seconds and would keep a serverless database awake), managed identity, no secret. The uat and prod gateways get none until they act
  (section 6).
- **Packages:** WolverineFx, WolverineFx.SqlServer, WolverineFx.EntityFrameworkCore, WolverineFx.Http (dead-letter endpoints),
  Microsoft.EntityFrameworkCore.SqlServer, OpenTelemetry and the Azure Monitor exporter; all pinned.
- **Host:** Solo mode on the lease holder (rule 8), registered only with a connection string, storage set up in the background (rule 5),
  OpenTelemetry, the dead-letter admin page.
- **Shadow history:** every journal line (from the answers, S1) and every event (S3) gets a history row, best-effort.
- **CI:** SQL Server as a service container in the Tier 1 jobs and for `Fworker.Workflow.Tests`; the owner jobs split once more to stay
  within their time limit.
- **Gate:** 7 days in tdd with a history row for every posted journal line; the gateway healthy and acting with the database stopped;
  a CI check that the image starts and turns ready with no connection string (the uat and prod case, whose promotions are held now,
  cmdemo1-system#314), and the next uat or prod promotion that happens, too.

### R3. Durable side effects (M)

- S2 on the outbox in tdd (`commands`): runtime start and stop and runbook runs, with result messages back into `RuntimeManager`;
  journal lines through the handler that runs the `journal` verb; the Copilot launch reservation; error classification and rate limits
  (`wolverine-factory.md` 4.4).
- The database is now required in tdd (rule 5); `FACTORY_V2_OFF` is the rollback.
- A declared deviation (rule 10): v1's own side effects gain durability first, because the gateway is one process.
- **Gate:** a tdd batch with no lost or duplicated journal line, start or stop, including a gateway kill mid-batch; the journal-fold and
  octopus suites green.

### R4. Durable event ingress and the board reconciler (S)

- S3 publishes every webhook and verb event to the inbox; a C# Octopus watcher publishes releases and deployments; the correlation map is
  filled from verbs, runtimes and the watcher.
- Card moves: an organization webhook or App delivery of `projects_v2_item` (an admin action for the owner), or a **board reconciler**
  (a Wolverine recurring job comparing the board with the item states) that publishes card-move events. The reconciler is built here
  either way, as the safety net.
- **Gate:** every board-relevant change of a tdd batch has its event in the history.

### R5. DeliveryTrainSaga (M to L)

- The train for the app's tdd, uat and prod, all driven by the tdd gateway: candidates, today's evidence (version, Healthy reads,
  settle), the newest qualifying release per environment, Sign-off answers, due timers.
- **Inputs from v1:** v1's merge path emits `ItemMerged(commit, gates)` through S6, carrying the per-item gates (Auto Merge, Hold,
  merged by the gateway after a PASS, demo video published), and the train reads `promote_health` from the state comment until it owns
  the reads.
- **Shadow first:** it decides and logs next to `Verbs.Promote`; v1 still acts.
- **Then authoritative** (`delivery`): at a moment with no gateway Sign-off pending, or with v2 adopting the gateway's pending
  deployments, v1's promotion and Sign-off answers switch off for every release. v1 keeps settling its items, read-only.
- The smoke runbook and the other cross-repo pieces of `delivery-speed.md` 9 to 11 join in R7, after their owner decisions.
- **Gate:** zero shadow differences over 3 releases; one batch promoted to prod by v2.

### R6. WorkItemSaga on the docs plan (L)

- **Which items:** the criterion is **the replenish marker (`<!-- keeper-replenish -->`) and the Auto Merge label**: the factory's
  own docs items, which need no QC gate before R9. The label `engine:v2` is only the mark of that decision, written by whichever entry
  point first sees the item (`Invoke-Intake`, a person's card move, Replenish, keeper `create-item` and `split-item`, the board-added
  repair), before either engine acts.
- **Runtime:** v2 items run on a separate runtime with its own budget, so v1 keeps both host slots: Cursor or Copilot cloud. Not the
  Container Apps job runtime: Foundry is off by the owner's decision of 2026-10-16. The plant offers a v2 item **only to sessions of
  that runtime**, not to any connected owner session (`preferConnected` would otherwise hand it to a host container).
- **Merging:** the saga merges by the **direct strategy**, implemented in C# in `Fworker.Integrations` (the squash merge with the PASS
  head as `sha`, the mergeability wait, the branch update and the out-of-date retries of `Verbs.Merge.ps1`), because the rule script
  refuses `engine:v2` items once `Test-ItemInScope` checks the label. R7 then moves v1's merge onto the same C# code.
- **The saga:** the docs plan, decision verbs through S1 by `InvokeAsync`, the board and state comment as a versioned projection, the
  recovery policy, generation and arm ids, `NotFound` on every message. A v2 item whose pull request leaves the docs paths is parked for
  a person until R7.
- **The plant serves v2 items in-process:** offers, leases and pushes (`item.assigned`, `base.updated`, `session.end`), only to sessions
  of the separate runtime, so sessions behave as today (one node, rule 8); the session mailbox waits for R9's second node.
- **Every v1 path skips or forwards v2 items:**
  - in the rule scripts, `Test-ItemInScope` (Board.ps1:41 and 127; Verbs.Sweep.ps1:392, 857, 878) covers settle, `Invoke-QcSync`,
    Replenish, AutoPromote, merge after PASS and base-watch;
  - in C#: `BaseUpdate.cs`, the plant's offers, leases and expire-claim, `Plant.PassMerge`, `Manager.cs` actions, the operator's unpark
    and resettle, keeper verbs, `CopilotCiRelease`, copilot-settle and copilot-owner, runtime adoption from host reports;
  - manager, keeper and operator actions on a v2 item go to its saga through S1;
  - v2 items still count in intake WIP and the replenisher's `targetOpen`;
  - the dashboard, `/api/v1/status` and the patrol digest show v2 items from the history.
- **Gate:** 8 docs items to Done on v2 in tdd; v1's items unaffected in the same days, on their own slots.

### R7. The feature plan (L)

- The full `CapacityScheduler` owns the whole ledger; v1's rule scripts ask it through S6. Slot freed on `SessionEnded`, host headroom,
  docs-only overflow (`delivery-speed.md` 4, after the owner's overflow decision).
- The feature plan: section 5 of `delivery-speed.md` as a whole (Functional Testing with CI alongside, the 5.1 answer texts for v2
  items), the file-overlap hold decided inside the invoke, recovery.
- `MergeTrainSaga` with one strategy for both engines; v1's merge moves onto R6's C# direct merge (Verbs.Merge.ps1 runs inside rule scripts), so
  both engines merge through the train. The merge queue, `merge_group`, the ruleset and the kit's `release.yml` change come in
  only after their owner approvals (`delivery-speed.md` 9, 10, 15).
- The smoke runbook and `[Category("Smoke")]` in cmdemo1-workorders join the train's evidence (`delivery-speed.md` 11).
- The intake puts every new Auto Merge item in tdd on v2; items that need the QC gate stay on v1 until R9.
- **Gate:** an 8-item code batch to prod on v2, compared with the 2026-10-25 baseline.

### R8. Incidents (M)

- `IncidentSaga` and the hotfix plan in the tdd gateway, watching the app's three environments; the Azure Monitor alert webhook; rollback
  as a command to the DeliveryTrain; the factory's own failures alerted out of band (`wolverine-factory.md` 3.3).
- **Gate:** a staged incident in the app's tdd, rolled back or fixed end to end.

### R9. Retire v1 (L)

- QC environments and the QC gate ported (`Qc.cs`, `Qc.ps1`); then every item on v2; `engine:v1` items drained.
- **The verbs, by destination** (the inventory of R1, completed):

| Verbs | Become |
|---|---|
| `get-item`, `append-body`, `comment`, `claim-item`, `accept`, `release`, `my-items`, `record-review`, `move-next`, `complete`, `validation-verdict`, `open-pr`, `update-pr`, `item-pr`, `park-item`, `unpark-item`, `resettle-item`, `expire-claim` | WorkItemSaga (decision verbs by `InvokeAsync`, event verbs published) |
| `pr-checks`, `wait-for-checks`, `inspect-facts`, `get-delivery`, `model-tier`, `check-secrets` | read models over the history and the integrations |
| `approve-pr`, `merge`, `clear-hold`, `base-watch`, `base-update-branch` | MergeTrainSaga and its integration commands |
| `settle`, `publish-demo`, `ci-release`, `copilot-settle`, `copilot-owner`, `private-build` | DeliveryTrainSaga and the runtime integrations |
| `sync-board`, `ingest-event` | the board reconciler and S3 ingress |
| `journal` | the history plus the projection |
| `qc-*` (14 verbs) | a QC service with its own saga per environment |
| `backlog`, `amend-criteria`, `split-item`, `create-item`, `set-priority`, `link-dependency`, `manager-action` | keeper and manager commands to the sagas |
| `revocations`, `revoke-worker`, `git-credential` | C# services in the gateway (revocations keeps gating `/ready`) |
| `operator-releases`, `operator-release`, `metrics` | C# services and read models |
| `transcript-save`, `transcript-load`, `transcript-prune` | transcripts move to the database before the ref store goes |
| `plant-snapshot-save`, `-load`, `-clear`, `-await`, `leader-lease` | removed: Wolverine leader election and durable state replace them |

- **The host:** Balanced mode and Wolverine's leader election replace the lease and snapshot; the session mailbox for a second node;
  the sweep remains only as the board reconciler; `RunnerPool` and `runner-host.ps1` retire with the server verbs (workers run their
  scripts on their own machines).
- **Gate:** no v1 item left; Tier 1 green with every capability on v2.

## 6. Environments

The tdd gateway is the only acting gateway, so every v2 capability is exercised there. It also delivers to the app's uat and prod and
watches them for incidents. The uat and prod gateways get every fworker release later, through `ui-auto-promote` or a person, and must
start and stay ready with all capabilities on v1 and no database. This is rule 5, and each phase's gate checks it.

| Capability flag | tdd gateway | uat and prod gateways |
|---|---|---|
| `commands` | v2 from R3 | v1 (inert) |
| `ingress` | v2 from R4 | v1 |
| `delivery` | v2 from R5 | v1 |
| `intake.v2Plans` | `[docs]` from R6, `[docs, feature]` from R7 | `[]` |
| `scheduler` | v1 through R6 (v2 items use their own runtime's budget); v2 from R7 | v1 |
| `merge` | direct by the saga for v2 items from R6; the train for both engines from R7 | v1 |
| `incidents` | v2 from R8 | off |

**Moving the acting role** (`activate-prod.sh` makes the prod gateway act): the board's database moves with it, one database per board
as in `wolverine-factory.md` 2, so the saga rows follow the board.

## 7. Decisions and approvals for the owner

1. **The base:** the fworker gateway (decision 2 of `wolverine-factory.md`, second option); cmdemo1-aisf off board 679 first (R0).
2. **System changes:**
   - at least one replica for the acting gateway;
   - Azure SQL for the acting gateway (cmdemo1 owns the deployables and `containerapps.bicep`);
   - `ui-auto-promote` application paths (cmdemo1-system);
   - `FACTORY_V2_OFF` set by the cmdemo1 session when needed.
3. **Packages:** WolverineFx, WolverineFx.SqlServer, WolverineFx.EntityFrameworkCore, WolverineFx.Http,
   Microsoft.EntityFrameworkCore.SqlServer, OpenTelemetry and the Azure Monitor exporter, pinned.
4. **Protected paths and CI:** the Dockerfile (`src/`), `config/deploy.overrides.json` (each flag switch), SQL Server in `tests.yml`.
5. **`projects_v2_item` delivery:** an organization webhook or App (admin), or rely on the board reconciler.
6. **The delivery-speed decisions R7 needs:** the merge queue ruleset, the workorders pipeline changes, the kit's `release.yml`, the uat
   settle, and overflow for docs-only items.
7. **The incident rollback policy** in prod (R8).

## 8. Risks

- **A subtle difference in a ported rule:** parity tests per rule function, the golden-write harness, shadow runs for the first saga, and
  Tier 1 as the black-box check.
- **Two engines in one process (R1 to R8):** the checks of R6, a scenario per switch showing an `engine:v2` item is never touched by v1,
  and `FACTORY_V2_OFF` as the fast rollback.
- **The database as a new dependency:** required only where a capability is on v2; elsewhere the gateway runs as today.
- **CI time:** SQL Server in the Tier 1 jobs and new workflow tests; the jobs are rebalanced in R2.
- **Wolverine's pace and single maintainer:** pinned versions, each upgrade its own pull request with the full suites.
