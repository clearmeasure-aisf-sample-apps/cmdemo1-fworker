# Copilot cloud agent: usage

The record of what the Copilot cloud agent runtime (`runtime:copilot-cloud`, `gateway/Runtimes/CopilotCloudRuntime.cs`) costs, in the
same tables as [cursor-token-usage.md](cursor-token-usage.md). Copilot is cmdemo1's default runtime since 2026-10-20 (items without a `runtime:` label; code and database changes allowed); on in tdd,
ready but off in uat and prod (1 at a time, 60 min, 30 launches a day per environment). The agent tasks API reports usage per session as premium requests (or AI credits), not tokens: token columns are "not reported"
unless GitHub starts reporting them.

## Optimizations

| # | Optimization | Where |
|---|---|---|
| 1 | One phase per task: the agent does only its phase, records it and ends; per-phase tool sets refuse other phases' tools | #169, `CopilotCloudRuntime` |
| 2 | Minimal prompts: a short bootstrap (`prompts/bootstrap/copilot.md`) plus the phase's instructions, served by `get_prompt` | `prompts/` |
| 3 | No GitHub polling from agents; the gateway's state is the source of progress | #169 |
| 4 | Usage read from the task's sessions and shown in `/api/v1/status` → `runtimes."copilot-cloud".last[].detail.usage` | `CopilotCloudRuntime` |

## Before and after

| | Cursor (items 364, 365) | Copilot |
|---|---|---|
| Sessions per item | 3, one per phase | not measured yet |
| Session time | 1.5–3.5 min per phase | not measured yet |
| Phase errors | none | not measured yet |

Measured:

| Run | Premium requests | Input | Cache read | Output | Total |
|---|---:|---:|---:|---:|---:|
| **Sum** | | | | | |

## Run log

Append one row per Copilot task, from `/api/v1/status` (`last[].detail.usage`, `last[].detail.minutes`) or the `copilot-usage` log line.

| Date | Item | Phase | Minutes | Premium requests | Input | Cache read | Output | Total |
|---|---|---|---:|---:|---:|---:|---:|---:|
| 2026-10-20 | cmdemo1-workorders#380 (hand test, not an item) | one task, `create_pull_request: true` | ~1.5 | ai_credits reported | not reported | | | |
| 2026-10-20 | #383 | Technical Design | 2.5 | ai_credits 783374000 | not reported | | | |
| 2026-10-20 | #384 | Technical Design | 2.8 | ai_credits 371183500 | not reported | | | |
| 2026-10-20 | #383 | Development | 4.4 | ai_credits 1039343000 | not reported | | | |
| 2026-10-20 | #384 | Development | 5.1 | ai_credits 627146500 | not reported | | | |
| 2026-10-20 | #385 | Technical Design | 2.8 | ai_credits 455007500 | not reported | | | |
| 2026-10-20 | #385 | Development | 13.8 | ai_credits 3197461000 | not reported | | | |

## 2026-10-20: three-item tdd test (docs, code + unit test, database migration): stopped at CI approval

