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

## 2026-10-20: three-item tdd test (docs, code + unit test, database migration): not run yet

Blocked on the task token: `COPILOT_TASKS_TOKEN` for tdd (cm-ai-ops-bot's token in the tdd Key Vault) is a secret-store write that the
permission check refused to this session; the owner sets it (and the bootstrap tokens, the MCP configuration and the firewall, see
HANDOFF). Until then the tdd gateway logs `{"runtime":"copilot-cloud","enabled":false,...,"token":false}` and unlabelled items wait.

| Item | Kind | Phases | Times | ai_credits | Outcome |
|---|---|---|---|---|---|
| (to create) | docs | | | | not run |
| (to create) | code + unit test | | | | not run |
| (to create) | DbUp migration + model/mapping + test | | | | not run |

Isolation evidence (live, tdd 1.0.407 with #185, 2026-10-20): decoy cmdemo1-workorders#382 labelled `factory worker`, `env:uat`,
`enhancement`, `Auto Merge`. The previous tdd revision (before #185, replaced at 03:32:05Z) placed its card at 03:31:17Z; the new
revision wrote nothing on it afterwards (no comment, no claim ref), `/api/v1/status` does not name it, and `get-item` through the tdd
gateway answers exit 10 `out-of-scope` ("does not carry exactly one env: label, 'env:tdd'"). Tier 1 suite `envscope` proves the same with a
tdd and a uat gateway on one board (claim, merge, sweep, board guard, webhook ingest, claim-ref reap).
