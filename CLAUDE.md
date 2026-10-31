# CLAUDE.md

Guidance for an AI session that works in this repository. This repository is the single source of the factory worker gateway: everything
needed to build, test, change and release it is here. Project state, environments, the owner's decisions and the backlog: `docs/HANDOFF.md`
(read it before the first change).

## What this is

A gateway (ASP.NET Core minimal API, .NET 10) that holds the GitHub and Octopus credentials, runs the PowerShell rule scripts of the
plugin (`pwsh factory.ps1 --server <verb>`), sweeps the board of `clearmeasure-aisf-sample-apps/cmdemo1-workorders` and hands work items to
AI sessions (workers) that connect with a prompt from its dashboard. It ships as one container image, deployable `fworker` of the cmdemo1
demo system.

## Layout

| Path | Content |
|---|---|
| `gateway/` | The gateway: `Program.cs` (routes, dashboard, connect prompt), `Plant.cs` (workers, assignments), `Messaging.cs`, `Qc.cs`, `Auth.cs`, `RunnerPool.cs`, `Operator.cs` (the operator session) |
| `src/Fworker.Database/` | The schema of the gateway's database: plain `.sql` scripts run by DbUp at gateway start (`scripts/Update/NNN_*.sql`, `schema-manifest.json`); see "Database" below |
| `plugin/` | What a worker downloads from the gateway (`GET /api/v1/plugin`): `scripts/` (`factory.ps1`, `lib/*`), `skills/`, `agents/`, `tools/shot`, `launcher/` (the session loop, `docs/launcher.md`) |
| `config/instance.json` | The source defaults. The tests run on these: messaging off, supervisor off, `ownerSessions` false |
| `config/deploy.overrides.json` | The settings of the deployed image, merged over the defaults by `config/apply-overrides.ps1` when the image is built |
| `tests/scenarios/` | Tier 1: scenario suites against the fake GitHub (`run.ps1`, `run-all.ps1`, one file per area) |
| `tests/FakeGitHub/` | The fake GitHub the suites talk to |
| `tests/smoke.ps1` | Parses every script and checks the pure rule functions; no network |
| `tests/stress/` | `messaging-stress.ps1`: 10 minute stress of messaging and the supervisor; run by hand, alone |
| `tests/live/`, `tests/fullrun/` | Tier 2 and the fully connected run against the real GitHub and board. They need the operator's machine (see below) |
| `tools/github-app-setup/` | One-page app that creates the test GitHub Apps |
| `docs/design/` | Designs: `plant-manager.md`, `worker-messaging.md`, `pr-qc-environment.md` and their reviews, `operator-session.md` |
| `docs/deploy/` | `gen-connect-prompt.sh`, `activate-prod.sh`, `create-webhook.sh`; `MORNING.md` and `runbook-add-deployable.md` are records of 2026-10-11 |
| `Dockerfile`, `.dockerignore` | The image: `gateway/`, `plugin/`, `config/` only |
| `.github/workflows/` | `build.yml` (image + tests, the required check `Build result`), `tests.yml`, `release.yml`, `secret-scan.yml`, `architecture.yml` |
| `run-gateway.sh`, `run-owner-gateway.sh` | A local gateway against the real GitHub; they need credentials (environment or the maintainer's keyring) |

## Build

Needs the .NET 10 SDK and PowerShell 7.5 or later (the image and the workers run 7.5.4), plus `openssl`, `tar` and `curl`.

```bash
dotnet build tests/FakeGitHub -c Release -nodeReuse:false -p:UseSharedCompilation=false
dotnet build gateway          -c Release -nodeReuse:false -p:UseSharedCompilation=false
```

The image (optional locally; the pull request builds it): `docker build -t fworker --build-arg VERSION=0.0.1 .`

## Test

Build first (above); the suites start the built binaries. Nothing here needs a secret or the network.

```bash
pwsh -NoProfile -File tests/smoke.ps1                                                  # seconds; "all passed"
pwsh -NoProfile -File tests/scenarios/run.ps1 -Suite owner -Tier full -NoBuild         # one suite
pwsh -NoProfile -File tests/scenarios/run.ps1 -Suite messaging -Tier full -NoBuild     # qc, messaging and manager split into 4 groups by themselves
pwsh -NoProfile -File tests/scenarios/run-all.ps1 -Tier fast -NoBuild                  # the curated subset of every suite, 4 suites at a time
pwsh -NoProfile -File tests/scenarios/run-all.ps1 -Tier full -NoBuild                  # everything (about 30 minutes on 4 cores)
pwsh -NoProfile -File tests/scenarios/run.ps1 -Suite auto -Only 'substring of a scenario name' -NoBuild
pwsh -NoProfile -File tests/scenarios/run.ps1 -ListSuites
FACTORY_TEST_SQL='Server=localhost,1433;User Id=sa;Password=...;TrustServerCertificate=True;Encrypt=False' \
  pwsh -NoProfile -File tests/scenarios/run-all.ps1 -Engine v2 -Tier full -NoBuild      # the claims of tests/scenarios/v2-claims.txt on the v2 engine (a SQL Server of your own; a database per process)
```

Rules of the runners:

- One run at a time on a machine. A second `run.ps1` or `run-all.ps1` exits 4: wait and start again. Do not pass `-Force`.
  Wait with `while pgrep -x pwsh -a | grep -q 'tests/scenarios/run'; do sleep 5; done`.
- A run needs 2 GB free on the filesystem of the temp directory and 1.5 GB of memory for full parallelism. Where `/tmp` is a small tmpfs,
  set `TMPDIR` to a directory on disk before building and testing.
- A `pwsh` that ends with exit 134 or 139 is the machine (load, memory), not the product: run the failed suite again, alone.
- Never stop processes by a name pattern (`pkill -f`): the runners stop what they started.
- `run-all.ps1` without `-NoBuild` builds with `--no-restore`: on a fresh clone run the two `dotnet build` commands above first.
- A new suite is appended at the END of `$SuiteDefs` in `tests/scenarios/run.ps1` (the position is its port slot) and added to a job of
  `.github/workflows/tests.yml` (the workflow fails when one is missing). A scenario tagged ` [full]` in its name is left out of the fast tier.
  A suite's groups listen on `10000 + 1000 * slot + 100 * group` and up, and the jobs move the machine's ephemeral port range to 50000-60999 so that an
  outgoing connection cannot hold such a port: the step "Ephemeral ports above the test ports" fails when the last slot reaches 50000 (slot 40).
- The v2 engine has a second Tier 1, the matrix jobs `Tier 1 (v2 ...)` of `tests.yml`: they run the suites and scenarios named in `tests/scenarios/v2-claims.txt` again with
  `run-all.ps1 -Engine v2` (`run.ps1 -Engine v2 -V2Profile`; `FACTORY_ENVIRONMENT` v2ci, or the suite's own name where it sets one (tdd for copilot, promote, envscope), or unset as in v1 (tdd) for owner, messaging, cursor, cursor-deadline and runtime,
  `engine.v2Environments` naming it, `FACTORY_DB_CONNECTION` a database of its own on the SQL Server of `FACTORY_TEST_SQL`; a machine without one exits 2). A claim names its profile:
  `saga-docs` and `saga-feature` turn the engine's intake on, so that the Todo items of the suite `saga` start sagas. The entries of the matrix name their suites after `claims:` (not `suites:`:
  they are not shards), and `python3 tests/ci/run-v2-claims.py tests/scenarios/v2-claims.txt --workflow .github/workflows/tests.yml --require-complete` (run by the coverage job) proves that every claim is in exactly one of them and that every suite of `run.ps1 -ListSuites` is claimed whole or declared `v1-only`; a scenario of a claimed suite that tests v1's behaviour is declared `v1-only-scenarios <suite> | <regex> | <reason>` (run.ps1 -Engine v2 neither lists nor runs it).
  Where v2 writes differently on purpose, `tests/scenarios/golden/v2-differences.txt` says so. `Tests result` reports the jobs and does not fail on them until `V2_BLOCKING` there is set to true
  (after the first green run of `Tests` on master). The job `Image starts without a database` runs the built image with no `FACTORY_DB_CONNECTION`. The unit tests that need a SQL Server are `[SqlFact]`:
  without `FACTORY_TEST_SQL` a run reports them as skipped.
