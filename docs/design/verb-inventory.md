# The verb inventory

Task W1.10 of `fworker-wolverine-implementation.md`, the inventory that R1 of `fworker-wolverine-refactor.md` promises and R9 executes. One
row for each server verb: a verb that `plugin/scripts/factory.ps1 --server <verb>` runs, and that the gateway's `Runner` starts in a `pwsh`
child process where no v2 route answers it. The data is in `docs/design/verb-inventory.json`; this table is the same data for people. `tests/Gateway.Tests/VerbInventoryTests.cs`
fails when the three disagree: the `$ServerVerbs` list of `factory.ps1`, the JSON and this table (and, for the callers, the `VerbPolicy` sets of
`gateway/Policies/VerbPolicy.cs`, and the `$NoLockVerbs` list and `UseEndpointReason` for the two flags of the JSON). Adding or removing a verb
means a row here and an entry in the JSON in the same pull request.

**Count: 68.** `auto-promote` (v1's automatic promotion, `lib/Verbs.Promote.ps1`) left with the AutoPromoter in V3.2/V3.3 of the v2-only plan; the five `retired` verbs (`plant-snapshot-save`, `-load`, `-clear`, `-await` and `leader-lease`, `lib/PlantSnapshot.ps1` and `lib/Leader.ps1`) left in V3.3: the plant is kept in SQL and the SQL lock is the only lease, no gateway path called them and the verb route refused them to every caller. The transcript verbs moved to `lib/Transcripts.ps1`. In slice 5 the rule scripts moved to `server/rule.ps1` and `server/lib/` and kept 14 verbs (the routed verbs' fallbacks and the housekeeping). Slice 6 removed the fallbacks and slice 7 deleted their scripts (`merge`, `approve-pr`, `clear-hold`, `base-watch`, `base-update-branch`, `ingest-event`, `journal`: `Verbs.Merge.ps1`, `Verbs.Inspect.ps1`, `BaseUpdate.ps1`, `Verbs.Sweep.ps1`, `Journal.ps1` and the libraries only they reached). The rule scripts keep the 7 verbs of the gateway's housekeeping (`InertVerbs.Housekeeping`: `revocations`, `revoke-worker`, `operator-releases`, `operator-release`, `transcript-save`, `-load`, `-prune`); the other 61 have no script (`script: none`) and an environment the engine does not act in refuses them (`code: inert`).

## Columns

- **Verb**: the name in `$ServerVerbs`.
- **v1 script**: the file of `plugin/scripts/lib/` that defines `Verb_<name>` today. v1 keeps running every verb until the R9 cutover.
- **Owner**: the v2 component that takes the verb over (names of R9's table; the read models and services are the C# classes of `src/`
  that the stages after W1 create).
