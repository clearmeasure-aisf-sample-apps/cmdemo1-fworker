# Copilot cloud agent: usage

The record of what the Copilot cloud agent runtime (`runtime:copilot-cloud`, `gateway/Runtimes/CopilotCloudRuntime.cs`) costs, in the
same tables as [cursor-token-usage.md](cursor-token-usage.md). Runs are on tdd only (1 at a time, 60 min, 30 launches a day, docs-only
items). The agent tasks API reports usage per session as premium requests (or AI credits), not tokens: token columns are "not reported"
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
