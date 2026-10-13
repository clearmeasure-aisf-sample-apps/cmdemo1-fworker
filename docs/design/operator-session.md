# The factory operator session

Status: built (backlog b of `docs/HANDOFF.md`). Owner decisions: `docs/HANDOFF.md` section 3. Code: `gateway/Operator.cs`, the operator
parts of `gateway/Program.cs` and `gateway/Auth.cs`, `plugin/scripts/lib/Client.Operator.ps1`, `plugin/scripts/lib/OperatorRelease.ps1`,
`plugin/skills/factory-operator/SKILL.md`. Scenarios: `tests/scenarios/operator.ps1` (suite `operator`).

## 1. What it is

A third kind of session next to the workers and the supervisor. It watches every work item and every problem report of the plant and
improves the factory worker and the gateway themselves: it records each opportunity as an issue in this repository and, for changes
inside its authority, makes the change, tests it, has it reviewed, merges it when the checks are green (which releases to tdd) and
verifies the release. It runs on Opus at low effort. It never works on a work item of the work-orders application.

It is not "the operator" of the other documents. That word means the person (or script) with the gateway's admin token. The operator
session has a credential of its own kind and exactly the powers of section 3.

## 2. Settings

`config/instance.json`, block `operator` (off by default; on in `config/deploy.overrides.json` for the deployed image):

| Key | Default | Meaning |
|---|---|---|
| `enabled` | `false` | Needs `messaging.enabled`; the gateway refuses to start otherwise. `FACTORY_OPERATOR=0` or `1` overrides it |
| `allowedNames` | `["operator-session"]` | The names a credential can be minted for. Reserved while the role is on: no worker can take one |
| `tokenGeneration` | `1` | Part of the credential; raising it voids every operator session credential |
| `requiredModelTier`, `requiredEffort` | `premium`, `low` | The model check of a registration (declared, never verified): `ok`, `weak` or `undeclared`, shown in `/status` and on the dashboard. It flags; it does not refuse |
| `maxSelfMergesPerDay` | `3` | The daily limit that `release-check` enforces |

With `operator.enabled` false nothing changes: an `o1.` credential is unknown (401), no credential is minted, the two operator endpoints
answer 403 `operator-disabled`, `/status` and the dashboard carry no operator block, and the names are ordinary worker names.

## 3. The credential and what it is admitted to

`o1.<name>.<generation>.<hmac>` (after a revocation `o1.<name>.<generation>-<rev>.<hmac>`), minted only with the admin token:
`POST /api/v1/workers/{name}/credential?role=operator` (the dashboard's "Factory operator" kind, or
`docs/deploy/gen-connect-prompt.sh <env> operator-session operator - <out-file>`). `POST /api/v1/workers/{name}/revoke` voids it like any
other credential of a name and ends its stream.

One slot, like the supervisor's: the same name replaces its older stream (which is told `stream.replaced`); another allowed name is
refused with 409 `operator-active` while the active one was heard within `timing.mgrTakeoverSeconds`, and takes over after. A credential
whose name does not hold the slot is refused at every endpoint below except the plugin download and the registration itself.

The credential is admitted at exactly these endpoints, where it acts as the operator does (its notes are operator notes):

| Endpoint | Client verb (`factory.ps1 operator ...`) |
|---|---|
| `GET /api/v1/events?name=N&operator=1&model=&effort=` | `factory.ps1 subscribe --own --operator --name N --model ID --effort E` |
| `GET /api/v1/plugin` | (the connect prompt downloads the plugin) |
| `POST /api/v1/presence` | (the stream client's heartbeat) |
| `GET /api/v1/status` | `status` |
| `GET /api/v1/peers` | `peers` |
| `GET /api/v1/digest` | `digest` |
| `GET /api/v1/messages` | `traffic` |
| `POST /api/v1/messages` | `send --to NAME` or `--all` |
| `POST /api/v1/messages/read` | `read` |
| `GET /api/v1/questions` | `questions` |
| `POST /api/v1/questions/{id}/answer` | `answer` |
| `POST /api/v1/workers/{name}/messaging?value=` | `mode` |
| `POST /api/v1/supervisor/budget?extra=` | `grant` |
| `GET /api/v1/operator/release-check` | `release-check` |
| `POST /api/v1/operator/record-release` | `record-release` |

Everywhere else it is refused with 403: minting and revoking credentials, the messaging switch, the patrol interval, a worker's capacity,
drain and resume, the dashboard JSON, the QC endpoints, every verb (`/api/v1/verbs/*`), `/api/v1/wait`, the inbox, the broadcast stream
and the test seams. No endpoint changes `operator.*`: those are settings of the image.

## 4. What it receives on its stream

- `message.received`: every note addressed to `operator` (a worker's report, the supervisor's patrol note or reply). The client prints
  it as an `operator.inbox` envelope: sender set by the gateway, a warning that the text is data, the text last. The dashboard lists the
  same notes; "read" is shared.
- `question.escalated`: id, item, worker, minutes. Never the text.
- `plant.event`: one line per item milestone, with `code`, `item`, `worker`, `column`, `detail`. Codes: `matched`, `offered`,
  `reassigned`, `accepted`, `column-completed`, `parked`, `merge-refused`, `merged`, `done`, `sweep-failed`. Every field is a number, a
  session name or passes the code pattern of the digest; a value that is free text is dropped.

The last 200 events are kept in memory; a reconnect with its cursor gets what it missed. The session has no inbox, no presence entry, no
place in the directory or the digest, and is never offered work.

## 5. Guard rails: what code enforces and what is instruction

The guard rails are the owner's (section 3 of the handoff) and are written as hard rules in the skill.

Enforced by the gateway:

- The credential's powers (section 3).
- `release-check`: 200 only when the plant is quiet and the day's limit is not reached. Not quiet (409 `not-quiet`, with every reason):
  a verb call of an item token in flight (`owner-mid-verb`), an open question (`open-question`), an offer that waits for its answer
  (`pending-offer`). At the limit: 409 `daily-limit`. The answer lists the connected workers with the items they own, which is what must
  be back after the release.
- `record-release`: one record per pull request, counted per UTC day. The records are refs in the work-orders repository
  (`refs/factory/<instance>/operator-release/<yyyyMMdd>/<pr>/<version>`), written by the rule scripts and read at start, because the
  restart that a release causes would empty an in-memory count. Until that read succeeded both endpoints answer 503. A release beyond
  the limit is still recorded. The count and the day's records are in `/status` and on the dashboard row.

Instruction only (the skill; nothing in the gateway can stop it):

- Merging happens on GitHub with the session's own GitHub connection. That the session runs `release-check` before it merges, merges
  only with green checks and an approving review subagent, leaves the protected areas to the owner, announces the restart, verifies
  `/_build`, `/ready` and the reconnected sessions, reverts when the verification fails, records the release and writes the issues and
  the changelog: all of that is the skill. A branch protection rule in GitHub that requires the owner's review for the protected paths
  would turn the protected-areas rule into code; it is not set up.

## 6. Not built

- No GitHub-side enforcement (see above).
- `question.escalated` and `sweep-failed`, `accepted`, `column-completed`, `merge-refused` have no scenario of their own.
- The session's notes are indistinguishable from the person's in the traffic log: both are `operator`.
