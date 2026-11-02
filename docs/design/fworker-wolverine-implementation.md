# Implementing ADR 0001: the sequence of work and its parallel lanes

Status: implementation plan, 2026-10-26, revised the same day, on 2026-10-28 (the hybrid composition), on 2026-10-30 (the audit;
section "Revision 2026-10-30 (audit)" below) and on 2026-11-01 (v2 only; section "Revision 2026-11-01 (v2 only)" below, which
supersedes W9.11a to c, W9.12 and the uat and prod parts of the 2026-10-30 revision where they differ). Stage C, W0 to W7 and W9.0, W9.2, W9.5 and W9.7 are built; W9.1, W9.3, W9.4, W9.6, W9.8
and W8.0 to W8.3 are in flight; the rest is planned. Asked for by the owner: "prepare for
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
(The revision of 2026-10-28 brought back one per-plan exception, `intake.v2Plans`, and the revision of 2026-10-30 turns the remaining
gates into events; Appendix B lists the issues of that revision.)

| View | Source | Rendered |
|---|---|---|
| Stage C and the waves | `fwi-waves.puml` | [fwi-waves.svg](diagrams/fwi-waves.svg) |

![Waves](diagrams/fwi-waves.svg)

## Revision 2026-11-01 (v2 only)

**The owner's decision (2026-11-01):** "V1 goes away. V2 becomes the only way work items are implemented using the same original
labels in GitHub", and "v2 takes uat/prod too": the delivery train acts for the app's uat and prod, and v1's `AutoPromoter` goes with
the rest of v1. This supersedes decision 2 of 2026-10-30 (the `AutoPromoter` keeps uat and prod until W9.12) and the order of W9.9c,
W9.11a to c and W9.12. tdd stays the acceptance bar (2026-10-26). Everything else of the 2026-10-30 revision stands (the SQL lease kept,
Solo mode, `peer-approved` dropped, newest-only accepted, the nine defaults, W9.13 after the removal).

**The owner's decisions on this revision (2026-11-01, on #622):**
- **D1, no stop switch after v1.** The only rollback once v1 is gone is a redeploy of the previous fworker release. `FACTORY_V2_OFF`
  and its reader are removed in V3.2 and nothing replaces them (no `FACTORY_HALT`). The entry in cmdemo1-system (the tdd fworker
  settings) is **deleted, not renamed**: a step for the cmdemo1 session, after V3.2 is live.
- **D2, the W5 gate as restated in section 3:** the shadow's evidence as recorded, the unexplained rows explained, the train's dead
  letters closed, the acting `promote` suite green, then the live events after the switch, instead of "3 live releases matched".
  Fixing the app's failing tdd deployments is a precondition of V2.2's live events.
- **D3, yes:** a person's `hotfix` label starts the hotfix plan (top rank, no design step), and `intake.v2Plans` gains hotfix and
  redeliver (V1.3).

