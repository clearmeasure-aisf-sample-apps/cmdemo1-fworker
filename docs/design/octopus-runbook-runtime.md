# Octopus runbook runtime: AI workers on self-hosted machines through Octopus runbooks

Status: built 2026-10-22, off everywhere (`runtimes.octopus.enabled` false in `config/instance.json`, nothing in
`config/deploy.overrides.json`). Code: `gateway/Runtimes/OctopusRunbookRuntime.cs`, wired in `gateway/Program.cs` and
`gateway/Plant.Runtimes.cs`. Tests: suite `octopus` (`tests/scenarios/octopus.ps1`) against the fake Octopus of `tests/FakeGitHub`.
Builds on `cloud-worker-runtimes.md` (the adapter interface, the nonce and the w2, the plant's routing, restart recovery).

## 1. What it is

A runtime kind `octopus` beside `cae-job`, `cursor-cloud` and `copilot-cloud`. The gateway stays in Azure. A worker of this runtime is a
container on a machine the owner runs (an Octopus tentacle registered as a deployment target with target tag `fworker-host` in environment
tdd of space Spaces-355, https://clearmeasure.octopus.app). The gateway never talks to the machine: it asks Octopus to run a runbook on it,
and the machine's scheduled reconcile runbook tells the gateway what runs there.

An item reaches the runtime by the label `runtime:octopus` or a `runtimes.routing` rule naming `octopus`, as for every other runtime.

## 2. The contract (shared with the runbooks and the image; names are fixed)

- Octopus project `cmdemo1-fworker-runtime`, runbooks `Start worker`, `Stop worker`, `Reconcile hosts` (scheduled). Start/Stop take
  prompted variables: Fworker.Name, Fworker.Item, Fworker.Phase, Fworker.ModelProfile, Fworker.Nonce, Fworker.Agent (claude|cursor|codex),
  Fworker.Mode (detached|held). Container name `fw-<name>`, labels fworker.name, fworker.item, fworker.nonce.
- A fourth runbook `Start worker (overflow)`: the same prompted variables, its step runs on the Octopus Hosted Ubuntu dynamic worker pool
  (no deployment target), with Fworker.Mode = held: the container runs in the step and the task lasts as long as the worker.
- Adapter Provision: pick a host (machine) among healthy `fworker-host` targets with free capacity (per-host limit from settings, default 2;
  count from its own record plus List), then POST /api/{spaceId}/runbookRuns with the published snapshot of `Start worker`, EnvironmentId of
  tdd, SpecificMachineIds [machine], FormValues for the prompted variables (control ids resolved from the runbook run preview, see 4).
  Execution = the ServerTask id. If no self-hosted capacity and the worker's role is in overflowRoles and overflow in use < overflowMax: run
  `Start worker (overflow)` with Mode=held and no SpecificMachineIds. Otherwise return Ok=false with Error "no capacity" so the plant queues.
- Deprovision: run `Stop worker` on the machine the worker was started on (held mode: cancel the server task, POST /api/tasks/{id}/cancel).
- List: from the adapter's record plus GET /api/tasks/{id} states; map Octopus task states to the RuntimeInstance status vocabulary.
  Survive a gateway restart: rebuild from recent tasks of the project (GET /api/{space}/tasks?project=...) reading the Fworker.* values.
- Reconcile endpoints for the scheduled runbook: GET /api/v1/runtime/octopus/desired?host=<machine> returning the worker names that should
  run on that host, and POST /api/v1/runtime/octopus/report (host, containers [{name,item,state,startedAt}]). Authenticate with a dedicated
  secret env var FACTORY_OCTOPUS_HOST_TOKEN (constant-time compare), never the operator token. Report feeds List (a container gone/exited ->
  instance Stopped so the plant re-plans; see 5 for the one refinement).
- Settings under `runtimes.octopus` in `config/instance.json`: { enabled false by default, serverUrl, spaceId "Spaces-355", project,
  environment "tdd", hostTag "fworker-host", perHostCapacity 2, overflowRoles [...], overflowMax 4, maxConcurrent, auth, serviceAccountId,
  oidcAudience }. The gateway signs in as a service account allowed to run runbooks of that project only, by OIDC with its managed identity
  (`auth` "oidc", the default when no key is set) or by an API key from env OCTOPUS_RUNBOOK_API_KEY (`auth` "apiKey"); see 8. Disabled when
  the chosen credential is missing (serviceAccountId empty, or no key). Not enabled in config/deploy.overrides.json.

Further settings with defaults: `startRunbook`, `overflowRunbook`, `stopRunbook` (the names above), `agent` ("codex"; config/instance.json sets "cursor", the one CLI key in the Octopus project),
`maxExecutionsPerDay`, `maxFailuresPerPhase` 2, `unhealthyAfterFailures` 2, `ackSeconds` 600 (covers an image pull on a cold host),
`listSeconds` 30, `reportGraceSeconds` 90.

What the gateway sends and answers, by example:

```
POST /api/Spaces-355/runbookRuns
{ "RunbookId": "Runbooks-1", "RunbookSnapshotId": "RunbookSnapshots-10", "EnvironmentId": "Environments-11",
  "SpecificMachineIds": ["Machines-1"], "FormValues": { "<control id of Fworker.Name>": "oct-512-a1b2c3", ... } }

GET /api/v1/runtime/octopus/desired?host=host-a            Authorization: Bearer <host token>
{ "host": "host-a", "machine": "Machines-1", "workers": ["oct-512-a1b2c3"] }

POST /api/v1/runtime/octopus/report                         Authorization: Bearer <host token>
{ "host": "host-a", "containers": [{ "name": "fw-oct-512-a1b2c3", "item": 512, "state": "running", "startedAt": "2026-10-22T10:00:00Z" }] }
-> { "ok": true, "machine": "Machines-1", "accepted": 1, "stop": ["<fw- names running that should not>"] }
```

`host` is the machine's name or its id, and must be a deployment target of `hostTag` in the environment: any other host is answered 404
(and nothing is stored). A report keeps at most 200 containers. A container name is accepted with or without the `fw-` prefix. Until the
records of a previous process are read back (7), `desired` answers 503 (the reconcile removes nothing) and `report` is kept with an empty
`stop`. A worker is in `desired` from the moment its slot is reserved, before the Start run is posted, so a reconcile in between does not
remove the container being started.

## 3. Why start-and-exit

Octopus Cloud runs at most 20 tasks at a time for the whole instance, and that cap is shared with every deployment and runbook of every
space. A worker lives for hours; a task held open per worker would let three or four workers starve the deployments of the demo. So
`Start worker` starts a detached container (`docker run -d --name fw-<name> ...`) and exits within seconds: the task cap is spent for
seconds per worker, not hours. The container's life is then the machine's business, observed through the reconcile loop (6).

The overflow runbook is the one exception: the Hosted Ubuntu dynamic workers are torn down when the task ends, so a container there lives
only as long as its task (Mode held). That holds a task slot for the worker's whole life, which is why it is:

- only for short roles: `overflowRoles` (default plant-manager, supervisor, qc, operator). A coding role is refused whatever the list says
  (`worker`, `development`, `coding`, `own-item`): a coding session runs for hours and builds the product, it never belongs on a shared pool.
  The role is `ProvisionRequest.Role`; an own-item worker (the plant's phase workers, no role) never overflows. Today only the test seam
  `POST /api/v1/_test/octopus-provision` passes a role: a manager or QC start through this runtime is not wired yet.
- capped: `overflowMax` (default 4) held tasks at a time, so at least 16 of the 20 slots stay for deployments and starts.
- used only when no self-hosted machine has a free slot.

## 4. Host selection and the start

At every start the adapter reads, with the runbook key:

1. the project and the environment by name (`/projects/all`, `/environments/all`; cached),
2. the project's runbooks and their `PublishedRunbookSnapshotId` (read every time: a republished runbook is used at once; a runbook without a
   published snapshot refuses the start with `runbook-not-published`),
3. the deployment targets: `GET /api/{space}/machines?environmentIds=<tdd>&roles=fworker-host`, keeping those not disabled whose health is
   Healthy or HasWarnings (a tentacle with an upgrade warning still runs containers),
4. the control ids of the prompted variables, once per runbook: `GET /api/{space}/runbooks/{id}/runbookRuns/preview/{environmentId}`,
   `Form.Elements[].Name` is the control id and `Form.Elements[].Control.Name` the variable name (the approach of Octopus's documented
   sample "Run a runbook with prompted variables"). A variable the runbook does not prompt is not sent.

The load of a host is the number of distinct workers on it: this process's records of detached workers started there and not ended, plus
the `running` containers of its last report (a container started by a previous gateway process counts until it exits). The host with the
lowest load under `perHostCapacity` is chosen (ties by name); the slot is reserved in the record before the run is posted, so two starts at
once do not take the same last slot.

No host and no overflow: the start answers `no capacity`. The runtime manager treats it like a quota refusal: no failure of the item, no
launch and no execution of the day counted, and for 15 seconds (or `listSeconds` when longer) the runtime answers `runtime-no-capacity` to
the plant, so the item waits (journalled `waiting` with that code when it is labelled) instead of being retried at every plan.

## 5. Status mapping

| Octopus | Detached worker | Held worker |
|---|---|---|
| task Queued | Processing | Processing |
| task Executing, Cancelling | Processing | Running |
| task Success | from the host's reports, below | Succeeded |
| task Failed, TimedOut | Failed | Failed |
| task Canceled | Failed | Stopped |
| task gone (404) | Failed | Stopped |

A detached worker whose start succeeded is Running until a report of its host after the start says otherwise:

- container `running` (or `restarting`, `created`): Running;
- container `exited`: Succeeded. This refines the contract's "gone/exited -> Stopped": an exit is how a worker ends after `session.end`
  (one item per session), and the manager counts an unrequested Stopped as a failure of the phase, which would park items that finished
  normally. With Succeeded the plant still takes back whatever the worker held and re-plans the item, and an exit while it still owned the
  item still counts as a failure (`ended-with-item`), as for cae-job;
- container any other state, or not listed in a report received more than `reportGraceSeconds` after the start: Stopped (the plant
  re-plans; a stop the gateway asked for ends as `stopped:<reason>`, no failure).

## 6. The reconcile loop

The scheduled runbook `Reconcile hosts` runs on every `fworker-host` target (every minute or two). On each machine it:

1. asks `desired?host=<machine name>` for the workers that should run there,
2. lists its `fw-*` containers (`docker ps -a --filter label=fworker.name`), stops and removes those not desired and removes exited ones
   after reporting them once,
3. reports what it found to `report`. The answer's `stop` names the running containers the gateway does not want (the runbook may stop them
   at once instead of waiting for the next run).

So a container outlives neither its worker nor the gateway's intent: a lost Stop run, a gateway restart that did not adopt the worker, or a
crashed stop are all cleaned up at the next reconcile. The reports also feed host load (4) and the status (5).

## 7. Restart recovery

The records are in memory. Before the first reading, start, `desired` or report judgement after a process start the adapter reads the
project's tasks back to one day, page by page (`GET /api/{space}/tasks?project=<id>&skip=<n>&take=100`, newest first; the frequent
`Reconcile hosts` tasks do not push the starts out), for each runbook run task of the last day its run
(`GET /api/{space}/runbookRuns/{id}`: runbook, `FormValues`, `SpecificMachineIds`), and maps the values back to names through the runbook's
form. A Start (or overflow Start) whose worker has no newer Stop run becomes a record again; the runtime manager adopts it while it is
Running or Processing, with a fresh window for the worker to reconnect, so no second worker is started for its item. A detached worker read
back this way that its host's next report does not list had exited before the restart: it ends Succeeded, not as a failure.

