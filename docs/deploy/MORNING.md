# Morning pack (written 2026-10-11, overnight)

## Where things stand
- Live 30-item run on the real board: 22 items Done, 2 in Release Queue (waiting for their release), 1 in Development, 5 waiting for a person (see below). Releases up to 2.4.150 went tdd, uat, prod with sign-off.
- Bugs found and fixed in the run: see README "Findings".
- Deployment of the gateway to tdd/uat/prod: NOT done. The permission check denied the first external step (creating the repository `cmdemo1-fworker`, granting its trust and opening the tdd system PR: "Modify Shared Resources"). Nothing was created. The repository content is ready in `~/Projects/cmdemo1-fworker` (staged copy for the operator at `/var/tmp/cmdemo1-fworker-src`, definition at `/var/tmp/fworker-def/definition.json`).

## Decisions needed before deploying (defaults chosen, change any)
1. Name: `fworker` (the kit allows 2 to 10 characters; `factory-worker` is refused).
2. GitHub identity of the cloud gateway: an organization GitHub App (preferred) instead of a stored personal token. The two Apps from the local setup (reviewer, testperson) exist; the gateway also needs an author identity. Until it exists the `github-token` secret has no value to write.
3. Always-on in prod (about $8 to $35 a month, an estimate); tdd and uat scale to zero.

## Commands to run (each is one step; run them yourself with `!` or tell me to retry)
Step 1, repository + trust + tdd system PR (no merge):
```
sudo -n -u aiops -H bash -lc 'cd /home/aiops/demo-environment-kit && pwsh -NoProfile -File .claude/skills/demo-environment/scripts/add-demo-deployable.ps1 -Config /home/aiops/demo-environment-kit/fleet/systems/cmdemo1.json -Name fworker -Container -Source /var/tmp/cmdemo1-fworker-src -Port 8080 -HealthPath /health -Definition /var/tmp/fworker-def/definition.json'
```
Step 2, secrets for tdd (values come from your keyring or a generator, never printed): see runbook section 3 (`docs/deploy/runbook-add-deployable.md`).
Step 3, same command as step 1 with `-Merge`, then the repo's Build and Release deploy to tdd. uat and prod follow the runbook (PRs "fworker in uat", "fworker in prod", then `invoke-demo-promotion.ps1`). The dashboard link is the last PR.

## The 5 items that need a person (real hold rules working as designed)
- #140, #142, #144: held by the factory inspector (repeated demotions), PR approval by a person in `humanApprovers`.
- #106, #127: parked after repeated failed Functional Testing verdicts.

## Running your own AI session against the gateway
See README "Connect a session". Local gateway for this machine (owner mode, new code) is started with `~/Projects/factory-worker-next/run-owner-gateway.sh` (written overnight; not started so it does not sweep the board twice).
