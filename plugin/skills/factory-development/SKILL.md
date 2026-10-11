---
name: factory-development
description: Use when item N is claimed in the Development column. Implements the item on its own branch in a worktree, opens the pull request, drives CI to green and completes the column. Runs the analyst, implementer and reviewer subagents.
---

# Development

Orchestrator = this session. Subagents `factory-analyst`, `factory-implementer`, `factory-reviewer` start with clean context: pass each the full BRIEF (this text plus the `get-item` JSON), the previous roles' output verbatim, and any findings. At most 3 review rounds. Subagents never call `complete`.
Branch: `factory-worker/N-development`. Use a git worktree for the implementer (`isolation: worktree`).

Entry: `factory.ps1 get-item --item N`. The result decides the mode, exclusive:
- `existing_pr` absent, no `state.pr.rejection`: first pass (steps 1-6).
- `existing_pr` present with `rejection`: FIX-FORWARD. Check out `existing_pr.branch`. Bring it up to date by MERGING origin/master into it (no rebase, no force-push). Resolve conflicts keeping both sides. Address `rejection` and `failed_checks`. Push normally. Then steps 5-6 on the SAME pull request.
- the rejection says "already merged ... open a NEW branch": RESIDUAL. New branch from master, new pull request, only the residual gaps.

1. ANALYST: read the item (requirements, `## Technical Design`). Plan files, tests, acceptance criteria. Implement nothing.
2. IMPLEMENTER: branch off master, implement with tests, following existing patterns (read AGENTS.md / CLAUDE.md in the target repo). Every commit message contains `#N`. Push.
3. IMPLEMENTER: run the repo's private build and static analysis; fix until green. No pull request on a red build. Then `git fetch origin && git merge --no-edit origin/master`, resolve, rebuild.
4. IMPLEMENTER: `factory.ps1 open-pr --item N --head factory-worker/N-development --title "<title>" --body-file <file>`.
   - exit 10 `pr-not-created`: if the item is already satisfied, `complete --succeeded false` saying no change is needed. Never push an empty commit.
5. REVIEWER: read the real diff against the analyst's plan. Findings go back to the analyst (VALID/INVALID), the implementer fixes and pushes (3 rounds max).
   Then `factory.ps1 wait-for-checks --item N` until exit 0 (success). exit 21 = still running: call again at most 2 more times, then record the review and call `complete` anyway (step 6): exit 21 `accepted-checks-pending` there is the normal way to finish, because the gateway watches CI and advances the item, and a session left polling CI holds a worker slot for the whole build. exit 10 `checks-red`: the implementer diagnoses, fixes, pushes, repeat.
   `record-review ... --column Development` with last line `REVIEW: COMPLETE`.
6. REVIEWER, only on green checks and `REVIEW: COMPLETE`: ONE call
   `factory.ps1 complete --item N --column Development --review-ref <ref> --comment "PR open, CI green - <PR link>. REVIEW: COMPLETE - <one sentence>"`.
   - exit 0: done. exit 21 (`accepted-checks-pending`): the gateway watches the checks and advances the item; stop.
   - exit 10: `empty-pr`, `no-open-pr`, `checks-not-green`, `development-docs-only-pr` (the pull request changes only documentation and the item body does not carry `<!-- aisf:docs-only -->`; a docs-only item carries it from its technical design), `reviewer-verdict-missing`: fix the named cause, one more call.
   - exit 11: stop at once.
   - unrecoverable: `complete --succeeded false --comment "<summary>"`.

Never: merge, approve, force-push, rebase pushed history, open a pull request on a red build, or call `complete` before the checks report success.

## Model for each subagent (cost first, automatic)
Before starting any subagent, ask the ladder: `factory.ps1 model-for --role subagent --item N`. It returns `{tier, name, model, alias, effort, reasons}`: pass `model` to the Agent tool (an Agent tool that accepts only short names gets `alias`: haiku, sonnet, opus or fable). The gateway raises the tier by itself when the item shows a loop (a relaunch, repeated review findings, a demotion, fix-forward rounds, or three or more refused gate calls); a machine's own `models.json` decides which model sits on each rung. Default ladder: haiku 4.5 at xhigh, then sonnet 5.5 at medium, then opus 5.5 at medium. Never choose a model by judgement; never lower a tier. `mode: "fixed"` means the machine pins a model: use it as returned. `mode: "profile"` (the `testing` profile: haiku at low effort, for items labelled `factory test` and for automated test runs) is pinned too and never climbs: use it as returned even if the item loops.

Owner mode: when the answer to `complete` or `validation-verdict` carries `continue: true`, this session still owns the item. Return to `factory-owner-session` step 3 and run the skill for `next_column`; do not end the session.