- `run-all.ps1` rewrites `tests/scenarios/suite-times.json` (and a full run `scenario-stats.json`): commit them only when the timings
  really changed.

The pull request runs the smoke test and every suite at the full tier (`tests.yml`: about 50 Tier 1 shard jobs, 4 shards of the gateway tests, the src tests with the SQL-backed Wolverine tests, and the coverage proof; about 8 minutes wall clock). The stress test is not part of it:
`pwsh -NoProfile -File tests/stress/messaging-stress.ps1 -NoBuild` (10 minutes, alone on the machine).

## Deployed settings

`config/instance.json` holds the defaults and is what the tests read. The image gets `config/deploy.overrides.json` merged over it
(`Dockerfile`, `config/apply-overrides.ps1`): owner sessions on, merge queue depth 2, messaging and the supervisor on for every registered
worker. To change what a deployed gateway runs with, change the overrides file in a pull request; an override that names no existing
setting fails the image build. To see the effective configuration without docker:
`pwsh -NoProfile -File config/apply-overrides.ps1 -Base config/instance.json -Overrides config/deploy.overrides.json -Out /tmp/effective.json`.
The same image is promoted to uat and prod, so the settings travel with it. Runtimes are the exception: `runtimes.environments.<name>`
holds one whole runtimes block per environment, the gateway uses the entry named by `FACTORY_ENVIRONMENT`, and an environment without an
entry has every runtime off. Only tdd has one today.

