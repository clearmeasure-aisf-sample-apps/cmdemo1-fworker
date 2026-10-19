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
| 2026-10-17 | 371 | Technical Design | 2.3 | 0 | 0 | 0 | n/a |
| 2026-10-17 | 372 | Technical Design | 1.9 | 0 | 0 | 0 | n/a |
| 2026-10-17 | 371 | Development | 52.5 | 0 | 0 | 0 | n/a |
| 2026-10-18 | 374 | Technical Design | 2.4 | 0 | 0 | 0 | n/a |
| 2026-10-18 | 374 | Development | 1.6 | 0 | 0 | 0 | n/a |
| 2026-10-18 | 374 | Functional Testing | 1.9 | 0 | 0 | 0 | n/a |
| 2026-10-18 | 376 | Technical Design | 2.6 | 0 | 0 | 0 | n/a |
| 2026-10-18 | 376 | Development | 36.9 | 0 | 0 | 0 | n/a |
| 2026-10-18 | 377 | Technical Design | 2.7 | 0 | 0 | 0 | n/a |
| 2026-10-18 | 377 | Development | 2.0 | 0 | 0 | 0 | n/a |
| 2026-10-18 | 377 | Functional Testing | 1.8 | 0 | 0 | 0 | n/a |

n/a: Cursor's usage answer read all zeros (see "Usage fixes" below). 371 and 376 Development failed on a blocked push.

## Round A (2026-10-17): stopped, no token numbers

Plan: 3 docs-only items of different shape (371 new one-line page, 372 append to an existing glossary, 373 new page
with a table), 3 phases each, then hypotheses, changes and a Round B for comparison. Stopped after 3 runs on two
defects; no hypothesis was implemented, because nothing could have been measured against a baseline.

1. **Usage is all zeros.** `GET /v1/agents/{id}/usage` answered 200 with every count 0 for all three runs (two
   Technical Design runs of ~2 min, one Development run of 52 min). Item 364's runs had real numbers, so the
   read at session end is most likely too early for Cursor's accounting; the agent is then deleted and the number is lost.
   Needs a change in `gateway/Runtimes/CursorCloudRuntime.cs` (code-owner path): retry the read until it is non-zero
   or a short limit passes, before the delete. Unmeasured until then.
2. **The Development agent could not push.** Item 371: the agent released the item after 52 min with "Cannot push
   factory-worker/371-development: outbound TCP 443 blocked; github.com:22 has no authorized key", after a question to
   the supervisor waited unanswered. Items 364/365 pushed on the same day, so the Cursor environment's network or
   its GitHub access changed. Needs the owner's look at the Cursor account/environment.

Items 371, 372 and 373 were moved back to Todo so they do not use more of the 30-a-day cap; they are the Round A
items to rerun once both defects are fixed.

### Hypotheses for after the fix (not implemented, not measured)

Most tokens are cache reads (88% for item 364), which grow with the number of agent turns, so fewer turns per
phase is the main lever.

| # | Hypothesis | Expected effect |
|---|---|---|
| H1 | Item template gives the exact `Files:` line and docs-only marker in Scope; Technical Design copies them | fewer TD turns, ~20-30% less in TD |
| H2 | Skip Technical Design for docs-only items whose body already has `Files:` (fold into Development) | one session less per item, ~30% per item |
| H3 | Shorter worker prompts and terse review lines | small input saving per turn, <5% |
| H4 | A gateway tool that returns the PR diff for Functional Testing instead of git fetch/checkout/diff | 2-4 fewer FT turns, ~20% in FT |
| H5 | Development stops after `pr_open` plus one `pr_wait_checks` call | fewer polling turns, ~10-20% in Dev |

## Usage fixes (2026-10-18) and what they showed

| Change | PR | Result on tdd |
|---|---|---|
| Wait for a non-zero usage read before the delete (10 s, doubling, 300 s cap, one last read); `/status` `last[]` re-reads the detail | #174 | item 374: 3 runs, still 0 after the full 300 s |
| `/status` shows the shape of the usage answer (keys, each run's counts) | #175 | item 376: keys `totalUsage, cost, runs`; each run `id, usageUuid, usage, cost`; the run counts are 0 too |
| No cancel at session_end: the run finishes by itself; cancel only past the deadline and before the delete | #176 | item 377: 3 runs, still 0 |

Conclusion: the zeros come from Cursor. The timing of the read and the cancel are ruled out. Items 364/365
(2026-10-17, ~06:00 UTC) got real numbers from the same endpoint; every run since ~19:00 UTC reads 0. The owner needs
to check on Cursor's side whether `GET /v1/agents/{id}/usage` still reports tokens: the dashboard usage of these
agents, the account's plan or billing mode, or an API change. The `cost` fields are not shown yet.

**The push block is intermittent.** Development pushed for 374 (PR 375, merged) and 377. For 371 and 376 it failed
with "outbound HTTPS to github.com:443 times out" inside the agent's VM. The launch body is the same for all runs
(`LaunchBody` has not changed since #170). The Cursor GitHub App installation (168749053) is fine: all repositories,
contents and pull requests write, not suspended, unchanged since 2026-10-14. So the cause is the network egress of
Cursor's agent environment, not anything the gateway sends. The owner should raise it with Cursor or check the
account's cloud-agent network setting.

Rounds A and B were not run, and H1-H5 were not implemented, because usage cannot be measured. Phase times of the
successful runs (374, 377): Technical Design 2.4-2.7 min, Development 1.6-2.0 min, Functional Testing 1.8-1.9 min.

## End-to-end test, 2026-10-19 (item 379)

Docs-only item 379 (one glossary line, labels `runtime:cursor-cloud` and `Auto Merge`, no worker pin), placed in
Technical Design at 18:50 UTC after `POST /api/v1/runtimes/cursor-cloud/reset` cleared the unhealthy state left by
item 376 (376 was moved to Todo and its `factory worker` label removed).

| Phase | Start (UTC) | End (UTC) | Outcome | Agent id | Registered | Push | Usage |
|---|---|---|---|---|---|---|---|
| Technical Design, try 1 | 18:56:51 | 18:59:25 | start-failed, `launch-400` | none | no | n/a | n/a |
| Technical Design, try 2 | 18:59:54 | 19:01:23 | start-failed, `launch-400` | none | no | n/a | n/a |

Result: does not work. `POST /v1/agents` answers 400 on every launch, so no agent is created and the runtime turns
unhealthy again after two tries (`failures-exhausted` parks the item). Items 376's failures at 01:48 and 01:51 UTC
were the same kind (start-failed with no execution id). The gateway logs only the status code; the body of Cursor's
400 is not recorded, so the rejected field (model selection, repository, or MCP server entry) is not known yet.
Next step: log the 400 body in `CursorCloudRuntime.Provision`, or read it with one manual launch by the owner.
