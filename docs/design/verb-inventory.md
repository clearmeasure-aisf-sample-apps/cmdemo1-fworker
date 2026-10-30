# The verb inventory

Task W1.10 of `fworker-wolverine-implementation.md`, the inventory that R1 of `fworker-wolverine-refactor.md` promises and R9 executes. One
row for each server verb: a verb that `plugin/scripts/factory.ps1 --server <verb>` runs, and that the gateway's `RunnerPool` starts in a `pwsh`
child process. The data is in `docs/design/verb-inventory.json`; this table is the same data for people. `tests/Gateway.Tests/VerbInventoryTests.cs`
fails when the three disagree: the `$ServerVerbs` list of `factory.ps1`, the JSON and this table (and, for the callers, the `VerbPolicy` sets of
`gateway/Policies/VerbPolicy.cs`, and the `$NoLockVerbs` list and `UseEndpointReason` for the two flags of the JSON). Adding or removing a verb
means a row here and an entry in the JSON in the same pull request.

**Count: 74, not 73.** The refactor design says 73. The code has 74: the table of R9 does not name `auto-promote` (the automatic promotion of a
release to uat and prod, `lib/Verbs.Promote.ps1`, run by `gateway/AutoPromoter.cs`). It goes to the DeliveryTrainSaga with `settle` and
`publish-demo`; the row of R9 gets the verb in the same pull request. The design's counts stay at 73 until its owner reconciles them (follow-up).

## Columns

- **Verb**: the name in `$ServerVerbs`.
- **v1 script**: the file of `plugin/scripts/lib/` that defines `Verb_<name>` today. v1 keeps running every verb until the R9 cutover.
- **Owner**: the v2 component that takes the verb over (names of R9's table; the read models and services are the C# classes of `src/`
  that the stages after W1 create).