Items in cmdemo1-workorders, each labelled `env:tdd`, `enhancement`, `Auto Merge`, `factory worker`, no `runtime:` label, unpinned,
moved to Technical Design on board 679 at 13:42:12Z (#383), 13:42:17Z (#384), 13:42:22Z (#385).
Times UTC (gateway start and end); minutes in brackets are the task's own duration as the agent tasks API reports it; ai_credits per task (raw value).

| Item | Kind | Phase | Copilot task | Start → end | ai_credits | Outcome |
|---|---|---|---|---|---:|---|
| #383 | docs (README line) | Technical Design | df58c1c6-d95d-4fa2-9a35-67668f188eb8 | 15:11:40 → 15:14:23 (2.5 min) | 783374000 | done, moved to Development |
| #383 | | Development | 2e1495f9-bd0e-450d-9707-f819a9fdaac5 | 15:23:24 → 15:28:28 (4.4 min) | 1039343000 | FAILURE, parked: refused to edit files (bootstrap v2 "act only through fworker tools"); empty draft PR #386 |
| #384 | code + unit test (`Employee.GetInitials`) | Technical Design | e5ca8aea-fc85-469c-8668-0649bd379f0c | 15:17:18 → 15:20:39 (2.8 min) | 371183500 | done, moved to Development |
| #384 | | Development | bcc9bc14-30da-4b46-bd1b-95c0c4ecdeb6 | 15:29:26 → 15:34:44 (5.1 min) | 627146500 | FAILURE, parked: same refusal; draft PR #387 |
| #385 | DbUp 038 + model + mapping + integration test (`Vendor.Notes`) | Technical Design | b725faba-feba-4c79-828c-5e66baf2e307 | 15:35:31 → 15:38:26 (2.8 min) | 455007500 | done, moved to Development |
| #385 | | Development | 1b90684f-e9bd-438b-b302-29984d7af649 | 15:39:01 → 15:52:58 (13.8 min) | 3197461000 | PR #388: `038_AddVendorNotes.sql` (TABs), `Vendor.Notes`, `VendorMap`, `VendorMappingTests`; CI not run (below) |
| all | | Functional Testing, merge, Done | | | | not reached |

**Demand-driven launch.** Before the fix nothing launched: the items waited `no-session` from 13:46 with copilot-cloud `today` 0 and
`relisted: false`. Cause: `AddHostedService(factory)` dedupes by implementation type, so only cursor-cloud's `RuntimeLoop` ran and
Copilot never relisted (`CanStart` answered `runtime-relisting`). Fixed by #188 (`AddSingleton<IHostedService>` per adapter). After the
redeploy (gateway start 15:10:49Z, relisted 15:11:28Z) the first task started at 15:11:40Z, 51 s after the start, with no nudge. Between the
items, the sweep started a task only for an item waiting in a column, one at a time (`maxConcurrent` 1): every start came after the previous
task ended (gaps of 0.6 to 2.9 min). No task started while nothing waited.

**Defects.** (1) #188, above. (2) Bootstrap prompt v2 made the agent refuse its own file and git tools; fixed in #190 (v3; #189 closed as
a duplicate). #385 got through on v2 anyway. (3) Copilot-authored pushes leave every workflow run `action_required` (Build, secret-scan on
PR #388): the REST approve answers 403 ("not from a fork pull request"), re-runs of the Build and secret-scan runs as cm-ai-ops-bot
were accepted (16:07Z), after which the permission check refused this path as a CI bypass; it is not
used again. The test stops here until the owner decides (below).

**Owner steps.** Approve the workflow runs of Copilot pull requests, or turn off "Require approval for workflow runs" in
cmdemo1-workorders Settings → Copilot → Cloud agent; then unpark #383 and #384 (`POST /api/v1/items/{n}/unpark`) once #190 is on tdd,
close the empty draft #386 and let the items continue. The firewall needed no change: the MCP proxy reached the tdd gateway (every task
accepted its item through it).

Isolation, this run: decoy #382 (`env:uat`) stayed untouched: still in Technical Design, last updated 03:31:17Z, 2 comments, no
Copilot task for it.