## Database

The schema of the gateway's database (tdd and the v2 CI gateways; uat and prod have none) is built by DbUp from plain `.sql` files, numbered and journaled the way
cmdemo1-workorders does it (journal `dbo.SchemaVersions`). Design: `docs/design/fworker-database.md`. EF Core only maps the tables (`FactoryDbContext`); it creates and migrates nothing.
Wolverine builds and diffs none of its tables (`AutoCreate.None`); script 011 holds them.

- **Where.** `src/Fworker.Database/scripts/Update/NNN_PascalDescription.sql`, embedded in the assembly. The gateway applies them at start (`WolverineHostService`), under an
  application lock, before the Wolverine host, and then compares the catalog with `schema-manifest.json`. `/ready` is 503 `waitingFor: schema` until that check passes;
  `/api/v1/status` `schema` and `/_build` `schemaVersion` say where it stands.
- **Add a script.** The next free number (unique, no gaps); every CREATE and ALTER guarded (`IF OBJECT_ID(...) IS NULL`, `IF NOT EXISTS (... sys.indexes ...)`, `IF COL_LENGTH(...) IS NULL`),
  no DROP, TRUNCATE, DELETE or UPDATE, the TRY/CATCH transaction of the other scripts, UTF-8 without BOM. Then rewrite the manifest:
  `FACTORY_TEST_SQL=... FWORKER_UPDATE_SCHEMA_MANIFEST=1 dotnet test tests/Fworker.Workflow.Tests --filter TheManifestIsWhatTheScriptsBuild`. A change of `FactoryDbContext` needs a script
  (`SchemaMatchesEfModelTests` fails until it matches). `DatabaseScriptsTests` lints the files without a database.
- **Generated files.** Script `011_CreateWolverineMessageStore.sql` and `tests/Gateway.Tests/golden/wolverine-storage.sql` are written by
  `FWORKER_WRITE_WOLVERINE_SQL=1 dotnet test tests/Gateway.Tests --filter TheExportMatchesTheGolden`; never edit them by hand. A Wolverine upgrade that changes the DDL adds a delta script
  (`FWORKER_WRITE_WOLVERINE_DELTA=<file> dotnet test tests/Gateway.Tests --filter WriteWolverineDelta`; guard its ALTERs by hand; rewrite the golden and the manifest; steps in the design).
- **Expand, then contract.** Every script is expand-only (add tables, nullable or defaulted columns, indexes): the previous gateway process still runs during the lease hand-over. A script that
  drops something ships only after no running image uses it, and needs the owner's word. `__EFMigrationsHistory` on tdd stays until then (a revert of the pull request needs it).
- **Tests need SQL Server.** The SQL-backed tests run where `FACTORY_TEST_SQL` is set (a local container: `docker run -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD=... -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest`);
  without it they return at once. `tests/scenarios/run.ps1 -Engine v2` sets `FACTORY_DB_CREATE=1` so the gateway creates its per-process database; nothing deployed ever sets it.

## Release

1. Branch `{user}/{description}` from `master`; never push to `master`.
2. Change, build, run the smoke test and the suites of the area you touched.
3. Review before every commit: a fresh subagent (Opus 5.5, low effort) reads `git status` and `git diff --cached` only and approves or
   lists blockers; commit on approval.
4. Push and open a pull request. Required checks: `Build result` (the image and, through `tests.yml`, every suite) and `secret-scan`
   (gitleaks over the history).
5. When every check is green on the head commit: `gh pr merge N --squash --match-head-commit <full sha>`. Pull requests of this
   repository merge without a GitHub review once green (owner decision); the review of step 3 is the review.
6. The merge is the release to tdd: Build of `master` (image only), then `release.yml` pushes that image to the registry and creates the
   Octopus release of project `cmdemo1-fworker`, whose lifecycle deploys it to tdd. About 4 minutes from merge to live.
   **A release is done only when both are true**: `curl -s <tdd url>/_build` shows the new version (`1.0.<Build run number>`,
   `pluginHash`) AND `curl -s -o /dev/null -w '%{http_code}' <tdd url>/ready` answers 200 (it also means the database schema is good; `/api/v1/status` `schema` lists the scripts this start applied and `differences` is empty). `/health` 200 is not enough: on 2026-10-13 a
   release was live and never ready for 49 minutes. A 503 of `/ready` says what the start waits for
   (`{"ready":false,"waitingFor":"revocations","attempts":N,"since":"..."}`); if it has not turned 200 a few minutes after the
   deployment, treat it as an outage: find the cause or roll back.