- **Destination**: `saga` (a Wolverine saga handles it: decision verbs by `InvokeAsync`, event verbs published), `handler` (a handler, read
  model or C# service answers it; no saga state), `coordinator` (the board reconciler or the event ingress S3: it feeds sagas, it is not one),
  `retired` (removed at R9: Wolverine's leader election and durable state replace it). No verb "stays v1": at the cutover v1 is removed.
- **Callers**: the credential kinds that `VerbPolicy.AuthorizeVerb` lets call the verb (`owner` and `inspector` are item-token roles,
  `owner-runtime` an owner token minted for a runtime worker). The admin credential may call any verb. "admin only" is a verb that
  only the gateway's own loops (and the admin) start.

## Table

| Verb | v1 script | Owner | Destination | Callers |
|---|---|---|---|---|
| `get-item` | `Verbs.Item.ps1` | WorkItemSaga | saga | owner,inspector,manager,keeper,qc |
| `append-body` | `Verbs.Item.ps1` | WorkItemSaga | saga | owner |
| `comment` | `Verbs.Item.ps1` | WorkItemSaga | saga | owner |
| `claim-item` | `Verbs.Item.ps1` | WorkItemSaga | saga | owner |
| `accept` | `Owner.ps1` | WorkItemSaga | saga | owner,inspector,qc |
| `release` | `Owner.ps1` | WorkItemSaga | saga | owner |
| `my-items` | `Owner.ps1` | WorkItemSaga | saga | worker |
| `record-review` | `Verbs.Item.ps1` | WorkItemSaga | saga | owner |
| `move-next` | `Verbs.Item.ps1` | WorkItemSaga | saga | admin only |
| `complete` | `Verbs.Item.ps1` | WorkItemSaga | saga | owner |
| `validation-verdict` | `Verbs.Pr.ps1` | WorkItemSaga | saga | owner |
| `open-pr` | `Verbs.Pr.ps1` | WorkItemSaga | saga | owner |
| `update-pr` | `Verbs.Pr.ps1` | WorkItemSaga | saga | owner |
| `item-pr` | `Verbs.Pr.ps1` | WorkItemSaga | saga | admin only |
| `park-item` | `Verbs.Item.ps1` | WorkItemSaga | saga | admin only |
| `unpark-item` | `Verbs.Item.ps1` | WorkItemSaga | saga | admin only |
| `resettle-item` | `Verbs.Item.ps1` | WorkItemSaga | saga | admin only |
| `expire-claim` | `Owner.ps1` | WorkItemSaga | saga | admin only |
| `pr-checks` | `Verbs.Pr.ps1` | ChecksReadModel | handler | owner,inspector,manager,keeper |
| `wait-for-checks` | `Verbs.Pr.ps1` | ChecksReadModel | handler | owner |
| `inspect-facts` | `Verbs.Inspect.ps1` | ChecksReadModel | handler | inspector |
| `get-delivery` | `Verbs.Delivery.ps1` | DeliveryReadModel | handler | owner,manager |
| `model-tier` | `Models.ps1` | ModelTierReadModel | handler | owner,inspector |
| `check-secrets` | `Verbs.Sweep.ps1` | SecretsReadModel | handler | admin only |
| `approve-pr` | `Verbs.Inspect.ps1` | MergeTrainSaga | saga | inspector |
| `merge` | `Verbs.Merge.ps1` | MergeTrainSaga | saga | owner |
| `clear-hold` | `Verbs.Inspect.ps1` | MergeTrainSaga | saga | admin only |
| `base-watch` | `BaseUpdate.ps1` | MergeTrainSaga | saga | admin only |
| `base-update-branch` | `BaseUpdate.ps1` | MergeTrainSaga | saga | admin only |
| `settle` | `Verbs.Delivery.ps1` | DeliveryTrainSaga | saga | admin only |
| `publish-demo` | `Verbs.Pr.ps1` | DeliveryTrainSaga | saga | owner |
| `ci-release` | `Verbs.Pr.ps1` | DeliveryTrainSaga | saga | admin only |
| `copilot-settle` | `Verbs.Pr.ps1` | DeliveryTrainSaga | saga | admin only |
| `copilot-owner` | `Verbs.Pr.ps1` | DeliveryTrainSaga | saga | admin only |
| `private-build` | `Verbs.Pr.ps1` | DeliveryTrainSaga | saga | owner |
| `auto-promote` | `Verbs.Promote.ps1` | DeliveryTrainSaga | saga | admin only |
| `sync-board` | `Verbs.Sweep.ps1` | BoardReconciler | coordinator | admin only |
| `ingest-event` | `Verbs.Sweep.ps1` | S3EventIngress | coordinator | admin only |
| `journal` | `Journal.ps1` | HistoryProjection | handler | admin only |
| `qc-sync` | `Qc.ps1` | QcService | saga | admin only |
| `qc-link` | `Qc.ps1` | QcService | saga | admin only |
| `qc-view` | `Qc.ps1` | QcService | saga | qc-click |
| `qc-request` | `Qc.ps1` | QcService | saga | qc-click |
| `qc-reset` | `Qc.ps1` | QcService | saga | qc-click |
| `qc-teardown` | `Qc.ps1` | QcService | saga | qc-click |
| `qc-list` | `Qc.ps1` | QcService | saga | admin only |
| `qc-job` | `Qc.ps1` | QcService | saga | qc |
| `qc-progress` | `Qc.ps1` | QcService | saga | qc |
| `qc-report` | `Qc.ps1` | QcService | saga | qc |
| `qc-fail` | `Qc.ps1` | QcService | saga | qc |
| `qc-stopped` | `Qc.ps1` | QcService | saga | qc |
| `qc-transition` | `Qc.ps1` | QcService | saga | admin only |
| `qc-approval` | `Qc.ps1` | QcService | saga | admin only |
| `backlog` | `Verbs.Keeper.ps1` | KeeperCommands | handler | keeper |
| `amend-criteria` | `Verbs.Keeper.ps1` | KeeperCommands | handler | keeper |
| `split-item` | `Verbs.Keeper.ps1` | KeeperCommands | handler | keeper |
| `create-item` | `Verbs.Keeper.ps1` | KeeperCommands | handler | keeper |
| `set-priority` | `Verbs.Keeper.ps1` | KeeperCommands | handler | keeper |
| `link-dependency` | `Verbs.Keeper.ps1` | KeeperCommands | handler | keeper |
| `manager-action` | `Verbs.Manager.ps1` | ManagerCommands | handler | manager |
| `revocations` | `Owner.ps1` | RevocationService | handler | admin only |
| `revoke-worker` | `Owner.ps1` | RevocationService | handler | admin only |
| `git-credential` | `Apps.ps1` | GitCredentialService | handler | owner-runtime |
| `operator-releases` | `OperatorRelease.ps1` | OperatorReleaseService | handler | admin only |
| `operator-release` | `OperatorRelease.ps1` | OperatorReleaseService | handler | admin only |
| `metrics` | `Metrics.ps1` | MetricsReadModel | handler | admin only |
| `transcript-save` | `PlantSnapshot.ps1` | TranscriptService | handler | admin only |
| `transcript-load` | `PlantSnapshot.ps1` | TranscriptService | handler | admin only |
| `transcript-prune` | `PlantSnapshot.ps1` | TranscriptService | handler | admin only |
| `plant-snapshot-save` | `PlantSnapshot.ps1` | WolverineDurableState | retired | admin only |
| `plant-snapshot-load` | `PlantSnapshot.ps1` | WolverineDurableState | retired | admin only |
| `plant-snapshot-clear` | `PlantSnapshot.ps1` | WolverineDurableState | retired | admin only |
| `plant-snapshot-await` | `PlantSnapshot.ps1` | WolverineDurableState | retired | admin only |
| `leader-lease` | `Leader.ps1` | WolverineLeaderElection | retired | admin only |

## Totals

74 verbs: 44 saga (WorkItemSaga 18, QcService 14, DeliveryTrainSaga 7, MergeTrainSaga 5), 23 handler, 2 coordinator, 5 retired.

- Verbs the gateway answers itself with `use-endpoint` instead of running the script (`VerbPolicy.UseEndpointReason`): the `useEndpoint` flag of
  the JSON. Their destination is unchanged by that: the flag says who runs them today.
- Verbs in the QC sets of `VerbPolicy` that are not server verbs, handled by the gateway without a rule script: `qc-heartbeat` and `qc-touch`
  (the `gatewayVerbs` of the JSON).

## Internal callers and v1 fallbacks (W9.10l)

The 74 verbs above are what a worker or the gateway's pipeline calls. The gateway also runs rule scripts on its own: every `.RunAsync` call of `gateway/` outside `gateway/Verbs` is listed below, and so is every place where a verb that
has a v2 owner still falls back to the script. Each entry names the plan row that gives it a v2 owner (Owner) and the row at which it leaves this list (Leaves). `tests/Gateway.Tests/ArchitectureInventoryTests.cs` reads
`docs/design/verb-inventory.json` (the same data) and fails when a call or a fallback is in the code and not here, when an entry is no longer in the code (delete it in the pull request that removes the call), when a row
is not a row of the plan, and, once the server rule scripts are gone, when any entry without an exception is left. A new call needs a row first.

Kinds: `runner` calls `Runner.RunAsync` (a rule script); `bridge` calls through a per-pull-request bridge (`ServiceVerbCalls`) that W9.9a removes; `router` forwards inside the v2 router chain and runs no script (listed so that the count of call sites is exact).
Exceptions are the declared reasons an entry may outlive the rows before W9.11; the test pins their ids, so a new one is a visible edit.

### The gateway's own calls

| Entry | File | Sites | Verbs | Kind | Owner | Leaves | What it is |
|---|---|---|---|---|---|---|---|
| auto-promote | `gateway/AutoPromoter.cs` | 1 | auto-promote | runner | W9.10p | W9.12 | The automatic promotion of the app to uat and prod. It has no v2 route by decision. **exception until W9.12.** |
| base-watch-verbs | `gateway/BaseUpdate.cs` | 1 | by argument | runner | W9.3 | W9.9a | BaseWatch runs base-watch and base-update-branch (routed since #542). |
| base-journal | `gateway/BaseUpdate.cs` | 1 | journal | runner | W9.8 | W9.9a | The journal line of a base update (#477). |
| transcripts | `gateway/Composition/GatewayServiceCollectionExtensions.cs` | 1 | by argument | runner | W9.8 | W9.9a | TranscriptStore: transcript-save, transcript-load and transcript-prune. |
| plant-snapshot | `gateway/Composition/GatewayServiceCollectionExtensions.cs` | 1 | by argument | runner | W9.10k | W9.11b | SnapshotCoordinator: plant-snapshot-save, -load, -clear and -await. **exception until W9.11b.** |
| revocation-calls | `gateway/Composition/GatewayServiceCollectionExtensions.cs` | 1 | by argument | bridge | W9.7 | W9.9a | RevocationService through ServiceVerbCalls (revocations, revoke-worker). |
| copilot-owner-verbs | `gateway/Composition/GatewayServiceCollectionExtensions.cs` | 1 | by argument | runner | W9.4 | W9.9a | CopilotOwnerVerbs: copilot-owner. |
| delivery-bridge | `gateway/Composition/GatewayServiceCollectionExtensions.cs` | 1 | by argument | bridge | W9.4 | W9.9a | DeliveryVerbBridge: the delivery verbs go to v2's DeliveryVerbHost where the engine acts, and to the Runner everywhere else. |
| copilot-settle-client | `gateway/Composition/GatewayServiceCollectionExtensions.cs` | 1 | by argument | runner | W9.4 | W9.9a | CopilotSettleClient: copilot-settle. |
| plant-journal | `gateway/Composition/GatewayServiceCollectionExtensions.cs` | 1 | journal | runner | W9.8 | W9.9a | The plant's journal lines (#477). |
| operator-release-calls | `gateway/Composition/GatewayServiceCollectionExtensions.cs` | 1 | by argument | bridge | W9.7 | W9.9a | OperatorReleaseLoader through ServiceVerbCalls (operator-releases). |
| runtime-failed-park | `gateway/Composition/GatewayServiceCollectionExtensions.cs` | 1 | park-item | runner | W9.10h | W9.9a | The runtime adapters' park after a failed launch. The script's park-item stays the park (v2's route answers it from W9.9a); W9.10h tells the saga as well (ParkWorkItem). |
| runtime-release-claim | `gateway/Composition/GatewayServiceCollectionExtensions.cs` | 1 | claim-item | runner | W9.10j | W9.10j | The runtime adapters' ReleaseClaim; the saga's step lease and the scheduler's slot replace it. |
| credit-guard-park | `gateway/Composition/GatewayServiceCollectionExtensions.cs` | 1 | park-item | runner | W9.10h | W9.9a | The Copilot credit guard's park. As the runtime adapters' park: the script's park-item until W9.9a, and W9.10h tells the saga (ParkWorkItem). |
| feature-checks | `gateway/Composition/GatewayWolverineExtensions.cs` | 1 | by argument | runner | W9.5 | W9.9a | FeatureChecksJob reads pr-checks through PullRequestChecks. |
| publish-demo | `gateway/Endpoints/DemoEndpoints.cs` | 2 | publish-demo, publish-demo | runner | W9.4 | W9.9a | The demo endpoint (the Runner and the delivery bridge: v2 where the engine acts). |
| unpark-item | `gateway/Endpoints/ItemAdminEndpoints.cs` | 1 | unpark-item | runner | W9.2 | W9.9a | The unpark endpoint. W9.10h: it tells the saga (ResumeWorkItem), so the item is offered again. |
| resettle-item | `gateway/Endpoints/ItemAdminEndpoints.cs` | 1 | resettle-item | runner | W9.2 | W9.9a | The resettle endpoint. |
| metrics-calls | `gateway/Endpoints/MetricsEndpoints.cs` | 1 | metrics | bridge | W9.7 | W9.9a | Through ServiceVerbCalls. |
| operator-release-endpoint | `gateway/Endpoints/OperatorReleaseEndpoints.cs` | 1 | operator-release | bridge | W9.7 | W9.9a | Through ServiceVerbCalls. |
| ingest-event | `gateway/Endpoints/WebhookEndpoints.cs` | 1 | ingest-event | runner | W9.8 | W9.9a | The webhook endpoint hands the event to the script. |
| service-calls-runner | `gateway/Identity/ServiceVerbs.cs` | 1 | by argument | bridge | W9.7 | W9.9a | ServiceVerbCalls: the Runner while the engine does not act. The bridge itself is removed by W9.9a. |
| service-calls-v2 | `gateway/Identity/ServiceVerbs.cs` | 1 | by argument | router | W9.7 | - | ServiceVerbCalls: v2's own answer (no script). |
| leader-lease | `gateway/Leader.cs` | 5 | leader-lease | runner | W9.10b | W9.11b | The leader lease on the GitHub ref. **exception until W9.11b.** |
| plant-claim-renew | `gateway/PlantHooks.cs` | 1 | claim-item | runner | W9.2 | W9.9a | Claim renewal of an assigned item. |
| plant-expire-claim | `gateway/PlantHooks.cs` | 1 | expire-claim | runner | W9.2 | W9.9a | Claim expiry of an assigned item. |
| ticker-journal | `gateway/PlantTicker.cs` | 1 | journal | runner | W9.8 | W9.9a | The ticker's journal lines (#477). |
| item-verbs-v1 | `gateway/Ports/ItemVerbsPort.cs` | 1 | by argument | runner | W9.10q | W9.10q | ItemVerbsV1, the IItemV1 plumbing: it goes with the fallbacks. |
| item-admin-feed | `gateway/Ports/ItemVerbsPort.cs` | 1 | by argument | router | W9.2 | - | ItemAdminFeed forwards to the router it wraps (no script). |
| qc-calls | `gateway/Qc.cs` | 1 | by argument | runner | W9.1 | W9.9a | QcHub runs the qc-* verbs (QcService in W9.1). |
| qc-facts | `gateway/Ports/QcPort.cs` | 1 | inspect-facts | router | W9.5 | - | QcFactsAdapter asks the router for the inspector's facts; the router answers on v2 or runs the script. |
| delivery-open-pr | `gateway/Ports/DeliveryVerbHost.cs` | 1 | by argument | router | W9.4 | - | copilot-settle opens the item's pull request through the verb router (open-pr); no script of its own. |
| item-pr-lookup | `gateway/Runtimes/ItemPrLookup.cs` | 1 | item-pr | runner | W9.2 | W9.9a | The pull request of an item for the runtime adapters (the owner-task case is a fallback below). |
| owner-ci-release | `gateway/Runtimes/OwnerCiReleaseService.cs` | 2 | by argument | runner | W9.4 | W9.9a | OwnerCiReleaseService runs ci-release. |
| owner-ci-journal | `gateway/Runtimes/OwnerCiReleaseService.cs` | 1 | journal | runner | W9.8 | W9.9a | Its journal lines. |
| sweep-board | `gateway/Sweeper.cs` | 1 | sync-board | runner | W9.8 | W9.10g | The sweep: sync-board (the reconciler of W9.8, the item and plant-wide steps of W9.10e to W9.10g). |

### The fallbacks of verbs that have a v2 owner

| Entry | Kind | File | Sites | Owner | Leaves | What it is |
|---|---|---|---|---|---|---|
| qc-accept | item-v1 | `src/Fworker.Workflows/WorkItem/Verbs/ItemVerbs.Claim.cs` | 1 | W9.1 | W9.1 | The accept of a QC environment (#541 marks it W9.1, #544 marks it W9.2: W9.1 owns it). |
| complete-development | item-v1 | `src/Fworker.Workflows/WorkItem/Verbs/ItemVerbs.Flow.cs` | 1 | W9.10q | W9.10q | The red-head ruling and the migration check of complete in Development (marked W9.5, which merged without them). |
| open-pr-copilot | item-v1 | `src/Fworker.Workflows/WorkItem/Verbs/ItemVerbs.Pr.cs` | 1 | W9.10q | W9.10q | open-pr for a copilot/* branch (marked W9.4, not in #545). |
| item-pr-owner-task | item-v1 | `src/Fworker.Workflows/WorkItem/Verbs/ItemVerbs.Read.cs` | 1 | W9.10q | W9.10q | item-pr for an owner task (Resolve-OwnerPr). |
| merge-peer-approved | merge-script | `src/Fworker.Workflows/Merge/MergeVerbService.cs` | 2 | W9.11b | W9.11b | A merge policy other than auto: the inspector's approval of the current head and the QC gate. **exception until W9.11b.** |
| merge-hybrid-script | merge-script | `gateway/Verbs/MergeVerbs.cs` | 5 | W9.9b | W9.11b | The hybrid composition: the script answers merge, approve-pr, clear-hold and base-watch for an item the saga does not own. **exception until W9.11b.** |
| merge-refusal-counter | behavior-gap | `gateway/Verbs/MergeVerbs.cs` | 0 | W9.10q | W9.10q | v2 refusals of merge do not feed the model ladder's counter (Add-Refusal), #542. |
| merge-migration-collision | behavior-gap | `src/Fworker.Workflows/Merge/MergeVerbService.cs` | 0 | W9.10q | W9.10q | The script refuses a merge when another pull request took the same DbUp script number (Get-MigrationCollision, Verbs.Merge.ps1:190); the v2 merge verb does not check it, #542. |

### Exceptions

- **auto-promote** (until W9.12): Decision 2 of 2026-10-30: the app's uat and prod promotions stay with v1's AutoPromoter until the delivery train has a recorded clean live window (the W5 gate). W9.11b must keep Verbs.Promote.ps1 and the Runner path for this one verb.
- **plant-snapshot** (until W9.11b): The snapshot is the hand-over between gateway processes and the rollback to v1 until W9.11b removes v1; the plant is in SQL from W9.10k.
- **leader-lease** (until W9.11b): The GitHub-ref lease stays as the arbitration with a process that has no database (v1, the kill switch, uat and prod) until W9.11b; where a database is configured W9.10b's SQL application lock replaces these calls.
- **merge-peer-approved** (until W9.11b): The peer-approved merge policy is dropped (decision 4 of 2026-10-30), not ported; its suites go with v1 (W9.11a). Every environment runs mergePolicy auto.
- **merge-hybrid-script** (until W9.11b): The adoption (W9.9b) gives the saga every open item, and W9.11b deletes the branch with the router of v1.
