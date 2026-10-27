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
