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