- **Destination**: `saga` (a Wolverine saga handles it: decision verbs by `InvokeAsync`, event verbs published), `handler` (a handler, read
  model or C# service answers it; no saga state), `coordinator` (the board reconciler or the event ingress S3: it feeds sagas, it is not one).
  The `retired` verbs (Wolverine's leader election and durable state replace them) are deleted (V3.3). No verb "stays v1": at the cutover v1 is removed.
- **Callers**: the credential kinds that `VerbPolicy.AuthorizeVerb` lets call the verb (`owner` and `inspector` are item-token roles,
  `owner-runtime` an owner token minted for a runtime worker). The admin credential may call any verb. "admin only" is a verb that
  only the gateway's own loops (and the admin) start.

## Table

| Verb | v1 script | Owner | Destination | Callers |
|---|---|---|---|---|
| `get-item` | none | WorkItemSaga | saga | owner,inspector,manager,keeper,qc |
| `append-body` | none | WorkItemSaga | saga | owner |
| `comment` | none | WorkItemSaga | saga | owner |
| `claim-item` | none | WorkItemSaga | saga | owner |
| `accept` | none | WorkItemSaga | saga | owner,inspector,qc |
| `release` | none | WorkItemSaga | saga | owner |
| `my-items` | none | WorkItemSaga | saga | worker |
| `record-review` | none | WorkItemSaga | saga | owner |
| `move-next` | none | WorkItemSaga | saga | admin only |
| `complete` | none | WorkItemSaga | saga | owner |
| `validation-verdict` | none | WorkItemSaga | saga | owner |
| `open-pr` | none | WorkItemSaga | saga | owner |
| `update-pr` | none | WorkItemSaga | saga | owner |
| `item-pr` | none | WorkItemSaga | saga | admin only |
| `park-item` | none | WorkItemSaga | saga | admin only |
| `unpark-item` | none | WorkItemSaga | saga | admin only |
| `resettle-item` | none | WorkItemSaga | saga | admin only |
| `expire-claim` | none | WorkItemSaga | saga | admin only |
| `pr-checks` | none | ChecksReadModel | handler | owner,inspector,manager,keeper |
| `wait-for-checks` | none | ChecksReadModel | handler | owner |
| `inspect-facts` | none | ChecksReadModel | handler | inspector |
| `get-delivery` | none | DeliveryReadModel | handler | owner,manager |
| `model-tier` | none | ModelTierReadModel | handler | owner,inspector |
| `check-secrets` | none | SecretsReadModel | handler | admin only |
| `approve-pr` | none | MergeTrainSaga | saga | inspector |
| `merge` | none | MergeTrainSaga | saga | owner |
| `clear-hold` | none | MergeTrainSaga | saga | admin only |
| `base-watch` | none | MergeTrainSaga | saga | admin only |
| `base-update-branch` | none | MergeTrainSaga | saga | admin only |
| `settle` | none | DeliveryTrainSaga | saga | admin only |
| `publish-demo` | none | DeliveryTrainSaga | saga | owner |
| `ci-release` | none | DeliveryTrainSaga | saga | admin only |
| `copilot-settle` | none | DeliveryTrainSaga | saga | admin only |
| `copilot-owner` | none | DeliveryTrainSaga | saga | admin only |
| `private-build` | none | DeliveryTrainSaga | saga | owner |
| `sync-board` | none | BoardReconciler | coordinator | admin only |
| `ingest-event` | none | S3EventIngress | coordinator | admin only |
| `journal` | none | HistoryProjection | handler | admin only |
| `qc-sync` | none | QcService | saga | admin only |
| `qc-link` | none | QcService | saga | admin only |
| `qc-view` | none | QcService | saga | qc-click |
| `qc-request` | none | QcService | saga | qc-click |
| `qc-reset` | none | QcService | saga | qc-click |
| `qc-teardown` | none | QcService | saga | qc-click |
| `qc-list` | none | QcService | saga | admin only |
| `qc-job` | none | QcService | saga | qc |
| `qc-progress` | none | QcService | saga | qc |
| `qc-report` | none | QcService | saga | qc |
| `qc-fail` | none | QcService | saga | qc |
| `qc-stopped` | none | QcService | saga | qc |
| `qc-transition` | none | QcService | saga | admin only |
| `qc-approval` | none | QcService | saga | admin only |
| `backlog` | none | KeeperCommands | handler | keeper |
| `amend-criteria` | none | KeeperCommands | handler | keeper |
| `split-item` | none | KeeperCommands | handler | keeper |
| `create-item` | none | KeeperCommands | handler | keeper |
| `set-priority` | none | KeeperCommands | handler | keeper |
| `link-dependency` | none | KeeperCommands | handler | keeper |
| `manager-action` | none | ManagerCommands | handler | manager |
| `revocations` | `Owner.ps1` | RevocationService | handler | admin only |
| `revoke-worker` | `Owner.ps1` | RevocationService | handler | admin only |
| `git-credential` | none | GitCredentialService | handler | owner-runtime |
| `operator-releases` | `OperatorRelease.ps1` | OperatorReleaseService | handler | admin only |
| `operator-release` | `OperatorRelease.ps1` | OperatorReleaseService | handler | admin only |
| `metrics` | none | MetricsReadModel | handler | admin only |
| `transcript-save` | `Transcripts.ps1` | TranscriptService | handler | admin only |
| `transcript-load` | `Transcripts.ps1` | TranscriptService | handler | admin only |
| `transcript-prune` | `Transcripts.ps1` | TranscriptService | handler | admin only |

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

Kinds: `runner` calls `Runner.RunAsync` for a verb that has no route, or `Runner.RunScriptAsync` (the rule script whatever v2 answers), so it is a rule script on every engine (only the declared exceptions are left); `router` asks the route table, either by forwarding inside the v2 router chain or through `Runner.RunAsync`, which since W9.9a asks `V2VerbRoutes` first for every verb that has a route where the engine acts (the script runs only where it does not, and for a verb with no route); it is listed so that the count of call sites is exact. The per-pull-request bridges (`DeliveryVerbBridge`, `ServiceVerbCalls`, `Runner.V2Own`, the route tables of `BaseWatch` and `QcHub`) are gone.
Exceptions are the declared reasons an entry may outlive the rows before W9.11; the test pins their ids, so a new one is a visible edit.

### The gateway's own calls

| Entry | File | Sites | Verbs | Kind | Owner | Leaves | What it is |
|---|---|---|---|---|---|---|---|
| base-watch-verbs | `gateway/BaseUpdate.cs` | 1 | by argument | router | W9.3 | - | BaseWatch runs base-watch and base-update-branch through the Runner (routed since #542; its own table is gone). |
| base-journal | `gateway/BaseUpdate.cs` | 1 | journal | router | W9.8 | - | The journal line of a base update (#477). |
| transcripts | `gateway/Composition/GatewayServiceCollectionExtensions.cs` | 1 | by argument | runner | W9.8 | W9.11b | TranscriptStore: transcript-save, transcript-load and transcript-prune, the rule scripts of the ref store (RunScriptAsync) where the engine does not act; transcript-load alone where it acts, until 2026-11-13. **exception until W9.11b.** |
| plant-snapshot | `gateway/Composition/GatewayServiceCollectionExtensions.cs` | 1 | by argument | runner | W9.10k | W9.11b | SnapshotCoordinator: plant-snapshot-save, -load, -clear and -await. **exception until W9.11b.** |
| revocation-calls | `gateway/Composition/GatewayServiceCollectionExtensions.cs` | 1 | by argument | router | W9.7 | - | RevocationService calls the Runner (revocations, revoke-worker): v2's route where the engine acts. |
| copilot-owner-verbs | `gateway/Composition/GatewayServiceCollectionExtensions.cs` | 1 | by argument | router | W9.4 | - | CopilotOwnerVerbs: copilot-owner. |
| copilot-settle-client | `gateway/Composition/GatewayServiceCollectionExtensions.cs` | 1 | by argument | router | W9.4 | - | CopilotSettleClient: copilot-settle. |
| plant-journal | `gateway/Composition/GatewayServiceCollectionExtensions.cs` | 1 | journal | router | W9.8 | - | The plant's journal lines (#477). |
| operator-release-calls | `gateway/Composition/GatewayServiceCollectionExtensions.cs` | 1 | by argument | router | W9.7 | - | OperatorReleaseLoader calls the Runner (operator-releases): v2's route where the engine acts. |
| runtime-failed-park | `gateway/Composition/GatewayServiceCollectionExtensions.cs` | 1 | park-item | router | W9.10h | - | The runtime adapters' park after a failed launch: the Runner asks the park-item route where the engine acts; W9.10h tells the saga as well (ParkWorkItem). |
| runtime-release-claim | `gateway/Composition/GatewayServiceCollectionExtensions.cs` | 1 | claim-item | router | W9.10j | - | The runtime adapters' ReleaseClaim: the Runner asks the claim-item route where the engine acts; the saga's step lease and the scheduler's slot replace it (W9.10j). |
| credit-guard-park | `gateway/Composition/GatewayServiceCollectionExtensions.cs` | 1 | park-item | router | W9.10h | - | The Copilot credit guard's park: as the runtime adapters' park, through the Runner; W9.10h tells the saga (ParkWorkItem). |
| feature-checks | `gateway/Composition/GatewayWolverineExtensions.cs` | 1 | by argument | router | W9.5 | - | FeatureChecksJob reads pr-checks through PullRequestChecks. |
| publish-demo | `gateway/Endpoints/DemoEndpoints.cs` | 1 | publish-demo | router | W9.4 | - | The demo endpoint calls the Runner: v2's route where the engine acts. |
| unpark-item | `gateway/Endpoints/ItemAdminEndpoints.cs` | 1 | unpark-item | router | W9.2 | - | The unpark endpoint: the Runner asks the unpark-item route where the engine acts. W9.10h: it tells the saga (ResumeWorkItem), so the item is offered again. |
| resettle-item | `gateway/Endpoints/ItemAdminEndpoints.cs` | 1 | resettle-item | router | W9.2 | - | The resettle endpoint. |
| metrics-calls | `gateway/Endpoints/MetricsEndpoints.cs` | 1 | metrics | router | W9.7 | - | The metrics endpoint calls the Runner: v2's route where the engine acts. |
| operator-release-endpoint | `gateway/Endpoints/OperatorReleaseEndpoints.cs` | 1 | operator-release | router | W9.7 | - | The record-release endpoint calls the Runner: v2's route where the engine acts. |
| ingest-event | `gateway/Endpoints/WebhookEndpoints.cs` | 1 | ingest-event | router | W9.8 | - | The webhook endpoint hands the event to the router: `BoardVerbs.IngestEvent` where the engine acts, with no rule script behind it (V3.3, slice 6). |
| leader-lease | `gateway/Leader.cs` | 5 | leader-lease | runner | W9.10b | W9.11b | The leader lease on the GitHub ref. **exception until W9.11b.** |
| plant-claim-renew | `gateway/PlantHooks.cs` | 1 | claim-item | router | W9.2 | - | Claim renewal of an assigned item. |
| plant-expire-claim | `gateway/PlantHooks.cs` | 1 | expire-claim | router | W9.2 | - | Claim expiry of an assigned item. |
| ticker-journal | `gateway/PlantTicker.cs` | 1 | journal | router | W9.8 | - | The ticker's journal lines (#477). |
| owner-release | `gateway/Ports/ItemStepsPort.cs` | 2 | get-item, release | router | W9.10f | - | OwnerRelease: the saga's ReleaseOwner (an owner that waited out a hold) reads the claim with get-item and gives it up with release, through the verb router (v2's item verbs where the engine acts); no script of its own. |
| plant-restore-claim | `gateway/PlantRestoreCorrection.cs` | 1 | get-item | router | W9.10k | - | PlantRestoreCorrection: at start the plant restored from the database is corrected from the claim of each owned saga item, read with get-item through the verb router (v2's item verbs where the engine acts); no script of its own. |
| item-admin-feed | `gateway/Ports/ItemVerbsPort.cs` | 1 | by argument | router | W9.2 | - | ItemAdminFeed forwards to the router it wraps (no script). |
| qc-calls | `gateway/Qc.cs` | 1 | by argument | router | W9.1 | - | QcHub calls the Runner for the qc-* verbs: QcService's route where the engine acts (its own table is gone). |
| qc-facts | `gateway/Ports/QcPort.cs` | 1 | inspect-facts | router | W9.5 | - | QcFactsAdapter asks the router for the inspector's facts; the router answers on v2 or runs the script. |
| delivery-open-pr | `gateway/Ports/DeliveryVerbHost.cs` | 1 | by argument | router | W9.4 | - | copilot-settle opens the item's pull request through the verb router (open-pr); no script of its own. |
| item-pr-lookup | `gateway/Runtimes/ItemPrLookup.cs` | 1 | item-pr | router | W9.2 | - | The pull request of an item for the runtime adapters (the owner-task case is a fallback below). |
| owner-ci-release | `gateway/Runtimes/OwnerCiReleaseService.cs` | 1 | by argument | router | W9.4 | - | OwnerCiReleaseService runs ci-release. |
| owner-ci-journal | `gateway/Runtimes/OwnerCiReleaseService.cs` | 1 | journal | router | W9.8 | - | Its journal lines. |

### The fallbacks of verbs that have a v2 owner

None. V3.3 (slice 6) removed the last ones: `merge-hybrid-script` (the five `Script(...)` calls of `gateway/Verbs/MergeVerbs.cs`: merge, approve-pr, clear-hold and base-watch for an item the saga
does not own) and `merge-peer-approved` (`MergeVerbService`'s `if (!settings.Auto) return null;`, a merge policy other than auto), which the gateway now refuses (`not-owned`, `merge-policy`); and, outside this
table, the script behind `ingest-event` without a translator and `journal` without a ledger (`BoardVerbs`), which now refuse (`config-missing`, `not-ready`), and the prune of the ref store by `DatabaseTranscriptBackend` (its
read-only fallback of a load stays until 2026-11-13, below).

### Exceptions

- **plant-snapshot** (until W9.11b): The snapshot is the hand-over between gateway processes and the rollback to v1 until W9.11b removes v1; the plant is in SQL from W9.10k.
- **transcripts** (until W9.11b): The ref store is the durable store of the transcripts where the engine does not act (no route answers the transcript verbs there). Where it acts, `BoardWiring` replaces it with `factory.Transcripts` and keeps it as the read-only fallback of a load, through `Runner.RunScriptAsync` (the board routes answer `transcript-load` from the database and would never reach the refs), for the sessions written before the move of 2026-10-30. That read goes after 2026-11-13, when the last of them is past the 14 days; V3.3 (slice 6) already took out the prune of the refs. The rest goes with the Runner's script half.
- **leader-lease** (until W9.11b): The GitHub-ref lease stays as the arbitration with a process that has no database (v1, the kill switch, uat and prod) until W9.11b; where a database is configured W9.10b's SQL application lock replaces these calls.

## Labels that no verb reads

`agent:<name>` (for example `agent:cursor`) is not a label of the factory: no verb of v1 or v2 reads it. The agent of a runtime is runtime configuration (`runtimes...octopus.agent`, `runtimes...caeJob.agent`); the label of an item that names a runtime is `runtime:<kind>` (V1.3 of the v2-only plan reads it in the slot request, `SlotRequestSettings.RuntimeFor`).
