# Cursor cloud agents: token optimizations and usage

The record of what was done to keep the `api` mode's Cursor cloud agents cheap, and what each run has used since.
Runs are on tdd only (`runtime:cursor-cloud` items; 1 at a time, 60 min, 30 launches a day).

## Optimizations (2026-10-17)

| # | Optimization | Where |
|---|---|---|
| 1 | Model `auto-smart` with `optimize_for: cost` on every launch; plain `auto` is the fallback, and a launch is refused rather than run on the account default | #167, `CursorCloudRuntime` |
| 2 | One phase per session: the agent does only its launch phase, records it and ends; per-phase tool sets refuse other phases' tools | #169 |
| 3 | Minimal prompts: a short bootstrap plus the current phase's instructions only, served by the gateway (`get_prompt`), no repository docs, no exploratory reads | #169, `prompts/` |
| 4 | No GitHub polling from agents; the gateway's state is the source of progress | #169 |
| 5 | Functional Testing diffs against `origin/master` after a fetch, so a stale master no longer fails a PR and forces a rerun | #170 |
| 6 | Usage is read before the agent is deleted, and shown in `/api/v1/status` → `runtimes."cursor-cloud".last[].detail.usage` | #170 |

## Before and after

Usage before #170 was not recorded (the agent was deleted before it was read), so the before side is time only.

| | Before (item 363) | After (items 364, 365) |
|---|---|---|
| Sessions per item | 1 session for Technical Design and Development, then Functional Testing | 3, one per phase |
| Session time | 16 min (Technical Design + Development) | 1.5–3.5 min per phase; 6.5 min for a whole item (365) |
| Phase errors | verdict refused (wrong phase), PR opened in Technical Design | none |

Measured since #170:

| Run | Input | Cache read | Output | Total |
|---|---:|---:|---:|---:|
| 364 Development | 52,213 | 627,328 | 2,571 | 682,112 |
| 364 Functional Testing | 89,395 | 490,624 | 1,978 | 581,997 |
| **Sum** | **141,608** | **1,117,952** | **4,549** | **1,264,109** |

88% of the tokens (1,117,952 of 1,264,109) were cache reads, billed at a fraction of new input.

Cursor's agent and run answers carry no model field, so the model actually run cannot be confirmed; only the request
(`auto-smart`, `optimize_for: cost`) is.

## Run log

Append one row per Cursor run from here on, from `/api/v1/status` (`last[].detail.usage`).

| Date | Item | Phase | Minutes | Input | Cache read | Output | Total |
|---|---|---|---:|---:|---:|---:|---:|
| 2026-10-17 | 364 | Development | 2.1 | 52,213 | 627,328 | 2,571 | 682,112 |
| 2026-10-17 | 364 | Functional Testing | 2.0 | 89,395 | 490,624 | 1,978 | 581,997 |
