# Copilot cloud agent runtime

Status: design, owner decisions of 2026-10-19. Builds on [agents-roles-launch.md](agents-roles-launch.md) (api launch
mode, served prompts, `/mcp`, one phase per session) and the Cursor runtime ([cursor-token-usage.md](../cursor-token-usage.md)).

## Decisions (Jeffrey, 2026-10-19)

| # | Question | Decision |
|---|---|---|
| 1 | Copilot SDK in our own job, or Copilot cloud agent | **Copilot cloud agent**: GitHub hosts the run and opens the PR |
| 2 | How the agent reaches the gateway's tools | **Our stdio MCP package**, proxying to the gateway's `/mcp` |
| 3 | Identity that starts tasks and holds the seat | **cm-ai-ops-bot with a Copilot seat**; GitHub App tokens once the API accepts them |
| 4 | Tasks per item | ~~One task per phase~~ superseded 2026-10-21: **one task per item, resumed** (below) |
| 5 | Rollout | **tdd only, docs-only items, 30 launches a day**, label `runtime:copilot-cloud` |

## Model (Jeffrey, 2026-10-20)

Auto model selection, for efficiency and low cost. The agent-tasks API has no model parameter that we use: the
adapter sends none (`api.copilot.model` is empty, pinned by a smoke check), and GitHub uses Auto wherever no model is
picked. Auto carries a 10% discount on the model multiplier and is not subject to the weekly rate limits. The first
round trip (2026-10-20, PR cmdemo1-workorders#380) ran on Auto's pick, `gpt-5.3-codex`, and reported its usage as
`ai_credits`, not tokens. Copilot has no separate cost setting beyond Auto. Never set a premium model here without
the owner's word.

## Default runtime and all environments (Jeffrey, 2026-10-20)

- cmdemo1's default: `runtimes.environments.<env>.routing` sends every phase of an item without a `runtime:` label to `copilot-cloud`;
  `runtime:cursor-cloud` (and any other label) still wins. Code and database changes are allowed (the docs-only rule stays for Cursor only).
- Tasks are started as cm-ai-ops-bot in all three environments (one token, `COPILOT_TASKS_TOKEN` in each environment's vault); each
  environment has its own bootstrap token. `api.copilot.environments`: tdd on; uat and prod written with `"enabled": false` until the owner
  switches them on.
- One MCP configuration for all environments: the prompt carries the gateway URL and the one-time code; `fworker-mcp` accepts only the
  three gateway hosts (https, no port or path) and picks `FWORKER_BOOTSTRAP_TOKEN_<ENV>`.
- Branches: the coding agent pushes only `copilot/*`. The owner task is created with `create_pull_request: true` (since 2026-10-21 in every
  phase); `pr_open` with that branch takes the task's draft over (title gets `#N`, marked ready, recorded as the item's pull request). Functional Testing starts
  from the branch the Development task reported (`artifacts[].data.head_ref`). One task per phase.
- An item that already has its open pull request (fix-forward after a red check, rework after an FT FAIL, Functional Testing): the task's
  `base_ref` is that pull request's head branch (verb `item-pr`), and the prompt names the pull request. The agent tasks API never pushes to
  `base_ref`: Copilot makes a new `copilot/*` branch from it and opens its draft against it. `pr_open` with that branch checks it holds the
  old head (else `not-on-item-branch`), retargets the draft to the default branch, takes it over, closes the superseded pull request with a
  comment and deletes its branch. Extra `copilot/*` drafts against the item's branches are closed with their branches.
- At most `api.copilot.maxLaunchesPerPhase` (3) executions of one item in one phase in a row, however each ended; then the item is parked.

## One task, resumed (Jeffrey, 2026-10-21)

His words: "As soon as AI worker like the cloud session is started up and assigned to a work item that session should stay the owner of that
work item until that work item has been merged into the master branch and the master branch verified to be green." Chosen option: **one
task, resumed**. This supersedes "one task per phase" above; the per-phase prompts, tool sets, settle and CI release stay.

**Mechanism.** The agent tasks REST API (2026-03-10) has list, create and get only: no follow-up, continue, stop or archive endpoint. GitHub's
documented follow-up is a `@copilot` mention on the pull request by a user with write access: "Copilot pushes commits directly to the pull
request branch" and "remembers context from previous sessions on the same pull request" (docs: cloud-agent/make-changes-to-an-existing-pr;
changelog 2025-09-30). So:

1. The owner task's **first session** (whatever phase it starts in, Technical Design included) is created with `create_pull_request: true`,
   so Copilot opens the item's draft pull request at once. Every later session is a follow-up on that same pull request and branch.
2. A **follow-up** (next phase, fix-forward after red checks, rework after an FT FAIL, a red master) is a comment `@copilot <prompt>` on the
   pull request, posted with the seat's token (`COPILOT_TASKS_TOKEN`, cm-ai-ops-bot). The execution id is `<task>#<n>`, the task's n-th
   session; its state is read from `GET .../tasks/{id}` `sessions[n]` (ordered by `created_at`). Not listed yet: queued.
3. Follow-up prompts are **terse and self-contained**: phase, item, pull request and branch, what changed (the item state's `pr.rejection`:
   the red checks or the FT findings), the ownership rule, then the bootstrap and the connect lines. Correctness never depends on Copilot's
   memory, which is a summary per pull request, not a transcript.

**Ownership.** On the first session the item state records `copilot_task` {id, url (html_url, the session link), branch, pr, phase, since,
previous} (rule verb `copilot-owner`, the gateway's own; refused over HTTP) and the item gets one comment "**Owner:** Copilot task ... owns #N
from <phase> until its pull request is merged and master is green". The plant routes an owned item to copilot-cloud whatever its label, the
routing or a connected session says, and ignores a pin label for it (`Plant.CopilotOwns`): no other worker is offered it. The lease of each
session is the usual per-name one; between sessions the gateway holds the item for the task (no session, no credits, no concurrency slot).

**Waiting.** No session runs while CI, the merge or master run. The usual sweep and webhooks move the item (checks green: next column; red:
back to Development with `pr.rejection`). The owner watch (every `api.copilot.ciReleaseSeconds`, acting gateway only) reads `copilot-owner
status` for each owned item with no session running: `ci`, `next-phase`, `parked:<reason>`, `master-green`, `master-broken`, `done`, `closed`.

**Done.** Only when the pull request is merged AND master is green for the merge commit (the settle's `merge.green_at`): the watch closes
the ownership (`done_at`), comments "**Owner done:** ... Session link: <html_url>" and logs `owner-done`. An item closed without a merge ends
the ownership without that comment. A **red master** after the merge (`master-broken`) resumes the owner task once per merge commit to fix
forward on ONE new pull request against master; the item stays parked master-broken and goes on by the existing auto-unpark once master is
green, then the owner is done.

**Sessions in the prompts.** The agent owns the item until master is green, but each session does its phase step (push, `pr_open`,
`item_complete` or the verdict), calls `session_end` and ends. It never polls or waits for CI. Before every push that creates or updates the
pull request (Jeffrey, 2026-10-21: "runs the Private build and the acceptance tests within the session") it runs `pwsh ./PrivateBuild.ps1`
and `pwsh ./AcceptanceTests.ps1`, pushes only when both pass, and reports both in `item_complete`. cmdemo1-workorders'
`copilot-setup-steps.yml` installs the SDK and the Playwright Chromium; ubuntu-latest brings PowerShell and Docker (SQL Server container,
else the SQLite fallback).

**Kept.** `maxLaunchesPerPhase` (3) now caps sessions (resumes) per phase in a row; the 60 min limit per session, `maxConcurrent` (a running
session counts, an owned but waiting task does not), the daily cap (a master fix-forward counts too) and the credit guard (off; when on, per
task over all its sessions).

**Status.** `/api/v1/status` → `runtimes."copilot-cloud".owned`: item, task, url, branch, pr, phase, waitingOn (`session` while one runs),
sessions; the dashboard table "Copilot owned items".

**Limits.**
- Not verified live before the build: starting a throwaway task as cm-ai-ops-bot was refused by the operator's permission check. The fake
  GitHub models a mention as a new session of the task whose branch the pull request is on. The live check is the tdd watch after deploy.
- Whether GitHub lists a mention's session in the same task's `sessions` is undocumented. If it does not, the session still connects with its
  one-time code and works; its end is seen at `session_end` or at the 60 min deadline.
- No pull request to resume on (the first session never opened its draft): a new task is created on the owner's branch (`head_ref`), the
  owner moves to it and the old id is kept in `previous`.
- Items an earlier gateway ran one task per phase for get an owner at their next phase (a new task from their pull request's branch, as before)
  and are resumed from then on.
- After a restart the owner is read back from the item state at the item's next session; an item that only waits on master is picked up by
  the watch once it is read again.
- The master fix-forward pull request is opened by Copilot; its CI release and merge follow the usual rules for a pull request that names the item.

Tier 1 (suite `copilot`): the full single-task lifecycle, red CI fix-forward in the same task, FT FAIL rework in the same task, no other
worker offered while owned, done only after master green, a red master resuming the owner once.

## CI release (Jeffrey, 2026-10-20: "Build it, no file guard")

GitHub puts the workflow runs that a push of the Copilot cloud agent triggers into `action_required`. `POST /actions/runs/{id}/approve`
refuses them ("not from a fork pull request or queued by the Actions bot"); a re-run by an identity with write access starts them. The
gateway does that itself, so development flows without a person (merging green pull requests under `Auto Merge` it already does).

- **When**: every `api.copilot.ciReleaseSeconds` (120), by the acting gateway (leader lease), while the Copilot mode is on and
  `api.copilot.ciRelease` is true (the default, source and deployed; smoke pins it). REST only: one list call per pass; the item reads only
  when a held `copilot/*` run exists.
- **Candidates** (`Plant.CopilotItems`): the items labelled `runtime:copilot-cloud` that the sweep read (only this gateway's `env:` scope) and
  the items the Copilot adapter ran a task for since the process started.
  When `runtimes.routing` sends every phase of an unlabelled item to `copilot-cloud` (cmdemo1's default), the items named in the titles of
  the held runs' open pull requests are candidates too (their recorded pull request must still name the branch). Every pass runs, with or
  without candidates; every 30 minutes the pass also reads the installation's Actions permission with the App's JWT (`appActions` in
  status). Anything but `write` sets `permissionMissing` and the owner action before a run is refused.
- **Rule** (verb `ci-release`, `Verbs.Pr.ps1`; the gateway's own, refused over HTTP): `GET actions/runs?status=action_required`, runs whose
  `head_branch` is `copilot/*`. A run is released only when its item is open, in this gateway's env scope (`Get-ItemInfo`), carries no other
  `runtime:` label, and the item's pull request (`Find-ItemPr`, the head recorded by `pr_open`) has that branch and the run's `head_sha` as
  its head. Anything else is skipped (`head-mismatch`, `no-item`, `out-of-scope`, `other-runtime`).
- **No file guard**: the owner's decision. A held run is released whatever files the push changed (workflow files included); the guard is
  the item, scope and head match above, and the review and merge gates that follow.
- **Release**: `POST actions/runs/{id}/rerun` with the gateway's GitHub App installation token (factory-worker-author). Never another
  credential. A 403 that is not a rate limit counts as `refused`, is retried for that run after 5 passes (at least 10 s), and shows in
  `/api/v1/status` → `runtimes."copilot-cloud".ciRelease` (`released`, `refused`, `lastError`, `permissionMissing`, `ownerAction`) and on the
  dashboard as the owner action "grant factory-worker-author App Actions: Read & write".
- **Once**: a released run id is never re-run again (kept in the process, passed to the verb as `skip`); a run already re-run by someone
  else is no longer `action_required` and is not listed. A successful release is routine (the same session that opened the pull request merges it once green) and posts **no issue
  comment**: it is recorded in the item's state (`journal.ciReleased`, the last 20 lines, in the edited-in-place factory-state comment),
  in `/api/v1/status` (`released`, `lastRelease`) and as a gateway log line. An issue comment (`ci-release-refused`, once per run id) is
  posted only when a release needs a person: the 403, worded as the owner action "grant factory-worker-author App Actions: Read & write".
- Tier 1: suite `copilot`, scenario "CI release" (in scope released once with no comment, head mismatch and another env's item skipped, 403 surfaced
  with one comment, idempotent), against `held-run` of the fake GitHub.

## Shape

```
gateway sweep ── item labelled runtime:copilot-cloud, phase P
   │
   ├─ mint r1 role token (item, phase P, short-lived)
   ├─ POST /agents/repos/{owner}/{repo}/tasks      (as cm-ai-ops-bot)
   │     body: phase prompt from prompts/ (get_prompt), item number, branch,
   │           the r1 token reference, "do phase P only, then end"
   ▼
Copilot cloud agent (GitHub Actions environment of cmdemo1-workorders)
   ├─ local MCP server: fworker-mcp (stdio) ──HTTPS──▶ gateway /mcp (r1 token)
   │     tools: the phase's tool set only (per-phase tool sets from #169)
   ├─ GitHub MCP server (built in)
   └─ pushes branch / opens PR (Development), records phase done via /mcp
   ▼
gateway: task status (GET tasks), worker registration, phase verdicts, merge
```

## Parts to build

1. **`CopilotCloudRuntime`** (gateway/Runtimes), beside `CursorCloudRuntime`, off by default:
   launch (create task), poll status, cancel past the deadline, registration watchdog, daily cap, concurrency 1,
   60 min. Config under `api.copilot` in `config/deploy.overrides.json`, `environments.tdd` only.
2. **`fworker-mcp` stdio package**: a small proxy (stdin/stdout MCP ↔ HTTPS `/mcp`) published so the agent
   environment can install it; reads the gateway URL and the r1 token from the task's environment.
3. **Repository MCP configuration** in cmdemo1-workorders (Settings → Copilot → coding agent): one server,
   `fworker-mcp`, with its secret in the repository's `copilot` environment. The token there is a bootstrap token
   that can only exchange an item's one-time code for that item's r1 token, so a repo-wide secret never grants
   item rights by itself.
4. **Agent firewall allow-list**: the tdd gateway host added to the Copilot agent firewall.
5. **Seat and token**: Copilot seat for cm-ai-ops-bot; its token for task creation in the tdd Key Vault
   (`fworker-copilot-token`), `unset` in uat and prod.
6. **Prompts**: reuse `prompts/worker/*.md`; a short `bootstrap/copilot.md` (how to start the MCP server, end after
   the phase).
7. **Measurement**: per phase: duration, premium requests (billing usage API for the seat), and token usage if the
   task API reports it. Recorded in `docs/copilot-token-usage.md`, the same tables as Cursor.

## The manager on demand (2026-10-21)

The same task API serves one more role: a short task for the plant manager, launched by the gateway when the digest needs a judgement and
no supervisor is present (design: `agents-roles-launch.md` section 18). Same exchange (one-time code and bootstrap token for an `r1`), role
`manager`, at most one task, a cooldown, a daily count, `maxMinutes` 10; tdd only. Its usage is shown in `/status` `manager.copilot`.

## Risks and open points

| Risk | Mitigation |
|---|---|
| Task API is public preview; App tokens "coming soon" | Bot user token now; adapter isolates the API calls |
| MCP config is per repository, not per launch | Bootstrap token + per-item one-time code (part 3) |
| Token usage may not be exposed per task | Measure premium requests and time; record "not reported" honestly |
| Agent may drift past its phase | Per-phase tool sets refuse other phases' tools; prompt says end after the phase |
| Agent environment network | Explicit firewall allow-list for the gateway host; registration watchdog marks failures |

## Rollout

1. Adapter + MCP package + tests, off everywhere (merge when green).
2. Seat, token, repo MCP config, firewall: owner/infra steps listed in HANDOFF.
3. Enable in tdd; 3 docs-only items; record the phase times and usage; compare with Cursor.
