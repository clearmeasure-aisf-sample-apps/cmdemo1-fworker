# Handoff: state of the factory worker gateway

Written 2026-10-13, when the source moved into this repository. It is the starting point of a session that has only a clone of
`clearmeasure-aisf-sample-apps/cmdemo1-fworker`. How to build, test and release: `CLAUDE.md`. Designs: `docs/design/`.
Keep this file current: a change that closes a backlog entry or changes an environment edits it in the same pull request.

## 1. What the system is

A software factory for the work-order application (`clearmeasure-aisf-sample-apps/cmdemo1-workorders`, public, project board 679).
A work item labelled `factory worker` that is moved to Technical Design is taken by an AI session and carried through
Technical Design, Development and Functional Testing to a merged pull request, a release and Done.

- **Gateway** (this repository, `gateway/`): holds every GitHub and Octopus credential, runs the rule scripts
  (`plugin/scripts/factory.ps1 --server <verb>`), sweeps the board, keeps the plant (workers, assignments, owners), serves the dashboard,
  the connect prompt and the plugin download, and carries worker messaging. Workers never hold a GitHub credential: they call verbs.
- **Workers**: AI sessions (Claude Code local or cloud, Cursor cloud) started by a person with the connect prompt of the dashboard.
  The prompt downloads PowerShell and the plugin and runs `subscribe --own`; the session owns each item it is given until its pull request
  is merged and master is green. Label `worker:<name>` pins an item to one worker; `Auto Merge` lets it merge without a person;
  `factory test` selects the cheapest model profile.
- **Plant manager / supervisor**: one session (name `supervisor`) that answers the questions workers ask and reads the gateway's patrol
  digest. It advises; it changes nothing.
- **Operator**: the person (or script) with the operator token: dashboard, credentials for workers, revoke, messaging on and off,
  notes to any session.
- **Operator session**: one AI session (name `operator-session`) with a credential of its own kind, not the operator token. It watches
  every item and improves the factory worker and the gateway within its guard rails (`docs/design/operator-session.md`, section 6a).
- **Messaging**: notes, broadcasts, item threads, questions and answers between workers, the supervisor and the operator
  (`docs/design/worker-messaging.md`). All of it is in the memory of the gateway process.

## 2. Environments and how to check them

| Environment | URL | State |
|---|---|---|
| tdd | `https://ca-cmdemo1-tdd-fworker.graybush-19a6e13b.southcentralus.azurecontainerapps.io` | The acting gateway: sweeps the board. Version 1.0.37 on 2026-10-13 (pull request 18). Every merge to `master` deploys here |
| uat | not recorded in this repository; ask the operator | Receives a release only by a manual promotion |
| prod | `https://ca-cmdemo1-prod-fworker-bhj5.happyisland-465ac2bf.southcentralus.azurecontainerapps.io` | Inert: its GitHub credential is the placeholder, so it does not act. `docs/deploy/activate-prod.sh` (run by the owner) would make prod the acting gateway and tdd inert. Only one gateway may sweep the board |

Checks that need no credential:

```bash
U=https://ca-cmdemo1-tdd-fworker.graybush-19a6e13b.southcentralus.azurecontainerapps.io
curl -s $U/_build                                  # version, pluginHash, started (a new start time means a restart), environment
curl -s -o /dev/null -w '%{http_code}\n' $U/ready  # 200 when the revoked credentials are read; 503 "starting" before
curl -s -o /dev/null -w '%{http_code}\n' $U/health # liveness (also /alive)
# dashboard, in a browser: $U/dashboard  (asks for the operator token)
```

Checks with the operator token (`Authorization: Bearer $FACTORY_OPERATOR_TOKEN`): `GET /api/v1/status` (plant, workers, assignments,
messaging and manager counters), `GET /api/v1/messages?limit=200` (traffic), `GET /api/v1/questions?state=all`, `GET /api/v1/peers?all=1`.

### Credentials are NOT in this repository

A new session must be given them as environment secrets. Names the scripts read:

| Variable | What | Read by |
|---|---|---|
| `FACTORY_OPERATOR_TOKEN` | Operator token of the gateway of one environment (tdd for daily work) | `docs/deploy/gen-connect-prompt.sh`, the status calls above |
| `GH_TOKEN` | GitHub token with push to this repository (pull requests, `gh pr merge`, `gh run`) and, for live proofs, issues and the board of cmdemo1-workorders | `gh`, `run-*.sh` |
| `OCTOPUS_API_KEY` | Read-only key of the Octopus space `Spaces-355` at `https://clearmeasure.octopus.app` (releases and deployments of project `cmdemo1-fworker`) | `run-*.sh` |
| `FACTORY_DEV_TOKEN`, `FACTORY_WEBHOOK_SECRET`, `FACTORY_TOKEN_SECRET` | Only for a gateway started locally with `run-owner-gateway.sh`: any values of your own | `run-*.sh` |

On the owner's machine the same values are in the keyring (`secret-tool`, service `factory-worker`; the Octopus key under service
`demo-operator`); the scripts above fall back to it. `tests/live` and `tests/fullrun` read `gh auth token` and the keyring only.
Deployed gateways read theirs from the environment's vault, set by the operator with the demo-environment kit. The GitHub App keys
(author App `factory-worker-author`, reviewer and test-person Apps) exist only in the keyring and the vaults. Tier 1 needs none of this.

Octopus access is read-only for a session: deployments to tdd come from the release workflow, promotions to uat and prod from the operator.

## 3. Decisions of the owner (binding)

Made by the owner, 2026-10-10 to 2026-10-13.

- **Source of truth is this repository** (public). Tests, the fake GitHub and the stress script live here, pull request checks run the
  suites, there is no staging step and no local-only copy.
- **Pull requests of this repository merge automatically when every check is green**; no GitHub review is required. Speed of iteration
  is the goal; this is a demo system.
- **Review before every commit** by a fresh subagent that reads only the staged diff. Subagents run on Opus 5.5 at low effort unless he
  names another model.
- **Operator session role** (built, 2026-10-13): a third kind of session next to worker and supervisor, with its own connect prompt
  and skill, one slot, on **Opus 5.5 at low effort**.
- **Autonomy in tdd**: the operator session may fix, merge and redeploy to tdd by itself. Guard rails, all of them:
  1. tests and review before every merge;
  2. deploy only when the plant is quiet; announce the restart to the connected sessions, verify that they reconnect, roll back otherwise;
  3. protected areas need the owner: credentials and tokens, authorization, revoke, the deploy pipeline, the operator's own skill and
     guard rails, uat and prod;
  4. a daily limit of self-merged releases, with a report;
  5. every change is recorded as an issue in this repository.
- **uat and prod stay manual.**
- **Messaging and the supervisor are on for every registered worker** (`allowedWorkers` and `allowedAskers` are `*`), set in
  `config/deploy.overrides.json`; the source default stays off.
- **The operator reaches every session**, the supervisor included (shipped in 1.0.37).
- **The plant manager reports problems to the operator proactively** (backlog a): a stalled item, the last review round, a blocked
  worker note, repeated verb errors; one concise note per new problem.
- **Docs-only fast path** (shipped in 1.0.37): Technical Design is one pass and one review round (a second only for a concrete defect
  against an acceptance criterion); Development without the private build; a command that a subagent's shell refuses is run by the owner
  session. Target: about 10 minutes for a one-line documentation item.
- **The worker prompt and the plugin assume nothing about the machine** (2026-10-11): any OS and CPU, only what is downloaded or cloned.
- **Reports are short**: outcomes only. Hung or failed things are restarted without asking and reported afterwards. Questions to the
  owner one at a time.

## 4. Verification to date