## 8. Security

- **A separate service account** for the gateway: a team whose only role is Runbook Consumer (run and view runbooks, view tasks,
  deployment targets and environments) on project `cmdemo1-fworker-runtime` in environment tdd. It cannot edit a runbook, deploy a release
  or read a sensitive variable. It is not the read-only `cmdemo1-reader` and not the operator identities. Without its credential the runtime
  is off, whatever the file says.
- **No long-lived key: OIDC with the gateway's managed identity** (`auth` "oidc", default). Octopus "OpenID Connect" for service accounts,
  "Using OIDC with other issuers" (octopus.com/docs/api/authentication/openid-connect/other-issuers). The gateway asks Entra (Azure.Identity
  `ManagedIdentityCredential`, the user-assigned identity of `managedIdentityClientId` or env FACTORY_RUNTIME_IDENTITY_CLIENT_ID, else the
  system-assigned one) for a token of scope `<oidcAudience>/.default`, default `api://AzureADTokenExchange` (the audience managed identities
  issue for federation; its tokens are Entra v2), and exchanges it at `POST {serverUrl}/token/v1` (the `token_endpoint` of Octopus's
  `/.well-known/openid-configuration`), JSON `{ grant_type: urn:ietf:params:oauth:grant-type:token-exchange, audience: <serviceAccountId>,
  subject_token_type: urn:ietf:params:oauth:token-type:jwt, subject_token: <Entra JWT> }`. The answer's `access_token` is sent as
  `Authorization: Bearer`, cached until a minute before `expires_in` (half its life when shorter); a 401 makes one new exchange and one
  retry. Neither token is logged or shown in `/status` (which shows `auth`, `serviceAccountId` and the exchange count).
  What an Octopus administrator enters (Configuration > Users > the service account > OpenID Connect > Add new OIDC identity, type "Other
  issuer"):
  - **Issuer URL:** `https://login.microsoftonline.com/<tenant id>/v2.0` (the gateway's Entra tenant; `iss` of the token, no trailing slash)
  - **Subject:** the **object (principal) id** of the gateway's managed identity (`az identity show -n <identity> -g <rg> --query principalId`,
    or `az containerapp identity show` for a system-assigned one) — not its client id; exact and case-sensitive
  - **Audience (custom):** `api://AzureADTokenExchange` (must equal `oidcAudience`; the token's `aud` is not the service account id)
  - then set `runtimes.octopus.serviceAccountId` to the service account's id (e.g. `Users-903`, the "Service Account Id" shown on that page)
  Tests: a loopback serverUrl takes the Entra token from FACTORY_TEST_OCTOPUS_ENTRA_TOKEN (ignored for any other server), as cae-job's
  FACTORY_TEST_ARM_TOKEN.
- **API key** (`auth` "apiKey", env `OCTOPUS_RUNBOOK_API_KEY`, sent as `X-Octopus-ApiKey`): kept for a server without OIDC. With no `auth`
  setting a set key selects this mode.
- **The host token** (`FACTORY_OCTOPUS_HOST_TOKEN`) is the only credential of the reconcile endpoints, compared in constant time; the
  operator token, a worker credential or no token answer 401. Unset, the endpoints refuse every call. It is stored in the gateway app's
  secrets and as a sensitive variable of the Octopus project; it lets a caller read worker names and report containers, nothing more.
- **CLI keys of the agents** (Claude, Cursor, Codex) are sensitive variables of the Octopus project, scoped to tdd, injected by the runbook
  into the container. They never pass through the gateway, are never in a prompted variable and never in this repository.
- **The nonce** is the only credential the gateway hands out per worker: one-time, minutes long, redeemed by the container for its w2
  (`cloud-worker-runtimes.md` 2.6). Fworker.Nonce should be a sensitive prompted variable in the runbook so that the task log masks it. The
  gateway logs neither the nonce nor the key (the suite checks both), and `/status` shows machine, mode and task state only.

## 9. What Octopus Cloud maintenance does

Octopus Cloud instances get a maintenance window (weekly; upgrades restart the server). While the server is down:

- new starts fail (the start answers an HTTP error: a start failure of the item, and after `unhealthyAfterFailures` the runtime is marked
  unhealthy; the operator resets it with `POST /api/v1/runtimes/octopus/reset`);
- running detached containers keep running: they talk to the gateway, not to Octopus. Their status reads stay where they were (a detached
  worker's task is no longer read once its start ended);
- the scheduled `Reconcile hosts` does not run, so no report arrives and no container is cleaned up until the server is back; that is the
  same as a late report, nothing ends because of it;
- held overflow tasks may be cancelled by the restart: their workers end Stopped and their items are planned again.

## 10. Tests

Suite `octopus` (fake Octopus of `tests/FakeGitHub`: project, runbooks with prompted variables, five deployment targets of which two are
healthy `fworker-host` targets in tdd, runbook runs and server tasks; `/_fake/octo`, `octo-reset`, `octo-machines`, `octo-task`):
host selection and the prompted variables, no capacity (no failure, the item waits), the reconcile endpoints (401 for a wrong, absent or
operator token; desired by name or id; exited and gone containers), the stop on the start machine, overflow for an allowed role capped at
`overflowMax` and its cancel, a coding role that never overflows, and the restart rebuild (adopted, then not adopted after its Stop).
