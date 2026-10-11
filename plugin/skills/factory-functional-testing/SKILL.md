---
name: factory-functional-testing
description: Use when item N is claimed in the Functional Testing column. Reviews the pull request read-only, exercises every acceptance criterion in the running app, collects demo evidence and records one validation verdict. Runs the analyst, implementer and reviewer subagents.
---

# Functional Testing

READ-ONLY: no commits, no code changes, no labels, no moves. The only output is the verdict call. Check out the PR branch in a read-only worktree.
Subagents as in `factory-development`; at most 3 review rounds. The reviewer's output is `validation-verdict`, never `complete`.

1. ANALYST: `factory.ps1 get-item --item N`. The pull request is `existing_pr`; do not search for another. If `existing_pr` is absent the verdict is `passed:false`, summary "no open pull request found for this work item", pr 0.
2. IMPLEMENTER: review branch and diff for correctness, completeness against the design, and risk. Exercise EVERY acceptance criterion literally, as a user would, in the running app. A criterion not exercised is stated as such, not assumed met.
   Demo evidence by `demo_evidence` in `get-item`, exclusive:
   - `screenshot`: nothing is uploaded. Summary states the URL loaded and what the screen shows.
   - `video`: record, extract frames, LOOK at them, publish the MP4 to release `demo-N`, and list per criterion the time `mm:ss` where its result shows, or `NOT SHOWN`. A PASS on a video item without the MP4 is downgraded to FAIL by the script.
   Running the app in a headless session (works the same on Linux, macOS and Windows; no nohup, curl or kill): first `pwsh <scripts>/ensure-tools.ps1 dotnet --repo <worktree>` and keep the `dotnet` path it prints; build the PR worktree with that dotnet; then ONE call `pwsh <scripts>/app-run.ps1 check --dotnet <that path> --project <the web project folder> --paths "/,/<page to look at>"`. It starts the application on port 5199, waits for `/_healthcheck`, fetches each path (status and the first 4000 characters of page text), stops the whole process tree and prints one JSON line. For a longer look use `app-run.ps1 start ...` (prints `pid` and `url`), `app-run.ps1 probe --url <url>` as often as needed, and `app-run.ps1 stop --pid <pid>` when done. Never wait for an approval prompt: if a command is refused, say which and record the verdict from the evidence you have, stating plainly which criterion was not exercised in the running app.
3. REVIEWER: check the evidence covers every criterion; missing evidence is a finding (analyst rules, 3 rounds). Then `record-review ... --column "Functional Testing"` (last line `REVIEW: COMPLETE` or `REVIEW: FINDINGS`).
4. REVIEWER, final action, one call:
   `factory.ps1 validation-verdict --item N --passed true|false --summary-file <file> --pr P --review-ref <ref>`
   (`--review-ref` only for a PASS). The summary is one paragraph. Findings the implementer could not resolve read-only become `--passed false`.
   - `next: "demoted"`: the script moved the item back to Development. Stop.
   - `next: "merge"` (PASS, item has `Auto Merge`): make ONE more call, `factory.ps1 merge --item N --pr P`. Outcomes: `merged` (stop; the gateway settles the rest), `demoted` (back to Development), `parked` (stop). The script refuses without a recorded PASS, and never approves.
   - `next: "approve"` (PASS, no `Auto Merge`, policy `peer-approved`): stop here. The inspector (another session) approves; the owner session then calls `merge`. If `merge` is refused with exit 10 `qc-not-run` or `qc-approval-missing`, the item waits for a person (a QC environment for the head, then their GitHub approval) and for the `qc.approved` event: that is waiting, not parking and not a failure. Never retry in a loop.
   - `next: "parked"`: stop.
   - exit 10 `wrong-column` / 11: stop at once.

Never: modify code or the pull request, add or remove labels, move the item, call `complete`, approve the pull request, or claim a video that was not published.

## Model for each subagent (cost first, automatic)
Before starting any subagent, ask the ladder: `factory.ps1 model-for --role subagent --item N`. It returns `{tier, name, model, effort, reasons}`: pass `model` to the Agent tool. The gateway raises the tier by itself when the item shows a loop (a relaunch, repeated review findings, a demotion, fix-forward rounds, or three or more refused gate calls); a machine's own `models.json` decides which model sits on each rung. Default ladder: haiku 4.5 at xhigh, then sonnet 5.5 at medium, then opus 5.5 at medium. Never choose a model by judgement; never lower a tier. `mode: "fixed"` means the machine pins a model: use it as returned. `mode: "profile"` (the `testing` profile: haiku at low effort, for items labelled `factory test` and for automated test runs) is pinned too and never climbs: use it as returned even if the item loops.

Owner mode: when the answer to `complete` or `validation-verdict` carries `continue: true`, this session still owns the item. Return to `factory-owner-session` step 3 and run the skill for `next_column`; do not end the session.
