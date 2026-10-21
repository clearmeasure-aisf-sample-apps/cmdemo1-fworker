# Copilot cloud agent runtime

Status: design, owner decisions of 2026-10-19. Builds on [agents-roles-launch.md](agents-roles-launch.md) (api launch
mode, served prompts, `/mcp`, one phase per session) and the Cursor runtime ([cursor-token-usage.md](../cursor-token-usage.md)).

## Decisions (Jeffrey, 2026-10-19)

| # | Question | Decision |
|---|---|---|
| 1 | Copilot SDK in our own job, or Copilot cloud agent | **Copilot cloud agent**: GitHub hosts the run and opens the PR |
| 2 | How the agent reaches the gateway's tools | **Our stdio MCP package**, proxying to the gateway's `/mcp` |
| 3 | Identity that starts tasks and holds the seat | **cm-ai-ops-bot with a Copilot seat**; GitHub App tokens once the API accepts them |
| 4 | Tasks per item | **One task per phase** (Technical Design, Development, Functional Testing) |
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
- Branches: the coding agent pushes only `copilot/*`. The Development task is created with `create_pull_request: true`; `pr_open` with
  that branch takes the task's draft over (title gets `#N`, marked ready, recorded as the item's pull request). Functional Testing starts
  from the branch the Development task reported (`artifacts[].data.head_ref`). One task per phase.
- An item that already has its open pull request (fix-forward after a red check, rework after an FT FAIL, Functional Testing): the task's
  `base_ref` is that pull request's head branch (verb `item-pr`), and the prompt names the pull request. The agent tasks API never pushes to
  `base_ref`: Copilot makes a new `copilot/*` branch from it and opens its draft against it. `pr_open` with that branch checks it holds the
  old head (else `not-on-item-branch`), retargets the draft to the default branch, takes it over, closes the superseded pull request with a
  comment and deletes its branch. Extra `copilot/*` drafts against the item's branches are closed with their branches.
- At most `api.copilot.maxLaunchesPerPhase` (3) executions of one item in one phase in a row, however each ended; then the item is parked.

## CI release (Jeffrey, 2026-10-20: "Build it, no file guard")

GitHub puts the workflow runs that a push of the Copilot cloud agent triggers into `action_required`. `POST /actions/runs/{id}/approve`
refuses them ("not from a fork pull request or queued by the Actions bot"); a re-run by an identity with write access starts them. The
gateway does that itself, so development flows without a person (merging green pull requests under `Auto Merge` it already does).

- **When**: every `api.copilot.ciReleaseSeconds` (120), by the acting gateway (leader lease), while the Copilot mode is on and
  `api.copilot.ciRelease` is true (the default, source and deployed; smoke pins it). REST only: one list call per pass; the item reads only
  when a held `copilot/*` run exists.
- **Candidates** (`Plant.CopilotItems`): the items labelled `runtime:copilot-cloud` that the sweep read (only this gateway's `env:` scope) and
  the items the Copilot adapter ran a task for since the process started.
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
  else is no longer `action_required` and is not listed. One journal line `ci-released` per release on the item (key: the run id).
- Tier 1: suite `copilot`, scenario "CI release" (in scope released once, head mismatch and another env's item skipped, 403 surfaced,
  idempotent), against `held-run` of the fake GitHub.

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