| What | When | Result |
|---|---|---|
| Tier 1, 18 suites, full tier | 2026-10-12 | 3,110 checks, 0 failed (before the fix releases; the suites have grown since, the pull request checks give the current count) |
| Messaging stress, local, `tests/stress/messaging-stress.ps1` | 2026-10-12 | Passed. 600 s, 30 workers, 51,745 requests, no 5xx, 7,117 messages accepted and all accounted for (0 lost, none delivered twice after acknowledgement), supervisor takeover in 21 s, gateway killed and restarted in 1 s, messaging switched off and on, memory bounded (peak 166 MB), no process left |
| Tier 2 on tdd, two worker sessions | 2026-10-12 | Passed. Items 189 and 190 (pull requests 191 and 192): two sessions that edit the same file agreed on the order by notes and merged without help |
| Load on tdd | 2026-10-12 | Passed. 304 s, 10 sessions, 5,103 requests (16 per second), no 5xx, no transport error; 435 messages accepted, each read once, none missing; only documented refusals (429 rate and pair limits); p95 below 100 ms for ordinary calls |
| Tier 2b on tdd, two workers and the supervisor | 2026-10-13 | Supervisor answered worker questions and wrote two patrol notes to the operator about nine items pinned to a worker that was not connected |
| Team test on tdd: `team-cloud-1`, `team-cloud-2` (Claude cloud), `Cursor-worker-1` (Cursor cloud), supervisor | 2026-10-13 | Items 198 to 202 (docs-only) all merged and closed within 27 to 51 minutes. Findings: three review rounds on one-line changes, an item blocked on a build it did not need, no channel from the operator to the supervisor. All three addressed in 1.0.37 |
| 30-item live run (local gateway, before the cloud deployment) | 2026-10-11 | 25 Done, 5 held for a person |
| Image configuration after the move | 2026-10-13 | The image built from this repository carries exactly the configuration tdd ran before (compared key by key) |

Not verified: see backlog i, j and k. The docs-only fast path and operator-to-supervisor messaging are covered by Tier 1 and deployed,
but have not had a live proof on tdd since 1.0.37.

Raw evidence (logs, prompts, traffic) stays on the owner's machine and is not committed: prompts and logs can contain credentials.

## 5. Open defects and backlog, in priority order

a. **Done (2026-10-13): the plant manager reports problems to the operator proactively.** The patrol digest carries seven more codes
   (`item-quiet`, `last-review-round`, `review-rounds-exhausted`, `worker-blocked-note`, `verb-errors`, `merge-refused`,
   `gateway-restarted`; `docs/design/worker-messaging.md` 3.9 "Reported conditions") and the supervisor's skill sends the operator one
   note per new problem and one reminder when it persists. Not yet seen live: watch the first run on tdd for `item-quiet` notes about
   workers that are busy without calling a verb (raise `timing.mgrQuietMinutes` if they are noise), and run the stress test once.
b. **The operator session role.** Done 2026-10-13: credential kind `o1.`, one slot, connect prompt, skill `factory-operator`, the
   quiet-plant check and the daily limit enforced by the gateway (`docs/design/operator-session.md`). Open: it has not run live on tdd
   (section 6a), and nothing on the GitHub side stops a merge into a protected area (the rule is in the skill only).
c. **One revocation ref per name with a paging reader.** Revocations are refs `refs/factory/<instance>/revoked/<name>/<generation>` in
   the work-orders repository; the reader stops at 5,000 refs and every generation adds one. Keep one ref per name and page the read.
d. **Fast path: fallback cutoff by creation date, and the editable state comment.** An item without the docs-only field in its state
   falls back to reading the body; that fallback should end for items created after the field existed. The state comment can be edited
   by anyone who can comment; the gateway must not trust an edit it did not write.
e. **Dashboard nits**: stale data stays visible after Disconnect; a refresh can race a newer answer; a refused token is retried.
f. **Unknown cause of a gateway restart at 03:56 UTC on 2026-10-13** on tdd (no deployment at that time), and HTTP 503 answers while
   the gateway starts. Find the cause in the Container Apps logs (operator) and decide what clients see during start-up.
g. **Slow first pickup after an item is created**: the item waits for a sweep (180 s in tdd) although the webhook arrives at once.
h. **A worker cannot edit a pull request body through the verbs.**
i. **Worker-to-worker messaging is not yet proven with the owner's own sessions** (it is proven with test sessions).
j. **pwsh crashes (exit 134 or 139) under machine load in the test suites.** Not a product answer, but it costs reruns; find out whether
   it is memory, the runtime or the long-lived client host.
k. **The dashboard has never been tested in a real browser**; Tier 1 checks its HTML and its JavaScript render block only.
l. **One flaky check in the manager suite on GitHub's runners**: in the revoke scenario, "it registers again, is offered work" failed once
   of 702 checks and passed on a re-run. Cause not found.

Smaller, known: the Octopus project's uat and prod run older versions than tdd until the operator promotes; `docs/deploy/MORNING.md` and
`runbook-add-deployable.md` are records of 2026-10-11 and describe paths that no longer exist; `tests/live` and `tests/fullrun` have not
been run from this repository layout.

## 6. A live proof on tdd

