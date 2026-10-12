# The AI software plant manager and its workers

Status: decisions of 2026-10-10/11. **Implemented now: local workers** (persistent-local and ephemeral-local). **Designed, not built: cloud workers**
(Cursor cloud, Claude cloud). Sections say which is which.

## 1. Vocabulary

| Term | Meaning |
|---|---|
| Plant manager | The gateway in its role of matching triggers to jobs and jobs to workers, and journaling each decision on the work item. |
| Worker | A registered Claude session able to own work items. It has a name, a type, capabilities, a capacity and a delivery channel. |
| Owner session | The one worker session that owns a work item from first assignment until the item is complete. |
| Bot | A separate session with its own credentials for an independent check: the inspector now; quality-control and release bots later. |
| Job | A unit the plant manager assigns: `own-item` (a work item) or `bot-job` (a bot run on a pull request). |
| Trigger | A normalized event (webhook, sweep decision, CI result, approval, delivery) that concerns an item. |

## 2. The rule

1. A work item is dispatched to **one** owner session, which stays open and owns it to completion.
2. The owner's main thread only orchestrates. **Every prompt and every trigger is handled by subagents** in that session (analyst, implementer, reviewer, and later roles).
3. **Bots that must be independent run in their own session**, with their own identity and credentials. They are never subagents of the owner.
4. Scripts enforce, skills decide. Every gate is a verb that exits nonzero with a machine-readable reason. The session can advance, merge or close an item only through a verb.
5. The board and GitHub are the source of truth. A restarted or compacted session, or a restarted gateway, loses nothing.

"Complete" (decided): **the pull request is merged and master CI is green.** Everything after is deterministic (`settle`: release candidate, Release Queue, production, Done) and needs no session.

## 3. Worker types

| Type | Runs | Gets work by | Capacity | Status |
|---|---|---|---|---|
| persistent-local | The user's running Claude session on their own machine | Directed assignment over its stream | Set by the user, default 1 | **implement now** |
| ephemeral-local | A headless `claude -p` the launcher starts on a machine (tests, unattended) | The launcher spawns one per assignment | 1 | **implement now** |
| claude-cloud | One Claude cloud session per item | The gateway launches it | 1 | designed (section 11) |
| cursor-cloud | One Cursor cloud agent per item | The gateway launches it | 1 | designed (section 11) |

All types share the registry, the assignment protocol, tokens and journaling. Only how a session comes to exist, and how it receives triggers, differs.

## 4. Registry and assignment (local types)

### Registration
`factory.ps1 subscribe --own --name <name> [--capabilities dotnet,git,...] [--capacity N] [--roles owner,inspector]`

