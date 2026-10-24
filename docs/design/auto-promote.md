# Automatic promotion of cmdemo1-ui (owner decision 2026-10-23)

The acting gateway promotes a release of `octopus.project` (cmdemo1-ui) from tdd to uat, then to prod, by itself: "gated by checks", no AI
session and no person in the loop. Code: `plugin/scripts/lib/Verbs.Promote.ps1` (`Invoke-AutoPromote`), called at the end of every full sweep
(`sync-board`, not dry, not incremental) with the Release Queue the sweep collected. Suite: `tests/scenarios/promote.ps1` (`promote`).

## Settings

| Setting | Default (`config/instance.json`) | tdd gateway (`config/deploy.overrides.json`) |
|---|---|---|
| `delivery.autoPromote.enabled` | `false` | `true` |
| `delivery.autoPromote.environments` | `["uat", "prod"]` | same |
| `delivery.autoPromote.gatewayEnvironment` | `""`: nobody promotes (fail closed) | `"tdd"`: only the gateway with `FACTORY_ENVIRONMENT=tdd` promotes (the same image runs in uat and prod) |
| `octopus.tddUrl`, `octopus.uatUrl` | `""`: the release waits ("no health URL") | the cmdemo1-ui app URLs of tdd and uat |
| `octopus.healthPath`, `octopus.health` | `/_healthcheck/detailed`; spacing 5, settle 10 min, 3 retries 20 s | same |

Only the acting gateway sweeps (the leader lease), so only it promotes.

## Which items a release carries

The candidates are the releases recorded on Release Queue items (`merge.release`). A release is a master build and carries every earlier
commit, so it is judged by its whole content relative to the target environment: the commit running there (the release of the newest successful
deployment of the project to that environment, its commit) and the release's commit are compared through the GitHub compare API (the gateway's
own access). Every first-parent master commit in (deployed, release] must be the merge commit of a factory work item in any column (a Release Queue item, or found through the pull request its squash message names: a docs-only item in Done, a merge not settled yet, which makes the release wait), and every such item must
pass the item gates; a commit that is no item's merge commit (a person's merge, a direct push, a bot pull request) blocks the release and is
named. A release not newer than what runs in the target is skipped. Fail closed: when the running commit, the release's commit or the range
cannot be read, the release waits and says why. So release N+1 never carries an unqualified change of release N.

## Gates, per release and target environment

The target is the first of `environments` without a successful deployment of the release. All must hold:

1. every item: the `Auto Merge` label; no `Hold` label (nor one of `holdLabels`); `merge.pr` equals the PR of a passed verdict and the merge was
   not a person's (`merge.by` is not `person`); with the `video` label, release `demo-N` has the asset `demo-N.mp4`;
2. the release's newest deployment to the previous environment (tdd for uat, uat for prod) ended `Success`;
3. that environment's app (the rule of cmdemo1-system's ui-auto-promote): `GET <envUrl>/_build` answers the release's version; `healthPath`
   (`/_healthcheck/detailed`) answers `overallStatus` `Healthy`; no non-Healthy read since that deployment finished (one means waiting for a
   new deployment there); at least two Healthy reads `octopus.health.spacingMinutes` (5) apart, the later `settleMinutes` (10) after the
   deployment finished. An unreachable read is retried `retries` (3) times about `retrySeconds` (20 to 30) apart before it counts as
   non-Healthy. One read per sweep, kept per release and environment in the state directory (`health-<env>-<version>-<release id>.json`); lost on a
   restart, collecting starts again. The health of the previous environment is read only once every item and content gate holds. A failed write of the reads is a gap (and a line in the gateway log).

A failed gate: no deployment; each item gets one journal line `promotion-waiting` with every reason (keyed by release, target and reasons, so
it is said again only when the reasons change). The other items of the release are told which item holds it back.

## Promotion and following

- Before creating, the deployments of the release are read again (`GET /api/{space}/releases/{id}/deployments`); one to the target means no
  POST. So a restart, a second sweep or a person's deployment never leads to a second deployment.
- Create: `POST /api/{space}/deployments/create/v1` with `SpaceIdOrName`, `ProjectName`, `ReleaseVersion`, `EnvironmentNames` (the Executions
  API). The task and deployment ids are recorded on each item (`merge.promoted.<env>`) and journaled once: "promoted release X to uat (Octopus
  task Y)".
- Following: each sweep reads the task. While it runs and the deployment is one the gateway created (its task is recorded on an item), the
  pending interruptions of that task (`GET /api/{space}/interruptions?regarding=<task>&pendingOnly=true`), the Sign-off step, are answered:
  `POST .../interruptions/{id}/responsible`, then `POST .../interruptions/{id}/submit` with `Result` `Proceed` when every gate still holds
  (read again at that moment), else `Abort` with the reasons in `Notes`. A deployment someone else created is never answered.
- A deployment that ended `Failed` (or aborted) is explained once and not deployed again by the gateway: the operator deploys again, or the
  item goes back to Development. The prod deployment's success is what settle already watches: it moves the items to Done ("Delivered to Prod").

## Refusals

A 403 is reported once on each item with the permission Octopus names ("Missing permission: X", only that word is taken), and the release
waits; the next sweeps try again silently, and the promotion goes on once the permission is granted. Any other error is a sweep action
`promotion-error` and is retried at the next sweep.

## The key and the permissions it needs

The calls use `OCTOPUS_RUNBOOK_API_KEY` when it is set (the runtime's service account), else `OCTOPUS_API_KEY` (the reader, which gets 403s).
The account needs, on project cmdemo1-ui (environments tdd, uat, prod unless noted):

| Permission | For |
|---|---|
| `ProjectView`, `EnvironmentView` | the project and the environment names |
| `ReleaseView` | the release by version |
| `DeploymentView`, `TaskView` | the deployments of the release and their task state |
| `DeploymentCreate` (uat, prod) | creating the deployment |
| `InterruptionView` (uat, prod) | reading the pending Sign-off |
| `InterruptionSubmit` (uat, prod) | taking responsibility and answering Proceed or Abort |

If the Sign-off step is restricted to a team (the step's "responsible teams"), the account must be a member of that team as well.
The grant is an owner action. When the gate is live, cmdemo1-system's `ui-auto-promote.yml` (Octopus user cmdemo1-github) stops promoting
ui, so the two never both promote.