**Where tdd stands** (read on 2026-11-01, about 14:30Z, `/api/v1/status` and `/api/v1/delivery-shadow`, release `1.0.1124`
and later; v2 acts since #609 at 01:28Z):
- v2 starts almost no work. One saga is open (docs plan, Verify, 13 hours old). The three items the plant ran today (#1177, #1179,
  #1181, Technical Design, Octopus runbook sessions `oct-<item>-<hash>`) are replenished items, run by v1's sweep: `PlantSteps.Replenish`
  creates the issue and puts its card in Technical Design, and nothing starts a saga for it. The adoption job adopts only at the start of
  a lease term and skips a card with no state comment (`no-state-comment`), so a new card in a working column stays v1's.
- v2's intake (`DocsIntakeJob`) pulls from Todo only when `Plant.IntakeSlots()` is above 0, and that counts connected owner sessions.
  tdd's sessions are runtime sessions that the plant starts per item and phase; no owner session is connected, so the intake never pulls.
  (v1's intake has the same rule, `Sweeper.cs:170`, so Todo is pulled by neither engine on tdd today.)
- The app's uat and prod: 67 items wait in the Release Queue (#709 to #876), on releases 2.4.1228 to 2.4.1288 whose deployments to the
  app's tdd failed (Deployments-57439 to 57450). v1 waits on each ("its deployment to tdd is Failed"). The train, which judges the newest
  qualifying release only (#496), would have promoted 2.4.1298 to uat at 06:50Z (recorded as a shadow command, never executed).
- The shadow comparison since 2026-10-28 (`live`): 261 comparisons over 24 releases; 6 releases compared, all six `match` with class
  `wait:failed`; 0 `differs`; 38 `v2-only` and 1 `v1-only` rows not covered by the two declared differences (`queue-only-v1`,
  `newest-only`), `unexplainedLive` 6, `met` false. No Sign-off, health window or follow-up of a request has been compared, and none can
  be: in the shadow nothing is executed, so after a `promote` the train only sees "requested, Octopus showed none within 30 minutes; the
  request is dropped". The W5 gate as written cannot be met in the shadow (see "The uat and prod switch").
- v2 health: 123 dead letters (121 `TrainWake` with `MergeFactsException` since 2026-10-30, 1 `TrainDeploymentSeen`, 1 `TrainReplay`);
  the board reconciler made 395 requests in the last hour against the estimate of 150 (79 passes: the 120 s job and the pass that
  `sync-board` adds after every sweep).

### 1. The map: every v1 behaviour, label and setting, and its v2 owner

Sources: `docs/design/verb-inventory.md` and `.json` (74 verbs: 68 routed where v2 acts, 6 never routed), the architecture test of
W9.10l (`tests/Gateway.Tests/ArchitectureInventoryTests.cs`, six declared exceptions), `Verbs.Sweep.ps1` (`Invoke-SyncBoard`) and the
gateway. "Gap" means no v2 code owns it; the row of section 5 that closes it is named.

**The sweep (`sync-board`, `Verbs.Sweep.ps1:495-936`, called by `Sweeper.SweepOnce`).** Where v2 acts the script skips the saga's items
(`skip_items`, before any state read) and the plant-wide steps (`skip_steps`); what it still does is for items with no saga.

| Step | v1 | v2 owner | After v2 only |
|---|---|---|---|
| Board read, resume file, budget deadline | `Get-BoardList`, :511, :554, :560 | the board reconciler (`BoardReconcilerJob`, 120 s) | sweep scaffolding goes |
| Lease fence per item | :570, :846 | `ILeadership.MayAct` (SQL lock) | kept |
| Gap renew (owner claims that ran out while no gateway was up) | `Invoke-GapRenew` :291 | adoption only (`AdoptionMapper` "gapRenewed"), not for an item that is already a saga | **gap**: V1.4 |
| Labels per item (pins, priorities, runtime, profile, hold) | :586-603 | `SagaPlantFeed.Build` from the reconciler's cards | kept |
| Re-arm settle of a stranded merged item | `Invoke-RearmSettle` :258 | `RearmSettle` (`SweepSteps.cs`, W9.10f) | kept |
| Clear the park of a closed item | `Clear-ClosedPark` :272 | `ItemClosed` (W9.10h) | kept |
| Park expiry | `Reset-ExpiredPark` :311 | `ParkExpired` timer | kept |
| Settle and master-CI watch | `Invoke-SettleLocked` :165 | `MasterObserved` / `MasterWatchElapsed`, `ItemStepsJob` | kept |
| Auto-unpark of `master-broken` | `Test-AutoUnpark` :334 | `SweepSteps` (stricter: a green commit that contains the merge) | kept, declared (W9.10f) |
| Auto-unpark of `verdict-failed`, `fix-forward-exhausted`, the relaunch-budget parks | :348-366 | none on purpose (the saga counts the budgets; `ParkExpired` lifts the park after `timing.parkHours`) | declared difference, V1.4 records it in `v2-differences.txt` |
| Release Queue card without a merge record: flag | :672-679 | none | **gap**: V1.4 |
| Merge pickup (a person merged) | `Invoke-MergePickup` :184 | `ItemMerged` and W9.10f | kept |
| Held-by-label and demo-label skips | :681, :706 | `HoldObserved` (W9.10h), `IntakeRule`, `ItemVerbs.Claim` | kept |
| Demote a PASS whose pull request was closed | `Invoke-Demote` | `PullRequestClosed` (`SweepSteps`) | kept |
| Clear hold when approved | `Invoke-ClearHoldIfApproved` | `ApprovalSeen` | kept |
| Idle-owner release | :725-742 | `OwnerIdleElapsed` (`ItemStepsPort`) | kept |
| The PASS merge | `Verb_merge -Gateway` :748-786 | `MergeTrainSaga` (direct) | kept; the hybrid `Script(...)` branches of `MergeVerbs.cs` go (V3.2) |
| Awaiting checks after Development | `Complete-AwaitingChecks` :8, :802 | `FeatureChecksJob` and the CI rule (`ItemVerbs.Flow.cs`, W9.10q) | kept |
| Offers (relaunch, handoff, board move, second look) | :830-868, `seen.json` | `V2Ownership.OfferNodes`, the scheduler (W9.10j) | kept |
| Depends-on hold of offers | :852-867 | `DocsIntakeJob.DependenciesMet` (D1) at intake only | kept; an item admitted outside the intake (V1.1) is checked there too |
| Claim-ref reap | `Invoke-ClaimRefReap` :225 | job `claim-ref-reap` (`PlantSteps.ClaimRefReap`) | kept |
| Intake from Todo | `Invoke-Intake` :955 | job `docs-intake` (`IntakeRule`) | kept; slots from capacity (V1.2) |
| Board guard | `Invoke-BoardGuard` :398 | job `board-guard` | kept |
| QC sync | `Invoke-QcSync` (`Qc.ps1:263`) | job `qc-sync` (`QcService`) | kept |
| Replenish | `Invoke-Replenish` (`Replenish.ps1:76`) | job `replenish` (`PlantSteps.Replenish`) | kept; it starts the saga (V1.1) |
| Backlog low (`design_waiting`) | the sweep's count, `Keeper.cs` | job `backlog-low` | kept |
| Base update fallback after a missed webhook | `BaseWatch.AfterSweep` (`BaseUpdate.cs:83`), called by the sweep | none: the sweep is its only caller | **gap**: V1.4 (a recurring job) |
| Plant fields `held`, `pass_unmerged`, `unconfirmed`, `flags` | the sweep's answer | not fed for saga items (`SagaPlantFeed`) | `flags` with V1.4; the others are not needed (the saga merges its PASS, the intake holds dependencies) |

**The rule-script verbs.** Item (18), merge (5), read models (6), keeper and manager (7), services (6), board (6) and QC (14) are routed
to C# where v2 acts. Delivery is 6 of 7: `auto-promote` is not routed. Never routed: `auto-promote`, `plant-snapshot-save`, `-load`,
`-clear`, `-await` and `leader-lease`. Still partly script although routed: `merge`, `approve-pr`, `clear-hold` and `base-watch` for an
item with no saga and for a policy other than auto (`MergeVerbs.cs` `Script(...)`, `MergeVerbService` `if (!settings.Auto) return null`:
the declared exceptions `merge-hybrid-script` and `merge-peer-approved`); `sync-board` (the script's pass plus one reconciler pass,
`BoardVerbs.SyncBoard`); `ingest-event` and `journal` when the translator or the ledger is absent; `transcript-load` and `-prune` as the
ref-store fallback. The six declared exceptions of the architecture test are `auto-promote`, `leader-lease`, `plant-snapshot`,
`transcripts`, `merge-peer-approved`, `merge-hybrid-script`; each leaves in a row of section 5 (V2.3, V3.2).

**The gateway's own calls into the scripts** (`Runner.RunAsync`, which asks the routes first, and `Runner.RunScriptAsync`, which never
does): 28 of the 34 internal callers in the inventory are `router` forwards that need no script once the scripts are gone (`PlantHooks`,
`PlantTicker`, `BaseUpdate`, `Qc.cs`, `ItemPrLookup`, `OwnerCiReleaseService`, the metrics, admin, webhook, operator-release and demo
endpoints, `FeatureChecksJob`, the revocation, Copilot, journal and park calls of the composition, `ItemStepsPort`, `PlantRestoreCorrection`,
`DeliveryVerbHost`, `QcPort`). The six that run a script are the six exceptions above.

**Labels** (all compared without case; the names are the defaults of `config/instance.json`).

| Label | v1 meaning | v2 owner |
|---|---|---|
| `factory worker` (`label`) | the item is the factory's: every board filter, the intake, the issues query; written on new items | `IntakeRule`, `PlantSteps`, `KeeperCommands` |
| `AI Factory` (`rivalLabel`) | another factory's item: skipped, a claim refused | `IntakeRule`, `ItemVerbs.Claim`, `PlantSteps` |
| `Demo` (`demoLabel`) | no offer, no claim, no intake | `IntakeRule`, `ItemVerbs.Claim` |
| `enhancement` | written on replenished items only | written by `PlantSteps.Replenish` |
| `Auto Merge` (`autoMergeLabel`) | the gateway merges a PASS; a promotion gate; QC eligibility | `MergeVerbRules`, `ContentGate`, `ItemMergedDeriver`, `QcConfig` |
| `video` (`videoLabel`) | the demo evidence must be a video; a promotion gate | `PublishDemoVerb`, `ItemVerbs.Read`, `HttpMergeFactsReader` |
| `Hold` | blocks promotion | `ContentGate`, `ItemMergedDeriver` |
| `hold:owner` (`ownerHoldLabel`), any `hold:*`, `intake.holdLabels` (`Needs Human Review`) | a person holds the item: no offer, claim refused, merge held, no intake; keeper and manager verbs refused | `ItemVerbConfig`, `MergeVerbRules`, `HoldObserved`, `IntakeRule`, `SagaPlantFeed`, `FactoryRules` |
| `Needs Human Review` (written) | the inspector's escalation under `peer-approved` (`Verbs.Inspect.ps1:144`) | goes with the dropped policy (decision 4); stays a hold label that v2 reads |
| `worker:<name>` | pin: offered only to that worker; no intake | `SagaPlantFeed` (the plant's offer), `SlotRequestPort` (rank), `IntakeRule` |
| `priority:top`, `priority:high` | order | `SlotRequestPort` rank, `SagaPlantFeed`, `KeeperCommands` |
| `factory test` (`testLabel`) | the "testing" profile | `ModelTierReadModel`, `SagaPlantFeed` |
| `env:<name>` (with `envScope`) | the item belongs to the environment named; tdd takes only `env:tdd` | every v2 reader (`ItemVerbConfig`, `FactoryRules`, `ReadModelSettings`, `RuleKit`, `WebhookEventTranslator`, `BoardPort`); **not** `IntakeRule` (V1.2 checks it) |
| `runtime:octopus`, `runtime:copilot-cloud`, `runtime:cursor-cloud`, `runtime:cae-job` | the runtime of the item's sessions; wins over `runtimes.routing` | **gap**: the slot request takes `scheduler.runtime` only (`SlotRequestSettings.From`, "the plant's routing is v1's and is not read here"); V1.3 |
| `agent:cursor` (and any `agent:`) | no reader in v1 or v2 (the agent is runtime configuration: `runtimes...octopus.agent`, `caeJob.agent`) | nothing to port; V1.3 states it |
| `hotfix` | none in v1 | written by the incident saga on its hotfix item; a person's `hotfix` label starts nothing (**gap**: V1.3) |
| `incident` (`incidents.issueLabel`) | none in v1 | the incident saga |
| `engine:v2` (`intake.v2Label`) | none (parsed, never written) | removed with the setting (V3.2) |

**Settings with a v1 reader only:** `gatewayMerge` (the engine merges where it acts), `runner.pool`, `poolMaxCalls`, `maxProcesses`
(the Runner and its pool), `github.budgetReserve` (`GitHubBudget`; the v2 jobs' `budgetLow` reads the same budget object, kept),
`keeper.maxNewItemsPerDay` (only `Verbs.Keeper.ps1` and `Keeper.cs`: **gap**, V1.4 ports the cap into `KeeperCommands`), and the host
settings of `schedule` (`hostOwnedDefault`, `hostBuildsDefault`, `buildSlot*`, `leaseRenewMinutes`, `stale*`, `clientStartMinutes`,
`progressMaxMinutes`, `continueMinutes`), which `Hosts.cs` and `Lease.cs` still read for the plant's connected sessions and keep.
`engine.shadowEnvironments`, `intake.v2Plans` and `intake.v2Label` exist only for two engines and go in V3.2
(`delivery.autoPromote.trainActs` goes in V2.3) (`v2Environments` stays as "where the engine acts"; an environment it does not name is inert). Everything else
(`columns`, `ciGates`, `docsOnly*`, `timing.*`, `intake.*`, `keeper.replenish.*`, `manager`, `operator`, `messaging`, `delivery.*`,
`octopus.*`, `incidents.*`, `mergeQueueDepth`, `mergeStrategy`, `baseUpdate.*`, `qc.*`, `scheduler.*`, `runtimes.*`, `api.*`,
`recurringJobs.*`, `retention.*`, `wolverine.*`, `plant.*`, `stateAuthors`) has a v2 reader.

**The `AutoPromoter`** (`gateway/AutoPromoter.cs` over `Verbs.Promote.ps1`) and the train (`DeliveryTrain.cs`, `OctopusPromoter.cs`):

| v1 | v2 | |
|---|---|---|
| one project (`octopus.project`, cmdemo1-ui), targets `autoPromote.environments` (uat, prod), only on the gateway of `gatewayEnvironment` (tdd) | `DeliveryShadowSettings.From`, `AutoPromoteHere` | same |
| the queue: Release Queue cards grouped by `merge.release`; a release no queued item names is not judged | candidates from `ItemMerged`; the newest qualifying release | declared `queue-only-v1`, `newest-only` (#496) |
| previous environment's deployment `Success`; target deployment exists | `DeliveryTrain` :508-517 (same texts) | same |
| item gates (Auto Merge, no hold, merged by the gateway after a PASS, the video asset) | `ItemGates.Gaps` (`ContentGate.cs`) | same |
| content gate (every commit a queued or settled item's merge; fail-closed) | `ContentGate.Judge`, `CommitItemLookup` | same |
| health evidence (`/_build` version, `overallStatus` Healthy, no bad read since the deployment, two reads 5 minutes apart, 10 minutes settle) | `EvidenceComposite.Evaluate`, reads kept in the train's row (survive a restart) | same; `state.promote_health` on the first item is no longer written (only v1 read it) |
| a health read is tried 3 times, 20 s apart (`octopus.health.retries`, `retrySeconds`) before it counts as unreachable | `HttpTrainReads.Probe`: one try | **gap**: one stray failure is a stored bad read that blocks the release until a new deployment; V2.1 |
| Sign-off: interruptions of our own deployments; responsible PUT, submit Proceed or Abort; hard and soft gaps; abort after `signoffWaitHours` (2) with `signoff_wait_since` | `SignOffRequested`, `OctopusPromoter.Answer`, `SignOffWaitRecorder` | same |
| journal lines on the items: `promoted`, `promotion-waiting` (once per release, environment and reason, naming the item that holds the others), `promotion-signoff` | none (the train's history only) | **gap**: V2.1 |
| a 403 of Octopus is a waiting reason on the items naming the missing permission | logged and retried, never shown on an item | **gap**: V2.1 |
| no double deployment (Octopus re-read before the create) | `Promote` reads first; the ledger key | same |
| pass every 60 s (`timing.promoteSeconds`) | poll 120 s, safety wake 10 minutes | slower, accepted |
| no dependency rule | `dependencies-delivered` (on for an acting environment) | new behaviour with the switch, accepted (the owner's 2026-10-30 W9.12 text) |
| no incident hold | `TrainHold` (W8.1) | v2 only |
| `ReleaseQueue.cs` (`release.ready` on the operator stream, the dashboard's request button) | unchanged, fed by `SagaPlantFeed` `release_queue` | kept |

### 2. How every factory item becomes a v2 saga

The rule after this revision: **every open factory item in a column of a step has exactly one open `WorkItemSaga`**, whoever put it
there. The adoption job already maps an item from v1's state comment; it becomes the **admission** (V1.1): it runs every pass of the
board reconciler (not once per lease term) over the cards it has just read, and a card in a step column with no open saga is started.

| How the item arrives | What starts its saga | Plan | Runtime of its sessions |
|---|---|---|---|
| Todo, through the intake | `DocsIntakeJob` (`StartWorkItem`, source `intake`), when the WIP limit (6), the design-waiting rule and the free slots allow; the slots are the connected owner sessions' plus the scheduler's free runtime capacity (V1.2) | body with `docsOnlyMarker`: docs; label `hotfix`: hotfix (V1.3); otherwise feature, switched to docs by a docs-only design (as today) | V1.3's order: a `runtime:<kind>` label; else `runtimes.environments.<env>.routing` for the phase; else `scheduler.runtime` (octopus on tdd). A connected owner session still goes first where `preferConnected` and the label names no API runtime (v1's rule) |
| Replenish | `PlantSteps.Replenish` sends `StartWorkItem` (source `replenish`) right after it places the card in Technical Design (V1.1); the admission covers a crash between the two | feature (the replenished body carries no marker; its design is docs-only and switches the plan, as for any item) | as above (no runtime label is written: routing) |
| A person moves a card into a working column (from Todo or from nowhere) | the admission: no state comment, so a fresh start at the step of the column (`StartWorkItem` with the column's step, source `board`); the `Depends on` check of the intake applies, and an unmet one parks the item `depends-on` (resumed when met) | as for the intake | as for the intake |
| A person moves a card back to an earlier column | `CardMoved` (W9.10h) on the open saga | unchanged | unchanged |
| A card with v1's state comment and no saga (in flight before this revision, or after a rollback) | the admission's adoption mapping (W9.10m) with the fencing (W9.10n), every pass | from the state comment (`design.docs_only`, the pull request's files) | as above, with the live claim's runtime kept |
| A reopened item | the reconciler's reopen input: with a completed saga, `ReopenWorkItem` (the next generation, plan of the last one; redeliver goes back to feature); with no saga (finished by v1), the admission after the card is back in a step column. A reopened issue whose card is in Done or the Release Queue is not started (v1 did not either) | as left | as above |
| A delivery undone after the settle window | `DeliveryLost` on the completed saga (W8.3) | redeliver | none (Deliver only) |
| An incident | `CreateHotfixItem` (W8.1) | hotfix, top rank | as above |
| An item with `env:<other>` | not admitted and not pulled (`envScope`): it is another environment's | | |
| An item with `runtime:<kind>` | as its column says; the label sets the runtime of every slot request of the item. A runtime that is off in this environment holds the slot with the reason `runtime-off` (the scheduler's hold, shown in `/api/v1/status` `v2.capacity`) | | the label's |
| An item with `agent:<name>` | as its column says; the label is not read (it never was) | | |
| An item with `worker:<name>`, `hold:*`, `Demo`, `AI Factory` | not pulled from Todo; in a working column admitted but not offered (pin: offered only to that worker; hold: parked `hold: <label>` by `HoldObserved`) | | |

### 3. The uat and prod switch

What the train needs before it acts for the app's uat and prod (V2.1, then V2.2):
1. **Parity gaps closed** (V2.1): the health read retried as v1 does (3 tries, 20 s); the three journal lines on the items, once per
   reason, through the journal ledger (`promoted`, `promotion-waiting` naming the item that holds the others, `promotion-signoff`),
   and the refused-permission notice as a `promotion-waiting` reason; the `promote` suite run on v2 with the train acting
   (`run.ps1 -Engine v2` sets `trainActs` for it), the scenarios that depend on v1's pass design (`promote.ps1` :77, :344, :376, :403:
   the pass budget and the cold pass) declared `v1-only-scenarios` with that reason, and the multi-release ones checked against
   `newest-only` and declared in `golden/v2-differences.txt`.
2. **Health gates and Sign-off:** built and equal to v1's (section 1 table); nothing else is needed.
3. **The promote-health parity checks:** the evidence reads live in the train's row, so a restart keeps them; the state comment's
   `promote_health` is not written by v2 and is not needed by anything after v1 is gone (`state-guard.ps1` trims it; left as is).
4. **The W5 gate, restated a second time (decision D2 of 2026-11-01).** Its first event (3 live releases matched) cannot be met in the shadow: v1 waits on releases
   whose tdd deployments failed, and the train's steps after a request (follow, health window, Sign-off) never run in the shadow because
   nothing is executed. What was observed stays recorded (6 compared, 6 matched, 0 differs). The gate becomes: (a) the 6 unexplained
   live rows explained on #264 (the 38 `v2-only` rows of releases that no queued item names and the one `v1-only` row of a superseded
   release fall under the two declared differences once `ShadowGate` matches them by release and not only by the declared list; any
   that does not is a defect, fixed in V2.1); (b) the dead letters of the train cleared and their cause closed (the 121 `TrainWake`
   with `MergeFactsException` since 2026-10-30, after #607); (c) V2.1 merged with the acting `promote` suite green. The live evidence
   (timers, the Sign-off answered, a restart during a wait) is observed after the switch, as the switch's own events (V2.2).

**The order of the switch.** V2.2 is one overrides change, `delivery.autoPromote.trainActs: true` (uat and prod together: the rule
script promotes every environment or none, so the switch cannot be per environment while the `AutoPromoter` exists). The same release
stops the `AutoPromoter` loop (`AutoPromoter.Runs` is false) and removes the shadow (`/api/v1/delivery-shadow` answers 404). Before the
merge: record the comparison on #264 (it is gone afterwards) and say on #268 that uat and prod change hands. The live events below need
an app release that deploys to the app's tdd successfully, so fixing the app's failing tdd deployments (Deployments-57439 to 57450 and
later) is their precondition (D2); V2.2 may merge before, and its events then wait for the fix. After it, in this order:
1. the release check; `/api/v1/status` shows no `AutoPromoter` run and the train's uat and prod policies acting;
2. the first app release that reaches tdd healthy: one `PromoteRelease` to uat, one deployment, the health window, the journal lines on
   its items; expect a large first batch (the 67 queued items ride the first qualifying release, newest-only);
3. its prod promotion: the Sign-off answered by the train (Proceed, or Abort with the reason), one deployment, `DeliveredTo(prod)`, the
   items complete their settle window;
4. a gateway release during a train's wait, after which the `TrainWake` fires (#500);
5. dead letters 0.

**Rollback:** until V2.3 merges, a revert of V2.2 (one release, about 4 minutes) brings the `AutoPromoter` loop back; the train's
`merge.promoted.<env>` records and `signoff_wait_since` in the state comment (W9.10i) are what v1 reads, so it does not promote twice.
After V2.3 the rollback is a revert of V2.3 and V2.2 together. A wrong promotion in uat or prod is the operator's (incidents there
escalate only, default 7 of 2026-10-30).

### 4. The removal of v1 (W9.11a to c, revised)

**Deleted:**
- the sweep: `Sweeper.cs` (`sync-board` and its passes, `SweepV2Filter`, the `skip_items`/`skip_steps` arguments), `SweepGap.cs`,
  `Plant.IntakeSlots` (moved to the capacity call of V1.2), the verb `sync-board` (the reconciler stays), `BoardVerbs.SyncBoard`'s script
  half;
- the rule scripts of the server: `plugin/scripts/lib/Verbs.*.ps1`, `Apps`, `BaseUpdate`, `Board`, `Checks`, `Journal`, `Leader`,
  `Metrics`, `Owner`, `OperatorRelease`, `PlantSnapshot`, `Prs`, `Qc`, `Replenish` and `runner-host.ps1`; the server mode of
  `factory.ps1` (`--server`, `$ServerVerbs`, the dot-sourcing at :50);
- the Runner's script half: `RunnerPool.cs`, `RunnerGate.cs`, `RunnerEnv.cs`, `TestCause.cs` (as a script argument),
  `Runner.RunScriptAsync`, `V1VerbRouter` and `VerbRouterFactory`, `V2VerbFeed`'s v1 answer path; `Runner` keeps only the route table and
  its fences (renamed `VerbDispatch` or left, as the pull request finds simpler);
- the hybrid fallbacks: `MergeVerbs.cs` `Script(...)`, `MergeVerbService`'s non-auto branch (the `merge-peer-approved` and
  `merge-hybrid-script` exceptions), the transcript ref-store fallback (`RuleScriptTranscriptBackend`; rows older than the move have aged
  out of the 14 days), `ingest-event` and `journal` script fallbacks;
- the GitHub-ref lease (`leader-lease`, the five calls of `Leader.cs`), the ref snapshot (`plant-snapshot-*`,
  `SnapshotCoordinator`'s ref half): the SQL lock (W9.10b) and the SQL plant (W9.10k) stay;
- the `AutoPromoter` and `Verbs.Promote.ps1` (V2.3), the shadow machinery (`DeliveryShadow`, `ShadowComparison`, `ShadowGate`,
  `ShadowStore`, `DeliveryActingShadowHandlers`, `DeliveryShadowPort`'s shadow half, `ShadowHistory`, `AdminDeliveryShadow`, the shadow
  tables' prune; the tables stay, expand-only), `EngineMode.Shadow` and `engine.shadowEnvironments`;
- `FACTORY_V2_OFF` (`EngineOptions.KillSwitch`); its entry in cmdemo1-system is deleted, not renamed (D1; the cmdemo1 session, after
  V3.2 is live); the adoption break
  detection and the readoption hold (`AdoptionBreak`, `ReadoptionGate`, the `acted` heartbeat), which exist only for a v1 interval;
- the state-directory files: `refusals.json` (the model ladder's counter, already C# in `MergeVerbService`: into the database),
  `qc-items.json` (`FileQcIndex`: `factory.QcEnvironments` already holds it), `seen.json` (sweep only), `project-meta.json` (memory),
  `item-N.lock` (the in-process `IItemLock`);
- the settings `gatewayMerge`, `runner.*`, `intake.v2Plans`, `intake.v2Label` (`delivery.autoPromote.trainActs` already went in V2.3; an override that names
  a removed setting fails the image build, so `deploy.overrides.json` loses them in the same pull request).

**Kept:**
- the client side of the plugin that workers download: `factory.ps1` (client verbs), `lib/Paths.ps1`, `Models.ps1`, a client `Core.ps1`
  (without its `Leader.ps1` dot-source), `Client.*.ps1`, `Progress.ps1`, `qc-local.ps1`, `app-run.ps1`, `app-shot.ps1`,
  `ensure-tools.ps1`, `models.default.json`, `launcher/`, `skills/`, `agents/`, `tools/shot`, `runner/`;
- `pwsh` in the image for now (the `Dockerfile` checks `plugin/scripts/factory.ps1`; removing pwsh is a later, separate change of a
  protected file); the `ScriptsDir` anchor of `GitHubBudget.Load`, `LeaderSettings.Load`, `ManagerCopilotSettings.Load` and
  `PluginArchive.Root`;
- the SQL lease, Solo mode, the plant (`Plant*.cs`, `Hosts.cs`, `Lease.cs`, `PlantHooks`, `PlantTicker`, messaging, the manager, the
  operator, the keeper), `ReleaseQueue.cs`, `BaseWatch` (with its new caller), the adoption mapping (it is the admission's path for an item
  that carries v1's state comment);
- the v1-format state comment projection (W9.10i) through the W9.11 gate: it is what a rollback to the previous image reads. Retiring
  the format is a later contract step with the owner's word (expand, then contract);
- fworker's own uat and prod gateways: no database, inert on the board, on their last release before V3.2 until the owner says otherwise
  (W9.11b of 2026-10-30, unchanged). "v2 takes uat/prod" is the app's environments.

**The kill switch after v1: none** (decision D1 of 2026-11-01). There is no second engine to fall back to and no stop switch is added.
The only rollback is a redeploy of the previous fworker release through Octopus (it still reads the v1-format state comment, which is
why the projection stays through the W9.11 gate).

**CI and Tier 1:**
- the v1 Tier 1 matrix (`tier1` of `tests.yml`, about 50 shard jobs) goes; the v2 matrix (`v2`) becomes the only Tier 1 and keeps its
  shards, renamed `Tier 1 (...)`; `V2_BLOCKING`, the `-Engine` parameter, `FACTORY_V2_OFF` in the harness and `v2-claims.txt` go (every
  suite runs on the one engine; the coverage job checks the shards against `run.ps1 -ListSuites` as before);
- the suites `peer-approved` and `qc-gate` (declared v1-only; `merge-queue` is claimed on v2 and stays), the `v1-only-scenarios` lines and the scenarios
  that test the Runner and the pool (`runner gate`, `qc: runner pool`) are deleted; `leader` is rewritten on the SQL lease only;
- the parity tests that run the scripts (`MergeVerbParityTests`, `ServiceVerbParityTests`, `BoardVerbParityTests`, the script part of
  `tests/smoke.ps1`) are retired; the goldens are frozen as C#-owned fixtures; `golden/v2-differences.txt` folds into them;
- the job `Image starts without a database` stays (an environment with no database is inert); `Build result` and `secret-scan` remain
  the required checks, unchanged in name;
- the SQL Server service container becomes part of every Tier 1 job (it is already in the v2 jobs).

### 5. The pull requests, in order

Lanes: **V1** (tdd starts every item on v2), **V2** (the app's uat and prod), **V3** (v1 removed). V1.1 to V1.4 run in parallel, and
V2.1 beside them; the composition files (`GatewayWolverineExtensions.cs`, `RecurringJobSlots.cs`, `GatewayServiceCollectionExtensions.cs`)
are hot, so the main session integrates them as in section 1 of this plan ("How parallel subagents work"). Every pull request runs the smoke test and the suites of its area; every
tdd check is read with `/api/v1/status` and recorded on #268.

| PR | Scope | Files | Size | After | Tests | tdd check |
|---|---|---|---|---|---|---|
| **V1.1** admission | the adoption job becomes the admission: every reconciler pass (leader only), for each open factory card of the home repository in a step column with no open saga: the adoption mapping when it has v1's state comment, else `StartWorkItem` at the column's step (a new `Step` field on the message; `Begin` enters that step); the `Depends on` check parks `depends-on`; a reopened card with a completed saga sends `ReopenWorkItem`; `envScope`, rival, demo and hold labels respected; `PlantSteps.Replenish` sends `StartWorkItem` after the card is placed; the status block `v2.admission` (started, adopted, skipped by reason) | `src/Fworker.Workflows/Adoption/*`, `WorkItem/WorkItemSaga.cs`, `WorkItemMessages.cs`, `Reconcilers/PlantSteps.cs`, `gateway/Composition/Adoption*`, `GatewayWolverineExtensions.cs` (hot) | M | none | `Fworker.Workflow.Tests` per arrival (fresh card, v1 state comment, reopened, closed, env:other, held, depends-on); Tier 1 `saga` scenarios "a replenished item starts its saga", "a person moves a Todo card to Technical Design", "a reopened item"; `tests.yml` unchanged | a replenished item has a saga within one reconciler pass (`v2.sagas.byStep`); v1's sweep answers no action for it; #1177 to #1181 (or their successors) adopted with their live claims, no second session started |
| **V1.2** intake slots from capacity | `DocsIntakeJob`'s free slots: `Plant.IntakeSlots()` plus the scheduler's free capacity of the runtimes it would grant (concurrency less in flight and queued, bounded by what is left of the day budget); `IntakeRule` checks `envScope` | `gateway/Ports/DocsPlanPort.cs`, `src/Fworker.Workflows/Intake/IntakeRule.cs`, `Capacity/CapacityService.cs`, `gateway/Intake.cs` | S | none | `IntakeRuleTests`, a `CapacityService` free-capacity case; Tier 1 `saga` "the intake pulls with no owner session, on runtime capacity" | a Todo item is pulled with no owner session connected; never more in flight than `maxConcurrent` (2) and the WIP limit (6); `docs-intake` job's last outcome `started N` |
| **V1.3** runtime and plan from labels | `SlotRequestPort`: `runtime:<kind>` label, else `runtimes.environments.<env>.routing` for the phase, else `scheduler.runtime`; a runtime that is off holds with `runtime-off`; the pin honoured on the saga's offer (`V2Ownership.OfferNodes` offers a pinned item to that worker only); a person's `hotfix` label at the start chooses the hotfix plan (`StartWorkItem.Hotfix`; D3); `agent:` stated as not a label in `verb-inventory.md`; `intake.v2Plans` gains hotfix and redeliver (W8.1b's config half) | `gateway/Ports/FeaturePlanPort.cs`, `gateway/Ports/WorkItemPort.cs` (offers), `DocsPlanPort.cs`, `config/deploy.overrides.json` (protected: the owner's decision of 2026-11-01 covers it) | S | none | `SlotRequestPortTests` (label, routing, default, off), an offer test for the pin; one scenario each in `copilot` and `cursor` (both claimed on v2) whose item carries a `runtime:` label and runs on a saga | an item labelled `runtime:copilot-cloud` asks the scheduler for that runtime (or is held `runtime-off` while Copilot is off for tdd); an unlabelled one gets octopus |
| **V1.4** the sweep's last steps | the gap renew of owner claims of saga items at the first pass of a term; `BaseWatch.AfterSweep` as a recurring job (`base-watch-fallback`, 5 minutes); the Release Queue card without a merge record flagged (`flags` and a journal line); `keeper.maxNewItemsPerDay` in `KeeperCommands`; the auto-unpark difference declared in `v2-differences.txt` | `src/Fworker.Workflows/Reconcilers/*`, `Keeper/KeeperCommands.cs`, `gateway/BaseUpdate.cs`, `RecurringJobSlots.cs` (hot), `tests/scenarios/golden/v2-differences.txt` | M | none | `PlantStepsTests`, `BaseUpdate` job test, `KeeperCommandTests`; the base update scenarios of `owner` (`baseupdate.ps1`) with a missed webhook | after a release, a lapsed owner claim of a saga item is renewed before the first offer; `base-watch-fallback` runs (status `v2.jobs`) |
| **Gate V1** | events, recorded on #268: for one day of normal work every open factory card in a step column has an open saga (`v2.admission` skipped only with reasons); v1's sweep reports no item action; one item of each arrival (Todo, replenish, a card moved by a person, a reopened one) runs to Done or the Release Queue; dead letters 0 outside the train | | | V1.1 to V1.4 | | |
| **V2.1** train parity | the health read retry; the item journal lines (`promoted`, `promotion-waiting` once per reason with the holding item, `promotion-signoff`, the refused-permission reason) from the executor's and the train's decisions through the journal ledger; `ShadowGate` matches declared differences by rule, and the 6 unexplained rows are explained or fixed; the `TrainWake` `MergeFactsException` dead letters' cause closed; `run.ps1 -Engine v2` sets `trainActs` for `promote`; the pass-design scenarios declared; `DeliveryUatProdV1Tests`, `EngineOptionsTests` and `DeliveryShadowPortTests` follow | `src/Fworker.Workflows/Delivery/*`, `gateway/Ports/DeliveryExecutorPort.cs`, `DeliveryShadowReads.cs`, `src/Fworker.Integrations/Octopus/*`, `tests/scenarios/{run.ps1,v2-claims.txt,golden/v2-differences.txt}`, `tests/Gateway.Tests/*` | M | none (beside V1) | the `promote` suite green on v2 with the train acting; executor and journal unit tests; `DeliveryTrain` probe-retry tests | still shadow: the gate fields of `/api/v1/delivery-shadow` show `unexplainedLive` 0; v2 dead letters of the train 0 for a day |
| **V2.2** the switch | `delivery.autoPromote.trainActs: true` in `config/deploy.overrides.json` (protected; the owner's decision of 2026-11-01 is the approval); the comparison recorded on #264 before the merge | `config/deploy.overrides.json`, `tests/Gateway.Tests` (the overrides assertions) | S | V2.1 | `apply-overrides.ps1` check; the overrides tests | the events of section 3, 1 to 5 |
| **V2.3** the `AutoPromoter` and the shadow removed | `AutoPromoter.cs`, `Runs`, `OnActions`/`ObserveV1`, the loop's registration, `Verbs.Promote.ps1` from the server set, the shadow machinery (section 4), `trainActs` (the train always acts where `AutoPromoteHere`); the `auto-promote` exception leaves the architecture test | `gateway/AutoPromoter.cs`, `gateway/Composition/*` (hot), `gateway/Ports/DeliveryShadow*`, `src/Fworker.Workflows/Delivery/{DeliveryShadow,ShadowComparison,ShadowGate,ShadowStore}.cs`, `gateway/Endpoints/AdminDeliveryShadow.cs`, `docs/design/verb-inventory.*`, `config/*` | S | V2.2's event 3 (the first prod delivery by the train) | the `promote` suite; the architecture test with five exceptions | the next release: no shadow in status, `/api/v1/delivery-shadow` 404, a promotion still made by the train |
| **V3.1** (W9.11a) the tests on one engine | v2 the only engine of the harness; the v1 `tier1` matrix removed, the `v2` matrix renamed Tier 1; `v2-claims.txt`, `V2_BLOCKING`, `-Engine` removed; the v1-only suites and scenarios deleted; the script parity tests retired; goldens frozen as C# fixtures; `leader` on the SQL lease; `CLAUDE.md`'s Test section | `tests/*`, `.github/workflows/tests.yml` (the local addition of the kit; hot: the main session), `CLAUDE.md` | M | Gate V1, V2.3 | the whole Tier 1 on the one engine green on master (`gh workflow run Tests --ref master`); the coverage job | none (tests only); CI wall clock recorded |
| **V3.2** (W9.11b, first half) the gateway without v1 | the sweep, the Runner's script half and pool, the hybrid fallbacks, the ref lease and ref snapshot, `FACTORY_V2_OFF` and shadow mode (no replacement, D1), the adoption break detection, the v1-only settings (section 4); the state-directory files into the database or memory (a DbUp script for the refusal counter, expand-only); the architecture test's allow-list down to router forwards only | `gateway/*` (one subagent, last), `src/Fworker.Workflows/Adoption/*`, `src/Fworker.Database/scripts/Update/*`, `config/instance.json`, `config/deploy.overrides.json` | L | V3.1 | the Gateway, Workflow and src tests; the whole Tier 1; the image-start job | the release check with `leader.sql` (no `leader.role` of the ref); `restore.how` sql; one lease rollover with one acting node; no `pwsh` process started by the gateway in an hour (status `runner` absent); then the cmdemo1 session deletes the `FACTORY_V2_OFF` entry of cmdemo1-system (D1) |
| **V3.3** (W9.11b, second half) the plugin without server scripts | the server libraries and `runner-host.ps1` deleted; `factory.ps1` client only; `Core.ps1` split; the author suite's checks of the plugin kept green; `GET /api/v1/plugin` serves the smaller tree | `plugin/scripts/*`, `tests/smoke.ps1`, `tests/scenarios/author.ps1` | M | V3.2 | smoke, `author`, `workers`, `owner`, `operator` suites; a worker's download unpacks and `factory.ps1` client verbs answer | the `pluginHash` of `/_build` changes; a runtime session (Octopus) downloads the plugin and completes a phase |
| **V3.4** (W9.11c) the release check and the documents | `CLAUDE.md` (Layout, Release step 6 with `leader.sql` and `restore.how`, Rules: "only one gateway may sweep" becomes the lease rule, the rollback is a redeploy of the previous release), `docs/HANDOFF.md` (a v2 section), the operator skill's reading of `waitingFor`, the status fields; the W9.11 gate recorded | `CLAUDE.md`, `docs/HANDOFF.md`, `plugin/skills/factory-operator/*`, `gateway/Endpoints/{Health,Status}*` | S | V3.3 | smoke | the W9.11 gate (2026-10-30 text): the release check with the new fields, one lease rollover, a docs item and a feature item to the app's tdd, dead letters 0 |
| W9.13 | multi-repository, unchanged | | L | V3.4 | | |

Parallel: V1.1, V1.2, V1.3, V1.4 and V2.1 at once (V1.1 and V1.4 share the composition: the main session merges them one after the
other); V2.2 after V2.1, whatever V1 does; V2.3 after V2.2's first prod delivery; V3.1 after Gate V1 and V2.3; V3.2, V3.3 and V3.4 in
that order, one at a time.

**Risky points:**
- **V1.1** turns on v2 for items that v1 runs now: an item in flight under v1 at the merge is adopted with its live claim (W9.10m); the
  check is that no second runbook session starts for it. A fault here starts sessions for many cards at once; the admission starts at most
  `intake.wipLimit` items in the AI columns and logs each start.
- **V2.2** makes the first gateway promotion to the app's uat and prod by the train, likely with all 67 queued items in one release, and
  answers the prod Sign-off by itself. Merge it when the owner knows the time, and watch events 2 and 3.
- **V3.2** is large and removes the last rollback to v1; the rollback from it is the previous fworker release (it still has v1 and reads
  the state comment). Keep the v1-format state comment through the W9.11 gate.
- **The reconciler's GitHub cost** (395 requests an hour now): it falls when the sweep's extra pass goes (V3.2); until then it is read on
  every tdd check.
- **The app's tdd deployments are failing** (since at least release 2.4.1228). That is the app's, not the factory's, but it blocks every
  promotion in either engine; its fix is a precondition of V2.2's live events (D2). The factory's items keep moving to the Release
  Queue meanwhile.

**Decisions:** D1 to D3 were decided by the owner on 2026-11-01 (top of this revision); none is open.

## Revision 2026-10-30 (audit)

An audit of the program on 2026-10-30 (Opus, against master and the in-flight pull requests #540 to #549) found the verb-level port
(W9.1 to W9.8) nearly complete and built with care, and tdd safe today (shadow run, v1 acts, the v1 lease fences everything). It found
the plan for the rest **not correct as written**:
- under the hybrid composition, v1's lease, sweep, plant, snapshot and rule scripts are v2's base layer, yet W9.11 removed them and no
  task replaced them;
- W9.9 switched nothing (`intake.v2Plans` empty, and `intake.enabled` also turns on v1's intake);
- no Tier 1 scenario has started a `WorkItemSaga`, so the W6 and W7 gates and the cutover precondition could not be proven;
- the rollback path was unsound (the state comment projection is not wired);
- leadership needed a decision before W9.11.

This revision applies the owner's decisions of the same day and rewrites the rest of the plan. What it changes:

**The owner's decisions (2026-10-30):**
1. **Leadership:** keep a single-actor lease with Wolverine in Solo mode. The lease moves from the GitHub ref to a SQL application lock
   that also decides when the Solo node starts; the node runs only on the holder and stops before its hand-over. W9.11 does not
   remove the lease. Balanced mode with Wolverine's leader election (#464) is not the target any more.
2. **The app's uat and prod promotions** stay with v1's `AutoPromoter` after the cutover, until the delivery train has a recorded clean
   live window (the W5 gate, restated below as events). v2 acts in tdd only: the train decides uat and prod in shadow until then, and
   W9.12 hands those environments to it.
3. **`delivery.finalEnvironment` is tdd**: an item's delivery to the app's tdd starts its settle window.
4. **The `peer-approved` merge policy is dropped**, not ported: its reviewer-App approval stays in the script, and its suites
   (`peer-approved`, and `qc-gate`, which runs under that policy) go with v1 in W9.11a.
5. **#496:** the train promotes the newest qualifying release only, where v1 may promote two. Accepted as a declared difference.
6. **The nine defaults** of the audit, accepted as proposed:
   1. `intake.v2Plans` stays, as a deliberate per-plan exception to "no per-capability flags" (section 1, rule 9; section 4);
   2. at the cutover only parity evidence tdd can produce is required ("Owed evidence" below);
   3. dead-letter and envelope-backlog counts in `/api/v1/status` (W9.10o) instead of #462 (OpenTelemetry and Azure Monitor) and #463
      (the dead-letter page), which are not cutover work;
   4. a faster board-reconciler poll (1 to 2 minutes, W9.10g and the W9.9c checklist) instead of a `projects_v2_item` webhook;
   5. S1 is the pre-agreed fallback tier of the tdd database if the cutover batch saturates S0 (DTU p95 at or above 80%);
   6. the gateway's Octopus account gets `DeploymentCreate` on tdd for cmdemo1-ui (W8.2b), for the tdd rollback;
   7. incidents in the app's uat and prod escalate only: open the incident, hold the trains, notify; no automatic rollback there;
   8. no W8.4 analyst role (no new role, `Auth.cs` and `Mcp/RoleTools.cs` untouched); triage reads the health endpoints;
   9. synchronous decision verbs (a saga `InvokeAsync` that can refuse a session) are dropped in favour of the as-built feed
      (`V2VerbFeed`: v1's verb answers, then the saga is told); `wolverine-factory.md` 8 records it.
7. **Multi-repository** comes after the cutover, as its own row (W9.13). The cutover is home-repository only.

**The hybrid composition is the shape of the cutover**, not a stage before it. Where v2 acts, the sagas, the scheduler and the trains
own the items they started and every routed verb; what v1 still did for those items (the sweep's steps, the plant's offers and
starts, the gateway's own rule-script calls, the snapshot) moves to v2 in the W9.10 rows before the switch, so the cutover gate
measures what remains after W9.11, and W9.11 removes code that nothing calls. Section 4 is restated for it.

**New rows between W9.8 and W9.9** (W9.10 is free since #362 closed; lettered sub-rows, each one issue under the W9 epic #268; the
W9 table in section 4 gives content, owned files, order and size; Appendix B the issue numbers):

| Row | In short |
|---|---|
| W9.10a | Solo node only on the acting holder; the old node stops before the hand-over (on today's lease, now) |
| W9.10b | leadership: the lease as a SQL application lock that decides when the Solo node starts; kept by W9.11 |
| W9.10c | idempotency: an atomic pending claim in the command ledger; Copilot resume as unknown (#479) |
| W9.10d | v2 Tier 1: the saga profile, whole-suite claims, the declared-difference blocks, a sharded and blocking v2 job |
| W9.10e | v1 leaves saga items alone: the owned set into `sync-board`, no inline merge, v1 intake off for `v2Plans`, a `Loaded` flag |
| W9.10f | the sweep's item steps on v2 (settle and the master-CI watch, merge pickup, demote, clear hold, idle release, park expiry) |
| W9.10g | the sweep's plant-wide steps on v2 as recurring jobs (reap, renew, board guard, QC sync, replenish, backlog-low); faster reconciler |
| W9.10h | producers for the saga's inputs (park and resume, close, reopen, label, budgets, merge facts, start refused, Depends on) |
| W9.10i | the state comment projection wired (the rollback path) |
| W9.10j | session offers and starts from the saga and the scheduler; ledger executions subtracted from the plant's counts |
| W9.10k | plant state that survives a restart; the plant, digest, patrol and manager fed from saga state |
| W9.10l | the gateway's own rule-script calls and v1 fallbacks: the inventory and an architecture test |
| W9.10m | adoption as reconciliation: the mapping from v1 state to saga state, at every start |
| W9.10n | re-adoption fencing after `FACTORY_V2_OFF`: a new generation per item, ledger slots cleared |
| W9.10o | v2 health and fault handling: the status block, retry classification, save and outbox, `/ready` only where v2 acts |
| W9.10p | the app's uat and prod stay with v1's `AutoPromoter`; the train acts for tdd only |
| W9.10q | the item verb fallbacks closed (added by W9.10l: the inventory found five that no row owned): the red-head and migration checks of `complete`, `open-pr` and `item-pr` for `copilot/*` branches and owner tasks, the merge verb's refusal counter and migration-number collision; `IItemV1` removed |

**W9.9 is resized to L and split** (one issue, #361, three pull requests): W9.9a the router takes the gateway's own calls; W9.9b
adoption wired as a reconciliation; W9.9c the switch, its settings checklist, the drills and the cutover gate. **W9.11 is split** (one
issue, #363): W9.11a the oracle and the tests; W9.11b the removal, which keeps the lease and the `AutoPromoter` for uat and prod; W9.11c
the release check, `CLAUDE.md`, the handoff and the status fields. **New after the cutover:** W9.12 (the train takes the app's uat and
prod after the W5 gate) and W9.13 (multi-repository). **W8:** W8.0's default is the tdd-only rollback, W8.1 is split into a and b,
W8.2b is the infrastructure (cmdemo1 session), and the in-flight pull requests merge in a stated order (W8 below).

**Gates are events,** not windows (the owner's rule: no time minimum where time proves nothing). The W5 gate, the cutover gate, the
W9.11 gate and the W8 gate are restated below as lists of events, each observed and recorded on its epic.

**Owed evidence** (default 2 above). The gates of W2 to W7 were recorded in a reduced form or not at all: W2 had no database stop and no
leader election (Solo); W3's command parity became "v1 unaffected" (#471 was never built); W4 left the reconciler out of the shadow run;
W5, W6 and W7 have no gate comment. What tdd and CI can produce is owed at the cutover, and the rest is dropped with this record:

| Gate | Evidence | Disposition |
|---|---|---|
| W3 | v2's recorded commands matched with v1's writes | replaced by the v2 Tier 1 golden-write comparison of whole suites (W9.10d) and the measured batch; #471's report is not built |
| W4 | every board change of a window has its event in v2's history | owed: observed in the cutover batch (the reconciler runs from W9.10g) |
| W5 | live train decisions matching v1's | owed before W9.12, not before the cutover (decision 2): the W5 gate below |
| W6, W7 | the docs and feature suites green on v2 | owed before W9.9c: recorded on #265 and #266 once W9.10d's saga profile runs them |
| W2 | a database stop with v1 acting | dropped: v2 acts from the cutover, and W9.10o states what a database outage does where it acts |

**CI from now on** (section 1, rule 5, applied): the main session runs `gh workflow run Tests --ref master` now and after every merge
batch and records the result on the wave's epic; `V2_BLOCKING` becomes `true` after the first green master run that W9.10d produces;
#542 to #546 merge one at a time, each rebased on the one before, after the main session lands `V2VerbRouter.Below` and its test helper.

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
     and a successful restore (v1: `plantSnapshot.restore.how`; v2: the lease holder's Solo node running, no second node in
     `wolverine_nodes`, and the sagas resumed), and no unplanned restart. Each gate adds its own comparison.
   - From the revision of 2026-10-30 the remaining gates are lists of events, not windows of hours: a gate ends when every event in
     its list has been observed and recorded, and merges wait only while a drill or a measured batch is running.
   - Subagents keep working during a window; their pull requests wait for its end to merge.
   - Pull requests from outside the plan (operator-session fixes) avoid the hot files of the running wave; during stage C, `Program.cs`
     is frozen for them.
7. **Every pull request:** the pre-commit review by a reviewer subagent, Tier 1 green, then merge and release to tdd with no approval
   stop (owner, 2026-10-26). Flaky jobs are re-run at most twice, then read. The tasks the earlier plan marked "owner review" (section 2)
   get a reviewer brief with a security focus, and the main session records each in the handoff for the owner to read afterwards.
8. **Kit-owned files stay untouched:** `build.yml` belongs to the demo-environment kit (only its tests job is local). New checks go into
   `tests.yml`. The Dockerfile copies every `*.csproj` before `dotnet restore`.
9. **One engine switch per environment** (refactor plan rule 3), default v1. tdd goes to v2 shadow mode at W2 and to v2 at the cutover
   (W9.9), each a `deploy.overrides.json` change that merges on green like any other pull request. There are no per-capability flags,
   with one deliberate exception (owner, 2026-10-28, confirmed 2026-10-30): `intake.v2Plans` names the plans the engine carries where
   it acts, so a plan joins v2 by a settings change (docs and feature at the cutover, hotfix and redeliver with W8.1b).
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
| At least one replica for the tdd gateway (timers and recurring jobs need a running process; Solo on the lease holder since 2026-10-30, not Balanced) | cmdemo1 session | W2.2 (done) |
| `FACTORY_V2_OFF` in cmdemo1-system's infrastructure code (tdd fworker settings), unset; **overdue** (2026-10-30: absent from `system.json` and `infra/own/main.bicep`) | cmdemo1 session | W9.9c (After column); drilled there; removed with its reader in W9.11b |
| `projects_v2_item` delivery (organization or App webhook) | owner (admin) | not needed: default 4 of 2026-10-30, a 1 to 2 minute reconciler poll (W9.10g) |
| `DeploymentCreate` on tdd for cmdemo1-ui for the gateway's Octopus account (decided 2026-10-30) | cmdemo1 session | W8.2b, before the W8 gate |
| S1 as the tdd database's fallback tier (decided 2026-10-30) | cmdemo1 session | only if the cutover batch saturates S0 |
| The gateway's read access to cmdemo1-system; the `system.json` fields for policy, toolchain and WIP limit (kit and cmdemo1-system) | owner, Platform Hub | W1 (falls back without), W6 and W7 |
| Delivery-speed cross-repo pieces (merge queue ruleset, workorders pipeline, kit `release.yml`, docs-only overflow) | owner, Platform Hub | W7.10, each optional, off the critical path |
| The incident rollback policy (decided 2026-10-30): automatic in the app's tdd only; in its uat and prod open the incident, hold the trains and escalate, no automatic rollback | owner | W8.0 (answered) |

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
recording sink, v1 acts) and `v2Environments` at the cutover W9.9. `FACTORY_V2_OFF=1` (cmdemo1-system) puts the environment back on
v1 with no shadow, in a new revision and without an image change; reverting the switch's pull request does the same through a release.

**What "v2 acts" means: the hybrid composition** (owner, 2026-10-28; restated 2026-10-30). Where the environment is in
`v2Environments`, one gateway process holds the lease and runs both: v2 (the Wolverine host in Solo mode, the sagas, the scheduler,
the trains, the routed verbs) and what is left of v1 (the rule scripts of verbs not yet routed, the sweep for items v2 has not
started, the plant, the `AutoPromoter` for the app's uat and prod). The split is by item and by verb, decided in code, not by flags:
- **Items:** an item whose `WorkItemSaga` is open is v2's; v1's sweep, intake and inline merge leave it alone (W9.10e). The saga
  starts items of the plans `intake.v2Plans` names (the one per-plan exception to "no per-capability flags", section 1, rule 9);
  items of other plans, and items started before the switch and not adopted, are v1's. At the cutover W9.9b adopts every open item,
  so after it v1 owns none.
- **Verbs:** a verb with a route in `V2VerbRoutes` is v2's, whoever calls it: a session through the verb endpoint or the gateway itself
  (W9.9a). A verb with no route still runs on v1; W9.10l's list of them must be empty before W9.11.
- **Shared resources have one owner each:** capacity is the `CapacityScheduler`'s ledger, with the plant's own sessions counted once
  (W9.10j); merges of saga items are the merge train's; delivery to the app's tdd is observed by the train; promotions to the app's
  uat and prod are v1's `AutoPromoter`'s until W9.12 (decision 2 of 2026-10-30).
- **One actor:** the lease (a SQL application lock from W9.10b) fences both engines; the Solo node runs only on its holder (W9.10a).
- **The rollback path:** v2 writes the state comment in the format v1 reads for every decision that changes what v1 reads (W9.10i), so
  `FACTORY_V2_OFF` or a revert hands every open item back to v1, and re-adoption fences what happened meanwhile (W9.10n).
- **Home repository only** until W9.13.

**What stays from the side-by-side design, and why:**
- **S1, the verb router** (`VerbPipeline`): the verb endpoints call one engine interface, composed per environment. It keeps the
  agents' contract in one place and lets Tier 1 run either engine.
- **S2, the command port:** every external side effect of v2 is one integration command. Behind it are the outbox handlers, or the
  recording sink that shadow mode and the golden-write comparison read.
- **S3, the event ingress:** webhooks and the verbs' events enter through one interface, which in shadow mode also feeds v2.
- **The state comment as v2's projection, in the format v1 reads:** this is how a revert or the kill switch hands every open item back
  to v1 with nothing lost. Wired where the engine acts by W9.10i (the audit of 2026-10-30; see "W9.10i as built"); it stays through the W9.11 gate, because until then a
  revert of W9.11b is the rollback.

**What went, because it existed only for v1 and v2 side by side on one environment:** the `engine:v2` label and the intake labelling;
v1 skipping or forwarding v2 items (W6.4a to c, W6.5); the separate runtime and offer filter for v2 items; S4 as a seam in v1's code,
S5 (timers) and S6 (the loopback route); v1's side effects through the outbox; v1 asking v2's scheduler (W7.5); moving v1's merge onto
C# (in W7.2); the per-capability flags (`commands`, `ingress`, `delivery`, `scheduler`, `merge`, `incidents`); the expand-then-contract
steps for uat's and prod's lag; v1's promotion off-switch (W5.3); the per-item moves to v2 (W7.9, W9.9's QC items) and the drain of
`engine:v1` items in W9.11. **Came back:** `intake.v2Plans` (2026-10-28, the hybrid composition) and Solo mode on the lease holder
(2026-10-30, the leadership decision: the lease is the fence, Balanced mode is not the target).

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

**W2.2 as built, and the revision of 2026-10-30.** The host runs in **Solo** mode (`wolverine.mode` solo), not Balanced: the GitHub-ref
lease is its only single-actor fence, and the coordinators use local sequential or partitioned queues, not `GlobalPartitioned`. The node
is started whatever the lease state, so on every release the old and the new process each run a Solo node for up to the hand-over
time; W9.10a fixes that, and W9.10b moves the lease into SQL. Balanced mode with Wolverine's leader election (#464) is closed as
superseded by the lease decision. The W2 gate's "v2's node elected leader" is replaced, from the cutover, by checks Solo can show: the
lease holder's node running, no second node in `wolverine_nodes`, and a scheduled wake that fires after a restart.

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
- The item "command or timer lost between save and outbox" moved to W9.9 (#361), and on 2026-10-30 to W9.10o.

**Gate, as events** (restated 2026-10-30; it replaces "a tdd shadow run of 4 hours or 3 app releases", which was never recorded on
#264). It is the precondition of W9.12, not of the cutover: the
app's uat and prod stay with v1's `AutoPromoter` until it is recorded (decision 2). Every event is read from tdd's shadow comparison and
counts only decisions made after the watcher's replay was applied (#500, defect 6):
1. at least **3 live app releases** after the replay, each with a train decision for uat (and prod, where one is due) that matches v1's
   action or is a declared difference: the newest qualifying release only (#496, accepted), and any other difference declared on #264
   with its cause; an unexplained difference fails the gate;
2. each timer kind of the train fires at least once: a Sign-off wait, a health window, a retry and the safety wake;
3. one release restart of the gateway during a train's wait, after which its scheduled `TrainWake` fires (#500's open question; the
   evidence closes #500);
4. "would hold" decisions of the `dependencies-delivered` rule reported in the shadow comparison (the rule is off in shadow), each
   explained.

The gate is recorded on #264 with the release numbers and the comparison rows.

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
does not act in tdd yet. *Not recorded (2026-10-30):* the v2 Tier 1 job never starts a saga (intake off, `v2Plans` empty, no
declared-difference blocks), so this gate is recorded on #265 once W9.10d's saga profile runs the docs-only scenarios of `auto` and
`owner`; it is a precondition of W9.9c.

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
  (`Verbs.Sweep.ps1`; no task took it until the revision of 2026-10-30, which gave it to W9.10e, done: `skip_items`); writing the `engine:v2` label (a
  projection, W6.3). (The feeder, the decision verbs and the demotion and
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
  projection of the saga's own transitions (v1's verbs write it; W9.10i wrote it later), and the unpark and park verbs through the saga (they have
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
**Gate:** the feature-plan suites green in the v2 Tier 1 job with golden writes (CI). No window. *Not recorded (2026-10-30):* as for
W6, recorded on #266 once W9.10d's profile (`v2Plans` [docs, feature]) runs the feature scenarios; a precondition of W9.9c.

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

**W7.4, the Octopus project variables as built (#341, `deploy/octopus/main.tf`).** Only the five project variables of host admission; the Tentacle tags are verified separately, and no gateway code or setting changed (tdd stays in the shadow run). `Fworker.PerHostCapacity` 2, `Fworker.PerHostBurst` 2, `Fworker.ReserveMb` 2048, `Fworker.MinDiskMb` 4096 and `Fworker.HostFullWaitSeconds` 30 are in `local.settings`; they were checked against `runtimes.octopus` of `config/instance.json` (`perHostCapacity` 2, `reserveMb` 2048, `minDiskMb` 4096, `hostFullSeconds` 30), the tdd entry of `config/deploy.overrides.json` (`perHostCapacity` 2) and the defaults of `start-worker.sh`. One value differs in the letter and agrees in effect: the gateway's `perHostBurst` is 0, the script's limit is max(burst, capacity), so 2 is the same limit. `Fworker.FootprintMb` is not set (the gateway measures it; without it a start above the capacity is refused, as intended while there is no burst). Until the project is applied (`terraform apply` by hand with an Octopus API key) the script checks nothing, as before W7.4; after it a Start worker run on a host with two running `fw-*` containers ends with `host-full` and exit 75, which `OctopusRunbookRuntime` reads as a wait since W9.10h. Check afterwards: the project shows the five variables and the next Start worker log says "Slot 1 of 2 (guaranteed): no headroom test".

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
  factory sets it to tdd (the owner's decision of 2026-10-30; W9.9c sets it).
- **Not here (documented gaps):** the plant starts the sessions, on a runtime and host of its own choosing; the slot says whether a session may be offered and counts against the runtime's limits, but the plant does not honor its runtime or host, and what the plant's
  manager runs counts beside it (a ledger session was counted twice until the saga started sessions itself: W9.10j, see its notes below); a v1 owner session that already holds the item when a hold begins is not stopped (the hold keeps the next offer and the ledger honest); `StartRefused`
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
| W9.10a | **Solo node only on the acting holder** (on today's GitHub-ref lease, a live defect of the shadow run): `WolverineHostService` starts the node when this process is Acting (lease taken and the predecessor's snapshot seen), not at `StartAsync`; on supersession it stops the node (drain, release of ownership) **before** the hand-over saves the snapshot and lets the lease go, so the successor's `StartSoloModeAsync` never releases rows of a running node; the header comments corrected (Solo, not Balanced; not serverless) | `gateway/Composition/WolverineHostService.cs`, `gateway/Leader.cs` (hand-over order), `tests/Fworker.Workflow.Tests` (hand-over case) | W9.0 (now) | S |
| W9.10b | **leadership: the lease as a SQL application lock** (owner, 2026-10-30): where a database is configured, `sp_getapplock` (session owner, on a dedicated connection) plus a `factory.Leadership` row (epoch, holder, takeover request) is the single-actor fence; the newest process still wins (it writes a takeover request, the holder stops its node as in W9.10a, saves the plant and releases; the newcomer takes the lock, increments the epoch, then starts the node and the recurring jobs); a holder whose connection drops has lost the lock and acts no more; `ILeadership.MayAct` and W0.1's epoch read from it; the GitHub-ref lease stays as the arbitration with a process that has no database (v1, the kill switch, uat and prod) until W9.11b; W9.11 keeps this lease | `gateway/Leadership/*`, `gateway/Leader.cs`, the next free DbUp script, `tests/scenarios/leader.ps1` (hot: the main session) | W9.10a | M |
| W9.10c | **idempotency: an atomic pending claim** in the integration-command ledger (key, owner node and epoch, expiry, one statement before the side effect); a second handling of the key waits or ends; an expired claim is settled by looking up the effect (runbook runs by worker name, deployments of the release, comments by journal key, the pull request's merge state); a Copilot resume that times out is unknown, not failed, and is reconciled from the pull request's comments and sessions; the Copilot reconciler writes the `copilot_task` owner record and its `Settle` is conditional; reservations durable and the task lookup checked against the real API (#479) | `src/Fworker.Integrations/Commands/*`, `gateway/Runtimes/CopilotCloudRuntime.cs`, the next free DbUp script | W9.0 | M |
| W9.10d | **v2 Tier 1 that starts sagas:** `-Engine v2` takes the suite's own environment name per claim (tdd for `copilot`, `promote`, `envscope`); a v2 test profile per claim or suite (`intake.enabled`, `intake.v2Plans` [docs] then [docs, feature], `scheduler.featureSlots`, `mergeStrategy`, and a `wolverine` block with `scheduledJobSeconds` at its floor and the first run at 0); the W6.6, W7.3 and #496 blocks in `golden/v2-differences.txt`; a scenario with `gatewayMerge` true that asserts one merge write; the v2 job sharded as a matrix like `tier1` (`run-all.ps1 -Engine v2`); claims converted to whole suites, and the v1-only suites declared with a reason (`peer-approved`, `qc-gate`: the dropped policy; what `qc-gate` covers of the QC gate is checked against `qc` and moved where it is not); #415's copilot-2 and operator waits fixed, the other flakes quarantined under open issues, #386's quarantine moved to an open issue; SQL-backed tests reported skipped without `FACTORY_TEST_SQL`; the committed `__pycache__` file removed; `V2_BLOCKING` true after the first green master run | `tests/scenarios/{run,run-all,lib}.ps1`, `tests/scenarios/v2-claims.txt`, `tests/scenarios/golden/v2-differences.txt`, `.github/workflows/tests.yml`, `tests/ci/*` (hot: the main session integrates) | W9.0 | L |
| W9.10e | **v1 leaves saga items alone:** the Sweeper passes the owned set to `sync-board` (`skip_items`) and `Verbs.Sweep.ps1` skips those items in its item loop and tail steps (settle, merge pickup, the PASS merge, park, demote, auto-unpark); `validation-verdict` makes no inline merge for an owned item; v1's `Invoke-Intake` skips the plans `intake.v2Plans` names (a gateway argument, not a new flag); `V2Ownership` gets a `Loaded` flag (until the first successful read the sweep acts on no item and incremental passes wait) | `gateway/Sweeper.cs`, `gateway/Ports/WorkItemPort.cs`, `Verbs.Sweep.ps1` (hot), `Verbs.Pr.ps1` (inline merge), `tests/Gateway.Tests` | W9.10d | M |
| W9.10f | **the sweep's item steps on v2**, as saga and train rules, each with a v2 claim: settle and the master-CI watch (D5's master-green, master-broken and master-timeout parks and their auto-unpark), the settle re-arm, merge pickup (`ItemMerged` from a person's merge completes the item), demotion of a PASS whose pull request was closed, clear-hold when approved, idle-owner release, park expiry; where v2 acts, `sync-board` does none of these for owned items | `src/Fworker.Workflows/WorkItem/*`, `Merge/*`, `Delivery/*` (settle) | W9.10e, #542 and #545 merged | L |
| W9.10g | **the sweep's plant-wide steps on v2** as recurring jobs (leader only), each with a v2 claim: claim-ref reap and gap renew, board guard, QC sync through `QcService`, replenish, the keeper's backlog-low (`design_waiting`); the board reconciler at 1 to 2 minutes (default 4) with its GitHub budget measured, its seen state durable or a lookback republish of closed cards with no `MergedItems` row, and `ItemMerged` derivation retried hourly instead of dead-lettered; a retention prune (`ItemHistory`, `Transcripts`, the shadow tables) and the transcript cap | `src/Fworker.Workflows/Reconcilers/*`, `gateway/Composition/RecurringJobSlots.cs` (hot), `gateway/Keeper.cs` | W9.10e, #544 and #546 merged | L |
| W9.10h | **producers for the saga's inputs:** `ParkWorkItem` and `ResumeWorkItem` from every park and unpark path (the admin endpoint, the runtime adapters, the credit-guard park); close, reopen, label and card-move inputs from the ingress inbox, with `ProjectionEcho` consulted; `BudgetSpent` from launches and session ends; `MergeFactsRead` after the merge; `StartRefused` from the start command, with `host-full` (exit 75) mapped to a non-failure like "no capacity" (execution refunded, no failure counted); `Depends on` admission in v2 intake (D1); `LabelProjection` wired or deleted; the `V2VerbFeed.cs` comment fixed | `gateway/Ports/IngressInbox.cs`, `gateway/Endpoints/ItemAdminEndpoints.cs`, `gateway/Runtimes/*` (park and start outcomes), `src/Fworker.Workflows/Intake/*`, `src/Fworker.Integrations/GitHub/Projection.cs` | W9.10e | L |
| W9.10i | **the state comment projection wired** (the rollback path): `StateCommentProjection` registered where v2 acts; every `WorkItemDecided` that changes what v1 reads writes v1's format (park and unpark, including merge-blocked, fix-forward-exhausted and held; step and column; `verdict.processed`; the merge record with `awaiting` and `done_at`; handed-back); `PromoteRelease` and `AnswerSignOff` write `merge.promoted.<env>` and `signoff_wait_since`; a v2 scenario: an item mid-flow, the kill switch on, v1 makes no duplicate or stale write | `src/Fworker.Integrations/GitHub/Projection*`, its registration (through the main session) | W9.10h | M |
| W9.10j | **session offers and starts from the saga and the scheduler:** after `SlotGranted` the saga sends `StartRuntimeSession` with its arm id; offers of saga items come from saga state, not from the sweep's `PlanV2Offers`; `RuntimeManager` subtracts ledger-granted executions from its outside counts (concurrency and the day budget); the requested runtime is where the session starts; a failed dispatch (host not Running) settles like "no capacity" and offers wait for the host; failed stops are retried or reconciled with host reports; a scheduler reaper for items left in flight with no `ClearItem`; the manager seeds its ledger share from the scheduler's persisted day count and slots, so a restart does not bring the double count back; **two pull requests: the first (unit and wiring tests) does not close #562; the second carries the Tier 1 scenario "a v2 scenario runs more sessions than half the day budget"** (scheduler on with a fake runtime, a gateway restart mid-run) | `gateway/Runtimes/{RuntimeManager,RuntimeCommandService}.cs`, `gateway/Ports/CapacityPort.cs`, `src/Fworker.Workflows/Capacity/*`, `WorkItem/WorkSteps.cs` | W9.10h | L |
| W9.10k | **plant state that survives a restart; the plant fed from saga state:** assignments and offers (the ids the workers' item tokens name), owners, bot holders and drains in SQL (the next free DbUp script), corrected at start from `WorkItems` and the claim refs; an adapter that builds the plant's sweep-shaped input from saga state and the reconciler's cards (owners, parks, the release queue from the Deliver step, pins, priorities, runtime labels); `DigestBuilder` and the patrol read `V2ItemViews` (parked, lease-overdue, held); `manager-action` sends `ResumeWorkItem`, `ReleaseOwner` and `Requeue` to the saga; the release check counts saga items in flight; one manager-suite v2 scenario for each | `gateway/Plant*.cs`, `gateway/PlantSnapshot.cs`, `gateway/Messaging.cs` (`DigestBuilder`), `Manager.cs`, the next free DbUp script | W9.10j, #543 merged | L |
| W9.10l | **the gateway's own rule-script calls and v1 fallbacks:** the inventory of every `Runner.RunAsync` call outside `gateway/Verbs` (about 25: `PlantHooks` claim-item and expire-claim, the runtime adapters' park-item and `ReleaseClaim`, the credit-guard park, the unpark and resettle endpoints, `ItemPrLookup`, `sync-board`, `FeatureChecksJob`, `BaseUpdate` and `PlantTicker` journal (#477), `Leader`) and of every `IItemV1` fallback (QC accept, the red-head and migration checks of `complete`, `open-pr` and `item-pr` for `copilot/*` branches and owner tasks, `auto-promote`, the merge verb's refusal counter and migration-number collision), each with the row that gives it a v2 owner, in `verb-inventory.md`; an architecture test whose allow-list is that inventory and must be empty before W9.11 (the `peer-approved` path excepted, decision 4); one `IItemLock` for every port and `JournalLedger` (the file lock while scripts exist) | `docs/design/verb-inventory.md`, `tests/Gateway.Tests/Architecture*`, `gateway/Ports/*` (the lock) | #546 merged | M |
| W9.10m | **adoption as reconciliation, the mapping:** a pure mapping (`src/Fworker.Workflows/Adoption`) from v1's state (state comment, claim refs, card, pull request) to saga state: plan (docs or feature), step, park and budgets (awaiting-checks, inspector-escalated, pass-without-auto-merge, master-broken, master-timeout, relaunch budgets), step lease and scheduler slot from a live owner claim, QC row, `MergedItems` row, merge-train line, the delivery train's current release, `signoff_wait_since` and pending Sign-offs; the rule: at every start an upsert in which the state comment wins when its revision is newer; the mapping table added to this plan; one `Fworker.Workflow.Tests` case per mapping | `src/Fworker.Workflows/Adoption/*`, `tests/Fworker.Workflow.Tests/Adoption*`, this plan (the table) | W9.10i | M |
| W9.10n | **re-adoption fencing after `FACTORY_V2_OFF`:** every command and timer carries the item's generation (or an engine epoch); re-adoption after a v1 interval opens a new generation for every item v1 touched, so older inputs end as `NotFound`; the ledger slots of those items are cleared; saga rows of items v1 completed are closed; train rows built before the interval are validated or reset; a v2 scenario: an item v1 merged during the switch is not touched by v2 afterwards, and no stale timer acts | `src/Fworker.Workflows/Adoption/*`, `WorkItem/*` (generation), `Capacity/*` (slot cleanup) | W9.10m | M |
| W9.10o | **v2 health and fault handling:** a v2 block in `/api/v1/status` (dead letters by type; incoming, outgoing and scheduled envelopes with the oldest age; the last run of each recurring job; ledger and train summaries; saga counts by step and age), instead of #462 and #463 (default 3); transient SQL errors (-2, 1205, 4060, 40197, 40501, 40613, 49918, 10928, 10929) and the conflict exceptions retryable on long schedules, only `DbUpdateConcurrencyException` and duplicate keys caught as conflicts, the delivery retry bounded (#500); the saga save and the outbox in one transaction (`WolverineFx.EntityFrameworkCore` on `FactoryDbContext`) or a re-arm job for open sagas with no timer or an overdue `LeaseDue` (from W5.1 and #496); `/ready`'s schema gate only where v2 acts (in shadow the schema shows in status only); a malformed `FACTORY_DB_CONNECTION` treated as not configured; the `PromoteRelease` key carries the request's sequence and the ledger keeps successes for a retention period; merge attempts count only answers about the pull request | `gateway/Composition/Wolverine*` (hot: the main session), `gateway/Endpoints/Status*`, the saga stores, `gateway/Configuration/Database*`, `src/Fworker.Workflows/WorkItem/Plans/DocsSteps.cs` | W9.0 | L |
| W9.10p | **the app's uat and prod stay with v1's `AutoPromoter`** (decision 2): where v2 acts, `AutoPromoter` keeps promoting the app to uat and prod (#545 stops it there; this row narrows the stop to tdd, where the lifecycle deploys anyway), and the train's uat and prod decisions stay in shadow (recorded and compared, no `PromoteRelease` or `AnswerSignOff`); the train acts for tdd only (observes the lifecycle's deployment, publishes `DeliveredTo(tdd)`) | `gateway/AutoPromoter*`, `gateway/Ports/DeliveryShadowPort.cs`, `src/Fworker.Workflows/Delivery/DeliveryShadow*` | #545 merged | S |
| W9.10q | **the item verb fallbacks closed** (added by W9.10l, whose inventory in `docs/design/verb-inventory.md` found them with no owner): in `Fworker.Workflows` the completion of Development (the red-head ruling of the CI rule and the migration check, `ItemVerbs.Flow.cs`, ported from `Verbs.Item.ps1` on top of `ChecksReadModel`), `open-pr` for a `copilot/*` branch (the takeover of the draft) and `item-pr` for an owner task (`Resolve-OwnerPr`) in `ItemVerbs.Pr.cs` and `.Read.cs`, and in the merge verb the model ladder's refusal counter (`Add-Refusal`, which v2 refusals do not feed) and the migration-number collision (`Get-MigrationCollision`, `Verbs.Merge.ps1:190`, which the script checks on every merge); then `IItemV1`, `ItemDelegated` and `ItemVerbsV1` are deleted and the five `fallbacks` entries leave the inventory, each with a `Fworker.Workflow.Tests` case that the same input gets the script's answer | `src/Fworker.Workflows/WorkItem/Verbs/*`, `src/Fworker.Workflows/Merge/*`, `gateway/Ports/ItemVerbsPort.cs`, `docs/design/verb-inventory.json` | W9.10l merged | M |
| W9.9a | **the router takes the gateway's own calls:** `Runner.RunAsync` asks `V2VerbRouter` first for every verb that has a route where v2 acts; the per-pull-request bridges (`DeliveryVerbBridge`, `ServiceVerbCalls`, `Runner.V2Own`) removed; W9.10l's allow-list down to the declared exceptions | `gateway/Runner*.cs`, `gateway/Verbs/*` (hot), the bridges | W9.10l, W9.10q, W9.1 to W9.8 merged | M |
| W9.9b | **adoption wired as a reconciliation:** where v2 acts, at every start, W9.10m's mapping with W9.10n's fencing over every open item and pending Sign-off (W5.3); `DeliveredTo` backfilled from Octopus history and `MergedItems` from the merged pull requests (`mergeQueueDepth` 2 in tdd); train rows built in shadow validated or reset; the result in the v2 status block (adopted, upserted, skipped, by reason) | `gateway/Composition/Adoption*`, `src/Fworker.Workflows/Adoption/*` (wiring) | W9.9a, W9.10m, W9.10n | M |
| W9.9c | **the switch, the drills and the cutover gate:** `deploy.overrides.json` per the settings checklist below; the readiness checklist below; the kill-switch drill, the overlap drill and the measured batch of the cutover gate; the result recorded on #268 | `config/deploy.overrides.json`, `docs/HANDOFF.md` (the gate's record) | W9.9a, W9.9b, every W9.10 row; the W6 and W7 gates recorded (#265, #266); #500's wake-after-restart evidence from tdd; `FACTORY_V2_OFF` provisioned in cmdemo1-system; the claim list equal to `run.ps1 -ListSuites` less the declared v1-only suites; `V2_BLOCKING` true | S |
| W9.11a | **the oracle and the tests:** the goldens frozen as C#-owned fixtures; the parity tests that run the scripts retired (`MergeVerbParityTests`, `ServiceVerbParityTests`, `BoardVerbParityTests`, the script part of `tests/smoke.ps1`); v2 the suites' default engine; the v1-only suites removed (`peer-approved`, `qc-gate`, `merge-queue`, `snapshot`); `leader` kept, on the SQL lease | `tests/*` (hot: the main session) | the cutover gate | M |
| W9.11b | **remove v1:** the sweep engine, the server rule scripts (an inventory of `plugin/scripts/lib` files to delete or keep: the client scripts workers download stay), `RunnerPool`, shadow mode, the v1 side of S1, the snapshot (the plant is in SQL from W9.10k), the ref part of the lease, the `FACTORY_V2_OFF` reader together with its cmdemo1-system entry; the state-directory files (`refusals.json`, `qc-items.json`, `seen.json`, `project-meta.json`, `item-N.lock`) into the database or memory. **Kept:** the SQL lease (W9.10b), the `AutoPromoter` for the app's uat and prod (until W9.12), the v1-format state comment through the W9.11 gate (a revert of this row is the rollback until then). fworker's own uat and prod gateways (no database, inert on the board) are out of scope; the image they would get after this row needs a database to act, so they stay on their last v1 release until the owner says otherwise | many (one subagent, last; two pull requests) | W9.11a; W8.2 (#549) merged (both edit `GatewayAuthMiddleware`) | L |
| W9.11c | **the release check and the documents:** `CLAUDE.md`'s release step (the lease's fields instead of `leader.role`, the plant restore from SQL instead of `plantSnapshot.restore.how`), a Wolverine section in `docs/HANDOFF.md`, the `/ready` and status fields, the operator skill's reading of `waitingFor`; the W9.11 gate | `CLAUDE.md`, `docs/HANDOFF.md`, `plugin/skills/factory-operator/*`, `gateway/Endpoints/{Health,Status}*` | W9.11b | S |
| W9.12 | **the train takes the app's uat and prod:** after the W5 gate (restated above) is recorded, one overrides change lets the train act for uat and prod and the `AutoPromoter` is removed; the `dependencies-delivered` rule then holds for those environments too | `config/deploy.overrides.json`, `gateway/AutoPromoter*`, `src/Fworker.Workflows/Delivery/*` | the W5 gate recorded; W9.11b | S |
| W9.13 | **multi-repository after the cutover:** #452's catalog verb in C# with the App token, `catalog.enabled` for tdd, the per-owner App installation (W3.0, H6), App installations and webhooks per repository (owner actions), intake per repository; #455 and #456 are its gate | `src/Fworker.Integrations/Catalog/*`, `src/Fworker.Integrations/GitHub/GitHubTokenProvider.cs`, `src/Fworker.Workflows/Intake/*`, `config/deploy.overrides.json` | the cutover gate; W9.11b | L |

Order: W9.10a, W9.10c, W9.10d, W9.10o and W9.10l (when #546 is in) can start now, three at a time; W9.10b after W9.10a; W9.10e after
W9.10d; then W9.10f, W9.10g and W9.10h; W9.10i and W9.10j after W9.10h; W9.10k after W9.10j; W9.10m after W9.10i, W9.10n after W9.10m;
W9.10p when #545 is in; W9.10q after W9.10l. (An After cell that names a pull request means that pull request merged: #542 is W9.3's issue #355, #543
W9.6's #358, #544 W9.1's #353, #545 W9.4's #356, #546 W9.8's #360, #549 W8.2's #350.) W9.9a to W9.9c in that order once every W9.10 row is merged. After the cutover gate: W9.11a to W9.11c beside W8, then
W9.12 when the W5 gate is recorded, and W9.13.

**W9.10b as built (#556).** The owner's interpretation of 2026-10-30 stands: the SQL lock is added beside the GitHub-ref lease, which stays (and is deleted in W9.11b) and keeps arbitrating with every process that has no database.
- **The lock** (`gateway/Leadership/SqlLease.cs`): `sp_getapplock` on the resource `fworker:leader`, mode Exclusive, owner Session, timeout 0, on a connection of its own (pooling and connection resiliency off, so the lock lives exactly as long as the
  connection: a holder that dies or loses its network has lost it). Script `170_AddLeadership.sql` (the reserved range 170-179) builds `factory.Leadership`, one row (`Id` 1): `Epoch` (one more each time a process takes the lock), `Holder` (the process name the
  ref lease records too), `AcquiredAt`, and the takeover request (`TakeoverBy`, `TakeoverAt`). The scripts are applied before the lock is first tried (the row is a table of the schema; the host then finds nothing to apply).
- **Where it exists:** only where `FACTORY_DB_CONNECTION` is set, the engine is shadow or v2 (the Wolverine host runs) and the ref lease is on. v1 under the kill switch, uat, prod and every process without a database build no `SqlLease`: nothing of
  their behaviour changes, and `leader.sql` is absent from their status.
- **Where the engine acts (v2)** the lock is part of acting: `ILeadership.MayAct` is true only while the lock is held, and a starting process takes the lock before the ref (a newcomer that finds it taken writes a takeover request and refreshes it at every try;
  the newest process wins, a request that is not refreshed for four checks, at least 20 s, is stale). The holder looks at its lock every `timing.leaderSqlCheckSeconds` (5): a dropped connection (the session no longer holds the lock, or the call fails: any failure
  counts as lost) or a fresh request from another process ends its term in the order of the hand-over: the node leaves (W9.10a), the final snapshot, the streams end, the lock is released. The newcomer takes the lock with the next epoch, then the ref
  (the predecessor's snapshot is already written), restores, and only then starts its node and the recurring jobs. A process that lost the lock stands by like a superseded holder and takes the lock again when it is free, and takes the ref at once when the ref's
  holder is the one that last held the lock, or is this very process, while the lock is free (a holder that let both go, a newcomer that died between the lock and the ref), and otherwise only when the ref was released or ran out.
  The restore still waits for the predecessor's final snapshot (a holder that stepped down wrote it before it let the lock go; a really dead one costs `leaderHandoverSeconds`, 90). Where the engine acts the ref is never written without the lock: the start
  is lock, then ref, and if the lock is gone once the ref is written (`AcquireAllAsync`) it goes back to the lock, passively, before anything is restored or started. A holder whose look at its lock fails keeps the connection until its step-down ends
  (node left, plant saved), so no other process can take a lock the server still holds while that node runs; the look is repeated once before the lock counts as lost, and a failing database is asked with a growing pause (up to a minute). A standby never writes a request, and a newcomer whose request was overwritten by a newer one concedes.
- **In the shadow run** (v1 acts, the node is inert) the lock gates the node only: v1 acts on the ref at once and a database outage never stops it; the node starts when the lock is held and leaves when it is lost or asked for.
- **Epoch:** `GatewayLeader.Epoch` stays the ref's (the snapshot, the fence of the rule scripts and the restore compare it); `SqlEpoch` is the SQL term and `ClaimEpoch` (SQL term where there is one) is what the claims of the idempotency ledger carry. `/api/v1/status` `leader.sql`:
  `held`, `epoch`, `fencesActing`, `acquisitions`, `lost`, `yielded`, `failures`, `lastFailure` (the exception type and the SQL error number only), `lostAt`, `previousHolder`.
- **Known limit:** when the holder's connection really drops (the process is killed, the network is cut) the server frees the lock at once, and a successor may start its node before the old holder notices at its next look: up to `leaderSqlCheckSeconds` (5) plus the 10 s command timeout of the look. The old holder's ledger claims carry its own epoch, and its rule scripts are fenced by the ref as before.

**The cutover's settings checklist** (W9.9c; every value is written in `config/deploy.overrides.json` and checked with
`config/apply-overrides.ps1` before the merge):

| Setting | Value at the cutover | Why |
|---|---|---|
| `engine.v2Environments` | `["tdd"]` | the switch |
| `engine.shadowEnvironments` | without tdd | v2 acts there |
| `intake.enabled` | `true` | v2's intake feeds the sagas; v1's intake skips the `v2Plans` (W9.10e) |
| `intake.v2Plans` | `["docs", "feature"]` | hotfix and redeliver join with W8.1b |
| `delivery.finalEnvironment` | `tdd` | decision 3 |
| `delivery.autoPromote` | unchanged (uat, prod) | v1's `AutoPromoter` keeps them (W9.10p) |
| `mergeStrategy` | `direct` | the train strategy only when the owner names it |
| `scheduler.runtime` | `octopus` | matches where the plant starts sessions (W9.10j) |
| `scheduler.featureSlots` | `true` | the ledger counts each session once from W9.10j |
| `schedule.fileOverlap.enabled` | `false` | as v1; turned on only by an owner decision |
| `gatewayMerge` | `true` | one merge writer for owned items (W9.10e) |
| `catalog.enabled` | `false` | home repository only until W9.13 |
| the board reconciler's interval | 1 to 2 minutes | `recurringJobs.boardReconcilerSeconds`, 120 in the source defaults since W9.10g (was 300); its cost is in `/api/v1/status` `v2.reconciler` |
| `wolverine.scheduledJobSeconds` | at its floor (60) | the saga's timers fire within a minute |

**The readiness checklist** (W9.9c; each line closed or re-scoped on its issue before the merge):
- #477 (C# journal): the App token done (#498); `BaseUpdate` and `PlantTicker` go through the router (W9.9a, done: the Runner asks the routes); the empty key aligned (done: an empty key is the ticks of now in both writers, `JournalKeys.OrTicks` and `Add-Journal`, so two distinct events of one kind are never one);
  the wiring test added (done: `JournalCommandWiringTests`);
- #479 (Copilot reconciler): W9.10c;
- #496 (train parity): the newest-only difference declared; the event-time question carried by the W5 gate; save and outbox in
  W9.10o; hand-written handlers accepted (#491);
- #500 (train on replay state): defects 1 to 7 fixed; the wake after a restart observed on tdd; the open-ended retry bounded in W9.10o;
  W9.10o built (the status block, the retry classification, the re-arm, `/ready` only where v2 acts, the malformed connection string, the `PromoteRelease` key with the request's number and
  the ledger retention of 30 days for successes, the docs plan's merge answers): `/api/v1/status` `v2` (`gateway/Endpoints/StatusV2Health.cs`) is on tdd in shadow too; the Wolverine retry
  for the faults of the database is `FaultPolicy` (`src/Fworker.Workflows/Faults`): 2 s, 10 s and 60 s, then 5 minutes, 15 minutes, 1 hour and 3 hours, then the dead letters; the delivery
  train's rule for GitHub and the app is bounded the same way (5 minutes, 15 minutes, 1 hour, 6 hours); the save and the outbox are repaired by the `saga-rearm` job (not one transaction).
- #437 (Copilot and Cursor day budgets from history): done, or the Copilot and Cursor runtimes off for the measured batch;
- #465 (session mailbox): not required for the cutover, since one Solo node serves every session (pushes stay node-local and are lost
  on a restart, as the messaging rule says); kept open as post-cutover work;
- #562 (W9.10j): closed by its second pull request: the Tier 1 scenario `saga: slots: ...` (the scheduler on, a fake runtime, more sessions than half the day budget, a gateway restart mid-run, none held for `runtime-budget` before the budget and the fourth item held by it), claimed in `v2-claims.txt` (profile `saga-feature`); it found and fixed the double count after a restart for a runtime whose sessions are adopted from a host report ("W9.10j, second pull request as built");
- the board reconciler's GitHub cost (#559, W9.10g): after the switch, `/api/v1/status` `v2.reconciler` read on tdd after a full hour (`requestsLastHour` of 30 passes, `estimatedRequestsPerHour`), recorded on #559 and #268 next to the estimate of 150 points an hour for the 411 cards of the project (5 pages of 1 point, 30 passes), and the GraphQL `remaining` of the gateway's token no lower than that and the other readers' use allow; the tdd shadow run
  cannot show it (the reconciler runs only where the engine acts);
- the Tier 1 claims of the plant-wide steps (#559): claimed in `v2-claims.txt` after a run on v2 (the third pull request of the row, "W9.10g, claims as built"); the v2 job must stay green on them;
- the W9.10 rows merged; the W6 and W7 gates recorded; `FACTORY_V2_OFF` in the tdd fworker settings of cmdemo1-system; the W7.4
  Tentacle tags and project variables set only after W9.10h maps `host-full` to a non-failure.

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
**W9.8 as built (#360), the board group** (`gateway/Verbs/Routes/Board.cs`, `gateway/Verbs/BoardVerbs.cs`, `gateway/Ports/BoardPort.cs`). Owner decisions of 2026-10-28 stand: hybrid composition, tdd only, the W7 scope cut. Registered only where `Engine.V2Acts`
and the parts are there (a home repository in the instance file); v1, the shadow run and `FACTORY_V2_OFF` run exactly what they ran. No schema script: `013_AddTranscripts.sql` (W9.0) is the table; the 090 to 099 range is unused.
- **Transcripts in the database.** `TranscriptStore` saves to an `ITranscriptBackend` (`gateway/TranscriptBackends.cs`): v1's `RuleScriptTranscriptBackend` (the sealed commit in the ref store, unchanged: the seal, the 900000-character cap, the tree of the last save)
  and, where the engine acts, `DatabaseTranscriptBackend` over `SqlTranscriptRepository` (`Fworker.Workflows.Transcripts`): one `factory.Transcripts` row per session, in the clear, the item qualified with the home repository (`owner/repo#N`, the column W9.0 made for it), the revision as
  the concurrency token (a second writer reads again and writes on the new state). The whole kept transcript (2 MB of lines) is stored: the 900000-character cap was the commit message's. A session the database does not have is read once from the ref store (the 14 days written before the
  move) and is saved to the database from then on; the six-hourly prune removes rows whose session started before the last `Days` days (the day of the start, as the ref store compared) and the old refs as well, so they age out. Before the schema is applied nothing is saved (the sessions stay
  dirty) and nothing is logged as a failure. `FACTORY_TRANSCRIPTS=0` still keeps them in memory only. The verbs `transcript-save`, `-load` and `-prune` are registered and keep the wire form of the scripts: `data` is the sealed `fwt1:` document, opened with the gateway's secret on save and sealed
  on load; their only caller is the store (they are `use-endpoint` verbs, refused on the HTTP route), which now talks to the repository directly. Declared differences: the save answers `{ok, ref: "factory.Transcripts/<name>", store: "database"}` (no commit and tree: there are none); a document sealed with another secret
  or of another name than the call is refused as a usage error (the script stored any ciphertext); an unapplied schema or a failing database is exit 20 `database-not-ready` / `database-error` with `retryAfterSeconds`.
- **`ingest-event` into the ingress.** `WebhookEventTranslator` (`Fworker.Workflows.Reconcilers`) is the port of `Verb_ingest_event`: the same events with the same fields for `issues`, `workflow_run`, `projects_v2_item`, `pull_request` and `pull_request_review` (the same drops, `other-repository` and
  `pr.fork`, and the quirk that a fork drops the `base.merged` made before it). The two reads it makes (the column of a labeled item, the issue behind a board card) are `HttpWebhookLookups` (GraphQL with the gateway's own credential); a failed card read is the script's Reject (exit 30 `secret-missing`, exit 20
  `github-graphql-error` or `github-rate-limited`), a failed column read leaves the column empty as in the script. The gateway's own call (the webhook route) takes the v2 answer through `Runner.RunAsync`, which asks the routes first (W9.9a; it was `Runner.V2Own`, for `ingest-event` and `journal` only), so no rule script is started for either.
  `BoardVerbParityTests` runs the real `factory.ps1 --server` on the same inputs (every kind of event, the drops, the refusals) and compares the answers whole.
- **`journal` into the history.** The verb is answered by the journal ledger (the C# `Add-Journal`, W3.2) in the answer shape of `Verb_journal` (`{ok, id, posted}`, `reason: "rate-limited"` for a waiting line inside its window, `recorded` for a routine `ci-released`); a GitHub failure is `posted: false`, as in the script (best-effort).
  The verb and the plant's journal command write through one ledger, so one lock per item holds for both. Every line that was handled (the verb's, and the command path's `WriteJournalLine`) is also a row of `ItemHistory` (`journal.<event>`, the key, posted, the reason) written once per (item, event, key) (`JournalHistory`), so "where is item X"
  shows the plant manager's lines beside the saga's decisions; a history that cannot be written is logged and never fails the line. The state comment's journal ledger stays where `Add-Journal` keeps it (the comment is the journal); `ProjectionVersions` is not written by it (W6.3's projection owns the comment's other fields).
- **`sync-board`.** The sweep is v1's until W9.11: the hybrid plant reads its offers, owners, parks and intake from it, and the Tier 1 suites call it. The v2 route answers the script's answer, untouched, and after a sweep that succeeded adds `reconciler: {outcome, cards, published, dry}`: one pass of the board reconciler
  (W4.3's, the same instance, so what it publishes it remembers) for the same scope: the `items` named, or the whole board; `dry` counts and publishes nothing. A reconciler that cannot run adds `null` and never changes the sweep's answer or exit code. What the sweep does that the reconciler does not (the offers, the board guard, the
  intake, the claim-ref reap, the QC sync, the replenish) leaves with W9.11, where "the sweep remains only as the board reconciler".

**W9.1 as built (#353), the QC gate.** Owner decisions of 2026-10-28 stand (hybrid composition, tdd only, the W7 scope cut); no flag moves QC-gated items (revision of 2026-10-26). `QcService` (`src/Fworker.Workflows/Qc/*`) answers the 14
`qc-*` verbs of `Qc.ps1` where `Engine.V2Acts`: the same fields, exit codes and refusals in the script's order, and the same writes (the offer comments on the pull request and the item, the journal lines, the record under `state.qc["<pr>"]`). It is a saga
per (repository, pull request) in the sense of the plan: the record is the saga's state and `QcRecords` its moves (`QcMoves`, the revision, the history of 20), keyed `owner/repo!N` in `factory.QcEnvironments` (W9.0, no new script). In the hybrid composition the
**work item's state comment stays the home of the record**, because v1 still reads it (the sweep's `Invoke-QcSync` inside `sync-board`, the merge gate's `ran_sha`, the rollback by `FACTORY_V2_OFF`) and the eligibility reads the item's verdict from the same comment;
every save of it is followed by the copy in the table (`SqlQcEnvironmentStore`, kept when the schema is good, never fatal), which feeds the list of "Waiting for PR QC" and the index of live items and is adopted by a verb when the comment holds no record. The
index file `qc-items.json` of the state directory is written as before (`FileQcIndex`) so the v1 sweep keeps reaching an item that left Functional Testing. Ports (`QcPorts.cs`) keep Workflows off GitHub; `gateway/Ports/QcPort.cs` makes them over
the clients of the journal command (`GitHubJournalStore`, `JournalLedger`: Get-State, Save-State, Add-Journal in C#) and a `QcGitHubClient` that reads the answers as `Invoke-Gh` does; its requests carry the rule scripts' User-Agent and, in the Tier 1 test mode, the
cause id, so the golden writes see the same sources. `QcRoutes` (`gateway/Verbs/Routes/Qc.cs`) registers the verbs (they are in no group of W9.0; `V2VerbRoutes.Build` calls it last) and `QcHub` (`Qc.cs`) reaches the persisted half of Start, Reset, Teardown, the page and
the worker's report through the same routes, so the gateway's in-memory half (lease, URL, probe, relaunch) is unchanged. Differences, all in failure cases: a verb that meets a moved state comment reads it again up to three times before it answers 11 `state-conflict`; one lock
per item in this process replaces the lock file of a machine; a journal line is written after the record is saved (two saves of the state comment, not one) and a failed line is logged and never fails the verb; the inspector's facts come through the router (`inspect-facts`,
the rule script until W9.5 answers it). `QcGate` is the merge gate of Verbs.Merge.ps1 as a pure rule (the label or the gate, then `qc-not-run` before `qc-approval-missing`, the docs-only exemption of the run) for the merge verbs of W9.3 to call; it reads the `ran_sha` that `qc-report` writes. **Not here:** the `accept` verb of a QC job (kind `qc-env`) is the item verbs' (W9.2), which calls `QcService.Accept`; until then v1's script does it on the same comment. `qc-sync` as the sweep's embedded step stays in `sync-board`
until W9.8 moves the sweep into the board reconciler; the verb of the same name is ported and answers the same records, teardowns and approvals.
**W9.6 as built (#358), the keeper and manager commands.** Owner decisions of 2026-10-28 stand: hybrid composition, tdd only, the W7 scope cut. The seven verbs (`backlog`, `amend-criteria`, `split-item`, `create-item`, `set-priority`, `link-dependency`,
`manager-action`) are answered by C# where the engine acts and the gateway has a GitHub identity (GH_TOKEN or the author App) and a home repository; elsewhere `KeeperRoutes.Register` adds nothing and v1's `Verbs.Keeper.ps1` and `Verbs.Manager.ps1` answer
as before. The pipeline in front of the route is the same for both engines (authorization, the keeper's slot and daily limit of new items, the supervisor's step budget, the mode and hourly limit and ticket of a manager action, the plant's hooks after the answer).
- **The port** (`Fworker.Workflows.Keeper`, `VerbSupport.cs` to `ManagerCommands.cs`): each verb is the script verb by verb, check by check: the same refusals (exit and code: `unknown-policy`, `owner-hold`, `protected-criterion`, `already-amended`, `criterion-not-found`
  and `-ambiguous`, `reserved-marker`, `owned`, `settling`, `env-mismatch`, `cycle`, `already`, `not-linked`, `item-action-limit` with its `retryAfterSeconds`, `already-rerun`, `run-in-progress`, ...), the same text written into the issue (the decision block of
  an amendment, `Depends on` lines, the child bodies of a split, the superseded comment of its pull request), the same fields of the answer (`verb`, `ok` and `exit` added as `factory.ps1 --server` adds them), and the same state: the amendments, the split, the park and
  handoff, `manager_actions` (the limit per item per day lives in the state comment, so a restart does not reset it) and `manager_reruns` (one re-run per head, the last ten kept). PowerShell's rules are kept where they show: `-match`, `-in`, `-eq` and `-replace`
  are case-insensitive, a text is "truthy" when it is not empty, `[int]` of a text. A verb with an item holds that item's lock, `backlog` does not.
- **The adapters** (`gateway/Ports/KeeperPort.cs`): `KeeperGitHub` is `Invoke-Gh` (three tries on a network error or a 502 to 504, the answers of Core.ps1: 404 to `github-not-found`, a 403 or 429 that GitHub says is a rate limit to `github-rate-limited` with
  `retryAfterSeconds`, any other 403 to `github-forbidden`, else `github-error`; GraphQL's `RATE_LIMITED`), `Get-ItemInfo`, `Move-Card`, `Add-ToBoard`, `Get-BoardList` (the environment scope and the home repository filter of the script), with the User-Agent of the rule
  scripts (`factory-worker-gateway`) so the golden writes count them as `rule` writes, the test cause header, and the rate limit reported to the gateway's shared budget. `KeeperStateStore` is the state comment and the journal of the W3.2 ledger
  (`JournalLedger.AddTo`, new: Add-Journal with the caller's holder, which the verb saves together with the state it changed); a journal line that cannot be written is logged and the verb goes on, as `Add-Journal` does. `KeeperFileLocks` takes the same files as
  `Use-ItemLock` and `Use-IntakeLock` (`item-N.lock`, `board-intake.lock` in the state directory, polled 300 ms for 45 s, then `item-busy` or `intake-busy`), so a rule script still running in pwsh for the same item excludes the verb and the other way round.
  The store and its GitHub client are made at the first use: an App token that cannot be minted at the start does not stop the gateway from starting.
- **The saga:** an item the `WorkItemSaga` owns (`V2Ownership`) is told what the verb did to it: a released park (`manager-action unpark`, `amend-criteria --unpark`) is `ResumeWorkItem` (the offer of a parked saga item would never come back otherwise), a split
  that closed the item is `ParkWorkItem` with the reason `split` (the lease stops and no step message is taken; the item is closed, its pull request closed, its claim ref removed). The verb's answer is the same whatever the saga takes; a message the host does not take is logged.
  Everything else the verbs change (labels, the body, the state comment's park, handoff and claim, the journal) is what v1's plant and the projections read, so v1 can take the item back at any time.
- **No schema:** the verbs are handlers without state of their own (the keeper's daily count and the manager's hourly count are the pipeline's, in memory; the per-item count is in the state comment), so there is no script `07x_` and no table.
- **Not here (documented gaps):** the lease fence (`_lease`) is ignored, v2's leadership being Wolverine's (the verbs are not in `FencedVerbs` in v1 either); `manager-action release-owner` ends the claim lease in the state comment and does not touch the saga's own step lease, and
  `requeue` sets the handoff the plant reads (a saga item is offered from its own state), so neither tells the saga; a keeper `create-item` does not start a saga (the intake does that, from the board); the golden writes are compared in the `manager` suite
  of the v2 job for the scenarios named in `v2-claims.txt` ("act: ..." and "keeper: ..."), no difference is declared.

**W9.3 as built (#355): the merge train verbs on v2.** `approve-pr`, `merge`, `clear-hold`, `base-watch` and `base-update-branch` are registered by `gateway/Verbs/Routes/Merge.cs` where the engine acts. Owner decisions of
2026-10-28 apply (hybrid composition, tdd only, the W7 scope cut: no merge queue, no smoke evidence, no overflow). No table was added (`MergeTrains` and `WorkItems` are enough; the QC record is read from `QcEnvironments`, W9.0).
- **Hybrid:** a verb is v2's for an item the WorkItemSaga owns, and the rule script's (`Runner`, as before) for every other item, for merge policy other than `auto` (the inspector's review, the approval of the current head,
  the QC gate of `Verb_merge` are `peer-approved` rules, which stay on v1 as W6.2 declared; the migration-number collision is refused by the v2 verb since W9.10q) and whenever the saga's parts cannot be found (no host, no GitHub credential: not remembered, the next
  call tries again). One call is answered by exactly one of them. `MergeVerbHandlers` (`gateway/Verbs/MergeVerbs.cs`) finds the parts at the first call: the host is built after the route table.
- **`merge`** (`MergeVerbRules`, `MergeVerbService` in `Fworker.Workflows.Merge`): the script's checks in its order and with its codes, exits and texts, on the saga's state (`already-merged`, `item-closed`, `wrong-column`,
  `no-pass-verdict`, then the labels read from the issue: `held-by-label`, `pr-mismatch`, `no-auto-merge-label`, and `held` for a parked saga). The merge itself is the saga's: its Merge step asks the repository's MergeTrainSaga
  for the turn and the merge port runs the C# direct merge (W6.2, W7.2). The verb delivers the saga's retry timer at once (`MergeRetryDue` with the `MergeRetryAt` the state holds; nothing when a call is already out), looks at the
  state once a second for at most 20 looks, and reports what the saga did in the shape of the script's answers: `merged` (sha, `by` gateway), `waiting` (the saga's reason, `checks-pending` before it has one, `since`), `demoted`
  (`stale-head` when the head moved and the item is back to its verdict, `merge-out-of-date` when it is back in Development) and `parked` (the park reason up to its first colon). The script's own merge loop (the long waits of
  a session's call, the update of the branch, the out-of-date retries) is the train's and the port's now, once, so a session and the saga can never merge the same item twice.
- **`approve-pr` and `clear-hold`:** under `auto` they never decide anything about the pull request: `approve-pr` checks `item` and `text` and answers `not-required` (exit 10); `clear-hold` answers `item-closed` (11) for a saga
  that completed and `not-held` (10) otherwise, because only an inspector's escalation, which `auto` never makes, is a hold it clears.
- **`base-watch` and `base-update-branch`** (`BaseUpdateRules`, `BaseUpdateService`, adapters in `gateway/Ports/MergeVerbPort.cs`): the same GitHub reads as `BaseUpdate.ps1` (`branches`, `compare` with the 300-file bound, `pulls?state=open`,
  the files of a pull request, the 3 s wait for a mergeability GitHub has not computed, `update-branch` with the head the trigger saw), the same rows (`skip` reasons `pass-unmerged`, `merged`, `pr-closed`, `no-branch`,
  `verdict-recorded`, `merge-awaiting`, `in-validation`, `qc-for-head`, `error`) and outcomes (`not-open`, `head-moved`, `skip`, `conflict`, `unknown`, `current`, `refused`, `updated`). What the script reads in the state comment
  the saga answers: a PASS waiting is `VerdictHead` without a `MergeCommit` (`pass-unmerged`), a merge on record is `MergeCommit` (`merged`), the validation column is the Verify and Merge steps, and the QC record is the
  `QcEnvironments` row of the pull request. `base-watch` splits its items: the saga's are answered here, the others go to the script in one call with only those items and its rows join ours in the order of the call; with none
  of the saga's among them the call is the script's as it was. `BaseWatch` (`gateway/BaseUpdate.cs`) calls the route table first, so the standing trigger is v2's where the engine acts. What the Runner does before
  it starts the script of these calls is done here too: the two are not made while the GitHub budget is low (`GitHubBudget.DeferredCalls`, the same refusal), and `base-update-branch`, the one that writes, is refused with
  `lease-lost` (exit 20) by a gateway that no longer holds the leader lease.
- **Parity:** `tests/Gateway.Tests/MergeVerbParityTests.cs` runs 45 situations through the REAL scripts (`parity/merge-verbs-parity.ps1`: Verbs.Merge.ps1, Verbs.Inspect.ps1 and BaseUpdate.ps1 under PowerShell, with a scripted GitHub
  and item state) and through the C# verbs with the saga state of the same situation, and compares the two answers field by field, texts included, with the exit code. The unit tests are `MergeVerbTests`, `BaseUpdateVerbTests`
  (Workflow.Tests) and `MergeVerbRoutesTests` (Gateway.Tests: the route file, the hand-over to the script, the GitHub adapters, the QC read on a real SQL Server).
- **Declared differences from the script (for `v2-differences.txt` when the v2 Tier 1 job claims a scenario that shows them):** the answer of a merge is the saga's, so it carries `by: gateway` and no comment on the item (the saga's
  history is the record, as for the docs plan); a parked saga refuses the verb with `held` where the script's session call under `auto` ignored a park; the session's call no longer waits minutes for the checks (it answers `waiting`
  after at most 20 seconds, as the gateway's own call always did); the refusals do not feed the model ladder's counter (`Add-Refusal` of the script; the saga counts its own failures); a saga records no branch name, so the
  `pr.branch` fallback of the script's row (a branch recorded without a pull request number) does not exist for its items.
- **Not here:** the claims of these verbs in `tests/scenarios/v2-claims.txt` (a suite is claimed when the v2 Tier 1 job has run it; the baseupdate scenarios of the owner suite are the candidates, and run on v1's items through the
  hand-over); the peer-approved path in C# (the inspector's review as the reviewer App, the approval of the current head, the QC gate, the migration collision), which stays the script's until W9.11 decides whether the policy
  survives; the merge queue (W7.10).

**W9.2 as built (#354), the item verbs.** Owner decisions of 2026-10-28 stand (hybrid composition, tdd only, the W7 scope cut). The 18 verbs of `ItemRoutes` (`get-item` to `expire-claim`) are answered by v2 where the engine acts,
each as the rule script answers it for the same input: the same fields and exit codes, the same writes to GitHub, the board and the state comment, in the same order.
- **Where:** `src/Fworker.Workflows/WorkItem/Verbs/*` has the verbs as ports of `Verbs.Item.ps1`, `Owner.ps1` and `Verbs.Pr.ps1` against ports (`ItemVerbPorts.cs`: the wire to GitHub, the state comment, the journal, the item lock,
  the refusals file, the v1 fallback); the handler of a verb is the port of its `Verb_*` function and says which. `gateway/Ports/ItemVerbsPort.cs` is the other side of the ports over what the gateway has (the GitHub wire with the
  gateway's identity, `GitHubJournalStore` for `Get-State` and `Save-State`, `JournalLedger.AddInHand` for `Add-Journal` into the state in hand, the lock file `item-N.lock` and `refusals.json` of the state directory, so a script
  of v1 and a verb of v2 exclude each other and the model ladder sees both). `gateway/Verbs/Routes/Item.cs` registers the verbs. The only line outside those files is `JournalLedger.AddInHand`.
- **The answer is the script's:** the keys are read without regard to case, `_session`, `_cause` and `_lease` are taken out of the arguments, the verb runs under the item's lock unless it is `get-item` or `item-pr`, the answer
  has `verb`, `ok` and `exit`, a refusal is `ok: false` with its code and reason, an exit 10 of `complete`, `record-review`, `validation-verdict`, `open-pr` or `append-body` is one data point of the model ladder, any other fault is
  `internal-error` (20). Every request carries the user agent `factory-worker-gateway` (the state comment's and the journal's included) and, in the test mode, the cause id of the request, so the fake's golden recorder tells a write
  of a verb (`rule`) from the gateway's own loops as it did.
- **The saga:** the router is wrapped in `V2VerbFeed` (the decision verbs `append-body`, `record-review`, `complete`, `validation-verdict` and the lease renewals, as in W6.6 and W7.3) and `ItemAdminFeed`: `park-item` and
  `unpark-item` tell the saga of an item it owns (`ParkWorkItem`, `ResumeWorkItem`) so its lease stops at the park and its step is armed again at the unpark.
- **Left to the script until W9.10q, through `IItemV1` (the runner), before anything was written and after the item's lock was let go:** the `accept` of a QC environment (W9.1), the `complete` of Development with the red-head ruling and the
  migration check of the CI rule (W9.5), and the pull request of a `copilot/*` branch or an owner task (`open-pr`, `item-pr`; W9.4). W9.10q ported them and deleted `IItemV1`, `ItemDelegated` and `ItemVerbsV1` (see "W9.10q as built").
- **Declared difference:** the inline merge of a PASS in `validation-verdict` (`Verb_merge -Gateway`) is not made in the call. The answer says `next: merge` as before; the merge is the sweep's and the saga's right after (a verdict
  asks for a sweep), and `merge` called by the session still answers `already-merged`. W9.3 (the merge train verbs) closes it, and
  W9.10e (done) makes the sweep leave an owned item alone and the verdict make no inline merge for it. The Tier 1 suite `auto` runs with `gatewayMerge` off, so none of its scenarios sees it.
- **Routed since W9.9a:** the gateway's own calls of these verbs (`PlantRuleHooks` renew and take-back, the park of the runtime adapters, the unpark and resettle endpoints, `ItemPrLookup`) go through `Runner`, which asks the routes first
  where the engine acts (they were the script's until then; see "W9.9a as built").
- **Proof:** `ItemVerbsTests` (55 cases of the scripts' branches on an in-memory GitHub), `ItemVerbsPortTests`, and `ItemVerbsFakeGitHubTests`/`ItemVerbsReplayTests`, which start the fake GitHub of Tier 1 and compare the writes it
  records with the golden files the scripts wrote (`auto`: gates refuse what they should, escalate earlier, the docs-only fast path's design reviews; `owner`: update-pr). The three `auto` scenarios are claims of `v2-claims.txt`.

**W9.7 as built (#359, owner review: credentials and authorization).** The six service verbs answer on v2 as their rule scripts answer (`Owner.ps1`, `Apps.ps1`, `OperatorRelease.ps1`, `Metrics.ps1`), in C#:
`gateway/Identity/RevocationVerbs.cs` (`revocations`, `revoke-worker`), `GitCredentialVerb.cs`, `gateway/Operator/OperatorReleaseVerbs.cs` and `MetricsVerb.cs`, over `ServiceGitHub` (the GitHub calls of `Core.ps1`: the same retries,
the same refusals, the same user agent, the credential of `Get-GhToken`: the author App where `FACTORY_AUTHOR_APP` is 1, else `GH_TOKEN`, nothing else) and registered by `Routes/Services.cs`. Who may call is not touched: `VerbPolicy`,
the pipeline's own refusals (`not-owner`, `use-endpoint`, the GitHub budget) and the guards of the endpoints come first, as in v1, and a test pins that order. The verbs that have an endpoint (`revocations`, `revoke-worker`,
`operator-releases`, `operator-release`) stay refused to every caller of the verb route; the gateway's own calls (`RevocationService` and `OperatorReleaseLoader` at start, the revoke and record-release endpoints, the metrics
endpoint) call the Runner, which asks the route table first where the engine acts (W9.9a removed `ServiceVerbCalls`, which did the same for these six verbs only), so the kill switch is v1 again at the next start. Same answers, same exit codes, same calls: `ServiceVerbParityTests` runs each
case through `plugin/scripts/factory.ps1 --server` and through the C# verb against the fake GitHub and compares the answer, the calls in order and the refs left, for every refusal (a missing or bad argument, a 404, a refused permission, a
rate limit, a 500, a bad gateway, a 422 that is not "already exists", a ref answered and not kept, an item whose comments cannot be read); `ServiceVerbsTests` covers the rest (the scope of a git credential in every wrong form: the token is
revoked and not handed out; the credential; the retries; the timeout; the budget; `RevocationService` and `OperatorReleaseLoader` on v2). Two differences, both on the side of refusing: `revocations` keeps two revoked names that differ by case only
(the rule script's hashtable made one of them, and the generations of the gateway are per exact name), and `git-credential` takes the App key from `secret-tool` on a gateway on the operator's machine, as `Apps.ps1` does (no shell, fixed arguments). No new table.

**W9.10c as built (#551): the pending claim, and Copilot resume as unknown.** Script 140 (`factory.IdempotencyClaims`: key, owner node, owner epoch, claimed and expiry times; `CopilotReservations.Mode`), expand-only
(the numbers of the scripts are ascending, no longer contiguous: `DatabaseScriptsTests`). **The ledger** (`IIdempotencyLedger`) takes `Claim(key)` in one batch and one transaction before the side effect: `Claimed`, `TakenOver` (an
expired claim), `Pending` (a live claim of another handling) or `Done` (a result is recorded; `Record` inserts the result and deletes the claim in one transaction, and `Claim` reads the ledger only after it holds the claim, so no
handling gets through between the two). The claim names this process and the epoch of the lease it holds (`SqlCommandStores(cs, owner)`); `ReleaseClaim` and `ExpireClaim` act only on the owner's own. The default lifetime is 90 s: longer than
any one call (the HTTP clients stop at 60 s), shorter than the 102 s of `RuntimeCommandService.RetryPauses`, so a command whose process was killed finds the claim expired at its last retry. **The handler base:** a second handling ends
Transient (`PendingError`, the caller waits and asks again and then finds the result); an answer that settles is recorded; a retryable answer gives the claim back; a transport failure (the effect may be there) expires the claim at once; a
stop of the host expires it too; a taken-over claim looks for the effect first (`EffectLookup`: Found is recorded and returned, Absent calls, Unknown ends Transient and expires the claim again). Lookups: a runbook run or an Octopus start by worker name
(`OctopusRunbookRuntime.FindStartRunOf`, the prompted `Fworker.Name` of the Start runbooks, 6 hours back), a merge by the pull request's merge state (`MergeDirect.LookUpMerge`), a journal line by its key in the item's journal ledger
(`JournalLedger.Recorded`); without a lookup (stop, and the runtimes whose starts have no way to find the run) the call is made again, as before. **Copilot:** a resume (`resume-<exception>`) is unknown like a launch: the reservation stays with
its `Mode`, the start answers `reserved` (RateLimited: the manager gives the launch back and counts no failure, `RuntimeManager` handles the word beside `no capacity` and `quota`), so a fast unknown outcome is no longer exhausted into a failure
before the reconciler's 2 minutes and no second comment is posted. The reconciler looks for the task (launch) or the `@copilot` comment and the session (resume), writes the owner record first (`CopilotCloudRuntime.RecordReconciledOwner`, the
item state's `copilot_task`) and settles with `ICopilotReservations.Transition`, a conditional write. `TaskForItem` (#479) is no miss, and so does not release, when a full page was created since the reservation or a task created since it
carries no text to match. **Not done here:** the real Copilot API has not been called (the fields the tasks list returns are the operator's to confirm on a live run), the delivery commands of #545 (`PromoteRelease`, `AnswerSignOff`) adopt the
base with a lookup by the release's deployments when that pull request lands, and the `PromoteRelease` key's sequence and the ledger retention stay with W9.10o.

**W9.5 as built (#357), the read models.** Owner decisions of 2026-10-28 stand (hybrid composition, tdd only, the W7 scope cut). The six verbs of `ReadModelRoutes` answer on v2 where the engine acts and each answers what its rule script
answers for the same input: the same fields and exit codes, the same refusals (`Reject`), the same writes. The code is `src/Fworker.Workflows/ReadModels/` (`ChecksReadModel` for `pr-checks` and `wait-for-checks`, `InspectReadModel` for
`inspect-facts` (the inventory names `ChecksReadModel` as its owner; it reads that model, and `approve-pr` of W9.3 reads the same `InspectReadModel.Facts`), `DeliveryReadModel`, `ModelTierReadModel`, `SecretsReadModel`; `CheckRules` is the part
that needs no GitHub, `ReadModelSettings` what they take from `config/instance.json`) and `gateway/Ports/ReadModelsPort.cs` (the wire and the files they read and write through). Workflows reaches the outside only through ports
(`IGitHubApi`, `IOctopusApi`, `IItemStateStore`, `IItemLock`, `IRefusalCounts`), as the dependency rule has it.
- *Reads.* The gateway's own GitHub identity (the token of the rule scripts: `GH_TOKEN`, else the author App's installation token) with the rule scripts' headers, so the fake GitHub tells their writes from the gateway's own
  (`User-Agent factory-worker-gateway`, `X-Fake-Cause` in the test mode); the rate limit of every answer goes to the gateway's budget. A job log or an artifact is a redirect that is followed without the credential, as `Invoke-RestMethod` does.
  GitHub's refusals map as `Invoke-Gh` maps them (404 exit 12, rate limit exit 20 with the wait, other 403 exit 30, anything else exit 20); the detail of `pr-checks` (logs, annotations, artifacts, the baseline) turns a refusal into data
  in the answer and never an error of the verb. Octopus is read with `OCTOPUS_API_KEY` of the moment (exit 30 without it, 404 is nothing, any other failure exit 20).
- *Writes, the same ones as v1.* `wait-for-checks` counts a red head once in the item's state (`checks_red`, the push gate of `open-pr` and `update-pr`) and reruns the one failed job that the base branch fails too, once per head
  (`preexisting_reruns`); `model-tier` records the tier in `model_tier` when it changed. The state comment is saved through the same store as the journal (the revision check of `Save-State`: a state that moved is exit 11
  `state-conflict`), and the item lock is the file `item-N.lock` of the rule scripts' state directory, so a verb that v1 still runs and one that v2 answers exclude one another. The refusals `model-tier` counts are the file
  `refusals.json` of the same directory, which the server wrapper of v1 writes on every refused gate call until the verbs that refuse move to v2 (W9.2, W9.3).
- *Parity.* The Tier 1 scenarios of these verbs are the oracle: `checks.ps1`, `model ladder` and `testing profile` run on v2 as claims of `v2-claims.txt`. `Gateway.Tests` (`ReadModels*FakeTests`) puts the read models against the same
  fake GitHub and Octopus (the cases of those scenarios, and of the owner and messaging suites for `inspect-facts` and `get-delivery`), `Fworker.Workflow.Tests` pins the rules and the refusals, `ReadModelsPortTests` the gateway's side.
- *Differences, on purpose.* (1) A call to `pr-checks --full-log` or `--artifact` has no deadline of the detail of its own, where the script's `$script:CkDeadline` is set by a detail call and can still be set when the next call runs in the same pooled worker. (2) `check-secrets` reads the
  board's Status field every time, where the script serves `project-meta.json` for ten minutes (the probe should see the board as it is now); the file is still filled by v1's verbs. (3) The login of the author App comes from
  `FACTORY_APP_AUTHOR_SLUG` (or the fake apps), not from `~/.config/factory-worker/apps/author.json`, which a gateway container does not have. (4) The times of an Octopus answer are written as PowerShell writes a time it parsed (a
  local time with its offset). (5) A regular expression of the configuration or of the diff that takes more than ten seconds is an `internal-error` (exit 20) and not a wait without end.
- *Not here.* No smoke evidence, no overflow, no merge queue (the W7 scope cut). `get-item` and the other item verbs (W9.2) read the same state through the same store.

**W9.10d as built (#552), the v2 Tier 1 that starts sagas.** Tests, the harness and CI only; the gateway is not changed (tdd stays in shadow, everything new is inert unless `Engine.V2Acts`). Two defects of the fake
GitHub that the first saga found were fixed in `tests/FakeGitHub` (the single-issue read now carries the labels, as GitHub's does, and the card's project node carries the project's id).
- *The environment name per suite.* `run.ps1 -Engine v2` takes the suite's own `FACTORY_ENVIRONMENT` where it sets one (tdd for `copilot`, `promote`, `envscope`, so the env: label scoping and the promotion settings
  hold) and `v2ci` for every other suite; `engine.v2Environments` of the test configuration names it.
- *The profiles* (`$V2Profiles` of `run.ps1`, `-V2Profile`, laid over the test configuration key by key by `Start-TestEnv -Merge`): every profile sets `mergeStrategy` direct; the two saga profiles set the Wolverine host's timers at their floor (`scheduledJobSeconds` 60,
  `firstScheduledJobSeconds` 0, so that the saga's leases and the scheduler's wakes run inside a scenario), `plain` leaves them at the defaults (with them at the floor the octopus scenario `journal order` fails on v2 after #572; cause not analysed further, the run is as master's); `saga-docs` turns `intake.enabled` on with `v2Plans` [docs]; `saga-feature` with [docs, feature].
  `scheduler.featureSlots` is **off** in both: with it on a feature item waits for a slot that only a running runtime grants, and Tier 1 has none for the scheduler (audit finding 20: the batch of W9.9 runs with it off until W9.10j).
  (Until W9.10e, #557, the rule script's own intake would have pulled the Todo card within seconds, long before the engine's job could start the saga, and `run.ps1` switched the script's intake off in the configuration file once the gateway ran: `Disable-ScriptIntake`, now removed; v1's intake skips the plans of `intake.v2Plans` itself.)
- *The suite `saga`* (`saga.ps1`, slot 29, appended at the end): a Todo item, a connected owner session with a free slot, the intake, the owner session's design, development with its pull request, validation and PASS, and the merge.
  It runs on both engines from one script; its golden files are recorded on v1, and `golden/v2-differences.txt` declares what v2 writes instead: the W6.6 block (docs plan: the cards and the plant's journal lines are the gateway's
  writes, the merge is the saga's own call and no comment says that it merged, the card goes to Done by the projection) and the W7.3 block (feature plan: the same intake, projection and journal; the PASS's inline merge is still the
  rule script's, written after the verdict call has answered). The docs scenario asserts, with `gatewayMerge` on, one merge write, from the gateway, and no rule-sourced card move; the checks that the feature scenario's merge is the gateway's
  and the state comment holds no wait record of the rule script's were quarantined under #557; W9.10e made the saga the only merger and they are plain checks. No #496 block exists: the app's uat and prod stay with
  v1's `AutoPromoter` (decision 2), so the `promote` suite runs unchanged on v2 and shows no newest-only difference in Tier 1.
- *The W6 and W7 gates* (#265, #266) are therefore proven by the two claims of `saga` (`v2-claims.txt`, profiles `saga-docs` and `saga-feature`) once the matrix job is green; the entry for the record is the run of `Tests` on master.
- *Claims.* `v2-claims.txt` gained the profile (`| profile=<name>`), the whole-suite claim and `v1-only <suite> | <reason>`; `tests/ci/run-v2-claims.py` is now the static check (the claims parse, list a scenario, and are each in exactly one
  entry of the v2 matrix; `--require-complete` is W9.9c's precondition: every suite of `run.ps1 -ListSuites` claimed whole or declared v1-only). `run-all.ps1 -Engine v2` runs the claims, one job per whole suite (or shard of one) or per claim
  by scenarios, and fails a claim that lists nothing and a job whose scenarios that ran are not the ones listed. The suites claimed whole today: octopus, repos, envscope, mcp, copilot-deadline, copilot-manager, operator, merge-queue,
  qc-rate, qc-proxy, the six one-scenario `qc-gate-*` suites, promote, qc-worker and saga; v1-only: `peer-approved` (the dropped policy). `qc-gate` is the merge gate of that policy; W9.1 (#544) claims its first scenario on v2, so the suite is not declared v1-only while the QC verbs are ported. Two of its scenarios are the QC record's own, not the gate's: a reset carries `ran_sha` over, and an Auto Merge label added to an item with a live environment tears it down; they moved to the suite `qc` and were recorded again under the auto policy. Every suite is claimed whole or declared v1-only since "W9.9c prep as built" below.
- *Flakes of #415.* The `operator` suite's takeover waits for the window and asks again (`Op-Hold`) instead of a fixed sleep, and its registration read waits up to 15 s; the comparison of the three scenarios whose caused writes
  differ now and then (`keeper: split-item ...`, `mgr: text that comes from a message ...`, `copilot: a red master after the merge ...`) and the 4-requests check of the promote cold pass are quarantined under #415 (`$script:GoldenCausedFlaky`);
  the quarantine of the golden misses moved from the closed #386 to #415. The unit tests that need a SQL Server are `[SqlFact]` (88 tests): without `FACTORY_TEST_SQL` they are reported as skipped. The committed `tests/ci/__pycache__` file is removed.
- *Not here.* `V2_BLOCKING` stays false: it becomes true after the first green run of `Tests` on master, which the main session starts after the merge batch and records on #268.
  (Done 2026-10-31: `Tests` dispatched on master f7dd889a, after the claim list was made complete by W9.9c prep #603, run 41227931900 green, all 46 v2 jobs; `V2_BLOCKING` true from the pull request that sets it.)

**W9.10e as built (#557): v1 leaves the saga's items alone.** Only where the engine acts (`Engine.V2Acts`); tdd stays in the shadow run and its v1 sweep is called as it always was (no `skip_items`, no `intake.v2Plans`, no `Loaded` wait).
- *The owned set into the script.* The Sweeper adds `skip_items` (the numbers `V2Ownership` holds) to every `sync-board` call (`Sweeper.AddV2Arguments`). `Verbs.Sweep.ps1` skips those items in its item loop before it reads their state, so the settle and the master-CI watch,
  merge pickup, the PASS merge, park, demote, clear-hold, idle release and auto-unpark are not done for them, and the claim-ref reap leaves their refs alone. They still count in `design_waiting` (the backlog is the board's). The gateway's filter (`SweepV2Filter`) stays as the second line.
- *No inline merge.* The gateway gives the verdict of an owned item the flag `sagaMerges` (`V2VerbFeed`, like `ftWithPendingChecks`: a caller's own is taken out of every call); `Verb_validation_verdict` records the verdict as always and makes no `Verb_merge -Gateway`.
  The answer still says `next: merge`; the saga's merge train merges, once. An item with no saga keeps the script's inline merge. (Where the item verbs are routed to C# (W9.2, `ItemRoutes`) the verdict never merges inline; the flag is for the script path, which a gateway whose routes are not registered still runs.)
- *Intake.* The Sweeper adds `intake.v2Plans` (the names of `intake.v2Plans`) to a full sweep's `intake` block; `Invoke-Intake` leaves in Todo an item of those plans: the docs-only marker in the body is the docs plan, any other item the feature plan (the rule of `DocsIntakeJob`),
  except an item whose body names what it depends on, which the engine's intake leaves to v1's until W9.10h admits `Depends on` in v2's intake (done: see W9.10h below; v1 now skips every item of those plans).
- *`Loaded`.* `V2Ownership.Loaded` is false until the first successful read of the saga's table (`Set` counts as one). Until then the Sweeper runs no pass, full or incremental (items asked for stay queued), and says so once a minute; after the first read a failed read keeps the last answer.
  (Stricter than "acts on no item": a pass over an unread set would also plan from the script's answer for items the saga owns.)
- *Harness.* `Disable-ScriptIntake` of `run.ps1` is gone: the saga profiles leave the switches on and v1's intake skips the plans. The checks of `saga.ps1` that were quarantined under #557 are plain checks (the merge write is the gateway's, no merge comment, no wait record of the rule script's);
  the two blocks of `v2-differences.txt` lose the lines of v1's pickup and of the rule script's merge, and the feature block's merge is the gateway's.
- *Known window.* The owned set is read at the start of a pass, so an item whose saga starts during a pass (up to the script's 280 s) can still be acted on by that pass once; the saga starts only from Todo, which the pass does not touch for it. A claim ref of a merged saga item is removed by
  the reap after the item closes and its claim has lapsed, not by a pickup at the merge.
- *Proof.* `SweeperV2ArgumentsTests` (the arguments; a v2 gateway whose table cannot be read sweeps nothing, a v1 control sweeps), `V2OwnershipTests` (`Loaded`), `FeatureFeedTests` (the flag), the gateway test of `WorkItemWiringTests` (the pass carries `skip_items [701]` while the saga is open). The Tier 1 claims of `saga`
  are unchanged; their quarantines and the declared differences were changed without a local Tier 1 run (see the pull request).

**W9.10f as built (#558): the sweep's item steps on v2.** Only where the engine acts (`Engine.V2Acts`): the settings of the rules (`SweepRules`) are in the policy that `DocsPlanSettings` builds, and `sync-board` skips the saga's items since W9.10e, so nothing changes in tdd's
shadow run, uat or prod. No table and no DbUp script: the new state is optional fields of the saga's JSON document (`WorkItemState`), which a state saved before them reads as none.
- *Rules (`Fworker.Workflows.WorkItem`, `Plans/SweepSteps.cs`, messages in `Plans/SweepMessages.cs`).* Each is a step the sweep took in `Verbs.Sweep.ps1` and is an input of the saga with a business key (or, for a timer, the time it was scheduled for), a decision, and a Workflow test (`SweepStepsTests`):
  - *Master's CI after the merge* (the settle's master-green wait, D5): a merged code item (feature and hotfix plans) enters Deliver with `MasterState` "waiting" and a timer `MasterWatchElapsed` at `timing.masterGreenTimeoutMinutes` (30 min; a re-run of the failed jobs counts it again from the re-run, issue 79).
    `MasterObserved` Green ends the wait (`master-green`); Red (after the one re-run) parks the item `master-broken` with the failed checks (`master-broken`, `parked`); no verdict in time parks it `master-green-timeout`. Both waits stay, as in issue 114: the item is not offered, the watcher goes on reading it, and a green master
    for the merge (the commit or a later one that contains it) lifts the park (`resumed`, `master-green`). That is the auto-unpark of master-broken, at most `timing.autoUnparkMax` (3) times per park code while the park stands; the timeout park and an expired park are lifted without a count, as the settle's polling did. A delivery of the item
    (`DeliveredTo`) means master was green: the wait ends and a master park with it. The docs plan has no wait (it ends at the merge).
  - *Park expiry* (`Reset-ExpiredPark`, `Get-Park`): every park has `ParkUntil` (`timing.parkHours`, 24 h; `ParkWorkItem.Minutes` names another length) and a timer `ParkExpired`. An expired park gives the item a fresh start: its fix-forward rounds, demotions, mechanical fixes, failures and launches of the step are counted from 0 (`park-expired`) and the step is
    armed again (an item past its merge only loses the park: the wait for master goes on). `WorkItemRearm` (`saga-rearm`) repeats a park timer that was lost between a save and its outbox.
  - *Merge pickup* (`Invoke-MergePickup`): was already `MergeSeen` (the delivery train's `ItemMerged` of a person's merge, from the webhook or the board reconciler): it ends the Merge step from whichever step the item was on, takes it out of its park and gives its slot back; W9.10f adds that the merged code item now starts the wait for master (above).
  - *Demotion of a PASS whose pull request was closed* (`Invoke-Demote`, cause pr-closed): `PullRequestClosed` sends an item on its Merge step with a PASS back to Development (`demoted`, counted as a demotion; past `maxDemotions` it is parked `demotion-limit`), the pull request is no longer the item's (the owner opens a new one). A PASS of an item without the Auto Merge label
    (park `pass-without-auto-merge`: a person merges it) is processed and is not demoted, as the sweep's `unprocessedPass` was false for it.
  - *Clear-hold when approved* (`Invoke-ClearHoldIfApproved`): `ApprovalSeen` (a person of `humanApprovers`, not the author, approved the head the PASS covers) ends a park `inspector-escalated` on the Merge step (`hold-cleared`) and the merge goes on. Under merge policy auto the saga never parks `inspector-escalated` itself (W9.3 decided that a saga item is never held by the inspector); the rule is for the
    items W9.10m adopts from v1 with that park.
  - *Idle owner* (`owner-released-idle`): the owner of a PASS that is held (parked on its Merge step, still to be merged) gives the item up after `idleReleaseMinutes` (60): `OwnerIdleElapsed` decides `owner-released` and sends `OwnerReleaseRequested`, which the gateway answers (below).
  - *The settle's re-arm* (`Invoke-RearmSettle`, issue 114): `RearmSettle` gives a merged code item that has no wait for master (a state saved before the wait existed, or adopted without it) one, with the timeout counted from now.
- *The watcher `item-steps` and the reads (`ItemStepsJob`, `Delivery/ItemStepReads.cs`; `RecurringJobSlots.ItemSteps`, every minute, leader only, the budget gate of the other watchers).* It reads, for the saga's items of the home repository: master's CI for the merge commit of each item waiting for it (the settle's reading ported without its writes:
  `MasterCheckRules.Read`, over the delivery verbs' kit, the gate checks only, `masterIgnoredChecks` left out, the one re-run of the failed jobs started once with the lease fence and judged on a newer completed attempt, a parked wait read as `Find-GreenMaster`), the state of the pull request of each PASS that waits for its merge,
  and for an `inspector-escalated` park the human approval of its head (`Find-HumanApproval`). It tells the saga with keys that make a repeat a no-op; a read that fails is read again at the next run, never told as a state; at most 8 items of each kind per run, the longest unread first. A merged code item with no wait is told to re-arm.
- *The owner release (`gateway/Ports/ItemStepsPort.cs`).* `OwnerReleaseRequested` reads the claim with `get-item` and gives it up with `release` through the verb router (v2's item verbs where the engine acts), then the plant forgets the owner (`Plant.ManagerReleased`); an item with no claim, not there, or taken by another owner is left alone. The two calls are in `verb-inventory.json` as a router forward.
- *Configuration.* No new key: `timing.parkHours`, `timing.masterGreenTimeoutMinutes`, `timing.autoUnparkMax` and `idleReleaseMinutes` are read by `DocsPlanSettingsReader`; the wait for master is on where the engine carries the feature plan (`intake.v2Plans`).
- *Not here, on purpose or because it is another row's.* The comments the settle wrote on the item (master GREEN, BROKEN, the timeout, the release candidate) are not written: the saga's history is the record (W9.10i writes the state comment projection, and v1's format with it). The release candidate, the Release Queue and Done are the delivery train's (`DeliveredTo`, the settle window), which was so before this row; the card
  follows the saga as before (`DocsProjection`). The park of an item by a person or by a runtime adapter reaches the saga only when W9.10h sends `ParkWorkItem` from those paths; until then a park that a rule of the saga sets (merge, checks, demotion, master) expires, and one that only the script knows does not. v1's other lift of a master-broken park
  (master's head green with the failed checks passing, whether or not it contains the merge) is the saga's stricter one (a green commit that contains the merge). The auto-unpark of `verdict-failed`, `fix-forward-exhausted` and the relaunch parks is not here (the saga counts those budgets in its state and the checks watcher already restarts the rounds on a green head).
- *Proof.* `SweepStepsTests` (32 cases: every rule above, the timers, the stale and refused inputs, the rearm job), `ItemStepsReadTests` and `ItemStepsJobTests` (the readings over the fake kit of the delivery verbs, what the job tells and does not), `WorkItemTrackedTests` (a park that ends is a durable scheduled message in a real host), `ItemStepsSettingsTests`
  and `OwnerReleaseTests` (the settings, the release), and four scenarios of the suite `saga`, claimed on v2 (`v2-claims.txt`, profile `saga-feature`; on v1 they pass at once, the sweep does these steps there): the wait for master and its green (`master-green`), the red master after the one re-run parked and lifted by the saga when master is green at a later commit,
  the PASS whose pull request was closed (`demoted`, the card to Development), and a person's merge (`merged`, then `master-green`, no merge call of the gateway). The expiry of a park (a day), the clear-hold, the idle owner (an hour) and the re-arm have no producer or no time in Tier 1 and are covered by the Workflow tests only.

**W9.10h as built (#560): the producers of the saga's inputs.** Only where the engine acts (`Engine.V2Acts`); tdd stays in the shadow run (no `SagaInputs`, no saga handler, no `MergeFactsPort`; the Octopus runtime's exit 75 handling and the `HostFull` status are the one change that also
runs in v1 and in the shadow run, and it only turns a start the host refused for room from a counted failure into the wait the runtime already knows as "no capacity"). No schema change: the new state field (`CardSeq`) is part of the state document, so no script of the reserved range 210 to 219 is used.
- *Park and unpark.* `SagaInputs` (`gateway/Ports/SagaInputs.cs`, registered where `WorkItemParts` is) sends `ParkWorkItem` and `ResumeWorkItem` after the gateway's own paths did their work: the runtime adapters' park after a failed launch (`runtime-failed`), the Copilot credit guard's park
  (`credit-guard`) and the operator's unpark through the admin endpoint. The key is the `park.at` of the verb's answer, the key `ItemAdminFeed` uses for the same park, so a path that both tell the saga of is one input. After the resume `V2Ownership` offers the item again at its next read of the
  saga's table (test: park, no offer, resume, offered again). The sweep's auto-unpark is W9.10f's. A message the host does not take is tried again twice and dropped with a log line, as in `V2VerbFeed`.
- *Close, reopen, card moves, hold labels.* A second handler of the inbox message (`SagaIngressHandler`, derivation in `SagaIngress`), for an item with a saga only. `item.closed` and a poll's `board.card` with `open` false give `ItemClosed`: the saga completes the instance unless a merge happened or
  the merge step decides (a merge closes its own issue); `item.reopened` (webhook only: a poll reports every open card after a restart) gives `ReopenWorkItem`; a person's card move gives `CardMoved`, and a move back to an earlier step's column sends the item back to that step; a hold label gives `HoldObserved`
  (parks the item `hold: <label>`; its removal lifts only that park). `ProjectionEcho` is consulted for the card: the newest write is the strict echo, a webhook also counts the few latest writes of the last two minutes (`WroteRecently`), and no event counts before the card projection applied the version of the step entry
  (`WorkItemState.CardSeq`, the sequence of the step-entered decision). Limits: the webhook translation emits `item.labeled` for the factory and environment labels only, so a hold label is looked at on that event and on every card event of an item the saga holds (one REST read of the issue), not the moment it is added;
  widening the translation changes the events v1 consumes and is left to W9.10g's reconciler. Card moves of an item on its Merge step are not acted on (its card is not projected: the merge happens in the validation column), nor those forward or to a column of no step.
- *Budgets.* `RuntimeManager` reports to `SagaInputs.Budget` (under the arm of the step the item is on, read from the saga's state): a launch once the execution exists, a failure where it counts one (a failed start, a lost session, ended-with-item), an idle follow-up, and a session that connected or pushed (`Registered`).
  A failed start counts a failure and no launch (v1's counter counts both). The saga parks at its own limits, as the manager does; the second park is a refusal.
- *Start refused.* The manager also reports every refused start to the scheduler (`StartRefused`, no slot named: the item's current one): `host-full` for "no capacity" and for a start the host refused for room, `quota`, `reserved` or `start-failed` otherwise (the slot is freed, the request dropped). Nothing starts sessions
  from the saga yet (W9.10j); until then the scheduler answers `NotFound` for an item it holds no slot of.
- *Exit 75.* `start-worker.sh` exits 75 with `host-full:`; Octopus ends the Start worker task Failed. `OctopusRunbookRuntime` reads the failed task's error message ("... exit code 75") and, failing that, its raw log (`GET /api/tasks/{id}/raw`) for the line; the instance gets the status `HostFull`, and `RuntimeManager.End` ends it as
  `host-full`: the execution and its launch are refunded, no failure is counted (not against the phase, not for the runtime's health), the item is taken back and offered again after the next reading (`runtime-no-capacity` holds new starts for `listSeconds`, at least 15 s). Any other failed start stays a failure.
- *Merge facts.* A feature item entering its Deliver step (`step-entered` decision) starts `MergeFactsDue`: the merge commit's check runs (`IItemIssueReader.ReadCheckRuns`) are read every minute until all have completed, at most 30 times, and `MergeFactsRead` is sent with them and the pull request's files; the
  saga settles a docs-only merge as docs (WorkSteps). A GitHub that does not answer is a Build not finished.
- *Depends on.* `DocsIntakeJob.DependenciesMet` is Test-DependencyMet for the home repository's references (closed, or in the done or a hold column; the board's cards answer, an item off the board is read from its issue, one that cannot be read is not met; references to other repositories block nothing, as in v1).
  `Verbs.Sweep.ps1` no longer leaves an item that names a dependency to v1's intake when it is of a plan in `intake.v2Plans`: v1 skips them all.
- *`LabelProjection` deleted.* Nothing projects labels (`engine:v2` is gone with the per-item routing; the labels are the scripts' and the people's): `ProjectionLabels.cs`, its tests and `ProjectionTargets.Labels` are removed. The state comment projection is W9.10i's.
- *Also.* The comment of `V2VerbFeed` about the unpark is true now. `docs/design/verb-inventory.*`: the three park and unpark entries keep their script call until W9.9a (leaves W9.9a) and say the saga is told. (W9.9a: the Runner asks the park-item and unpark-item routes first; the entries are `router` entries now.)
- *Proof.* Fworker.Workflow.Tests `SagaProducerInputsTests` (the saga's side), Gateway.Tests `SagaInputsTests`, `RuntimeSagaFeedTests`, `HostFullTaskTests`, `SagaIngressTests`, `MergeFactsPortTests`, `DocsIntakeJobTests` (dependencies), Fworker.Integrations.Tests (`ProjectionEcho.WroteRecently`, the
  issue, check run and file reads), and the octopus scenario for exit 75 (`octopus: a start the host refused for room (exit 75) ...`, claimed with the whole suite on v2). The scenario was written without a local Tier 1 run (see the pull request).

**W9.10i as built (#561): the state comment projection wired (the rollback path).** Only where the engine acts (`Engine.V2Acts`); tdd stays in the shadow run (no `SagaStateProjection`, no `ProjectStateComment` queue, no executor hook; the train's shadow keeps no `RequestItems`). No schema change: the projection versions table (target `comment`) and the
state document are what they were, so no script of the reserved range 220 to 229 is used.
- *The edit is a function of the saga's state.* `SagaStateProjection` (`gateway/Ports/StateProjectionPort.cs`) takes every `WorkItemDecided` the pure `V1StateFormat.Wants` names (step entered, parked, resumed, park expired, hold cleared, moved back, merged, completed, handed back, settling, undelivered,
  master green, broken or timed out, settle re-armed, demoted, fix-forward, checks green), loads the item's `WorkItemState` (never older than the decision) and brings the state comment to what it says, through `StateCommentProjection.Apply` with the decision's sequence number as the version. A decision handled
  twice or late gives the same comment; an older one is skipped by the version; a decision of an earlier generation is left to its own. The edit never replaces what v1's verbs wrote: a park for the same cause (the saga's reason is "code" or "code: text", v1's is the code, which may carry a colon of its own) stays as v1
  wrote it, a merge record of the same commit is completed. `StateCommentProjection.Apply` writes nothing, and moves no revision, when the edit changes nothing (`Unchanged`; a missing comment is not created for it), so v1's own verbs are not made to conflict with a write that says nothing new. The read, the edit and the save are one step under the item's lock (`IItemLock`, the file `item-N.lock` the rule scripts and the verbs take too, 30 s; a busy lock is a fault of the try), so a verb of the item and the projection do not write the comment over each other.
- *What is written (`V1StateFormat`).* **Park:** `park` = `{ reason: code, at, until }` (`until` = the saga's `ParkUntil`, else the decision's time plus `timing.parkHours`), with `failed` and `sha` for master-broken and `pr` and `head` for fix-forward-exhausted, for the parks the saga makes (merge-blocked,
  merge-failed, fix-forward-exhausted, the hold parks as code `hold`, the master parks, the budget parks); removed by every decision after which the saga is not parked (resumed, park expired, hold cleared, a step entered, moved back, completed, handed back, settling, undelivered, master green, demoted), and then,
  for a resume or an expiry, `fix_forward_rounds`, `demotions` and `master_defect` too (the counts start from 0, as `unpark-item` and `Reset-ExpiredPark` leave them). The counters only go up from the saga's (v1's verb counts a demotion one past the limit and keeps it). **Verdict:** `verdict.processed` once
  the item is merged or parked for a failed merge (nothing merges the PASS again); a PASS the saga voided (demoted, fix-forward) is removed with `bot`. **Merge record:** for a merge v1 did not record (the gateway's own, the train's, a person's the saga saw) `merge` = `{ sha, awaiting, merged_at, pr }` and the ends
  of `Invoke-MergePickup` (`handoff`, `awaiting_checks`, `bot` removed); `awaiting` follows the saga: `master-green` (waiting or not watched), `master-broken`, `master-timeout`, `release-candidate` when master is green (with `green_at`, `green_via` and `rc_deadline`; v1's own `late-rc` or `release-queue`
  stay), `release-queue` with the release while it settles in the final environment; the one re-run of the failed jobs as `rerun_attempted`, `rerun_at`, `rerun_runs`; done (`awaiting` null, `done_at`) when the saga completed (the docs plan at the merge), and `done_at` removed again when the item is open again.
  **Step and column:** a `flow` stamp for the stage entered (not repeated when the session's verb stamped it; the last 15 are kept), the owner's `claim.column` follows the item when the saga moved it, and `awaiting_checks` of an earlier step goes. The card is `DocsProjection`'s, as before.
- *On a queue of its own.* The handler of `WorkItemDecided` only sends `ProjectStateComment` (an `OutgoingMessages`, as `MergeFactsHandler` does) to the local queue `state-projection`, durable and one at a time: GitHub being slow or down holds up this queue (three tries in the handler with 0.5, 2 and 8 s, then it throws `StateProjectionRetryException` and the handler's policy schedules the message again after 1, 5, 15 and 60 minutes and then every hour for ever, never the dead
  letters: the last decision of an item, a merge or a park, has no later one to project it, and v1 handed the item back would write the merge again as a person's; the edit is a function of the saga's state when the message runs, guarded by the sequence version, so a late run never undoes a newer decision) and neither the history of the decision, nor the card, nor the merge facts. A refusal for good (a state over its budget) is logged. With no GitHub credential the projection is a no-op.
- *The train's deployments.* `PromoteRelease` now names `Items` (the items of the release, "owner/repo#N"; the train keeps them per requested release in `TrainState.RequestItems`, so the retry of a request names them too; a shadow train keeps none). After `PromoteRelease` made or found the deployment, the executor
  (`DeliveryExecutorService`, with `PromotionStateWriter`) writes `merge.promoted.<environment>` = `{ task, at, release }` on the state comment of each item that has a merge record, as `Verbs.Promote.ps1` does: v1 reads it to know that a deployment is the gateway's own, whose Sign-off it answers. A fault of the write is
  thrown, so the handler runs again (the ledger answers the settled command; the write is idempotent). `SignOffWaitRecorder` (a handler of `TrainDecided` "signoff-waiting", published once per change) writes `signoff_wait_since` (the Sign-off's `Since` in the train's state) into the first item's record when its task is the
  record's, so the 2-hour limit of `signoffWaitHours` survives the kill switch. Items of another repository are not the writer's (the factory has one home repository).
- *Not here.* The claim ref and the claim's lease and heartbeat (the plant's, `ReleaseOwner`), `handoff` (v1's offer hint; v1 offers an item of a column without it), the journal lines v1's settle posts on the item (the saga's history is its journal), the labels, and the adoption of items at a start (W9.10m, W9.10n). A decision whose write
  failed is not left to the next decision of the item (see the queue above).
- *Proof.* Gateway.Tests `StateProjectionTests` (the edit per decision, idempotent, and the projection with its retries, versions, generations and refusals; a fault that outlasts the tries is projected later with no new decision; the retry policy), `PromotionExecutorTests`, `DocsPlanWiringTests` (the host with the real handlers writes the merge record, done, and the stages to the state comment), and
  **`StateProjectionRollbackTests`: the plan's row, the kill switch on**: the saga's decisions are projected by the real projection over the fake GitHub and v1's real rule script (`sync-board`) sweeps the item. A parked item is neither merged nor offered, an item the gateway merged is neither picked up as a person's
  merge nor written to, and one that awaits master's CI is settled without a pick-up; each case has its control, the same item as v1's verbs left it, on which the same sweep merges and picks up. Fworker.Integrations.Tests (`Unchanged`, `Update`, `PromotionRecord`), Fworker.Workflow.Tests (`DeliveryTrainTests`: the command names the items,
  also in its retry). The saga scenarios of Tier 1 check the merge record on v2 (`saga.ps1`, `Saga-V1Merge`; the declared difference of the docs block says the record is the projection's); these edits were written without a local Tier 1 run (see the pull request).

**W9.10g as built (#559, first pull request): the sweep's plant-wide steps as recurring jobs; the faster reconciler; retention.** Only where the engine acts (`Engine.V2Acts`); tdd stays in the shadow run and its v1 sweep runs every step as it always did (no `skip_steps`, no job).
- *The jobs* (`gateway/Ports/PlantStepJobs.cs`, slots in `RecurringJobSlots.cs`, leader only): `claim-ref-reap`, `board-guard`, `qc-sync`, `replenish` and `backlog-low` (the keeper's count of `design_waiting`). The gap renewal (issue 94) is not a job (review of the first pull request): it must come before the first full sweep after a restore can offer a claim that ran out in the gap as a relaunch, and a job cannot promise that order (the scheduler's first run is 3 s after it starts and a job waits at most 30 s for the restore); it stays in the Sweeper's first full pass, which takes the gap as before. Each job runs at the sweep's own interval (`FACTORY_SWEEP_SECONDS`, 180; `recurringJobs.plantStepsSeconds`; never faster than every 5 seconds, since the Tier 1 sweep runs every 2 and five script passes that often would only load a CI machine), the QC sync at
  most every minute (`recurringJobs.qcSyncSeconds`; a sweep a webhook asked for ran after 60 s at the earliest). A job does nothing while the process may not act, before the plant is restored (as the sweep waits), while the saga table has not been read once, and, for the reap, the guard and the QC sync, while the GitHub budget is
  below its reserve (the steps the sweep skipped on a low budget); the replenishment and the count ran whatever the budget, and still do. A pass the lease ended (`fenced`, or `MayAct` false when it returns) is dropped.
- *The sweep leaves them out.* The Sweeper adds `skip_steps` (`claim-ref-reap`, `board-guard`, `qc-sync`, `replenish`) to every `sync-board` call of a full or incremental pass, still takes the gap for the first full pass, and applies none of the results (`replenish`, `design_waiting`, the QC records); the script reports `<step>-job` in `skipped`. The script
  (`Verbs.Sweep.ps1`) got `plant_steps` (the first pull request; the second one removed it, the jobs being C# now): a pass that names steps reads the board list once and runs only those (`claim-ref-reap`, `board-guard`, `replenish`, `design-waiting`): no item loop, no intake, no seen file, no resume point, answer `plant: true`; a pass that names `items` is never a plant pass. The
  verb `sync-board` called over HTTP adds no reconciler pass to a plant pass.
- *QC sync through `QcService`.* The `qc-sync` job calls the verb of the same name through the router (`QcHub.Sync`): `QcService` where the engine acts, the rule script elsewhere; the records, teardowns and approvals go through `QcHub.Reconcile` as the sweep's did.
- *The steps are still the script's rules* (the board guard, the reap, `Replenish.ps1` and the count): the jobs are a call of `sync-board` that the inventory lists (`plant-step-jobs`, leaving at W9.10g). Porting them to C# is the second pull request of this row; W9.11b cannot remove `Verbs.Sweep.ps1` before.
- *The board reconciler.* Every 2 minutes (`recurringJobs.boardReconcilerSeconds`, 30 to 600; was 5; the owner's default of 2026-10-30 instead of a `projects_v2_item` webhook). Its memory is in SQL (`factory.BoardSeen`, script 200, `SqlBoardSeenStore`): read before the first pass (a store that cannot be read ends the pass with
  `seen-unavailable` and publishes nothing: an empty memory would republish the board), written after each pass (the changed cards; the cards that left the board are forgotten). A card that closed while no gateway was up is news again, which is what `ItemMergedDeriver` derives the merge from. A closed card with no row is history, as before.
  **Cost:** one GraphQL request (one point) per page of 100 cards; `BoardReconcileMeter` counts them per pass and `/api/v1/status` `v2.reconciler` shows `passesLastHour`, `requestsLastHour`, `requestsPerPass` and `estimatedRequestsPerHour` at the configured interval. For a board of up to 100 cards that is 30 requests an hour at 2 minutes,
  60 for up to 200 cards, of the 5,000 points an hour of the GraphQL resource. The measured number on tdd is recorded on #559 when v2 acts there (the reconciler does not run in the shadow run).
- *`ItemMerged` derivation.* Where the engine acts the retry steps (5 s, 30 s, 2 min, 5 min, 15 min) end in a retry every hour for ever (`ScheduleRetryIndefinitely`) instead of the dead letters; a derivation that keeps failing shows as an overdue scheduled envelope in `/api/v1/status` `v2.scheduled`. The shadow run keeps its dead letters
  (`ItemMergedDerivationHandler.RetryIndefinitely` is set by `Attach(..., acting: true)`).
- *Retention* (`RetentionPrune`, job `retention-prune`, once a day, `retention.*` of `instance.json`): `ItemHistory` older than 180 days of an item with no open instance; the shadow run's records older than 30 days (`ingress.inbox.*`, `delivery.train.shadow-command`, `delivery.train.replay`, `DeliveryShadowComparisons`); transcripts older than 14 days, then
  the oldest finished sessions until the stored lines are within 256 MB (the transcript cap; a session still writing is never cut). Deletes go in chunks of 5,000 rows, at most 40 chunks per table and run; the log line says `more` when a table has more.
- *Slots.* The local queue of the jobs runs four at a time (was two); the scheduler looks at the clock every 5 seconds, or at half the shortest interval of a slot that has a job where that is shorter (5 seconds in the Tier 1 tests), never faster than once a second.
- *Proof.* `PlantStepJobsTests` (what each job asks for, the gates, the results applied, the intervals), `SweeperGapTests` (the first full sweep waits for the restore and then carries the gap with the jobs on, a failed sweep gives it back), `PlantPassScriptTests` (the real `sync-board` against the fake GitHub: the board guard, the count, `skip_steps`, and a first full pass after a gap that offers no relaunch for a claim that ran out inside it), `BoardSeenTests` (the reconciler across a restart, the SQL store, the meter),
  `RetentionPruneTests` (SQL), `ItemMergedRetryPolicyTests`, `DatabaseScriptsTests` and `SchemaManifestTests` with script 200.
- *Not here (second pull request).* The C# port of the four script steps and the v2 claims of the Tier 1 scenarios that exercise each job (keeper.ps1, qc-gate, the board guard of scenarios.ps1, the gap of the leader scenarios): the scenario runners could not be run for this pull request, so no claim was added (`v2-claims.txt` unchanged).

**W9.10g, second pull request as built (#559): the four steps in C#; the reconciler's cost.** Only where the engine acts (`Engine.V2Acts`); the rule script keeps the same steps for the gateway that does not act (tdd while it shadows: its sweep runs every step as it always did).
- *The port* (`src/Fworker.Workflows/Reconcilers/PlantSteps.cs`): `ClaimRefReap` (`Invoke-ClaimRefReap`: under the item's lock taken without waiting, on a fresh read of the state; a ref of a closed or gone item at once, of an open one once it has been seen without a claim for a lease; the saga's items and another environment's are left alone; the time a ref was first seen is
  kept in the file the script keeps, `claim-orphans.json` of the state directory), `BoardGuard` (`Invoke-BoardGuard`: the open labelled issues, paged; under the board-intake lock taken without waiting; at most 20 attempts, three failures in a row or a rate limit end the pass; one journal line each), `Replenish` (`Invoke-Replenish` and the 24 topics, the glossary and the notes of `Replenish.ps1`; the finished item's state records `replenished`
  before the card is moved) and `DesignWaiting` (the count of the keeper's column). They stand on the ports of the keeper and manager verbs (`IKeeperGitHub`, the state store, the locks of `KeeperPort.cs`) plus `IPlantGitHub` (the claim refs, the labelled issues, the last column), and on the same files and locks as the scripts, so v1 and v2 exclude each other as before. A lease that ended is checked before each
  write (`PlantStepFenced`); a failure of a step is an error line of the answer, as the script's was. `PlantStepJobs` calls them, applies the answer as before (the plant's replenishment status, the keeper's count) and no longer runs a script: the inventory entry `plant-step-jobs` is gone, and so is the script's `plant_steps` pass (`skip_steps` stays: the sweep still leaves the steps to the jobs).
  `Verbs.Sweep.ps1` is as it was before the first pull request but for `skip_steps`.
- *One copy of a step.* The C# step cannot be killed on the job's timeout of 280 s as the script's pwsh was; the job only stops waiting. `PlantStepJobs` keeps the Task of each job and answers `busy` (counted in `Outcomes`) while the previous run of that job is still going, so a slow GitHub period cannot start a second replenishment beside the first (it reads the finished item's state and saves it without the item lock, as the script did, and two copies would create two issues). `PlantStepJobsTests` has the case.
- *Proof.* `PlantStepsTests` (Fworker.Workflow.Tests, the world of the keeper tests with the three reads added: each step's actions, calls, state and journal; the busy lock, the fence, the limits, the backpressure, the topics, the settings), `PlantStepJobsTests` (the jobs against a stand-in for the steps), `PlantPassScriptTests` (the script's `skip_steps` and the gap), the inventory and architecture tests.
- *The Tier 1 claims.* Not added in that pull request (they are in, see "W9.10g, claims as built" below): the scenario runners (`pwsh`) could not be run in this session, and the v2 job is blocking. The scenarios that exercise the jobs through the gateway's own sweep, to be claimed with their first green run on v2 (`run.ps1 -Engine v2 -Suite <suite> -Only <regex>`): suite `auto` (file scenarios.ps1) `^orphan claim refs` and `^board guard: an issue a verb is still putting` (the reap and the guard), suite `manager` (file keeper.ps1) `^keeper: replenish creates one docs-only` (the replenishment) (the count of the backlog-low job is checked by `^keeper: split-item ...`, already claimed in `v2-claims.txt`, and now runs on the C# step), suite `owner` `^owner: board guard` (the guard). The suite names are those of `run.ps1 -ListSuites`; the file names are not suites. #559 stays open until these claims are in `v2-claims.txt`. Scenarios that call `sync-board` through the verb route run the script's steps on v1 and v2 alike and prove nothing of the jobs.
- *The reconciler's GitHub cost per hour.* It cannot be measured from tdd's shadow run: the reconciler is registered only where the engine acts (`GatewayWolverineExtensions`, inside `V2Acts`; the shadow branch does not run it). What can be said today, from the real board and the query itself (2026-10-31): the project has 411 cards (`totalCount`), so a pass reads 5 pages; the page query costs 1 point (`rateLimit.cost` of the reconciler's own query, read once on the real project); at 2 minutes that is 5 x 30 = **150 points an hour**, 3% of the 5,000 an hour of the GraphQL resource,
  and it grows by 30 for every 100 cards. The page query of the jobs and the sweep (with the labels and the body of each card) costs 1 point a page as well (read once on the real project); each of the jobs that reads the board (the reap, the guard, the count, the replenishment while it is on) and the sweep reads it once an interval (180 s), another 100 points an hour each for this board. The running number (`/api/v1/status` `v2.reconciler` `requestsLastHour`) is a line of W9.9c's readiness checklist.

**W9.10g, claims as built (#559, third pull request, "Closes #559"): the Tier 1 claims of the plant-wide steps.** No schema change. Each scenario of the list above was run with `run.ps1 -Engine v2` (FACTORY_TEST_SQL a throwaway SQL Server 2022 container, the built gateway) and the ones that passed are claimed in `v2-claims.txt`:
- `auto | ^orphan claim refs:` (2 scenarios: the accept takes an orphan ref over, the sweep removes the ref of a closed item and keeps a young one; a busy item lock does not stall the sweep) and `auto | ^board guard: an issue a verb is still putting` (the guard leaves the issue to the verb that holds the intake lock, places it once the lock is free and journals it once), `owner | ^owner: board guard:` (placed, a stranded card repaired, the closed and the rival's issue left alone, journalled once, offered), `manager | ^keeper: replenish creates one docs-only` (the replenishment, its limits and its counts in `/status`). The count of the backlog-low job (`design_waiting`) is the check of `manager | ^keeper: (a worker-failure park|split-item)`, claimed since W9.6; it was run again here and passes on the C# count.
- *A finding, fixed here (the cause was in the code of the first row's port).* The board guard's `board-added` line and the replenishment's `replenish` line were refused by the C# journal ledger (`unsupported journal event`): `JournalEvents.Supported` is the list of `Verb_journal` and Add-Journal takes any event the text knows. The cards were moved but the guard's line was lost (a logged failure, `board-added-unjournalled`), and the replenishment line likewise; the scenarios "and journals it once" failed on v2. `JournalEvents.Writable` (the verb's events and these two) is what the ledger writes; the verb still refuses both (`JournalLedgerTests`). Without it a v2 gateway would have run the steps with no journal lines.
- *A declared difference.* In the owner board-guard scenario v2 writes the offered line through the Wolverine sink (source `gateway`) where v1's sweep script wrote it (source `rule`), as in the saga scenarios: a block of `golden/v2-differences.txt`.
- *Not claimed.* The QC sync job has the claims of the QC suites (`qc-gate`, `qc-worker`, `qc-*`), which were run on v2 before this row. The reconciler's cost is the estimate above (150 points an hour for 411 cards); the running measurement is a line of the W9.9c checklist.

**W9.10j as built (#562, first pull request; the second one carries the Tier 1 scenario, see Tests): the saga starts the sessions of its slots; the ledger's executions are not counted twice.** Only where the engine acts (`Engine.V2Acts`); tdd stays in the shadow run and v1 is unchanged (a plant offer
with no slot is routed as before; the stop retry and the host gate exist only with the dispatched commands, which only an acting engine has). No schema change and no DbUp script of the reserved range 230 to 239: the saga's new command is part of the state document (`StateCommand`, discriminator `start`).
- *The saga starts the session.* `FeatureFlow.Assigned` (a new grant, not a repeat of the slot it holds) sends `StartSession` (`StepContext.StartSession`: key `start|item|slot`, the arm the grant was made for, the slot, its runtime and host, the phase), published as `SessionStartRequested`.
  Nothing starts a session for a grant the item can no longer use (parked, past the session steps, another plan: those give the slot back, as before) or for a plan that uses no scheduler. A grant told twice publishes the same start again, which the handler takes as one.
- *The offer comes from the saga's state, at once.* `SessionStartPort` (`gateway/Ports/SessionStartPort.cs`, handler `SessionStartHandler`) reads the saga's table (`V2Ownership.RefreshAsync`; a read that fails throws and the host tries again), takes the item's offer from it (`OfferNodes`: nothing for an item with an
  owner, parked or past its sessions; a slot other than the saga's current one is `stale-slot`) and gives the plant that one offer (`Plant.PlanSagaOffers`, which keeps the item's runtime label and model profile, as an incremental pass would not). The offer node carries `slot`, `runtime` and `arm` from the
  saga's state (`V2Ownership.Slots`). The sweep still repeats the same offers from the same state (a full pass, and `Sweeper.PlanV2Offers` now through `PlanSagaOffers`) so that an offer the plant lost in a restart is made again and a full pass does not drop the assignment; W9.11 removes that repeat with the sweep.
- *The requested runtime is where the session starts.* `Plant.RouteRuntime` for an offer with a slot uses the slot's runtime, not the routing's or the label's (an item a Copilot task or Cursor agent owns stays with its owner), skips the preference for a connected session, and asks `CanStart(ledgerGranted: true)`:
  the runtime being off, not yet counted, unhealthy, over its quota or its host down still hold the offer (it waits, with the reason in the journal; it is never given to a connected session), but the manager's own concurrency and day budget are not asked again, because the scheduler admitted the session against
  them. `Begin` takes the slot and the arm; the ticket carries them to `StartRuntimeSession` (new optional fields `ArmId` and `SlotId`; the idempotency key is still the worker's name).
- *Ledger executions are subtracted, also after a restart.* `RuntimeManager.Load()` (what the scheduler is told runs outside its ledger) leaves out the executions begun for a slot and the day's count of them (`ledgerToday`, refunded with `RefundToday` wherever the manager refunds an execution that never started). The scheduler
  counted each ledger session once itself and the manager counted it again, so the day budget was reached at half and concurrency at half. The ledger's count is durable and the manager's is not, so the manager reads the ledger back (`RuntimeManager.ReadLedger`, set by `CapacityWiring.ConnectLedger` from the scheduler's store; the day's count
  of grants of the runtime and the slots it holds by item number, for the home repository's items): (1) when the day is counted from the runtime again (the first reading after a start, every UTC midnight) `ledgerToday` is seeded with the ledger's `ExecutionsToday` of the runtime for the current day, less the slots the ledger holds that no
  execution carries (granted, not begun, so not in the history yet), and at most `today` (a ledger of another day counted nothing for this one); (2) an execution the runtime lists at the first reading or adopts later (a host report) takes the slot the ledger holds for its item, and so is not counted as running outside the ledger.
  A ledger that cannot be read holds the reading back (`runtime-relisting`, as a history that cannot be read does), so a day is never counted without it. After a restart with a day budget of 30 and ten ledger sessions, `Load()` reports 0 running and 0 today and the scheduler grants twenty more. The error that the earlier
  version of this pull request accepted ("conservative, ends at the day's roll") is gone: tdd restarts several times a day.
- *A failed dispatch settles like "no capacity".* The commands go through `host.PublishAsync`; a host that is not running throws `WolverineHostDownException`, and `ProvisionCore` settles that as `no capacity` (the execution and the launch are refunded, `Refused(host-full)` frees the scheduler's slot and asks again, no
  failure, the runtime is not made unhealthy). Offers wait for the host before that: `DispatchReady` (the host's state is Running) is part of `CanStart`. Any other fault of the dispatch is still a start failure.
- *Failed stops.* A stop whose command could not be dispatched or whose result was a failure (`CompleteStop(ok: false)`) is tried again by the next readings (`Poll`) after a pause that doubles from `StopRetryPause` (30 s), `MaxStopAttempts` (4) times in all. The runtime's own reports end the record before that
  (the list that shows the execution ended; for the Octopus runtime the host report that shows the container gone); after the last try the record ends as `stop-failed` (no failure counted), and a container that still runs is counted by the host report as outside the ledger. v1's direct stop does not retry.
- *The reaper.* `CapacityReaper` is a recurring job (`capacity-reap`, every 5 minutes, leader only) that reads the sagas that are open and known to the scheduler (`SlotKnown`) and tells the scheduler (`ReapStale`, no key). The scheduler drops its request, its place among the items in flight and its hold for any other item that
  entered more than five minutes before the read, and never touches an item that holds a slot (the slot ends with its session, a refused start or its time limit; the next pass takes the rest). A read of the sagas that fails sends nothing.
- *Tests.* `FeaturePlanTests` (the start, once per slot, none for a grant given back), `CapacityReaperTests`, `SessionLedgerTests` (the subtraction, the refund, a run of eleven requests against a day budget of ten with no hold but the budget, the host that is down, the stop retries, the plant's route for a slot's offer,
  the port, the reaper), `LedgerRestartTests` (a manager restarted mid-day with history N and ledger N: `Load()` is 0 and 0; the plant's own sessions stay outside; a granted slot not begun; a ledger of another day; a ledger that cannot be read; an execution adopted later; the view of the ledger; and the audit's scenario end to end against the
  scheduler: day budget 30, ten sessions, a restart, twenty more grants and no more).
- *The Tier 1 scenario (second pull request, "Closes #562"): built, see "W9.10j, second pull request as built" below.* What follows is the reason it was a pull request of its own. The row's and #562's acceptance point "a v2 scenario runs more sessions than half the day budget" needs the scheduler on with a fake runtime in Tier 1: the profiles run with `scheduler.featureSlots` off (no runtime grants a slot there; audit finding 20). So this pull request says
  "Part of #562" and does not close it; the second pull request ("Closes #562") adds that scenario (the scheduler on, a fake runtime, more than half the day budget in sessions, the gateway restarted mid-run, the count checked after) and claims it in `v2-claims.txt` when it passes on v2. It is also a line of W9.9c's readiness checklist. Not claimed here.
- *Not in this pull request.* The plant's assignments still live in the plant (W9.10k puts them in SQL); the offer of a saga item that the plant lost in a restart comes back with the next sweep.

**W9.10j, second pull request as built (#562, "Closes #562"): the Tier 1 scenario, and the defect it found.** Only where the engine acts; v1 and tdd's shadow run are unchanged (the scenario passes at once on v1, which has no scheduler: its sessions are the plant's, suites `octopus` and `runtime`). No schema change and no DbUp script.
- *The scenario* (`saga.ps1`, `saga: slots: three sessions against a day budget of three ...`; claimed in `v2-claims.txt` with the profile `saga-feature`, so the suite `saga` is still claimed whole by its claims). The scenario writes into the configuration of its own gateway what the profile leaves off: `scheduler.featureSlots` true, the Octopus runtime against the
  fake Octopus with `maxExecutionsPerDay` 3, two hosts with `perHostCapacity` 2, `ackSeconds` 600 (the scenario plays the host, not a session that acknowledges), messaging on (the runtime's workers), and `FACTORY_INTAKE_WIP` 10; an owner session with room gives the intake its free slots. Items 1 and 2 are pulled by the intake, granted a slot, started on the
  runtime (one Start worker run is one session) and taken by their workers (the intake pulls the next item only when none waits in Technical Design for an owner); the hosts report the containers of the runs, as the host script does. The gateway is then killed and started again (the plant, the runtime manager's memory and the Wolverine host are gone;
  the scheduler's ledger in SQL and the fake Octopus's history are not). Checks: the runtime counts two sessions once each (`today` 2) before the restart; after it adopts both from the host's report and counts no more than two; no Start worker run is made again for an item that has one; the third item (more than half of the budget of 3 in sessions
  is the third and the second after the restart) is started and the budget is spent exactly (three runs, `today` 3); none of the three items was held `runtime-budget` before the budget was spent; the fourth item is held `runtime-budget` and no run is made for it. About 200 s.
- *The defect it found, fixed here.* After a restart the first reading of an Octopus-like runtime sees the sessions in the task history (`today` 2) but not in its list: the Start worker task of a detached worker ends at once and only the host's report finds the container. `SeedLedger` could not tell those two sessions from two slots granted and
  not begun, so it left the ledger's share of `today` at 0 and the two sessions counted twice (the scheduler's own ledger count 2 and `Load()` 2 outside) until the adoption, and the adoption did not move the share: the third session was held `runtime-budget` at a budget of 3 with two sessions run. The first pull request's unit tests had the list full at the first reading (the Cursor and Copilot shape).
  Now the adoption of an execution that takes a slot of the ledger raises the share by one (never above `today`, and not in the poll that seeds, which counts the executions that carry a slot itself). `LedgerRestartTests.SessionsTheHistoryCountsAndOnlyAHostReportFindsLater_AreTheLedgersWhenTheyAreAdopted` pins it (before the adoption the two sessions still count outside, as the seed cannot know better;
  after it `Load()` is 0 and 0).
- *The first pull request's "not in this pull request".* The plant's assignments in SQL are W9.10k (#585, merged). The sweep's repeat of the saga's offers stays until W9.11 (it is what brings back an offer the plant lost; the scenario's workers are offered their items again after the restart and take them again).
- *Runs.* The scenario alone, on v2, against a SQL Server of its own: 26 checks green (golden compare on, no golden file for the scenario). Before the fix: the third item held `runtime-budget`, the failure above.

**W9.10m as built (#564): adoption as reconciliation, the mapping from v1's state to the saga's.** The pure part of adoption: `src/Fworker.Workflows/Adoption` maps what v1 knows of one item to the `WorkItemSaga` row it stands for, and states the rule of the upsert. Nothing here runs yet: W9.9b gathers the snapshots and applies the results at every start (where
`Engine.V2Acts`; tdd stays in the shadow run, so v1's sweep, its verbs and the projection are untouched), W9.10n adds the fencing of a re-adoption. No schema change: the saga's row is a JSON document, so the revision the comment was read at is one optional field of it (`WorkItemState.CommentRev`, 0 in a row saved before it), and the
ledger, train, QC and merged-items rows are the tables that exist; no script of the reserved range 250 to 259 is used. One new action, `adopted`, is published once per adoption or change (not among the decisions the state comment's projection writes: the comment is where it came from).
- *The pieces.* `AdoptionMapper.Map(snapshot, settings, now)`: v1's state comment (claim, park, verdict, merge, counters, reviews, design, `awaiting_checks`, `qc`), the card's column, the issue, the pull request, the ledger's slot for the item and the delivery trains, to the row, the slot and the place in flight, the QC rows and the merged-items row, or a reason for
  skipping the item. `AdoptionReconciler.Reconcile(existing, projectedSequence, cardProjectedSequence, mapping, now)`: the rule. `AdoptionLedger.Apply` (the scheduler's state with the adopted slot) and `AdoptionTrain.SignOffs` (the train's state with the Sign-offs it must answer) are the edits of the shared state beside the row. All are functions of their inputs and idempotent.
- *The rule.* At every start, for every open item, an upsert in which the state comment wins when its revision is newer than `CommentRev`: no row, the mapped row is made (`Adopted`, one decision); a row the comment has not moved past stays (`Kept`); a newer comment is mapped and laid over the row (`Updated`, one decision whose reasons name the changed fields),
  keeping what the saga alone knows (generation, sequence, keys applied, start, the arm while the plan, step, park and verdict head are the same, the slot it holds, the merge calls made on this verdict, the clock of a wait for master it is in); a newer comment that says what the row says moves only `CommentRev` (`Unchanged`: the saga's own projection
  writes the comment, so its revision moves with every decision). Three guards: a row with decisions the projection has not written (`Sequence` above the projection version of target `comment`) stays, because the projection will write them and the comment is the older; a row whose card the projection has not moved
  to the step it is on (`CardSeq`, the sequence of the decision that entered the step, above the projection version of target `card`) stays when the card's column would make another step, because the column is the step the saga has left and taking it would
  end the live session (a restart between the comment's write and the card's); a completed instance is not brought back (W9.10n). The unchanged result is what advances `CommentRev` past the projection's own writes, so the projection needs no change:
  the writers of the comment (the projection, v1's verbs, the train's writer) all move its revision, so the rule does not tell the saga's writes from foreign ones; instead `Keep` carries what the comment does not hold, and a row the saga ran maps to itself.
  The failure and launch budgets, the mechanical fixes and idle follow-ups, the review rounds of the steps the item is not on, the approver, the owner's release of a park and the saga's wording of a park (the comment has its code) survive an update; the
  budgets and rounds only grow (a stale count parks the item sooner, it never loops).
- *Skipped, with a reason (cases X1 to X9; the v2 status block lists them).* `issue-closed`; `no-state-comment` (the intake has not started it); `settled` (`merge.done_at`); `hold-column: X` and `card-done` (no merge record); `column-of-no-step: X` and `not-on-the-board`; `docs-merged` (the docs plan ends at the merge); `delivered-and-settled` (delivered to the final
  environment and past its settle window); `plan-not-carried: X` (`intake.v2Plans` does not name it).

The table (one `Fworker.Workflow.Tests` case per row, `AdoptionMappingTests` and `AdoptionReconcileTests`, the row's letter and number in the test name):

| Row | v1 says | The saga's row (or the other state) |
|---|---|---|
| P1 to P6 | `design.docs_only` true / false / absent; a legacy body marker (`DocsOnly.ItemIsDocsOnly`); the pull request's files | docs plan / feature plan / feature plan; legacy marker docs plan; a docs-only design whose pull request changes anything but documentation is the feature plan (`DevelopStep`'s rule) |
| S1 to S3 | card in the design, development, validation column | Design, Develop, Verify (the claim's column places an item that is off the board) |
| S4, S5 | validation column with a FAIL / a PASS (`verdict.passed`) | Verify / Merge with `VerdictHead` = `verdict.head_sha` and `VerdictSummary` |
| S6 | Development done, waiting for CI (`awaiting_checks`, park `development:awaiting-checks`) | Verify, `PullRequest` and `Head` from it, `ChecksState` pending on that head (code plans); the gateway's own wait is not a park |
| S7 | `merge.sha` | Deliver, `MergeCommit`, `PullRequest` = `merge.pr`, no lease |
| S9 | the state's revision, `flow[0]`, `design` | `CommentRev`, `StartedAt`, source "adopted from v1 (state revision N)", generation 1, arm `1.1`; `DesignMarker` false for an item past its design with no decision (the design step's exit) |
| B1 | `reviews[<column of the step>]` {round, last, ref} | `ReviewRounds[step]`, `ReviewsComplete` |
| B2 | `fix_forward_rounds`, `demotions` | `FixForwardRounds`, `Demotions` |
| B3 | park `watchdog-relaunch-budget`, `session-relaunch-budget`, `validation-no-verdict` (`park.launches`) | parked with that reason, `Launches[step]` = `park.launches` (else the limit) |
| B4 | park `worker-failure` | `Failures[step]` = `MaxFailuresPerPhase` |
| B5, B6 | `claim.launches` in the step's column (the Merge step stands in the validation column) | `Launches[step]`; a claim of another column counts nothing; `claim.session_launches` is not carried (noted) |
| K1, K2 | a live park (`until` after now) / an expired one | parked with the reason verbatim and `ParkUntil` = `park.until` (the sweep's expiry only where `Policy.Sweep` is set) / no park |
| K3 | park `inspector-escalated` on a PASS | Merge, parked; `OwnerIdleDue` = `park.at` + `idleReleaseMinutes` (the saga's `Park`) |
| K4 | park `pass-without-auto-merge` | Merge, parked, no idle release |
| K6, K7, K8 | merged, `awaiting` master-broken / master-timeout, with its park or (expired) without | Deliver; `MasterState` broken (`MasterFailed` = `park.failed`) / timeout, parked master-broken / master-green-timeout; without a live park the wait is still watched, not parked |
| L1 | a live owner claim (`lease_until` after now, `owner`) on a code plan | `LeaseDue` = `lease_until`; a slot in the ledger (`AdoptionSlot`, id `adopted|item|generation`, runtime from the ledger or `scheduler.runtime`, expiring with the lease, not an execution of the day); `EverGranted`, `SlotKnown` |
| L2 | the same, and the ledger holds a slot for the item | the ledger's slot id, runtime and host; nothing adopted |
| L3 | a live claim of a session that is no owner | the lease only |
| L4, L5 | an expired claim / no claim, on a code plan, in a session step | no lease, `SlotAsked`, an `AskSlot` in the outbox (resume = the item was claimed before: rank 2) |
| L6 | the docs plan (no slots) | `LeaseDue` = the claim's, else a new arm's lease |
| L7 | parked | no lease, no slot |
| L8 | the pull request's files | `Footprint`, from the pull request |
| M1 | a PASS on the docs plan | Merge; `MergeRequest` attempt 1 on the verdict head, in the merge line (`TrainEntered`), the command published with the row |
| M2 | a PASS on a code plan | Merge; `MergeAwaitingChecks` (the watcher says when the head is green) |
| M3, M4 | a PASS on a code plan, unparked / parked | `SlotKnown` and an `AdoptionFlight` (the file-overlap rule keeps it among the items in flight until it is cleared) / neither |
| D1, D2 | merged, `awaiting` master-green, with a re-run (`rerun_at`, `rerun_runs`) | Deliver; `MasterState` waiting from `merged_at` (from the re-run), due +`masterGreenTimeoutMinutes`, the re-run's runs |
| D3 | `awaiting` release-candidate / late-rc / release-queue | `MasterState` green, `MasterGreenVia` = `green_via` |
| D4 | a plan or policy that watches no master | no `MasterState` |
| D5 to D8 | the delivery trains (`DeliveredItems` "item\|commit", `DeliveredBy`, `Current`, `Superseded`, the deployments) and `merge.release` | `Delivered[environment]` = the release that carries the merge (the train's record, else the current release, else the release v1 queued the item for or the one that superseded it, when it succeeded and was no rollback) |
| D9, D10 | delivered to the final environment | settling from the deployment's finish, `SettleUntil` + the settle window, arm `1.2`, the timer; past the window: skipped |
| Q1 | `qc` map {pr: record} | `AdoptionQcRow` (key `owner/repo!pr`) for `IQcEnvironmentStore.Save` (the store keeps the newer revision) |
| G1, G2 | `merge` {sha, pr, merged_at, by}, labels, verdict | a `MergedItem` row (source "adopted", the gates of `ItemMergedDeriver.GatesOf`, the dependencies of the body), message id fixed by item and commit so that the webhook's or the reconciler's derivation finds it taken |
| R1 to R15 | the rule above | adopted, unchanged, kept (comment not newer; projection behind; card behind), updated (new arm and the old session ended; same arm kept), completed instance skipped, the rows beside the state given in every case; a saga-run row (budgets, reviews of earlier steps, approver, a park with the saga's wording) is unchanged by the projection's revision (R8b, R8c) and keeps them across a foreign update (R8d); the card that lags is not the step (R13, R14) |
| C1 to C5 | an `AdoptionSlot` and an `AdoptionFlight` | the ledger with the slot (the item out of the queue, in flight when past design, no execution counted); idempotent; a slot the ledger holds is kept; a copy |
| T1 to T8 | `merge.promoted.<env>` {task, at, release, `signoff_wait_since`} and the Sign-offs Octopus shows | the train's `SignOffs` (`Since` = the earliest recorded wait, else Octopus's), `Requested`, `FirstRequested`, `RequestNumbers`, `RequestItems`; a Sign-off at somebody else's deployment, at another environment's, or already known is left |

- *Not here.* The reads that fill a snapshot, the writes of the results, the backfill of `DeliveredTo` from Octopus's history and of `MergedItems` from the merged pull requests, the validation or reset of train rows built in shadow, and the result in the status block (W9.9b); the fence of a re-adoption after `FACTORY_V2_OFF` (W9.10n, as built below). The merge line
  (`MergeTrainState`) has no state of v1 to adopt: v1's queue is in memory, so an adopted PASS asks to enter it (M1) and the train rebuilds it.

**W9.10n as built (#565): re-adoption fencing after `FACTORY_V2_OFF`.** The pure part, as W9.10m was: nothing here runs until W9.9b gathers the snapshots and applies the results at every start (where `Engine.V2Acts`), so tdd's shadow run and its v1 are unchanged. No schema change: the generation was always in the saga's row and in
every timer and command, the ledger and the merge line are JSON documents, and no script of the reserved range 260 to 269 is used.
- *The fence is the instance.* While the kill switch is on, v1 acts from the state comments and v2 does nothing, but what v2 left is still there: timers in the message store, commands in the outbox, slots and requests in the ledger, places in the merge line. A re-adoption that took the comment's word and went on with the same
  instance would let all of it act on an item v1 moved, merged or finished. So `AdoptionReconciler.Reconcile` takes `afterInterval` (v1 acted since the engine last did; W9.9b says so) and, then, an item whose row the comment changes is the next generation: `AdoptionFence.GenerationFor(row, afterInterval)` is the generation the mapping is
  made for (`AdoptionMapper.Map(..., generation)` names the arm `N.1`, the adopted slot, the slot request key and a settling arm after it; a mismatch is refused), the row keeps its counters, keys and decision numbering (`Keep(newGeneration)`), and the old instance's timers and inputs end as `NotFound` in the saga (it checks the generation
  first, `IGenerationInput`). An item the comment does not change keeps its instance and its timers (`Unchanged`): v1 did nothing they could contradict. The result carries `Fence` (`LedgerFence`: the item and the generation the ledger keeps) and `FencedFrom`.
- *Without a break nothing changes:* the update keeps the generation (R6), the guards hold (R7, R13), a completed instance is not touched (R9), a settled item is skipped (R10). After a break the two guards (the projection behind, the card behind) do not hold the row back, because v1 acted on the comment alone and the decisions the projection
  never wrote were never seen by it; the projection drops a decision of another generation (`SagaStateProjection.Project`), so the old instance's unwritten decisions cannot overwrite what v1 wrote.
- *Every command and timer names its instance.* The timers of `TimersOf` and `MergeRequested`, `SlotWanted`, `SessionStartRequested` already did; `ClearItem`, `FootprintChanged`, `MergeTrainLeft` and `OwnerReleaseRequested` now carry `Generation` too (0: any, as the events from outside that name only the item). The scheduler answers a `ClearItem` or
  `FootprintChanged` of another instance than the one its entries name `Stale` and clears nothing (`CapacityScheduler.GenerationOf`; `InFlightItem` now records the generation); the merge line ignores a `MergeTrainLeft` of an earlier instance. `SessionEnded` and `StartRefused` name the slot, and slot ids are unique. Not fenced: the owner release
  (`OwnerRelease` in the gateway runs the verb `release` for the item's claim holder and does not read the saga; the message carries the generation for it to be checked there, W9.9b).
- *Ledger slots cleared.* `AdoptionLedger.Fence(ledger, fence)` removes the slot, the waiting request, the place among the items in flight and the hold of the item that belong to another instance than the fence keeps (an entry with no generation counts as another), and everything of an item whose instance ended (`KeepGeneration` null). It is run before
  `AdoptionLedger.Apply` (whose adopted slot of the new instance is then not blocked by the old slot) and is safe to run again. The new instance asks for its slot under its own number; no `EndSession`, `ClearSlot` or `LeaveTrain` of the old instance is sent (they would name the new generation and be ignored).
- *Saga rows of items v1 completed are closed.* After a break, an open row whose mapping is skipped for `settled`, `docs-merged`, `delivered-and-settled`, `card-done`, or `issue-closed` while the saga has not merged (`AdoptionFence.V1Ended`) becomes `AdoptionKind.Closed`: completed as the saga's own `Complete` leaves a row, the decision `adopted-closed`
  (not one the comment's projection writes), the commands that ask back its session, its scheduler entries and its place in the line (under its own generation), `Fence` = nothing of the item stays. The other skips (a hold column, no state comment, a column of no step, a plan not carried) leave the row to the saga's inputs.
  An ended instance that the comment shows open again (newer than the instance's revision, not merged) is `Reopened` as the next generation, its decisions numbered after the old ones.
- *Train rows built before the interval.* `AdoptionTrainRows.ValidateMergeLine(line, sagas)` removes the entries that no longer stand for an open saga's request (no row, ended, another generation, not on Merge, merged, no place recorded, another pull request); the saga that still wants its merge asks again with its row.
  `AdoptionTrainRows.ValidateCandidates(train, merged)` removes the delivery train's candidates that no merged-items row holds (another commit of a reopened item, a merge never recorded) and those delivered here; it runs after W9.9b's backfill of `MergedItems`. The request bookkeeping of the train (`Requested`, `RequestNumbers`) is left: the expiry drops
  a request Octopus never shows, and a request number must never go back (the `PromoteRelease` key).

| Row | Case | Result |
|---|---|---|
| F1, F2, F3 | the generation a mapping is made for: none / no break / a break; the arm, adopted slot and request key named after it; a ledger slot of an earlier instance | 1 / the row's / the next; `N.1`, `adopted\|item\|N`, `ask\|item\|N\|1`; not taken for the new instance's |
| F4, F5 | the comment changed an item after a break (another step; the same step) | `Updated`, generation + 1, new arm, the timers and the request under the new number, `Fence`, `FencedFrom`; without a break the generation stays |
| F6, F7 | the comment does not change the item; the old instance's timers and inputs | `Unchanged`, same instance, its timer acts; every timer and input of the old instance is `NotFound`, the new instance's arm answers |
| F8, F9 | the projection and the card behind after a break; a mapping for another generation | the comment wins; refused |
| F10 to F14 | v1 settled / merged docs / delivered and settled / Done card / closed the issue; skips that do not end an item; no break; a place in the line | `Closed` (no timer, `NotFound` afterwards, the commands under its generation, `Fence` nothing); the row stays; skipped; `MergeTrainLeft` of the instance |
| F15, F16 | an ended instance that v1 opened again; no break, a comment not newer, a merged item | `Reopened`, generation + 1, sequence continues; skipped |
| L1 to L6 | the ledger fence (an earlier instance, the kept generation, no generation, an ended item, the adopted slot after it); `ClearItem` and `FootprintChanged` of an earlier instance | entries removed / kept / removed / everything / applied; `Stale`, nothing cleared; without a generation as before |
| M1 to M3, D1 | the merge line's entries (stays; no row, ended, another generation, not Merge, merged, no place, another pull request); a leave of an earlier instance; the delivery candidates | stays / removed; ignored; kept or removed by the merged-items table |
| G1, G2 | every timer and every command names the generation | all do, but the end of a session, which names its slot |
| S1 | the plan's scenario: two items run on v2, the switch goes on, v1 merges and settles one and merges the other, the switch is unset | the settled one is closed and gets no further input; the other is the next generation on Deliver; the ledger and the line hold nothing of the old instances; every timer from before the interval is `NotFound` |

- *The scenario, and what is still owed.* The plan's row asks for a v2 scenario. The adoption is not wired yet (W9.9b), so there is no gateway start to run it against; S1 is that scenario at the level of the pure rules and the saga (`AdoptionFenceTests`), and the Tier 1 version (kill switch drill: an item v1 merged is not touched by v2, no stale timer acts) is
  the kill-switch drill of the cutover gate (W9.9c, step 2), which this row's fencing is for. Not in `v2-claims.txt`: no scenario of the suites changes.
- *Not here:* detecting that a break happened (W9.9b: the kill switch was on at the last stop, or v1 ran since v2 last acted), applying the results and the fences, the backfill of `DeliveredTo` and `MergedItems`, and the owner release's generation check in the gateway.

**W9.10q as built (#588): the item verb fallbacks closed.** Only where the engine acts (`Engine.V2Acts`: the item verbs and the merge verbs are registered only there); tdd stays in the shadow run and its v1 is unchanged. The item verbs run no
rule script any more: `IItemV1`, `ItemDelegated`, `ToV1` and `ItemVerbsV1` are deleted, and the inventory (`verb-inventory.json`) lists no item verb fallback (its two entries left are the merge verb's `peer-approved` rules). Each port is the script's
branches, with its fields, codes, exits and texts, proved by `ItemVerbFallbacksTests`, `MigrationNumbersTests` and `MergeVerbTests` on the in-memory GitHub of the item verbs' tests.
- *The completion of Development* (`ItemVerbs.Development.cs`, `ItemCiRule`): the item's open pull request, `empty-pr`, `development-docs-only-pr`, the migration check, then the one CI rule for the checks of the head (`Get-RedHeadRuling`: `running`,
  `rerun` once per head, `rerun-wait`, `deferred`, `master-defect` with its cache, its report once per set of checks and its fixed state, `red`), the awaiting-checks park with exit 21, and `ftWithPendingChecks`. The checks come through a new port,
  `IItemChecks`, over the checks read model (`ChecksReadModel.ChecksFor`, and the new `BaseFailures` for `Get-BaseFailures`); `ItemChecksAdapter` (`gateway/Ports/ItemChecksPort.cs`) turns its refusals into the verbs'.
- *`open-pr` for a `copilot/*` branch and `item-pr` for an owner task* (`ItemOwnerPrs`): `Use-CopilotDraft` (the takeover of the draft: retarget or close a stacked one, name the item, mark it ready, record it), `Close-PrWithBranch`,
  `Close-StrayCopilotDrafts`, `Set-PrItemTitle`, `Set-PrReady` and `Resolve-OwnerPr` (a successor branch, a newer recorded pull request, the retarget and the consolidation of a stack), as the script has them; `item-pr` takes the item's lock for the
  resolution and answers from the state when it is busy. The delivery verbs have the same rules on their own ports (`Delivery.Verbs.OwnerPrs`); they are not shared because the two sets of ports differ (the item verbs' wire and state).
- *The accept of a QC environment* was a sixth fallback that the inventory attributed to W9.1 and that W9.1 left in the code. It is `QcService.Accept` through `IItemQc` (`ItemQcAdapter`); the verb holds no lock, as the gate takes the item's lock itself.
- *The merge verb* (`MergeVerbService`): the migration-number collision (`MigrationNumbers`, the pure rule of `Get-MigrationCollision`, and `GitHubMergeMigrations` for the pull request's files and the scripts directory of the base branch) is a refusal 10
  `migration-number-taken` of the session's call with the script's fields, and a refusal 10 of `merge` or `approve-pr` that v2 answers counts for the model ladder (`CountRefusal`, the refusals file the item verbs and the scripts count in).
  *Not ported:* the script's gateway-mode branch of the same check (the sweep's own merge demotes the item to Development with the renumbering text). The saga's own merge call (its retry timer, the C# direct merge) does not ask the migration
  check, so a collision is refused only at a session's `merge` call. That is the scope of the issue (the merge verb); the saga's merge step is W7.2's and a gap for the owner to place.

**W9.9a as built (#361, first of three pull requests; W9.9b and W9.9c follow): the router takes the gateway's own calls.** Only where the engine acts (`Engine.V2Acts`); tdd stays in the shadow run, uat and prod have no engine, and where the engine does not act `Runner.Routes` is never set,
so every call is the rule script as before (`V2VerbRoutes.Build` returns the empty table first, and `ItemVerbsPortTests` pins `Routes` null under the kill switch). No database script (the reserved range 280 to 289 is untouched), no setting.
- *One door.* `Runner.RunAsync` asks `Runner.Routes` (the `V2VerbRoutes` table, set at the end of `V2VerbRoutes.Build`) before it starts anything: a verb that has a route is answered by the route's handler, whoever calls (a session through the verb endpoint, which
  reaches the same table in `V2VerbRouter` first, or the gateway itself: `PlantRuleHooks`, the sweep, `ItemPrLookup`, the park and unpark paths, the admin endpoints, the demo upload, the runtime adapters, `QcHub`, `BaseWatch`, `FeatureChecksJob`, `OwnerCiReleaseService`,
  the transcripts, the journal lines of `BaseUpdate` and `PlantTicker` (#477), the webhook, the service verbs). A verb with no route (`auto-promote`, `leader-lease`, `plant-snapshot-*`) is the script's in both cases: they are the declared exceptions of the inventory.
  `Runner.RunScriptAsync` stays what a route falls back to (the board verbs without their parts, the merge verbs for an item the saga does not own); a route must never call `RunAsync`, which would ask itself again (`MergeVerbHandlers.Script` did, and calls `RunScriptAsync` now;
  `V2VerbRoutesTests` scans the gateway for it).
- *Removed.* `DeliveryVerbBridge` (the delivery verbs only), `ServiceVerbCalls` (the six service verbs only), `Runner.V2Own` and `BoardWiring.OwnCalls` (`journal` and `ingest-event` only), and the route tables that `BaseWatch` and `QcHub` carried since W9.3 and W9.1. The table is
  built at start, in `UseGatewayLifetime`, before the read of the revocations and the first sweep (it was built by `BaseWatch`'s constructor); `V2VerbRoutesTests` pins the order.
- *What changes where the engine acts.* Calls that used to run the script now take the route: `claim-item` (renew, release) and `expire-claim`, `park-item`, `unpark-item`, `resettle-item`, `item-pr`, `pr-checks`, `sync-board`, `ci-release`, `copilot-owner`, `copilot-settle` and `publish-demo`
  (the last four already did through the bridge). `sync-board` is the sweep's own call: its route is the script's pass with one pass of the board reconciler after it (W9.8), so a sweep now also runs a reconciler pass beside the recurring job (one pass at a time, `BoardReconciler`'s gate).
  The park and unpark paths tell the saga with the same key as before (`ItemAdminFeed`, `park.at`), so a path that both tell it of stays idempotent.
- *The inventory* (`verb-inventory.json`, `.md`): the entries that were `runner` or `bridge` with a leaving row of W9.9a, W9.10g or W9.10j are `router` entries (they ask the table, the Runner is the door); the three bridge entries are deleted; the kind `bridge` is gone. What is left that runs a script
  is the declared exceptions: `auto-promote`, `leader-lease`, `plant-snapshot`, `transcripts` (callers; the last reads the ref store through `RunScriptAsync`, the legacy fallback of the database backend) and `merge-peer-approved`, `merge-hybrid-script` (fallbacks). `ArchitectureInventoryTests` pins the list.
- *Tests.* `V2VerbRoutesTests` (the Runner asks the routes first, the bridges are gone, no route calls `RunAsync`, the table is built at start), `ItemVerbsPortTests` (the table is the Runner's where the engine acts and null under the kill switch), `BoardRoutesTests`, `QcRoutesTests`, `ServiceVerbsTests`,
  `DeliveryVerbHostTests`, `SweeperGapTests` and `PlantStateGatewayTests` (the stubs set `Runner.Routes` where they set `Runner.V2Own`), `ArchitectureInventoryTests`.
- *Not here.* No Tier 1 claim is added: the gateway's own calls are exercised by the claims that already run on v2 (`v2-claims.txt`); the new paths (the plant's claim renew, the sweep's `sync-board`) are for the measured batch of W9.9c. Adoption is W9.9b; the switch, the drills and the gate are W9.9c.

**W9.9b as built (#361, second of three pull requests; W9.9c follows): the adoption wired.** Only where the engine acts (`Engine.V2Acts`) with a database, a home repository and a GitHub credential; tdd stays in the shadow run and its v1 is
unchanged (nothing below is made there: `AdoptionWiring.Make` is called inside the acting branch of `GatewayWolverineExtensions`, the `adoption` block of the v2 status is added only for an acting engine, and the one thing a
non-acting process can write, the kill switch marker, needs `FACTORY_V2_OFF` and an environment named in `v2Environments`, which no environment is today). W9.10m's mapping and W9.10n's fencing are not changed; this pull request gathers
their inputs and applies their results.
- *The run* (`src/Fworker.Workflows/Adoption/AdoptionRunner.cs`). One run reads the open cards of the home repository from the board (a card in a column of no step is counted as skipped `column-of-no-step` and not read; a card in a step
  column, a hold column or Done is read; an open row of the saga whose card is closed or gone from the board is read too, so that v1 ending it is seen), and for each item its issue and state comment and its pull request (`IAdoptionGitHub`, new on
  `HttpMergeFactsReader`: `ReadV1Record`, `ReadPullRequestFacts`, `ListMergedPulls`, `CommitsPast`; the files of an open pull request through `IItemIssueReader`). It maps the snapshot (the ledger's slot for the item and the delivery trains are in it) for
  `AdoptionFence.GenerationFor(row, afterBreak)` and reconciles it with the row and the projections' versions. The results are applied in this order: the ledger (`AdoptionLedger.Fence` for an instance that ended, then the adopted slot), the QC rows (a record
  the table holds is not written again), the merged-items row (`TryRecordAll` with the adoption's message id: a row the webhook or the reconciler holds is theirs and nothing is sent; otherwise `ItemMerged` goes to the host so that the delivery train makes the
  merge a candidate, unless the saga knows already that the release reached the train's environment), and last the saga's row (a save of the revision that was read; a writer that was first means the item is read again, up to four times), then
  the decision, the commands and the timers of the row through the host (`WolverineHostService.PublishAsync`, and `ScheduleAsync` for a timer, new). A run that broke off between two of these steps finds the same result again, because the row is last and every
  write before it is idempotent; a host that takes a message late loses nothing, because the row carries its timers and commands and `WorkItemRearm` sends what is overdue and silent. The decision `adopted` is never projected (the comment is where it came from), so
  after the save the projections' versions of the item (`comment` and `card`) are brought to its sequence (`IProjectedSequences.Advance`); without that the rule "decisions the projection has not written" of W9.10m would keep the row against every later comment.
- *Beside the items.* (1) The merged items are backfilled from the newest merged pull requests of the default branch (`MergedWindow` is `mergeQueueDepth` + 4, between 4 and 12; only those past the commit tdd runs when GitHub can compare): each is derived with the
  deriver of W5.0 (`base.merged`, the message id fixed by the pull request and its commit), so a merge no row holds becomes a row and an `ItemMerged`. (2) The Sign-offs Octopus shows now (`PendingSignOffReader`: the project's newest deployments, the executing ones'
  pending interruptions) become the train's to answer with `AdoptionTrain.SignOffs`, which now also takes one the watcher's poll put into the train first (it is not answered and not yet counted as requested) and teaches it whose it is and how long it has waited
  (T6b); one that was answered stays as it is. (3) Only when (1) ran: the delivery trains' candidates that no merged-items row backs are removed (`ValidateCandidates`; `IMergedItemStore.Held`, new). (4) The merge line's entries that stand for no open request
  are removed (`ValidateMergeLine`). (5) `DeliveredTo` is backfilled only where the table has no row (`DeliveredToTable.RecordIfAbsent`, new) from the saga rows' `Delivered` (which the mapping took from v1's state and the trains) and from the trains' `DeliveredBy`, whose history is
  Octopus's, replayed by the watcher; a row the train wrote itself is never replaced. A train that has no state yet (the watcher has not polled) is not created by (3), (4) or (5); (2) creates it when a Sign-off waits.
- *When it runs* (`AdoptionJob`, the recurring job `adoption`, every minute on the leader, the first run 3 s after the scheduler starts). Once per term of the lease (the epoch of the claim; a process that loses the lease and takes it again begins a new term), and only after the
  plant's restore is over and the GitHub budget is not low, so the owners' claims are in place and the lease decides who adopts. A run is checked against the lease between items: a lease lost ends the run `aborted` and leaves the rest to the next holder; two
  processes that overlap write rows with revisions, so the loser reads again and the result is one row per item (`B3`). A partial run (an item or a part failed) is tried again the next minute, at most five times a term, and then the term is over with its errors in the
  status; except after a break (below): there the tries go on past five, with pauses growing to five minutes, until a run is `done` (status `breakPending`, `retryAt`), because an item whose read failed was never fenced and the heartbeat would hide the break from the next start. The break of W9.10n is read from the markers (new script `290_AddEngineMarkers.sql`, `factory.EngineMarkers`, expand-only; the reserved range 290 to 299): `acted` is the heartbeat of the acting process (written once a minute after the term's adoption is over, never
  before, so that a start that broke off finds the same evidence), `v1-ran` is written once by a process that the kill switch turned back to v1 in an environment named in `v2Environments` (`KillSwitchMarkerService`). A break is `v1-ran` newer than `acted`, or an `acted` older than
  `AdoptionBreak.Gap` (30 minutes: a rollback to an image without the engine writes no marker); the first start (no marker) is none. The decision is kept for the term. Without the markers the run waits instead of guessing (the wrong answer is the unfenced one).
- *The hold between the host's start and the adoption (review of #602).* The fence of W9.10n works once the adoption has moved a changed item to its next generation, but the host recovers its durable inbox, outbox and timers when it starts, and the adoption waits for the plant's restore and
  for GitHub. So a timer of the old instance (`PhaseLeaseExpired`, `MergeRetryDue`, `SettleWindowElapsed`...) could reach a row that was still generation N and act on an item v1 had moved or merged. `WolverineHostService.BeforeHost` runs after the schema is good and before the Wolverine host is
  built: `ReadoptionGate.Begin` reads the markers and, after a break (or when they cannot be read), holds. While it holds, `WorkItemService` answers a generation-bearing input (`IGenerationInput`: the timers, the session and slot messages, the card and pull request facts) of an open row that the
  adoption has not examined in this term with NotFound ("awaiting re-adoption") and changes nothing; the runner opens each item as soon as its row is saved (or found to stand), so the adoption's own messages pass. What is dropped by the hold is the timers (`WorkItemRearm` sends an overdue one
  again once the item is open) and reports that the adoption reads again from the board and the state comment. The job releases the gate when the term is not after a break or when its run is `done`; a partial run after a break keeps it, and the status `adoption.hold` shows `held`, `why`, the number of items
  opened and of inputs refused. No schema change, and nothing is held where there is no break, in tdd's shadow run (no gate, no `BeforeHost`) or in v1.
- *The same hold on the plant's side (second review of #602).* The saga's old-generation rows are also read without going through `WorkItemService`: `V2Ownership` builds the plant's offers from them (the sweep's full pass, `PlanV2Offers`, `SessionStartPort`, the docs intake's design check), and
  `WorkItemRearm` resends the last transition's commands (`SessionStartRequested`, `MergeRequested`) of every row that has been silent for ten minutes, which after a break is every row. An unexamined row (still generation 1, Open, Develop, a slot set) would have had a session started on an item v1 moved
  or merged, before the adoption made it generation 2. `V2Ownership.Offers` and `OfferNodes` skip an item for which `ReadoptionGate.Refuses` holds (asked at each read, so the offer is back as soon as `Adopted` is called or the gate is released; `Owns` is unchanged, v1's sweep still leaves the item
  alone); `WorkItemRearm` sends no timer and no command for such an item and counts it (`Held`, status `rearm.awaitingReadoption`). Tests: `V2OfferTests` and `SessionLedgerTests` (no offer and `no-offer` from `SessionStartPort` until `Adopted`), `WorkItemRearmTests`.
- *The status.* `/api/v1/status` `v2.adoption` (only where the engine acts, also while the schema is not good): `state` (idle, waiting with `waitingFor` plant-restore or engine-markers, running, done, partial, aborted, not-leader), `attempts`, `done`, `doneAt` and `last`, the latest run:
  `outcome`, `afterBreak` with `breakWhy`, `cards`, `read`, `adopted`, `upserted` (adopted + updated + reopened + closed), `updated`, `unchanged`, `kept`, `closed`, `reopened`, `skipped` and `skippedBy` (the mapping's reason codes without their detail: `column-of-no-step`,
  `hold-column`, `issue-closed`, `no-state-comment`, `settled`, `docs-merged`, `delivered-and-settled`, `card-done`, `plan-not-carried`, `instance-completed`), `conflicts`, `publishFailed`, `errors` with `errorKinds` (exception types), `qcRows`, `mergedRows`, `mergedPublished`,
  `mergedFromPulls`, `deliveredToWritten`, `signOffs`, `candidatesRemoved`, `mergeLineRemoved`.
- *Tests.* `AdoptionRunnerTests` (the items, the skips by reason, the second start, two processes at once, the break, the Sign-offs, DeliveredTo and the merged items, the trains' rows, the lease and the faults), `AdoptionJobTests` (the term, the restore, the heartbeat, the break from the markers),
  `AdoptionSqlTests` (the markers, `RecordIfAbsent`, `Held`, and the whole adoption over the SQL stores started twice; they need `FACTORY_TEST_SQL`), `AdoptionWiringTests` (the job and the block only where the engine acts, the marker only for the kill switch in a v2 environment),
  `V2HealthTests`, T6b of `AdoptionReconcileTests`. Tier 1: `saga: adoption: a start with v1 items in flight ...` (v2 only): two items in v1's hands (one in Development with a live owner claim, one in Technical Design), the gateway started, both adopted and reported, the owner's slot in
  the ledger, and the next start adopts nothing and decides nothing again; claimed in `v2-claims.txt` with the profile `saga-feature`.
- *Which plans it adopts (found by the v2 job of #602).* The adoption carries the plans of `intake.v2Plans` and no others. `WorkItemPolicy.Carries` answers true for every plan when none is named (the saga on its own, as the unit tests run it), so a plain v2 run (the Tier 1 claims
  of every suite but `saga`, where `intake.v2Plans` is empty) adopted every item the scenarios put in v1's hands (a card with a state comment): `V2Ownership` then took them from v1's sweep, and the scenarios of auto, owner, manager and promote failed on v2. `AdoptionWiring.Make` sets
  `CarriedPlans` to the empty list when none is named, so every item is skipped `plan-not-carried` and v1 keeps them, which is what "nothing starts a saga until the owner names the plans" means for the items already in flight.
- *A defect of W9.10m found by the Tier 1 scenario.* A row of a plan without slots whose claim holds no lease has the lease of a new arm (now + the step's lease), which moves with the clock; the rule of the upsert took the later of the two leases, so every start that found the comment on a
  new revision (the plant writes it) read the item as changed, wrote a decision and renewed the lease. The mapping now notes `NewArmLease` and the row that keeps its arm keeps its own lease (R3b).
- *Runs.* `Fworker.Workflow.Tests` 1645 and `Gateway.Tests` 2223 green with `FACTORY_TEST_SQL` (the one failure, the list of the recurring slots, was the new slot and is fixed), `Fworker.Integrations.Tests` 323, the smoke test, and the Tier 1 scenario alone on v2 (`run.ps1 -Suite saga -Only 'adoption: a start' -Engine v2 -V2Profile saga-feature`): 15 checks green.
- *Not here (W9.9c).* The switch (`deploy.overrides.json`), the kill-switch drill (the mechanism is here: the marker, the fence, the break), the overlap drill, the measured batch, the claim list equal to `run.ps1 -ListSuites`. Known limits: the backfill of the merged items takes the newest pull requests
  when the train has no running commit for tdd (a fresh train), which can make a candidate of a merge tdd already runs; a Sign-off at a deployment that left the project's newest 30 deployments is not seen; the sweep of v1 runs beside the adoption until the first run is over (the rows are written to the
  table the sweep's filter reads, `V2Ownership`, which refreshes before each pass).

**W9.10k as built (#563; the first pull request built it, the second one carries its Tier 1 scenarios, see Tests): the plant survives a restart and is fed from saga state.** Only where the engine acts (`Engine.V2Acts`); tdd stays in the shadow run and v1 is unchanged (nothing below is made, registered or called
there: the store, the feed, the correction and the new inputs are built by the composition only for an acting engine with a database and a home repository). Script `240_AddPlantState.sql` (the reserved range 240 to 249) builds four tables, expand-only.
- *The plant in SQL.* `factory.PlantAssignments` (one row per offer or accepted assignment: the id the workers' item tokens name, item, kind, role, column, mode, reason, worker, pull request, pin, accepted and when, created, and the scheduler's slot, runtime and arm of a saga item),
  `PlantHolders` (the owner of an item, `Kind` owner, and its bot holder, `Kind` bot), `PlantDrains` (the names the operator drained and the names the plant manager drained) and `PlantState` (row `plant`: the rest of the hand-over as one JSON document, the manager's mode, the day of
  its last summary, the digest's carry, the leases the last sweep saw, the Copilot and Cursor owners, with the epoch and holder of the term that wrote it). `Fworker.Workflows.PlantState` (`PlantStateSplit`, `SqlPlantStateStore`) splits the very snapshot `Plant.Snapshot()` writes into the rows and the
  document and joins it back, so the plant's own `Restore` and its validation (names, numbers, codes) are the same as for the ref. A save replaces every row in one transaction and is refused when the stored epoch is later than the writer's (a process that lost the lock).
- *Written and read by the snapshot coordinator.* `SnapshotCoordinator.Sql` replaces the rule script `plant-snapshot-save` where it is set: the write of every change (`LoopAsync`, also without the lease), the final write of a hand-over and the write at the stop. At start the plant is read from the database first
  (`how: "sql"` in the status's `plantSnapshot.restore`, any age within a day, the predecessor's `final` flag deciding the gap the first sweep renews) and the ref is read only when the database holds no plant yet (the first start of this version, `how: "no-predecessor"` and the like). A database that
  cannot be read starts an empty plant (`skipped: "load-failed"`) as a failed read of the ref did, and the restore waits for the schema (`FactorySchemaStatus.IsGood`, at most two minutes) where the host's own upgrade runs in this process. The suites run with `FACTORY_PLANT_SNAPSHOT=0`, which switches this off as
  it switched the ref off, so the plant of one scenario never reaches the next one (the item numbers repeat in the fake).
- *Corrected at start.* `PlantRestoreCorrection` runs after the restore and before the first sweep (the sweeper waits for it): for each item the plant holds something for it reads the item's saga (`WorkItems`) and the claim. An item whose saga completed keeps no assignment, owner or bot holder
  (`Plant.ForgetItems`); an item whose saga is open keeps its restored owner only if the claim, read with `get-item` through the verb router, says so: the claim's holder replaces the owner (the session that held it loses its accepted assignment), no claim (released, taken back, never made) drops
  it, a claim that cannot be read leaves the owner as restored. An item with no saga is v1's and the first sweep corrects it. The result is the `corrected` block of the restore (`looked`, `open`, `forgotten`, `owners`, `unread`).
- *The plant fed from saga state.* The sweep leaves the saga's items alone, and `SweepV2Filter` takes everything it says about them out of its answer, so the plant saw no lease, park, pin, priority, runtime label or Release Queue entry for them, and `Plant.ReplaceOwned` dropped their owners after two full
  sweeps. `SagaPlantFeed` (`gateway/SagaPlantFeed.cs`) builds the same fields from the saga's states (`V2Ownership.States`) and from the labels of the reconciler's cards (`BoardReconciler.LastCards`; `BoardCard.Labels`, read by an `HttpBoardReader` built `withLabels` only where the engine acts), and `Sweeper` adds them
  to the answer after the filter in every full pass: `owned` and `owners` (the plant's own owner of the item with the column of the saga's step, the step's lease as `lease_until` and whether it is parked), `parks` (code, column, when the park ends), `release_queue` (the open Deliver-step items with a merge,
  with the release they reached), `pins`, `priorities`, `runtimes`, `profiles` and `holds` from the labels by the rules of the rule script (`worker:`, `priorityLabels`, `runtime:<kind>`, `testLabel`, `ownerHoldLabel`). Labels not read yet (no pass of the reconciler) claim nothing. `held` and `pass_unmerged` are not fed: a
  saga item waits for its dependencies in the intake (D1) and the saga merges its PASS itself.
- *Digest and patrol.* `V2ItemViews.Latest` (the last good read) gives the plant the parked items of the engine (`SagaAttention`, since the newest "parked" decision), which `Plant.DigestInputLocked` adds to the parks the owners' triggers announced (one entry per `parked|item|code`); the sweep refreshes the views once per full
  pass. A stalled or lease-overdue owner of a saga item is reported through `owned` (its step's lease), as for any item; `DigestBuilder` itself is unchanged. The dashboard and `/api/v1/digest` already carried the engine's items.
- *The manager's actions reach the saga.* `manager-action` release-owner and requeue send `OwnerReleaseAsked` and `RequeueWorkItem` to the saga of an item it owns, as unpark sends `ResumeWorkItem` (`IKeeperSaga.OwnerReleased` and `Requeued`, `KeeperSagaPort`): the first records the decision only (the verb has ended the lease itself and the plant offers the item again, as in v1; a `ReleaseOwner` carries no holder and, run late, would release the claim of the session that took the new offer), the second records it (the offer comes from the state: a session step, no owner, not parked) and is refused for a parked, delivered or Merge-step item. Neither changes the step, the arm or the lease.
- *The release check counts the saga.* `Plant.ReleaseCheck` adds `saga-merge-in-flight` (a merge call asked for and not answered: `MergeRequest`) and `saga-session-starting` (a slot granted for an item for which the plant holds neither an owner nor an assignment yet); the endpoint reads the saga's table first.
- *Tests.* `PlantStateStoreTests` (split and join; the real tables: replace in one transaction, the epoch guard), `PlantStateGatewayTests` (a plant restarted from the store: assignments, ids, owners, holders, drains, slots and the mode; an empty store reads the ref once, an unreadable one starts empty; the correction from `WorkItems` and
  the claims; the feed's fields and their merge into the sweep's answer, a sweep through `Sweeper`, the owner that survives with the feed and is dropped without it; the digest's parked items; the release check), `PlantStateDatabaseTests` (the same restart on a real SQL Server with the correction), `SweepStepsTests` and `ManagerCommandTests` (the two new
  inputs and what tells the saga), `BoardReaderLabelsTests`.
- *The Tier 1 scenarios (the second pull request).* Four scenarios of the manager suite, `act: saga item: ...` in `manager-actions.ps1`, claimed in `v2-claims.txt` (`manager | ^act: saga item:`); on v1 each says that the item has no saga and passes. Each restarts the suite's gateway with the intake on (the docs plan), has the intake pull a
  Todo item and the owner session `sg1` of the suite `saga` take it, acts through the real supervisor client and reads the saga's history from the dashboard (`v2Items`); at the end the item's issue is closed (the webhook says it) so that its saga completes and the next scenario starts clean. They are: `unpark` (the owner reports a failure, the item is
  parked in the state comment and the saga; unpark ends both, the saga decides `resumed` and the session is offered the item again); `release-owner` (the lease ends, the saga decides `owner-release-asked` with the reason and keeps its step, the session is told `item.taken_back`, and the session that takes the new offer keeps its claim a few seconds later: nothing
  of the saga released it); `requeue` (an offered item nobody holds: the saga decides `requeued`, the session is offered it again; a second requeue of the then owned item is refused by the verb and the saga hears nothing); and the restart (the suite's `FACTORY_PLANT_SNAPSHOT=0` is lifted for this gateway only, which is stopped with SIGTERM and started again: `restore.how` is `sql`, the offer is back
  under the id in the session's hands and an `accept` with that id answers 0, which a new offer of the sweep could not; a second restart with the owner: the claim confirms it and `restore.corrected` changed nothing). About 55 s each and 110 s for the restart on a development machine. Also not here: the owner of a saga item comes from the plant's own record of the accept and the corrections above (the claim itself is read only at start, not at every sweep), and the digest's `lease-overdue` for an item with no owner is not a patrol entry
  (the saga's own lease timer re-arms or parks it; `/api/v1/digest` lists it).

**W9.10p as built (#555): the app's uat and prod stay with v1's `AutoPromoter`** (owner decision 2026-10-30, decision 2 of the audit). Where the engine acts the train promotes nothing in uat or prod, and the loop that calls the rule
script `auto-promote` keeps promoting them; the train acts for tdd only, where it observes the release lifecycle's deployment and publishes `DeliveredTo(tdd)`. #545 (W9.4) stops the loop wherever `Engine.V2Acts`; this row narrows that stop
to the day the train takes the environments over. It changes the shared gateway in one place, the composition's line that decides whether the loop starts (`AutoPromoter.Runs`); everything else is inert unless the engine acts.
- *The switch.* `delivery.autoPromote.trainActs` (`config/instance.json`, false; `InstanceConfig.TrainActs`). `AutoPromoter.Runs(instance, engineActs)`: the loop starts when `autoPromote` names this gateway's environment, except where the engine acts
  and `trainActs` is on. One of the two promotes: the rule script promotes every environment of `autoPromote.environments`, so the train takes them all or none. W9.12 sets `trainActs` in `deploy.overrides.json` and removes the loop;
  nothing in the deployed overrides names it today.
- *The train in shadow.* With the engine acting and `trainActs` off, `DeliveryShadowSettings.From` gives the policy no promoted environment and names the autoPromote environments in `DeliveryPolicySettings.ShadowEnvironments`; their
  `DeliveryPolicy` has `Acts` and `Shadow`. The train decides there as it does in the shadow run (the same decisions, the same state), but what it would send, `PromoteRelease` and `AnswerSignOff`, leaves as `ShadowCommand` (name, key and
  release as plain text), so no executor sees a command for uat or prod. `dependenciesDelivered` is off for a shadow environment (v1 has no such rule; W9.12 turns it on with the environments). tdd is `Promoter.Pipeline`: its policy neither
  acts nor shadows, and `Settle` still publishes `DeliveredTo(tdd)` for the items of a succeeded deployment.
- *Recording and comparison.* `DeliveryActingShadowHandlers` is `DeliveryShadowHandlers` for a host where the engine acts: the `TrainDecided` of a shadow environment goes to `DeliveryShadow.ObserveV2` (the `AutoPromoter` hands its pass to
  `ObserveV1`, as in the shadow run) and a `ShadowCommand` is recorded (`executed: false`); a decision of an environment the train promotes for real is left to the history. `/api/v1/status` and `GET /api/v1/delivery-shadow` show the
  comparison, which is the evidence the W5 gate (restated above) reads for W9.12.
- *Where it is registered.* `DeliveryActingWiring.Configure` (`gateway/Ports/DeliveryExecutorPort.cs`, what `Attach` calls for the acting host) registers `DeliveryActingShadowHandlers` beside the train's, the router's and the executor's handlers, and the
  `DeliveryShadow` they record to; `Attach` sets `DeliveryShadowHolder.Shadow`. The composition registers the holder and the comparison tick (`DeliveryShadowService`) where the engine acts and `AutoPromoter.Runs` (so the `AutoPromoter`'s `OnActions`
  reaches `ObserveV1`, and `/api/v1/status` and `GET /api/v1/delivery-shadow` show the comparison). Both are absent once `trainActs` is on: nothing is in shadow then. `DeliveryActingShadowHostTests` runs `Configure` in a real Wolverine host: the decision
  of a shadow environment lands in the comparison, the `ShadowCommand` is recorded and no `PromoteRelease` reaches the executor; with the environment promoted by the train the command reaches the executor and the shadow records nothing.
- *Parity.* `v2-claims.txt` claims two scenarios of the suite `promote` on the v2 engine (`run.ps1` names the gateway environment of `autoPromote` `v2ci` there): a release goes to uat, its Sign-off is answered, then to prod, each deployment
  created once; and a Sign-off of a deployment the gateway did not create is left alone. Tests: `DeliveryUatProdV1Tests` (the settings, `Runs`, a started gateway whose engine acts has the loop, the holder and the tick on, and none of them once `trainActs` is on), `DeliveryShadowEnvironmentTests` (the
  train in a shadow environment, the handlers, the comparison with an `AutoPromoter` pass).

**W9.4 as built (#356), the delivery train verbs.** Owner decisions of 2026-10-28 stand: hybrid composition (v1's plant offers the sessions and v1's sweep script still runs what v2 does not own), tdd only, the W7 scope cut (no smoke
evidence, no overflow, no merge queue). Everything is registered only where `Engine.V2Acts`; v1, the shadow run and `FACTORY_V2_OFF` are what they were.
- **The verbs** `settle`, `publish-demo`, `ci-release`, `copilot-settle`, `copilot-owner` and `private-build` are answered by `Fworker.Workflows.Delivery.Verbs`, ported one for one from `Verbs.Delivery.ps1` and `Verbs.Pr.ps1`: the same refusals with the
  same codes and exits, the same answer fields (`verb`, `ok`, `exit` as `factory.ps1 --server` writes them), the same GitHub calls in the same order, the same state comment (the format v1's `Get-State` reads, through `GitHubJournalStore`), the
  same comment texts (`Add-Comment`'s robot line, the journal marker and the session footer). The helpers they call are the kit's (`RuleKit`: `Get-ItemInfo`, `Get-ProjectMeta`, `Move-Card`, `Close-Issue`, `Find-ItemPr`, `Get-ChecksFor`,
  `Resolve-OwnerPr`, ...; Fworker.Workflows never references Integrations, so GitHub, the state comment and Octopus are the ports `IGhTransport` and `IItemStateStore`). The D4 docs-only rules (`Fworker.Domain.DocsOnly`) serve `copilot-settle`
  and `settle`. `copilot-settle` opens the item's pull request of a task's own branch through the verb `open-pr`, answered by whoever answers it (the item verbs' route, W9.2, or the rule script): it does not copy the script.
- **The gateway side** (`gateway/Ports/DeliveryVerbHost.cs`, the route file `gateway/Verbs/Routes/Delivery.cs`): one HTTP client with the identity of the rule scripts (the author App or `GH_TOKEN`, user agent `factory-worker-gateway`, so the golden
  writes keep their source `rule`), the test cause id of the request (`X-Fake-Cause`), a retry of a network error and of a 502 to 504 as `Invoke-GhRaw` does, the rate limit headers to the GitHub budget, the lease checked before a write of the
  verbs v1 fences (`settle`, `copilot-settle`, `ci-release`: `lease-lost`, exit 20), the item's lock for a verb that takes one (an in-process lock; the revision of the state comment guards the rest, as it does against a script run), the timeout of the
  call. A call writes a log line `{"v2route": verb, "exit": n}`. The gateway's own calls of these verbs (the CI release, the Copilot owner and settle, the demo upload, which called the runner directly) go through `Runner.RunAsync`, which asks the routes (W9.9a; the `DeliveryVerbBridge` that did this for the six verbs only is gone);
  a gateway where the engine does not act always runs the rule script. A session's `publish-demo` and `private-build` are still a sign of life for the item's step lease (`V2ActivityTap` into `V2VerbFeed.Observe`).
- **The train acts** (`gateway/Ports/DeliveryExecutorPort.cs`, `src/Fworker.Integrations/Octopus/OctopusPromoter.cs`): where the engine acts the delivery train is attached (the router, the train's handlers, the Octopus watcher's events through the
  same port the shadow run uses) and its two commands are executed from a durable queue of their own with the runtime commands' retry rule: `PromoteRelease` creates the deployment with the route of `Verbs.Promote.ps1` (executions API for an
  untenanted project) unless Octopus already lists one of that release to that environment (read just before), `AnswerSignOff` takes responsibility by PUT and submits Proceed or Abort (an interruption that is no longer pending is answered
  already). A refusal for good (403 with the permission Octopus names, a project or release that is not there) is `IntegrationFailed` to the log and the history; a transient one is retried, then settled as a failure (#475). What the train decides is
  in the history (`delivery.train.<action>`). `AutoPromoter` (the loop that called the rule script `auto-promote`) does not start where the engine acts, so one of the two promotes. The train acts for the environments `delivery.autoPromote` names, as v1 did.
- **Parity.** The Tier 1 scenarios that call these verbs run unchanged on v2 against the golden writes: `auto | ^(push gate|video label):` (`private-build`, `publish-demo`) and `owner | ^owner:.the.owner.publishes.the.demo.video` (the demo upload endpoint, through the Runner) in `tests/scenarios/v2-claims.txt`; no declared difference is
  needed. The unit tests (`DeliveryVerbs*Tests`, `OctopusPromoterTests`, `DeliveryVerbHostTests`) pin each verb's refusals, answers and call order against what the scripts do.
- **Not here, on purpose or because it is another item's:** the v1 sweep (the script of `sync-board`, W9.8) still calls its own `Invoke-Settle`, so the settle of an item in the sweep is the script's until W9.8; the verb `auto-promote` has no route and the script
  stays until W9.11; the journal lines v1's promotion wrote on the items (`promoted`, `promotion-waiting`, `promotion-signoff`) are not written by the executor (the history has the train's decisions); the `merge.promoted` records and `signoff_wait_since` in the state comment are, since W9.10i;
  `Get-AppActionsPermission` reads the App's key from the environment only (`FACTORY_APP_AUTHOR_*`), not from a keyring; the `copilot` suite stays unclaimed on v2 (it needs the environment name `tdd`, see `v2-claims.txt`); run by hand with `$script:V2EnvName = 'tdd'` in `run.ps1`, its CI release, owner-task and Development-end scenarios (`copilot-settle`, `copilot-owner`, `ci-release` served by the v2 routes) pass with the golden writes unchanged.

**Cutover gate, as events** (restated 2026-10-30: it replaces the earlier "full Tier 1, the drill, one measured batch, v2's node
leading, a 4-hour quiet window", and W9.11's 2-hour window; recorded on #268 by W9.9c). Before the switch merges:
the precondition list of W9.9c's After column. After it, in this order, every event observed and recorded:
1. **The release check of the switch:** `/_build` shows it, `/ready` 200, the lease holder's Solo node running, no second node in
   `wolverine_nodes`, the adoption result in the v2 status block (every open item adopted or skipped with a reason).
2. **The kill-switch drill**, through the cmdemo1-system setting (not an ad-hoc update): `FACTORY_V2_OFF` set while one item is
   mid-flow on v2 and one is waiting for its merge; v1 resumes every item from the state comments and acts for one full sweep (no
   duplicate or stale write, the parked items stay parked); v1 merges one item during the switch; the switch is unset; re-adoption
   opens new generations, **no stale timer acts, and v2 leaves alone the item v1 merged** (W9.10n).
3. **The overlap drill:** a release of the gateway while a session start and a merge are in flight; afterwards exactly one start and
   one merge (the runbook runs, the merge write and the ledger's claims show no duplicate effect), and one acting node.
4. **The measured 8-item batch** on v2 (docs and feature items, from the first worker start to deployed in the app's tdd): no stage
   median more than 5 minutes slower than the 2026-10-25 baseline, pickup latency (Todo to the first start) compared with v1's, no item
   parked for a gateway fault, no item v1 acted on.
5. **Each timer kind of v2 fires at least once** inside the batch: a step lease, `MergeRetryDue`, `CapacityWake`, the delivery safety
   wake, and each recurring job (status shows its last run).
6. **One release restart with sagas resumed** (any merge to master during the batch counts) and **one lease rollover with exactly one
   acting node** (the old node stopped before the new one started: `wolverine_nodes` and the lease's epoch).
7. **Observations through the batch:** dead letters 0; DTU p95 under 80% and the database size recorded (above it, S1 by default 5);
   the uat and prod promotions of the batch's releases made by v1's `AutoPromoter`, and the train's shadow decisions for them recorded
   for the W5 gate.
8. **Owed evidence:** W4's board events (every board change of the batch has its event in v2's history).

A failed event is fixed forward and the event observed again; the drills and the batch run again only if the fix touched what they
measure. Rollback during the gate is `FACTORY_V2_OFF` or a revert of W9.9c.

**W9.11 gate, as events** (recorded on #268 by W9.11c): the release check with the new fields; one lease rollover with exactly one
acting node; a small batch (one docs item and one feature item) delivered to the app's tdd with no regression; dead letters 0.
Rollback from W9.11b on is a revert of W9.11b, possible while the v1-format state comment is still written (through this gate).

### W8: R8, incidents, after the cutover (2 to 3 at once)

| Task | Content | Owns | After | Size |
|---|---|---|---|---|
| W8.0 | integrator: the rollback policy (automatic in the app's tdd only; uat and prod escalate); the incident tables (`014_AddIncidents.sql`); `delivery.rollback.migrationPaths`; the gate's staging method | `config/*`, `src/Fworker.Persistence/*` (tables) | the cutover gate (built ahead in #547, v2 acts only) | S |
| W8.1a | `IncidentSaga` per (deployable, environment), the train holds and the app's merge-train hold that let the incident's own work through, the app-health job | `src/Fworker.Workflows/Incident/*`, `Delivery/*` and `Merge/*` (holds) | W8.0; #542 and #545 merged | L |
| W8.1b | hotfix and redeliver intake; `intake.v2Plans` gains them | `WorkItem/Plans/Hotfix*`, `src/Fworker.Workflows/Intake/*`, `config/deploy.overrides.json` | W8.1a | M |
| W8.2 | alert ingress: the Azure Monitor webhook route (security-focused review) | `gateway/Endpoints/Alerts*`, `gateway/Http/GatewayAuthMiddleware.cs`, `docs/deploy/incident-alert-rules.md` | W8.0 | M |
| W8.2b | the infrastructure: action group, alert rules, the factory's out-of-band alert, reader RBAC, the alert secret, Octopus `DeploymentCreate` on tdd, Tentacle tags (the cmdemo1 session and the owner) | cmdemo1-system, Octopus | W8.2 | M |
| W8.3 | rollback through the DeliveryTrain with the migration precondition; `Undelivered` | `src/Fworker.Workflows/Delivery/Rollback*` | W8.1a | M |

**W8, revised 2026-10-30** (the rows above; #547 to #549 are in flight):
- **W8.0** (#348, in #547): the rollback policy's default is **automatic in the app's tdd only; in its uat and prod open the incident,
  hold the trains and escalate** (default 7); the incident tables are `014_AddIncidents.sql` (expand-only); `delivery.rollback.migrationPaths`
  names each deployable's migration paths (ui: `src/Database/`), and a compare GitHub answers incompletely (above 250 commits or 300
  files) counts as "migration present"; the staging method of the gate: a tdd-only setting in a release snapshot that turns the app's
  `/_healthcheck/detailed` Unhealthy, which the rollback to R-1 clears.
- **W8.1a** (#349, in #547 plus a follow-up): `IncidentSaga`, the holds, **including a merge-train hold on the app's repository** that lets
  only the incident's hotfix or revert through (a revert pull request is the default hotfix, so the bad commit does not come back with
  the next release), and a leader-only recurring app-health job with the confirmation rule, suppressed during deployments. Starts only
  after #542 and #545 merge (it touches `Merge/*` and `Delivery/*`).
- **W8.1b** (#349, second pull request): hotfix and redeliver intake (the saga owns the item from its creation, a pinned rank, the WIP
  bypass) and `intake.v2Plans` gaining hotfix and redeliver.
- **No W8.4 analyst role** (default 8).
- **W8.2** (#350, #549): owns `gateway/Endpoints/Alerts*`, `gateway/Http/GatewayAuthMiddleware.cs` (the azure-monitor source) and
  `docs/deploy/incident-alert-rules.md`; merges before W9.11b.
- **W8.2b** (new, the cmdemo1 session and the owner, before the W8 gate): an action group with the secret webhook to tdd's
  `/webhooks/azure-monitor`; alert rules on the ui tdd app; the factory's out-of-band `/ready` availability alert; reader RBAC for
  triage; the alert secret in `system.json` and Key Vault; Octopus `DeploymentCreate` on tdd for cmdemo1-ui for the gateway's account
  (default 6); the W7.4 Tentacle tags and project variables (after W9.10h); DTU 80% and size 70% alerts on the tdd database. uat and
  prod alert rules are deferred.
- **W8.3** (#351, #548): merge #547 first; #548 rebases onto #547's message names (one contract between the `IncidentSaga` and the
  train), with a wiring test that the saga's rollback request reaches the executor and its answer returns; the migration precondition
  reads `delivery.rollback.migrationPaths`.
- **Tests:** the fake Octopus redeploy and alert endpoints (`tests/FakeGitHub/Program.cs`), an incident suite at the end of
  `$SuiteDefs`, its `tests.yml` job and its v2 claim belong to the W8 rows (the main session integrates the hot files).

**W8 gate, as events** (recorded on #267): an alert arrives; the incident opens with its issue; the trains and the app's merge train
are held; the rollback deployment to R-1 runs in the app's tdd; N healthy reads follow; `Undelivered` is published for the release's
items; the incident resolves; the holds are released. **Negative case:** a release with a change under `src/Database/` escalates and
is not rolled back. The uat and prod case (open, hold, escalate, no deployment) is a scenario of the incident suite, not staged live.

**W8.0 and W8.1 as built (#348, #349).** Owner decisions of 2026-10-26 stand: tdd only, hybrid composition, no `incidents` flag, and one exception to "the tdd train only observes": the incident rollback of the app in tdd, automatic, is the one deployment
the train may create itself (uat and prod are not configured). The saga exists only where `Engine.V2Acts` (tdd is in the shadow run until the cutover, W9.9, so nothing of it runs in any environment today).
- **Tables (W8.0):** script `100_AddIncidents.sql` (the reserved range 100-109 of W8.1; `DatabaseScriptsTests` now requires 001-099 to be contiguous and the numbers from 100 to be unique) builds `factory.Incidents`: one row per (deployable, environment), the key
  `deployable/environment` in lower case, the state as a JSON document with a revision, beside the columns `Generation`, `Status` and `HotfixItem` that the routing and the listings read. The row is the open-instance index (a status other than `quiet` is an
  open incident). The incident timeline is **not** a table of its own: it goes to `factory.ItemHistory` (the item key `incident:deployable/environment`, one row per decision), which the design of that table names as its purpose.
- **Settings (W8.0):** the `incidents` block of `config/instance.json`. `environments` (`["tdd"]`) are the watched ones, `rollback` the policy by environment (`automatic`, `signoff`, `off`; `{ "tdd": "automatic" }`; one not named is off),
  `deploymentFailureOpens` (`[]`: a failed deployment opens an incident only where listed; the design wants prod, which is not configured), `confirmReads` 3 within `confirmWithinMinutes` 15 (the confirmation rule kept after the phantom incidents of 2026-09-13),
  `rollbackWithinMinutes` 60 (a release deployed less than that before the incident correlates with it), the timeouts `triageMinutes` 30, `rollbackMinutes` 30, `fixMinutes` 240, `verifyHealthyMinutes` 10, `verifyMinutes` 60, `deploymentMaxMinutes` 60, `maxRelapses` 2,
  `issueLabel`; the owner to mention is the root's `escalationMention`. `deploy.overrides.json` is not touched (a protected area); the defaults are what a deployed gateway runs with.
- **The saga** (`Fworker.Workflows.Incident`): `IncidentSaga` is a pure state machine (`Apply(state, input, now)`), `IncidentService` loads and saves it with a revision (five attempts, then `IncidentConflictException`), the handlers publish its decisions, commands and timer after the
  save (a redelivery of the last input publishes the same again, a repeated business key does nothing). Stages as in `wvf-incident.puml`: quiet, triaging, rolling-back, fixing, verifying, escalated; resolving returns to quiet and the next incident of the pair is the next generation.
  **Signals:** an alert (`AlertReceived`, the contract W8.2's webhook fills), the confirmation rule on `HealthObserved` (a recurring job `incident-health`, every minute on the leader, reads the health endpoint of each watched environment, `IncidentHealthJob`), and a failed deployment
  where the environment's policy says so; all but the failed deployment are suppressed while a deployment to the environment runs (the Octopus watcher's `OctopusDeploymentChanged`, routed by `IncidentRouter`; a start without an end stops suppressing after `deploymentMaxMinutes`).
  A signal while an incident is open joins it; in verifying it is a relapse back to triaging (`maxRelapses`, then the owner).
- **On opening:** the incident issue (`OpenIncidentIssue`, the owner is notified by it; found again by a marker, so a repeat opens one), the **hold of the pair's delivery train** and, by policy, either the rollback or the triage. A release deployed within `rollbackWithinMinutes` of the incident and an
  `automatic` policy means `TrainRollback` at once; `signoff` escalates with the rollback as the recommendation; `off` (or no correlated release, or a failed deployment, whose release never ran) asks for a verdict (`TriageRequested`, answered by `TriageReported`: rollback, fix-forward, escalate, close).
- **The train's hold lets the incident's own work through** (`Delivery/*`): `TrainHold` now carries the incident id and `Allow`, the items of the hotfix. A held train that gateway-promotes (uat, prod) judges every release as before, and promotes (and answers the Sign-off of) only a release that carries an allowed item,
  after the same gates as any release; with no hotfix item yet nothing passes, as before. In tdd the train observes, so the hold is recorded and stops nothing (the pipeline deploys). The train answers `TrainRollback` with `RollbackAnswered`; the saga follows "started", "done", "refused" (code `migration`
  fixes forward, any other code goes to the owner) and "failed"; it also sees the previous release running again in the Octopus events.
- **The hotfix plan:** `CreateHotfixItem` creates an issue in the home repository (labels `hotfix`, the top priority label and the auto-merge label, marker in the body, put on the board), starts its saga on the hotfix plan (`StartWorkItem` with `Hotfix`) and tells the incident (`HotfixCreated`). The plan (Develop, Verify, Merge,
  Deliver) is the feature plan's steps where the engine carries them, minus the design: reviews, a pull request, CI alongside Functional Testing, fix-forward rounds, the merge line, every gate; it asks the scheduler for its slot at the **top rank** (`SlotWanted.TopRank`), is not held by `mergeQueueDepth`, and
  is never switched to the docs plan or settled as docs (`WorkPlans.IsCode`; the card, the checks watcher, the fix-forward notice and the verbs' feed treat it as a code plan). The incident follows the item through `WorkItemDecided` (merged, parked, handed back) and `DeliveredTo` (delivered to the incident's environment: verifying).
- **Verifying and resolving:** Healthy reads for `verifyHealthyMinutes` without a bad read resolve the incident: the issue is closed with a note built from the incident's own timeline, the hold is released. Every stage has a timeout that escalates (a comment that mentions the owner); the owner's choice from escalated
  is `OwnerDecided` (rollback, fix forward, fixed by hand, close).
- **Left (the hotfix plan is complete here; these are other tasks or later):** **W8.2** the alert webhook and the alert rules (`AlertReceived` has no sender yet; the factory's own out-of-band alert); the train's execution of the rollback is **W8.3** (built, below: one contract, the train
  answers `TrainRollback` with `RollbackAnswered`); **W9.9** attaches the delivery train to a host where the engine acts, so until then `TrainHold`
  and `TrainRollback` have no route there (the host drops them; the Octopus watcher's deployment events reach the incidents through a queue of their own, which the train's wiring replaces by `DeliveryTopology.RouteOctopusEvents`); the **analyst session** of the triage (`TriageRequested` has no consumer, so a
  triage without a verdict escalates after `triageMinutes`); an **endpoint for the owner's decision** (`OwnerDecided` is a message; an endpoint is an authorization decision); the staged incident of the gate; `uat` and `prod` (not configured by the owner's decision).

**W8.3 as built (#351): rollback through the DeliveryTrain.** Owner decisions of 2026-10-29 apply: tdd only, hybrid composition, and one explicit exception to "the tdd train only observes": the tdd train may create exactly one kind of
deployment itself, the incident rollback of the app in tdd, automatic (uat and prod are not configured). This is the train's side of the contract W8.1 defined.
- **The contract (one, with W8.1):** the incident sends `TrainRollback(key, incidentId, generation, toRelease?, reason)` to the train; the incident id is the request's identity, so a request delivered twice is one rollback. The train answers with `RollbackAnswered`
  (the incident reads it as `RollbackReported`): `started` (with the release) when it asked for the deployment, then `done` when tdd runs the release again; or `refused` (nothing was asked of Octopus) / `failed` (the deployment was asked for and did not
  happen) with a code: `not-permitted` (any train but tdd's, or no Octopus project), `migration` (a migration was added since the target: the incident fixes forward), `no-previous-release`, `unavailable` (the train cannot decide or cannot show the rollback is safe), `deployment-failed`.
  There is no second set of names: W8.3's first draft (`Rollback`, `RollbackFinished`, `TrainRollbackRequested`) and W8.1's `IRollbackExecutor` plug were merged into this one. Nothing else in the incident calls Octopus.
- **The plan (`RollbackTrain.cs`, a part of `TrainRun`; the state is in the train's JSON document, so no table and no DbUp script):** any train but tdd's escalates ("configured for tdd only"). The tdd train waits for a deployment that is still
  running (30 minutes, then escalates), takes the release named or the one that ran before the newest deployment (the newest successful deployment to tdd that is not it and was not rolled back from; a failed newest deployment is rolled back to the
  release that still runs), and checks the **migration precondition** before anything is deployed.
- **The migration precondition (`RollbackRules.Judge`):** cmdemo1's DbUp migrations are forward-only, so going back is a rollback only when no migration was added since the target. The train reads the files GitHub's compare lists between the target's
  commit and the commit that runs now (`IMigrationProbe`, a GET) and escalates, with the file names, when one matches `delivery.rollback.migrationPaths` (default `scripts/Update/`, DbUp's folder; a renamed file counts under both names). It escalates also
  when it cannot show that none was added: no probe, no comparison (404, 422), a comparison that is not a straight line from the target to the release (`behind`, `diverged`), a list GitHub cut (300 files), a release without a commit.
  A fault of the read (HTTP, timeout) is not an escalation: the input is saved and the train looks again after `TransientRetry`.
- **The command and its executor:** `RollbackRelease(key, deployable, project, version, environment, incident)` (an integration command, `Fworker.Contracts`), executed by `RollbackExecutor` on the W3.0 command base
  (`src/Fworker.Integrations/Octopus/RollbackExecutor.cs`, the Executions API route of `PromoteRelease`). Unlike `PromoteRelease` it deploys although a deployment of that release to tdd exists (that is a rollback); it deploys nothing when tdd's
  **newest** deployment of the project already is of that release (idempotent against a replay after a crash between the create and the ledger, a restart, a second gateway), and a replay of a settled key is answered by the idempotency ledger.
  The executor itself refuses every environment but tdd for good, writes only as the leader, and runs on a sequential durable queue (`rollback-commands`) with the retry rule of the runtime commands; a refusal for good reaches the train as
  `TrainRollbackFailed` and it escalates. The train sends the command again (same key) every five minutes while Octopus shows no deployment, and gives up after 30.
- **The deployment is the rollback's:** the first deployment of the target that appears while the rollback waits is flagged (`TrainDeployment.Rollback`). Settling it delivers nothing (no `DeliveredTo`); it sets what runs in tdd; an older deployment
  of the same release seen again by the watcher's replay does not take its place.
- **Undelivered:** when the rollback deployment succeeds, every item the rolled-back releases delivered receives `Undelivered(item, tdd)` (the delivered-to row goes, and the WorkItemSaga's `DeliveryLost` returns the item to its Deliver step inside its
  settle window; both existed since W5.0 and W6.x) and becomes a candidate of the train again, so the release that fixes forward delivers it (`DeliveredBy`: the items of the newest ten delivered releases). The release rolled back from is
  never a target again. A failed rollback deployment escalates and undelivers nothing.
- **Held trains:** an incident's hold does not stop the rollback (it is the incident's own deployment); the hold's other half, letting the incident's hotfix release through, is W8.1's.
- **Wiring:** `RollbackWiring.Attach` (`gateway/Ports/RollbackPort.cs`) is called by `DeliveryActingWiring.Attach` (W9.4), so it exists exactly where the acting train does (`Engine.V2Acts`, a catalog and GitHub access); v1, the shadow run and `FACTORY_V2_OFF` register nothing. It registers the executor when the Octopus URL, space and a key
  (`OCTOPUS_RUNBOOK_API_KEY`, else `OCTOPUS_API_KEY`) are configured, and the compare read when GitHub can be read. `DeliveryTrainService` takes the probe as an optional part.
- **Not here (documented gaps):** the incident's hold and rollback request reach the acting train through the local queues of `DeliveryTopology` (W9.4; the train handles `TrainHold` and `TrainRollback` as any input); the items delivered before this train state
  existed are not remembered (`DeliveredBy` starts empty), so the first rollback after the upgrade undelivers only what is delivered from then on; uat and prod are never rolled back by the gateway.

**W8.2 as built (#350).** `POST /webhooks/azure-monitor?deployable=<name>&environment=<name>&secret=<secret>` (`gateway/Endpoints/AlertsEndpoints.cs`, `Fworker.Integrations/Alerts/AzureMonitorAlerts.cs`). It is the
fourth route of the webhook port (the middleware lets it through on that port and answers 503 on a replaced process like the others) and answers 404 unless the engine acts (`Engine.V2Acts`; the ingress inbox alone is not the gate, the shadow run registers it too for the delivery train), so v1, the
shadow run (tdd today) and `FACTORY_V2_OFF` are what they were. No new table: the durable queue `ingress-inbox` (W4.1) is the store, and its history record files an incident trigger under the key `incident:<deployable>/<environment>`
(type `ingress.inbox.azure-monitor.incident.alert`) instead of `board`.
- **Credential:** the W4.0 per-source secret, source `azure-monitor`, variable `FACTORY_WEBHOOK_SECRET_AZURE_MONITOR`. An Azure action group cannot send a header, so the secret is the query parameter `secret` of the webhook URI
  (a relay may send `X-Azure-Monitor-Secret`); compared in constant time like Octopus's, checked before the body is read, the body bounded at 256 KB, never logged. A source without a secret refuses every delivery (401).
- **Subject:** the deployable and environment come from the URI (one action group per deployable and environment), not from the alert, which names resources and not the factory's deployables. tdd only: an alert for another
  environment than the gateway's is acknowledged (202, `environment-not-served`) and ignored; so is one for the factory's own deployable `fworker` (`factory-alert-is-out-of-band`: its failures cannot run on its own bus).
  A deployable the catalog lists nothing like is a mistake of the URI (400, `unknown-deployable`); a catalog with no deployables (the single repository fallback) cannot say and accepts.
- **The trigger:** the common alert schema only (`useCommonAlertSchema: true`). Fired is `incident.alert`, Resolved `incident.alert.resolved`, both as an event object of the inbox with source `azure-monitor`, ref `<alertId>|<condition>`
  (so the delivery Azure sends again after a 5xx is dropped by the inbox's delivery layer, and the resolution of an alert is a delivery of its own), the rule, severity (`Sev0` to `Sev4` also as `severityLevel`), signal type, service,
  fired and resolved times, a description and up to ten target ids, bounded; the alert's own context is not carried. The `IncidentSaga` (W8.1) consumes these events: it joins them to the open incident of the (deployable, environment),
  and applies the suppression while the environment is deploying (the route decides nothing of that).
- **Answers for Azure's retry policy:** 202 stored, or ignored on purpose; 401; 400 and 413 (no retry); 503 with `Retry-After: 30` when the inbox did not take the event (the host not up, the database not answering): Azure retries,
  and the log line `{"alertIngress":"not-stored"}` is what the factory's out-of-band rule watches.
- **The factory's out-of-band alert:** Azure rules on the factory's own health notify the owner by e-mail, not through the webhook (`docs/deploy/incident-alert-rules.md` has the exact rule and action group definitions, for the
  cmdemo1 session to apply in cmdemo1-system). The rollback itself (the tdd train's one deployment of its own, the incident rollback of the app in tdd, automatic, tdd only) is W8.3's.

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

**What remains, from the revision of 2026-10-30** (same units and rules; the in-flight #542 to #549 are assumed to merge in about an
hour of serial merges):

```
W9.10a, c, d, l, o (now) -> W9.10b, W9.10q; W9.10e -> W9.10f, g, h -> W9.10i, j -> W9.10k, m -> W9.10n; W9.10p
  -> W9.9a -> W9.9b -> W9.9c -> cutover gate (drills, measured batch with the timer events inside)
  -> W9.11a -> W9.11b -> W9.11c (W9.11 gate: a small batch), beside W8.1b, W8.2b and the W8 gate
  -> W9.12 when the W5 gate is recorded (app releases); W9.13 when the owner's App installations exist
```

| Stage | Tasks (units) | Longest chain (units) | Coding | Gate events | Clock time |
|---|---|---|---|---|---|
| W9.10 and W9.9 | 19 (26) | W9.10d, e, h, j, k, then W9.9a, b, c (12) | 10 h | drills 1 h, measured batch 3.5 h | 14.5 h |
| After the cutover | W9.11a to c 3 (4); W8.1b and follow-ups 2 (2) | W9.11a, b, c (4) | 3.3 h | small batch 1.5 h, staged incident 1 h | 5.5 h |
| **Remaining** | **24 (32)** | | **about 13 h** | **about 7 h** | **about 20 h** |

About 20 hours, in a range of 18 to 28; W9.12 waits on three live app releases after the replay (the W5 gate) and W9.13 on the
owner's App installations, so neither is in the figure.

## 6. What the main session does in each wave

1. Brief and start the wave's integrator, then the parallel tasks as their "after" items merge (two to three at once, lane D
   included).
2. Review each pull request with a reviewer subagent; watch CI; re-run flaky jobs at most twice; merge on green outside a window.
3. Integrate hot-file additions in order; run `gh workflow run Tests` on master after each batch of merges.
4. At the wave's end, open its tdd window, run its batch or comparison, and record the gate's result in the item history and the
   handoff.
5. If a gate fails: before the cutover v1 is acting, so fix forward (shadow mode can be stopped with `FACTORY_V2_OFF`); from the
   cutover, revert the switch's pull request or set `FACTORY_V2_OFF` through the cmdemo1 session, then fix forward.
6. (2026-10-30) Run `gh workflow run Tests --ref master` after every merge batch and record the result on the wave's epic; merge the
   in-flight pull requests one at a time, each rebased on the one before; land a hot-file prerequisite (such as `V2VerbRouter.Below`)
   before the pull requests that need it.

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

## Appendix B. Issues of the revision of 2026-10-30 (audit)

**New issues** (each a sub-issue of its epic; listed in dependency order):

| Issue | Row | Depends on |
|---|---|---|
| #550 | W9.10a: Solo node only on the acting holder | #352 |
| #551 | W9.10c: an atomic pending claim in the command ledger | #352 |
| #552 | W9.10d: v2 Tier 1 that starts sagas | #352 |
| #553 | W9.10o: v2 health and fault handling | #352 |
| #554 | W9.10l: the gateway's own rule-script calls and v1 fallbacks | #360 |
| #555 | W9.10p: the app's uat and prod stay with v1's `AutoPromoter` | #356 |
| #556 | W9.10b: the lease as a SQL application lock | #550 |
| #557 | W9.10e: v1 leaves saga items alone | #552 |
| #558 | W9.10f: the sweep's item steps on v2 | #557, #355, #356 |
| #559 | W9.10g: the sweep's plant-wide steps on v2 | #557, #353, #360 |
| #560 | W9.10h: producers for the saga's inputs | #557 |
| #561 | W9.10i: the state comment projection wired | #560 |
| #562 | W9.10j: session offers and starts from the saga and the scheduler | #560 |
| #563 | W9.10k: plant state that survives a restart | #562, #358 |
| #564 | W9.10m: adoption as reconciliation, the mapping | #561 |
| #565 | W9.10n: re-adoption fencing after `FACTORY_V2_OFF` | #564 |
| #566 | W9.12: the train takes the app's uat and prod | #264 (its gate), #363 |
| #567 | W9.13: multi-repository after the cutover | #361, #363, #452 |
| #568 | W8.2b: incident infrastructure (W8 epic #267) | #350 |

**Rewritten:** #361 (W9.9: the tdd cutover, size L, three pull requests, depends on W9.1 to W9.8, every W9.10 issue, the W6 and W7
gates and #500) and #363 (W9.11: three pull requests, keeps the lease and the uat and prod `AutoPromoter`, depends on #361 and #350).

**Closed:** #464 (Balanced mode for W2.2c: superseded by the lease decision) and #496 (W5.1 follow-ups: newest-only accepted as
declared; the event-time question carried by the W5 gate; save and outbox in #553; hand-written handlers accepted in #491).

**Commented** with what changed: #261 (W2: Solo on the lease holder, not Balanced), #268 (W9: the new rows), #348 to #351 (W8: the
tdd-only rollback default, the split of W8.1, W8.2b, the merge order of #547 to #549), #465 (the session mailbox is not a cutover
prerequisite), #500 (the wake after a restart is a W9.9 precondition and a W5 gate event; the retry bounded in #553).

**W9.9c prep as built (#361): the claim list is complete.** `run-v2-claims.py --require-complete` passes and is part of the job "Every scenario is in exactly one shard", so the list stays equal to `run.ps1 -ListSuites` less the declared v1-only suites.
- *The decision per suite.* Claimed whole on v2 (shards in the matrix of the job v2 as in the v1 jobs): `auto` (8), `owner` (10), `manager` (8), `copilot` (4), `qc` and `messaging` (4 groups each in one job), `runtime`,
  and `cursor`, `cursor-deadline` and `author` together; the claims by scenarios of the earlier gates are part of them (their comments are in `v2-claims.txt`). v1-only: `peer-approved` and `qc-gate` (decision 4 of 2026-10-30: the
  policy and the gate that rests on it; what of `qc-gate` is the QC record's own moved to `qc` earlier, and W9.1's verbs are claimed by `qc`, `qc-worker` and `qc-gate-lease`).
- *Scenarios that stay on v1 (`v1-only-scenarios <suite> | <regex> | <reason>`).* run.ps1 -Engine v2 neither lists nor runs them, and run-all.ps1 lists the job's scenarios with the same filter. They test (a) the inline merge of the verdict
  call (`Verb_merge -Gateway`: 13 scenarios of `owner`, 2 of `qc`): v2's verdict answers `next: merge` and the merge follows it (W9.3, `ItemVerbs.Flow.cs`), so the verdict answer and the writes the call caused differ; W9.11 removes the inline
  merge with the sweep; (b) the Runner's slots and the pool's timeout (1 of `owner`, 1 of `qc`), W9.11b's. The two `leader:` scenarios of `owner` (the overlap and the graceful stop) and the manager's busy-item-lock scenario are claimed on v2: the GitHub-ref lease stays beside the SQL lock until W9.11b (W9.10b as built), so the hand-over they test is v2's too.
- *Findings of the v2 run, fixed.* (1) `V1CommandPort` had no entry for `cae-job`: every start of the CAE job runtime was refused "runtime off" where the engine acts (the 13 checks of `runtime`; tdd was in shadow, where nothing starts);
  (2) `FileItemLock` was not re-entrant: copilot-settle's takeover calls open-pr (an item verb) inside its own item lock and waited 45 s for it, then answered `takeover-refused item-busy`, so the draft of a Copilot task was never taken over;
  (3) `GitHubTokenProvider` took GH_TOKEN before the App where `FACTORY_AUTHOR_APP=1` (the scripts take the App), so the gateway adopted its own old state comment instead of writing the App's, and every port minted its own installation
  token (six for two verbs): one provider per App and API now; (4) a 4xx refusal of a launch (Cursor's 400) was Transient and tried four times before it counted against the item; it is Permanent now (408, 425, 429 and 5xx stay Transient); (5) with the lease on, the restore of the plant from SQL waited for the schema to be verified, which the Wolverine host does when it starts, and the host starts only after the restore: every start waited out the two minutes of the wait and then restored nothing (a newer process had no plant and swept two minutes late; `leader:` scenarios of `owner`). The restore now reads as soon as this process holds the SQL lock, which it takes only after the scripts ran (`SnapshotCoordinator.WaitForTablesAsync`); (6) the `journal` verb answered `posted: false` when the item lock stayed busy for its 45 s, where the script answers Reject 20 `item-busy` and its callers retry on that code (the escalation comment of a question): the question was marked journal-failed although the comment was posted. The ledger posts before it saves (the lock is held around the save only, W9.10l), so the verb now answers 20 `item-busy`, and the retry finds the comment by its marker and posts nothing twice (`mgr: the escalation comment waits out a busy item lock`; the first check of that scenario allows the one comment already there while the lock is held).
- *The environment of the v2 runs.* The CI-only name `v2ci` turned the baked `caeJob` off (RuntimeSettings.Load: only tdd, local or no name keeps it) and showed itself where scenarios check the name (the dashboard, Cursor's agent names, the
  scale-to-zero warning). `owner`, `messaging`, `cursor`, `cursor-deadline` and `runtime` run on v2 with `FACTORY_ENVIRONMENT` unset, as on v1, and the test configuration names tdd in `engine.v2Environments`.
- *Declared differences.* A 5xx refusal of a Copilot create is transient on v2 and tried again by the start command with the same worker (the item is not offered to a next worker); the scenario checks that on v2. The two `leader:` scenarios
  do not see the snapshot ref's writes (the plant is rows of SQL) and see the accepted and offered lines from the gateway's journal, not the sweep's script; the busy-item-lock scenario sees the escalation comment and the state write from the
  gateway's ledger, the comment while the lock is held and the state after it frees (the post is outside the lock by W9.10l's design).
- *Scenarios adapted for both engines.* The plant snapshot of the graceful restart is a row in SQL on v2 (the log line says `store: sql`); the two `leader:` scenarios take `restore.how` `sql` with the epoch of the predecessor's SQL term (`leader.sql.epoch`) where v1 says `handover` or `released` with the ref's epoch; the budget scenario asks until the sweep has reported the new window; the CI-release check does not
  count the state comment (it holds `journal.ciReleased` as soon as the engine records it).

### V3.2 as built: leadership on the SQL lease only

The GitHub-ref lease (`refs/factory/<instance>/gateway-leader`), the ref snapshot of the plant and the rule-script calls that kept them (`leader-lease`, `plant-snapshot-save`, `-load`, `-clear`, `-await`) are gone from the gateway.
`GatewayLeader` is one state machine on the SQL application lock `fworker:leader` (`SqlLease.cs`); `SnapshotCoordinator` keeps the plant in the database (script 240) and reads it back; `LeadershipService` decides when each runs.

- *The start.* The lock first (the newest process writes a takeover request and the holder makes room), then the restore of the plant from the database, then the confirmation that the lock is still held, then acting. A process
  that lost the lock while it restored (a newer one asked, or the connection dropped) stands by without acting, and the standby loop takes the lock again when it is free and no newer process asked for it.
- *The hand-over.* Holder: node leaves, final plant to the database, streams end, the lock goes (in that order). The newcomer takes the lock with the next epoch, so it reads the final plant; a dead holder's last periodic write is
  what it reads. The store refuses a writer of an older epoch than the stored one (`PlantStateStore.Save`).
- *A plant that cannot be read at the start* (three tries) starts empty and writes none for the whole term, so the plant of the term before is never overwritten with an empty one; the sweep rebuilds from the board.
- *The gap* handed to the first full sweep is the time the stored plant was written (`restore.gapSince`).
- *Claim-epoch guard* (item 5 of #578). `SqlIdempotencyLedger.Claim` takes over at once a claim of an older term (its process no longer holds the lock) and refuses any claim of a process whose own term is older than the one on
  `factory.Leadership` (it lost the lock and has not noticed yet): the SQL lock is the only fence, and the ledger honours it.
- *Without a database* (uat, prod today; a suite's second gateway; `FACTORY_LEADER_LEASE=0`) there is no lease: the process acts at once and keeps no plant.
- *The status.* `leader`: `enabled`, `role`, `holder`, `epoch` (the SQL term), `revision`, `replica`, `acquiredAt`, `acquisitions`, `supersededAt`, `supersededBy`, and `sql` (`held`, `epoch`, `acquisitions`, `lost`, `yielded`, `failures`,
  `lastFailure`, `lostAt`, `previousHolder`, `checkSeconds`). Removed with the ref: `predecessor`, `renewedAt`, `until`, `renewals`, `checks`, `failures` (of the ref), `leaseSeconds`, `renewSeconds`, `checkSeconds`, `handoverSeconds`,
  `sql.fencesActing`. `plantSnapshot`: `enabled`, `leader`, `restore` (`how` is always `sql`; `predecessorEpoch` is gone), `saves`, `saveFailures`, `lastSave`, `heldEpoch`; `maxAgeMinutes` is gone. The release check
  (CLAUDE.md) reads `leader.role` acting, `leader.sql.held` true and `plantSnapshot.restore.how` sql.
- *Settings removed:* `timing.leaderLeaseSeconds`, `leaderRenewSeconds`, `leaderCheckSeconds`, `leaderHandoverSeconds`. `leaderSqlCheckSeconds` (5) stays.
- *Left for V3.3:* the plugin's `Leader.ps1` and `PlantSnapshot.ps1` verbs and the fencing in `Core.ps1` (the gateway no longer sends `_lease`, so the scripts run unfenced), the verb-policy names.
