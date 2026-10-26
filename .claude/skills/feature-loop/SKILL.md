---
name: feature-loop
description: >
  Run the feature loop on ONE GitHub work item of cmdemo1-fworker: drive it across the
  cmdemo1-fworker delivery board (687) (https://github.com/orgs/clearmeasure-aisf-sample-apps/projects/687)
  one column at a time — design, implement, verify — through code, tests, a conflict-free
  PR, API-verified green CI, bot-finding triage, merge, and board moves. Use when asked to
  "run the feature loop on work item #N", "work issue #N through the board", or when
  /feature-loop N is invoked. For a batch of items, use /feature-loop-dispatch instead.
---

# Feature Loop (single work item)

Drives exactly one work item end-to-end. Configuration (board, columns, build commands,
merge policy, cached board IDs) comes from `.claude/factory-loop.json` at the repo root.
These rules are the contract for this repository; a user's own global rules may add to but
never weaken them.


## Children first, depth-first (before touching the item)

Resolve `gh api repos/{owner}/{repo}/issues/{N}/sub_issues` BEFORE touching #N. Recurse to
the deepest open descendant and work the tree bottom-up: a child is driven to Done, then
its parent is reconsidered. A parent is never started while any of its descendants are
open. Independent siblings may be worked in parallel — each in its own worktree. Report
the resolved tree and execution order before starting.

## Column progression

- **One column at a time.** The item advances exactly one board column per transition —
  never skips — and only after that column's work is verified. Columns and their roles
  (design / implement / verify / terminal) are in `factory-loop.json`.
- **Independent subagent per column.** Each column's work is done by its own dedicated
  subagent; one column's deliverable is the next column's input. No single subagent
  carries the item through multiple columns.
- Non-applicable columns are passed through with a recorded no-op justification (an issue
  comment stating why the column does not apply).
- On completion of the loop's scope, leave the card in `doneForNowColumn` from
  `factory-loop.json` unless the item is genuinely Done.

## Subagent rules

- **One git worktree per writing subagent.** Any subagent that edits files, builds, or
  runs tests gets its own git worktree (`isolation: "worktree"`). Parallel subagents never
  share a checkout. Read-only search agents may use the main checkout.
- **All subagents run on Opus at low effort** (`model: "opus"`; the owner's standing rule of 2026-10-12).

## Build, PR, and merge gates

- Branch naming: `{username}/{branch-description}` (the account that initiated the session).
- **Private build before commit:** the dotnet build and `dotnet test` of the fworker specifics
  (end of this file) must pass.
- **Acceptance tests before merge:** Tier 1 and the smoke test in the pull request's CI.
- **Merge master before PR (mandatory):** before pushing or opening/updating a PR, fetch
  and merge `origin/master` into the branch, resolve conflicts, and re-run the private
  build. PRs must arrive conflict-free against current master.
- **CI is API-verified only:** after pushing, poll
  `gh api repos/{owner}/{repo}/commits/{sha}/check-runs` and confirm EVERY job's
  `conclusion` is `success`. Never report a PR complete on "PR created" or a local build,
  and never infer status from a shell exit code. Fix and re-push until green.
- **Triage every bot review finding before merge:** list all review comments from
  static-analysis bots (`github-code-quality[bot]`, `github-advanced-security[bot]`,
  etc.). Every finding is either fixed in the same PR or explicitly declined with a
  one-line reply on the PR. Never merge past bot findings silently.
- Merge method and issue-close policy come from `factory-loop.json`.

## Testing policy (definition of done, non-negotiable)

No functionality is added or changed without automated tests **in the same PR** at every
applicable layer:

1. **Unit tests** (dotnet test projects under `tests/`) for pure logic, policies and Domain
   ports; parity tests when porting a rule-script function.
2. **Tier 1 scenarios** (`tests/scenarios`, black box against `tests/FakeGitHub`) for anything
   visible from outside the gateway: verbs, routes, journal lines, board writes.
3. **Behaviour-neutral refactorings** prove neutrality with the golden-write harness, the
   route-and-auth table and the start-up test (once task C0 has added them).

If a layer genuinely does not apply, state explicitly in the PR description why. Respect the
project dependency rules of the plan (`src/` projects point inward) and this repository's
`CLAUDE.md`.

## Discovered work becomes a child

When working #N spawns new work — a follow-up from testing, a defect found while
implementing, a deferred bot finding — create it as a **child sub-issue** of #N, never a
free-floating sibling, and place it in the leftmost board column:

```
child_id=$(gh api repos/{owner}/{repo}/issues/{child} --jq .id)
gh api repos/{owner}/{repo}/issues/{N}/sub_issues -X POST -F sub_issue_id=$child_id
gh api repos/{owner}/{repo}/issues/{N}/sub_issues   # verify
```

Note: the POST takes the child's numeric `id`, NOT its issue number.

## Parent clamp

A parent's board status may never be further right than the LEAST-advanced of its open
children. Before every parent card move, list the parent's sub-issues and clamp:
`parent_status = min(intended_status, min(status of each open child))`. A parent cannot
reach Done while any child is open; filing a follow-up child from a late column pulls the
parent BACK to the child's column (reopening it if closed). Record each clamp as a comment
on the parent naming the child that caused it. The parent advances again only as its
children advance.

## GitHub API budget

The GraphQL budget is shared across all agents. Use REST (`gh issue`, `gh pr`,
`gh api repos/...`) for reads and comments. Reserve GraphQL for board field mutations,
using the pre-cached IDs in `factory-loop.json` `boardIds` — re-fetch them only if a
mutation fails with an unknown-ID error. Check `gh api rate_limit` before any fan-out.

Card moves use:

```
gh api graphql -f query='mutation{updateProjectV2ItemFieldValue(input:{projectId:"<projectId>",itemId:"<itemId>",fieldId:"<statusFieldId>",value:{singleSelectOptionId:"<optionId>"}}){projectV2Item{id}}}'
```

(Find `itemId` for an issue via the org project: it is the ProjectV2 item ID, not the
issue node ID.)

## Completion heartbeat (mandatory — never hand off while pending)

The agent driving #N MUST NOT end a turn or send a final message while CI, merge, issue
close, or card move is still pending.

When CI is running, poll every **60–90 seconds** until every check-run on the PR head (and,
after merge, on `origin/master` tip) has `conclusion` `success` or `skipped`:

```
gh api repos/{owner}/{repo}/commits/{sha}/check-runs \
  --jq '[.check_runs[] | select(.status != "completed" or (.conclusion != "success" and .conclusion != "skipped")) | {name, status, conclusion}]'
```

Forbidden final states: "CI still running", "in progress", "will follow up", "monitoring".
Keep polling and finishing closeout in the same session.

The final message MUST begin with **`STATUS: COMPLETE`** or **`STATUS: BLOCKED`**, then
final board column, PR number, merge commit SHA, and any children created — only after
independent API verification of green CI on the merge commit.

## Reporting

Every status update states, in order: what happened, what it means for the work item, and
what happens next — plain software-team vocabulary (work item, defect, pull request,
build, automated tests, board status), no orchestration jargon. Finish with: final board
column, PR number, merge commit SHA, and any children created.

## cmdemo1-fworker specifics (these add to the rules above)

- **The plan** `docs/design/fworker-wolverine-implementation.md` gives each task's row: the files it
  owns and must not touch, what it comes after, its size and "done when". Read it first. A change
  to a hot file the task does not own (plan section 1.4) goes to the orchestrator.
- **Builds and tests a subagent can run:** `dotnet build tests/FakeGitHub -c Release -nodeReuse:false
  -p:UseSharedCompilation=false && dotnet build gateway -c Release -nodeReuse:false
  -p:UseSharedCompilation=false`, plus `dotnet test` of the test projects that exist. The worktree
  guard refuses `pwsh` and Tier 1 allows one run per machine, so the smoke test, rule-script parity
  tests and Tier 1 (`tests/scenarios`) run in the pull request's CI (`tests.yml`). Never work around
  the guard. This replaces the private build and acceptance-test commands above.
- **Testing policy for this repository** (replaces the work-orders layers above): Tier 1 scenarios
  for anything visible from outside (verbs, routes, journal lines, board writes); dotnet unit tests
  for pure logic, policies and Domain ports (parity tests when porting a rule-script function);
  behaviour-neutral refactorings prove neutrality with the golden-write harness, the
  route-and-auth table and the start-up test once task C0 has added them. A new Tier 1 suite comes
  with its dot-source line and `$SuiteDefs` entry in `tests/scenarios/run.ps1` and its `suites:`
  line in `.github/workflows/tests.yml`.
- **Pre-commit review:** a reviewer subagent reviews every staged change before commit.
- **Pull request:** body contains `Closes #N`; squash merge with auto-merge enabled when the PR is
  opened; poll until the PR state is MERGED. Green, still open and auto-merge off is your own merge
  failure: fix it. Known flaky Tier 1 jobs (issues #154, #240) are re-run at most twice, then read.
- **Merge windows:** while the orchestrator holds merges (a measured batch, soak or shadow period)
  it labels the PR `hold:merge-window` and the watchdog skips it; that wait is the orchestrator's,
  not yours: report STATUS: BLOCKED (merge window) and stop.
- **Done:** merged with every check-run on the PR head `success` or `skipped`. tests.yml does not
  run on master pushes, so the orchestrator runs the master Tests workflow after each batch.
- Branch prefix `cm-ai-ops-bot/`; no "Generated with Claude Code" anywhere.