7. uat and prod are manual promotions by the operator (Octopus sign-off, the demo-environment kit). A session does not promote to them.

A gateway restart drops the in-memory state (below) except the plant: the process that holds the leader lease (`gateway/Leader.cs`) snapshots it
to GitHub on every change and at its stop, and hands it over to the next process (which waits for that snapshot before its first sweep, and
renews the owners' claims that ran out while no gateway was up). Deploy to tdd only when the plant is quiet, say so beforehand, and check
afterwards that the workers reconnected (`/api/v1/status`: `leader.role` acting, `plantSnapshot.restore.how`). Roll back by reverting the pull request.

## Rules

- **Never commit a secret.** The repository is public. No token, key, webhook secret, connect prompt (it contains a credential) or raw log
  of a live run. Test fixtures that look like credentials are composed at run time (`'ghp_' + ('A' * 26)`) so the secret scan stays
  meaningful; do not add an allowlist for them. Scan before pushing: `gitleaks git . --redact --no-banner --log-opts=HEAD`.
- **Credentials are not here.** The operator token of a gateway, the Octopus key and the GitHub App keys live in the owner's keyring and
  in the environments' vaults. A session gets what it needs as environment secrets (names in `docs/HANDOFF.md`). Never print one.
- **Tests green before merge**; a red suite is investigated, not retried until green (except a pwsh crash, see above).
  A check whose failure has an open backlog entry and no known cause may be a `CheckQuarantined` (tests/scenarios/lib.ps1): it reports
  and is counted as a quarantined failure without failing the suite. Read that count in every summary.
- **The fake GitHub behaves like GitHub.** Where the product depends on how an endpoint pages, orders, limits or refuses, the default of
  `tests/FakeGitHub` is what GitHub really does; another behaviour is a test control a scenario switches on.
- **Messaging state is in memory.** Messages, questions, presence, the worker registry and the supervisor slot live in the gateway process;
  a restart loses them (senders are told what was lost, workers register again). The durable truth is the board, the item state comments
  and the revocation refs in the work-orders repository. Do not design a feature that assumes a message survives a restart.
- **The plugin assumes nothing about the worker's machine**: any OS, x64 or arm64, only what the prompt downloads or clones; no fixed
  paths, no preinstalled `gh`, `jq`, `python` or `dotnet`, no shell variables in commands of skills and agents. The author suite checks this.
- **Only one gateway may sweep the board.** The leader lease enforces it: the newest process with a GitHub identity takes the lease and the
  one before stands down. So do not start `run-gateway.sh` or `run-owner-gateway.sh` against the real repository while tdd is the acting
  gateway: it would take the plant over from tdd (`FACTORY_LEADER_LEASE=0` turns the lease off for a local gateway, and then two would sweep).
  Where a database is configured (tdd, the v2 CI gateways) the process must also hold the SQL application lock `fworker:leader`
  (`gateway/Leadership/SqlLease.cs`, row `factory.Leadership`, status `leader.sql`): the Wolverine node runs only on its holder, and where
  the engine acts so does everything else. A process without a database (v1 under the kill switch, uat, prod) is arbitrated by the ref alone.
- **Protected areas need the owner**: credentials and tokens, authorization, revoke, the deploy pipeline (`.github/workflows/`,
  `Dockerfile`, `config/deploy.overrides.json`), uat and prod. `build.yml`, `release.yml`, `secret-scan.yml` and `architecture.yml` come
  from the demo-environment kit: the only local addition is the `tests` job of `build.yml`.
- Code style: follow the file you are in (long lines, comments that say why). Documentation is plain and exact; no emojis.

## What needs the operator's machine

`tests/live/run.ps1`, `tests/fullrun/*`, `docs/deploy/activate-prod.sh`, `docs/deploy/create-webhook.sh` and
`tools/github-app-setup/serve.ps1` use the keyring (`secret-tool`), a signed-in `gh`, a sandbox clone of cmdemo1-workorders
(`FACTORY_SANDBOX_REPO`, default `~/Projects/factory-worker-sandbox/cmdemo1-workorders`) and, for promotions, the demo environment kit
(`FACTORY_KIT_DIR` and `FACTORY_KIT_USER`). They are kept for the operator; a cloud session does not need them for build, test or release.
