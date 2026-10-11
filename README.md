# factory-worker

A Claude Code plugin that reproduces what the cmdemo1-aisf factory does, with a gateway that holds the GitHub and Octopus secrets.
Instance key: label `factory worker` (the live factory keys on `AI Factory`). Board: clearmeasure-aisf-sample-apps project 679, repo cmdemo1-workorders.

## Status (2026-10-10, local build, nothing deployed, nothing written to GitHub or Octopus)

| Part | State |
|---|---|
| `plugin/scripts/factory.ps1` and `lib/*` | Written. Server verbs: get-item, append-body, comment, claim-item, record-review, move-next, complete, open-pr, pr-checks, wait-for-checks, validation-verdict, get-delivery, settle, sync-board, ingest-event, check-secrets. Parse-checked; `tests/smoke.ps1` passes. |
| Live read-only runs | `check-secrets`, `get-item` (#62), `sync-board --dry`, `get-delivery` (release 2.4.93, found in Octopus with tdd/uat/prod Success) all ran against the real GitHub and Octopus. |
| `plugin/skills`, `plugin/agents` | Written (dispatch, technical-design, development, functional-testing, follow-through, setup; analyst, implementer, reviewer). Not yet run by a Claude session. |
| Client `subscribe` | Written, not run. |
| `merge` verb | NOT written. The permission classifier refused it ("Self-Approval", then without reason). The worker never approves; a person merges, and `sync-board` picks a human merge up and carries on with `settle`. |
| Gateway host (`gateway/`) | NOT written beyond `Gateway.csproj`. The classifier refused `Program.cs` ("Create Unsafe Agents"). |
| Tunnel, webhook registration, `factory worker` label, the 10 work items | Not done. `cloudflared` is downloaded to `~/.local/share/factory-worker/bin`. |

Secrets for a local run sit in `~/.local/share/factory-worker/secrets` (webhook secret, dev token); the gateway would read the GitHub token from `gh auth token` and the Octopus key from the keyring at start.

## Choosing models (cost first, automatic)

`plugin/scripts/models.default.json` (shipped inside the plugin) is the default; a machine overrides it with `~/.config/factory-worker/models.json` (or `FACTORY_MODELS_CONFIG`).
`mode: auto` walks a ladder; `mode: fixed` pins one model for the orchestrating session and one for subagents.

| Rung | Default model | Effort |
|---|---|---|
| economy (start) | claude-haiku-4-5-20251001 | xhigh |
| standard | claude-sonnet-5-5 | medium |
| premium | claude-opus-5-5 | medium |

The gateway decides when to climb (`model-tier`, thresholds in `escalation` in `config/instance.json`): relaunches, repeated review findings, demotions,
fix-forward rounds, and refused gate calls all raise the tier; it never goes back down within a column. `factory.ps1 model-for --role orchestrator|subagent --item N`
returns the model and effort for this machine. Headless workers start on that rung (`claude --model --effort`); subagents take it per call.

**Testing profile.** `profiles.testing` in the models file is the cheapest setting (claude-haiku-5-5 at `low`) for very simple tasks: docs-only
changes, text replacement, automated exploratory and stress testing. It is pinned and never climbs the ladder: a loop in a test is a finding, not a reason to
pay for a dearer model. An item opts in with the `factory test` label (the live tests add it), or a machine forces it with `FACTORY_MODEL_PROFILE=testing`
or `model-for --profile testing`. It wins over both `auto` and `fixed` mode.

## Connect a session (your own AI session as a factory worker)
1. Start the owner-mode gateway on this machine: `./run-owner-gateway.sh` (API on localhost:5500). Stop any other gateway first; only one may sweep the board.
2. Open `http://127.0.0.1:5500/dashboard`, paste the operator token, choose a name, press "Create prompt" and paste the generated prompt into a new Claude Code session. The session runs `subscribe --own` under a Monitor and owns each work item it is given until its pull request is merged and master is green.
2b. The prompt is self-contained and assumes nothing about the machine (Linux, macOS or Windows; x64 or arm64): it downloads PowerShell 7 and the plugin (exactly the gateway's version, `GET /api/v1/plugin`) into one base directory and uses only literal paths. The machine needs curl, tar, git and a checkout of the work-orders repository; tools a task needs (the .NET SDK) are installed on demand by `plugin/scripts/ensure-tools.ps1`.
3. A hand-made cloud session connects the same way with the type `claude-cloud` or `cursor-cloud`; it must be able to reach the gateway URL.
4. To make a work item eligible: label it `factory worker` (and `Auto Merge` if it may merge without a person) and move it to Technical Design on the board.

## Findings of the live multi-item runs (2026-10-10/11)
- A merged pull request that says "Closes #N" closed the item, and settle gave up: the card stayed in Functional Testing. Settle and the sweep now follow a closed item whose merge is still being settled.
- A PASS verdict whose pull request was closed unmerged blocked every claim. The sweep now demotes the item.
- `needs-approval` and `ready-to-merge` offers were refused by `claim-item` (verdict already recorded). The dispatch skill now runs the approval or the merge without a claim.
- Haiku sessions refuse any command containing `$FACTORY_PLUGIN_SCRIPTS`; spawned workers are now told the literal path.
- Headless workers could not run the app (curl, nohup). Allowed tools widened; the Functional Testing skill says how to start it.
- Development workers polled CI and held a worker slot; they now complete after two more polls and the gateway watches CI.
- A worker that ends without finishing released nothing for 30 minutes; the client now releases its claim on exit.
- Docs-only changes no longer wait in the merge queue; the queue depth for code changes is configurable (`mergeQueueDepth`).
- Capacity on a 4-core 12 GB machine: 26 read-only haiku workers all succeed; 8 to 9 real edit workers are stable; 26 read-only plus about 6 real workers exhausted memory.