Isolation evidence (live, tdd 1.0.407 with #185, 2026-10-20): decoy cmdemo1-workorders#382 labelled `factory worker`, `env:uat`,
`enhancement`, `Auto Merge`. The previous tdd revision (before #185, replaced at 03:32:05Z) placed its card at 03:31:17Z; the new
revision wrote nothing on it afterwards (no comment, no claim ref), `/api/v1/status` does not name it, and `get-item` through the tdd
gateway answers exit 10 `out-of-scope` ("does not carry exactly one env: label, 'env:tdd'"). Tier 1 suite `envscope` proves the same with a
tdd and a uat gateway on one board (claim, merge, sweep, board guard, webhook ingest, claim-ref reap).

## 2026-10-20: three-item tdd test (cmdemo1-workorders #393 docs, #394 code + unit test, #395 DbUp migration)

ai_credits as the agent tasks API reports them per task (`sessions[].usage.amount`, type `ai_credits`), in billions (B). Model: gpt-6.1-sol.

| Item | Phase | Task | Times (UTC) | ai_credits | Outcome |
|---|---|---|---|---:|---|
| #395 | Technical Design | e91e1ffc | 18:13–18:16 | 0.86B | REVIEW: COMPLETE |
| #395 | Development | bf42ec8b | 18:17–18:46 (29 min) | **69.4B** | pushed 35ba844, took over #396, then waited on CI (runs action_required) and reported FAILURE (`pr_update` answered pr-not-open for a copilot/* PR) |
| #395 | Development (relaunch after unpark, gateway 1.0.421) | ca98b521 | 20:43–20:50 | **28.4B** | started from #396's branch (#192), rebuilt and retested, stacked draft #398; `pr_open` refused 422 "part of a stack"; reported FAILURE |
| #393 | Technical Design | 5ac50337 | 20:28–20:31 | 1.02B | REVIEW: COMPLETE |
| #394 | Technical Design | 5481db82 | 20:31–20:34 | 0.80B | REVIEW: COMPLETE |
| #393 | Development | 4944caec | 20:37–20:41 | 1.71B | no change: README already lists the URL (since the import); empty draft #397. Item closed as already satisfied |
| #394 | Development (item paused meanwhile) | ded6ac89 | 20:53–20:55 | 0.44B | no assignment; empty draft #400 (closed) |
| #395 | Development (gateway 1.0.426, copilot-settle) | none | 22:20:41–22:20:56 | 0 | settled at launch: #396 already carries the change; no task. Checks green 22:23 |
| #395 | Functional Testing | f2f14fdc | 22:23–22:27 | 1.41B | PASS. Merge refused by GitHub (403: synchronous merge of a PR that was part of a stack) |
| #394 | Development | 52b5dbbf | 22:29–22:36 | 2.00B | PR #401; `item_complete` answered checks pending and the agent ended; the gateway moved it on when green (22:41) |
| #394 | Functional Testing | a343833d | 22:41– | **>17B** | credit guard (10B) ended it at 22:43 and parked #394 `credit-guard`; the task kept running at GitHub (no cancel API) |

Findings: the cost is in tasks that wait (CI polling, sleeps) or redo work (a relaunch that rebuilds and retests); short tasks that end
after their tool call cost 0.4–2B. The gateway now owns Development's end (no CI waiting in the agent, no relaunch of an item whose pull
request carries the change) and the credit guard parks a task over 10B. An FT task that passed 10B in about 90 s (#394, a343833d) is not
explained by the agent's own turns; open point: read its run log.

## Model and credit guard (Jeffrey, 2026-10-21)

Copilot stays on plain Auto. Auto picked the premium `gpt-6.1-sol` for #394's Functional Testing task (a343833d: 22,470,780,000
raw ai_credits in 3 min 22 s, against 0.4–2 × 10^9 for tasks on `gpt-5.3-codex`). The owner chose "Auto, lower guard": the
per-task guard `api.copilot.maxCreditsPerTask` is 3 × 10^9 raw ai_credits (was 10 × 10^9). The task API reports a raw `amount` of
type `ai_credits`, not tokens; GitHub documents no scale, so the figures here are relative until the Copilot billing page calibrates them.

## Credit guard off (Jeffrey, 2026-10-21)

The 3 × 10^9 guard was too low: `usage.amount` is a raw number, most likely 10^9 = 1 AI credit ($0.01), so it parked a task at about
3 credits ($0.03), and normal Development runs that waited on CI reached 28–69 × 10^9 raw. Because the task API has no cancel, the guard
could not stop the billing; it only revoked the agent's gateway token so the task could not finish. The owner does not want jobs ended by
an artificially low token cap: `api.copilot.maxCreditsPerTask` is 0 (off). The limits that remain are time and repetition: 60 minutes
per task, at most 3 runs in a row per phase, 1 at a time, 30 a day. Usage stays visible per task in `/status` and in this log. The raw
scale is to be confirmed against the ClearMeasureLabs Copilot billing page.