Needs `FACTORY_OPERATOR_TOKEN` and `GH_TOKEN` (section 2). It changes the real board and merges a real documentation change into
cmdemo1-workorders: use documentation-only items, and add the label `factory test` for the cheapest model profile.

1. tdd is healthy and runs the version under test: `/_build`, `/ready`.
2. The plant is quiet: `GET /api/v1/status` shows no owner and no accepted assignment you do not expect.
3. A worker is connected. Either the owner starts one, or write a connect prompt to a private file outside any repository:
   `FACTORY_OPERATOR_TOKEN=... docs/deploy/gen-connect-prompt.sh tdd <name> claude-cloud 1 <out-file>` (type `supervisor` for the
   supervisor's prompt), and paste the file into a new session. The file holds a credential: never commit, print or post it; delete it
   afterwards. A name whose credential is no longer wanted is revoked from the dashboard.
4. Create the item: `pwsh tests/fullrun/create-item.ps1 -Title "..." -BodyFile body.md -Label 'factory worker','Auto Merge','factory test' -HandOver`
   (needs `gh` signed in). The body names the file, the exact text and the acceptance criteria. Check the text against the unit tests of
   the work-orders repository first: a documentation change can break a test that reads the file.
5. Watch: `GET /api/v1/status` (the assignment is accepted, then the owner appears), the traffic and the questions, the item's comments
   and its pull request. For messaging, give two workers items that touch the same file and read the notes they exchange.
6. Pass: the pull request is merged by the author App, the item is closed and its card is in Done, no 5xx in the traffic, no item
   left waiting, and (docs-only) about 10 minutes from hand-over to merge.
7. Record the result in section 4 and the defects as issues in this repository.

## 6a. Starting the operator session

The role is on in the deployed image (`operator.enabled` in `config/deploy.overrides.json`); the source default is off.

1. Write its connect prompt to a private file outside any repository:
   `FACTORY_OPERATOR_TOKEN=... docs/deploy/gen-connect-prompt.sh tdd operator-session operator - <out-file>`, or use the dashboard
   ("Connect a new session", kind "Factory operator"). The file holds a credential: never commit, print or post it.
2. Paste it into a new Claude Code session that runs on Opus at low effort. The session needs two secrets and no other:
   - its operator session credential, which is in the prompt (`FACTORY_OPERATOR_SESSION_TOKEN`). It is not the operator token: it
     cannot mint or revoke credentials, and the gateway admits it only at the endpoints listed in the design;
   - a GitHub connection that can push branches, open issues and pull requests and merge pull requests in `cmdemo1-fworker`.
   It does not get the operator token, the Octopus key or any GitHub App key.
3. The prompt clones this repository, has the session read `CLAUDE.md` and this file, and hands over to
   `plugin/skills/factory-operator/SKILL.md`. The dashboard shows it in the group "Factory operator" with its model check and the
   self-merged releases of today against the limit (`operator.maxSelfMergesPerDay`, 3).
4. To stop it: revoke the name `operator-session` on the dashboard, or switch messaging off (both act at once). To stop the role: set
   `operator.enabled` false in the overrides. Its GitHub connection is removed on GitHub.

Enforced by the gateway: where its credential is admitted; that its notes and answers carry its own name and kind, never the
operator's; the limits on its notes and budget grants; the quiet-plant check (a question it answered itself does not count as quiet
for ten minutes); the daily limit; the release records. By the session's skill only: everything about the merge (green checks, review,
protected areas, announcement, verification, rollback, recording), because merging happens on GitHub. For real enforcement there the
owner sets: CODEOWNERS plus a required code-owner review on the protected paths, no bypass right for the session's GitHub identity,
and no push right for it in the work-orders repository (`docs/design/operator-session.md` section 5).

## 7. History in one paragraph

2026-10-10: plugin, rule scripts and a local gateway; ten trial items. 2026-10-11: owner sessions, plant manager, dashboard, PR QC
environment, 30-item live run; the gateway became the container deployable `fworker` of cmdemo1 and went live in tdd; first cloud worker.
2026-10-12: worker messaging and the supervisor (eleven steps of the design) live in tdd 1.0.31; fix release 1.0.35 (pins wait for their
worker, 120 s accept window, operator revoke). 2026-10-13: team test with three cloud sessions; 1.0.37 (docs-only fast path, operator
reaches every session, clearer dashboard, ordered journal); the source moved into this repository.