- Opens the event stream and announces: name, type, capabilities, capacity, roles, current model. A stable `name` means a restart is the same worker.
- Authenticated with the **worker credential** (one per worker, issued once by `factory.ps1 worker-token --name <name>`, kept in the user's keyring). The credential can register, receive assignments and call `accept`/`my-items`/`wait-event`. It cannot act on an item.
- The gateway keeps the registry in memory. Durable truth is the claim in each item's state comment (`claim.holder` = worker name), so a gateway restart is rebuilt as workers reconnect.

### Choosing a worker
For each job the plant manager takes the workers that: hold the `owner` (or bot) role, have every capability the item needs, have free capacity, and are not draining. A `worker:<name>` label on the item pins it: the item is offered to that worker and to no other. If that worker is not connected, is draining, is at capacity or lets the accept window pass, the item waits (journal `waiting`, the assignment without a worker on the dashboard) and is offered to the same worker again when it can take it. Among candidates the order is: persistent-local, then cloud, then ephemeral-local; ties go to the worker with the fewest owned items.

### Directed assignment
`item.assigned` is sent to **one** worker, not broadcast:
```json
{ "event": "item.assigned", "item": 101, "column": "Development", "mode": "first|fix-forward|relaunch|resume",
  "job": "own-item", "assignment": "a-4f2c", "token": "<item+role scoped>", "ackBySeconds": 120 }
```
- The worker confirms with `factory.ps1 accept --item N --assignment a-4f2c` within `ackBySeconds` (120 by default: `timing.assignAckSeconds`, overridden by `FACTORY_TIMING_assignAckSeconds` or `FACTORY_ASSIGN_ACK_SECONDS`; an offer nobody confirms goes to the next worker, a pinned one waits for its worker). `accept` is the claim: it creates the claim ref, writes the claim into the state comment and returns the item-scoped token's details.
- No confirmation in time: the plant manager offers the item to the next candidate and journals it. A worker that accepts late is refused (`assignment-expired`).
- Verbs: `register` (via `subscribe`), `accept`, `my-items` (everything this worker owns, to rebuild a session after compaction or restart), `drain` (stop new assignments, finish owned items), `resume` (a restarted worker takes back what it owned), `wait-event`.

### Ownership lifetime
- The claim is **per item** and spans columns: `complete` moves the column and keeps the claim; the lease is renewed by the session's heartbeat.
- It ends when: the item is complete (merged, master green), is parked or held and the idle limit passes, the session releases it, or the lease expires.
- **Idle limit:** an owner waiting on a hold longer than 60 minutes releases ownership (journaled); the next trigger assigns it again and a new owner **resumes** from the item state.
- **Lost session:** the lease expires, the item is offered again with `mode: resume`, and the relaunch budget applies.

## 5. Tokens and roles

| Credential | Held by | Can do |
|---|---|---|
| Worker credential | A registered worker | Register, receive assignments, `accept`, `my-items`, `drain`, `wait-event` |
| Item token, role `owner` | The owner of one item | `get-item`, `append-body`, `open-pr`, `complete`, `record-review`, `validation-verdict`, `merge`, `claim` renewal, `comment` — **for that item only** |
| Item token, role `inspector` | The inspector bot session for one item | `get-item`, `inspect-facts`, `approve-pr` — for that item only |
| Gateway secrets | The gateway only | The GitHub token, the reviewer and test-person App keys, the Octopus read key |

Revocation: `POST /api/v1/workers/{name}/revoke` (operator token only; the `revoke` button of a session row on the dashboard). Every credential minted so far for that name is refused with 401 on every endpoint: the worker credential, the item tokens issued to it and, for a supervisor name, the supervisor credential. Its open stream ends, an offer it has not answered goes back to the plan, and what it owns is handed on as for any owner that is gone (its claims are no longer renewed). A credential minted afterwards for the same name works. Tokens are stateless, so each name has a generation that every token of it is bound to (0, the original token format, until the first revocation); the generation is kept in GitHub as refs `refs/factory/<instance>/revoked/<name>/<generation>` (the highest counts) and read back when the gateway starts, before any session credential is answered (`GET /ready` says when; until then such a request waits up to 20 s and is then answered 503). The offline `factory.ps1 worker-token` mints the original (generation 0) form only: after a name was revoked, its new credential comes from the dashboard, `POST /api/v1/workers/{name}/credential` or `docs/deploy/gen-connect-prompt.sh`.

An item token is an HMAC of `{item, role, expiry}` made with a gateway secret (the idea of AISF's callback token, with a role added). The gateway refuses a verb when the token's item differs from the call's item or the verb is outside the role. This turns "the author never approves its own work" into a gate.

## 6. Triggers

The plant manager routes every trigger that concerns an item to **that item's owner**, never to a new session. Triggers: checks settled, branch build finished, inspector approved / requested changes / escalated, hold cleared, master green, release candidate found, a deployment finished, a human comment (delivered as untrusted data), an unexpected column change by a person.

Delivery per type: persistent-local receives them on its stream (the same `Monitor` that delivered the assignment); ephemeral-local and cloud sessions call `wait-event --item N [--timeout S]`, an outbound long-poll that costs no tokens while idle. Each owner has its own buffer with `Last-Event-ID` replay, so a dropped connection loses nothing. Triggers are hints; the session re-reads the item before acting.

## 7. Bots (separate sessions)

When a PASS verdict is recorded under the `peer-approved` policy, the plant manager creates a `bot-job` of kind `inspector` and assigns it to a worker registered with the `inspector` role. That session has a clean context, gets an inspector-role item token, runs `inspect-facts`, judges, and submits `approve-pr`. The owner is told the result by a trigger. Quality-control and release bots follow the same shape: a job kind, a role, a token scope, a result trigger. The inspector rules (mandatory escalations computed by script, three verdicts, holds cleared only by a named person's approval of the current head) are unchanged; see `plugin/scripts/lib/Verbs.Inspect.ps1`.

## 8. The journal

The plant manager writes a comment on the work item at each decision, as AISF does for `matched` and `Agent session created`, but with every decision covered. Prefix `🏭 Plant manager:`; verb outcomes keep `🤖 factory-worker:`.

| # | Event | Posted when |
|---|---|---|
| 1 | matched | A trigger (webhook or sweep) is matched to a job; names the trigger (event and delivery id) |
| 2 | offered | The job is sent to a chosen worker (or, for a cloud type, a launch is requested) |
| 3 | assigned | The worker accepted: worker name, type, mode, attempt, model and effort, lease; a **link** for cloud types |
| 4 | routed | A trigger was delivered to the owner (one line, only for state changes people care about) |
| 5 | reassigned | The lease expired, the worker declined or did not confirm, or ownership was released and resumed |
| 6 | tier raised | The model ladder climbed, with the reason |
| 7 | waiting | No worker qualifies (none subscribed, no capability, capacity full, merge queue full); at most once per column per 30 minutes after a 5-minute delay |
| 8 | skipped | A rule excludes the item (ambiguous owner, demo exclusion, not an AI column) |

Rules: hidden machine marker on every comment (`<!-- plant event=assigned key=… -->`) and a journal ledger in the state comment, so redelivered webhooks and repeated sweeps never duplicate; best-effort with one retry and never blocks a dispatch; redacts token-like strings; at most ~1,500 characters; never includes item bodies or raw payloads; a per-item cap of 30 comments, then one final "journal full" note; no `@mention`. Worker names and model/effort are shown; machine names and user names are not.

## 9. Model selection

- A machine's `models.json` maps ladder rungs to models; the gateway decides when to climb (`model-tier`: relaunches, repeated review findings, demotions, fix-forward rounds, refused gate calls; never lowered within a column). Default: haiku 5.5 `xhigh` → sonnet 5.5 `medium` → opus 5.5 `medium`.
- `profiles.testing` (`claude-haiku-5-5`, `low`) is pinned and never climbs; items labelled `factory test`, or `FACTORY_MODEL_PROFILE=testing`, use it.
- The inspector uses at least the standard rung for real items, and the testing profile for `factory test` items.
- A long-lived owner cannot change its own model. When the gateway raises the tier, the owner finishes the step, releases, and a new session resumes on the higher rung.

## 10. Merge queue, policy, and CI rules (already built)

`mergeQueueDepth` limits merged items waiting for their release candidate (the Release workflow keeps one queued run). `mergePolicy` is `auto` or `peer-approved`. A merged docs-only change finishes after master has been green for a grace period even if no release run appears. CI is classified strictly: zero check runs is pending, and each configured gate must be present and successful.

## 11. Cloud workers (designed, NOT implemented)

### Shape
The gateway **launches** one cloud session per item through an adapter, then journals the assigned event with the session URL (as AISF does with its Cursor agent URL). Routing is per column with a fallback, like AISF's `AiToolColumnRouting`:
```json
"workers": {
  "routing":   { "Technical Design": "claude-cloud", "Development": "cursor-cloud", "default": "persistent-local" },
  "providers": { "cursor-cloud": { "apiKeySecret": "cursor-api-key", "model": "auto-smart" },
                 "claude-cloud": { "environmentId": "<ccpool_…>", "routineFire": "<token secret>" } },
  "maxConcurrent": { "cursor-cloud": 3, "claude-cloud": 3 }
}
```

### Cursor (known)
AISF's client launches through Cursor's API with repository URL, branch, prompt and model, remembers a claim for a window (default 30 minutes), and can ask the run state (`GET /v1/agents/{id}/runs/{runId}`) to detect a dead run. Needs: Cursor's GitHub App installed on `cmdemo1-workorders`, and `cursor-api-key` on the gateway.

### Claude cloud (to verify)
The CLI has `--cloud [description|session_id|url]` (create or attach to a cloud session) and `--environment <ccpool_…>` (run on a self-hosted environment); a routine's `/fire` endpoint starts a new session per fire (30 fires an hour per routine, 100 per account, text labelled untrusted). **Unknown until a one-session probe:** what `--cloud` prints, whether it runs unattended and returns the `claude.ai/code` URL. Decision: use routine tokens on the gateway (narrow) and not a full Claude login on the server.

### What cloud adds
1. Reachability: a cloud session reaches the gateway over public HTTPS (the container app; the tunnel for a pilot).
2. Credentials: the cloud session gets only an item-scoped token at launch (section 5).
3. Git: it pushes with its provider's GitHub access; a ruleset limits new branches to `factory-worker/*`.
4. Tooling: an environment with the .NET SDK (and Playwright if acceptance tests run) — **open: does one exist, and who creates it.**
5. Capacity and liveness: a script-owned cap per provider (journaled as waiting), the same lease and heartbeat, plus the provider's run status to spot a dead session.
6. Triggers arrive through `wait-event` (outbound), so nothing needs to reach into the cloud.
7. Model: the testing profile is pinned per launch; otherwise the provider's default.

## 12. Testing

Tier 0 unit; Tier 1 against a fake GitHub and fake Octopus, with scripted workers registering and receiving assignments; Tier 2 against the real repo with the real Apps and a scripted worker; Tier 3 with real Claude workers on the testing profile and an adversarial explorer. Invariants checked on every run: no sweep or internal errors in the gateway log; every assignment has a preceding "matched"; no comment contains a secret; no merge without an approval on the current head; no verb accepted with a token for another item or role. Cloud adapters get a fake launcher in Tier 1 (URL comment, redacted failure, refused wrong-item token, concurrency cap, re-offer after a dead session).

## 13. Implementation order

Local first, in this order: (1) worker registry, `subscribe --own`, `accept`, `my-items`, `drain`, `resume`, `wait-event`, directed assignment and acknowledgment timeout; (2) item-scoped, role-scoped tokens and the worker credential; (3) the owner-session skill (per-item ownership across columns, all prompts and triggers via subagents); (4) the inspector as a bot job in its own session; (5) the journal events; (6) Tier 1 scenarios for each, then a Tier 2 run. Cloud adapters are a later phase behind the same interfaces.

## 14. Decision 2026-10-11: cloud sessions are created by hand

Open question closed: nobody provisions a cloud environment automatically. A person starts a Claude cloud session (or a Cursor cloud agent) and gives it the connect prompt; from then on it behaves exactly like a persistent local session (`subscribe --own`, same credentials, same skills). The registry only labels it `claude-cloud` / `cursor-cloud` (assignment preference: persistent-local, then cloud, then ephemeral-local). No cloud adapter is built.

The worker dashboard (`GET /dashboard`, data from `GET /api/v1/dashboard`, operator token) shows the connected sessions of one running factory (the gateway's `FACTORY_ENVIRONMENT`: tdd, uat or prod), their state, load and assignments, drain/resume, and generates the connect prompt. Creating the prompt issues a one-time worker credential for the chosen name (`POST /api/v1/workers/{name}/credential`); the gateway does not store it. `revoke` on a session row refuses every credential issued so far for that name (section 5).
