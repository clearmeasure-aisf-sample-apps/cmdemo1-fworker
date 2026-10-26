# Golden writes

The baseline of what each Tier 1 scenario writes to GitHub (and to the fake Octopus, ARM, Cursor and Copilot APIs). During the stage C
refactoring of the gateway (docs/design/gateway-composition.md), a change that is meant to be behaviour-neutral must leave every scenario's
writes as they are; a difference fails the scenario.

## Layout

`<suite>/<slug>.writes.txt`, one file per scenario. `<suite>` is the suite's name in `tests/scenarios/run.ps1`; `<slug>` is
`Get-ScenarioSlug` of `tests/scenarios/lib.ps1`: the scenario's name without its ` [full]` tag (so a change of tier keeps the file),
lowercase, every run of other characters one `-`, at most 80 characters, then `-` and the first 8 hex digits of the SHA-256 of that name.

No baseline is committed yet: until a scenario has a file here it is recorded only, never compared.

## What is recorded

The fake GitHub (`tests/FakeGitHub/WriteRecorder.cs`) records when `FAKE_RECORD_DIR` is set (`Start-TestEnv` sets it); without it the fake
records nothing. Recording never changes an answer of the fake.

- Recorded: every request with method POST, PUT, PATCH or DELETE, and `POST /graphql` whose query starts with `mutation`, with the status
  the fake answered. An upload to `/_uploads/...` is recorded with the body `{"len":N}`; `len` is masked (below), so upload sizes are
  not compared, only that the upload happened.
- Not recorded: `/_fake/*` (the harness's control), GraphQL queries, the credential mints (`POST /app/installations/{id}/access_tokens`,
  `POST /token/v1`: their count depends on token caching), and the fake's TCP forwarder.
- Each write's source: `rule` (User-Agent `factory-worker-gateway`: the rule scripts), `harness` (PowerShell's User-Agent: the scenario
  itself, `Gh-As`, `Set-ItemState`) or `gateway` (no User-Agent: the gateway's own C# clients). Harness writes are kept: they are the
  scenario's inputs.

`Run-Scenario` calls `/_fake/scenario-begin` right after `/_fake/reset` and `/_fake/scenario-end` after the body. The end writes
`<FAKE_RECORD_DIR>/<suite>/<slug>.writes.txt` (normalized, compared) and `<slug>.raw.jsonl` (every write as sent, for reading a
difference). `GET /_fake/writes` shows the open record, and `unlabelledWrites`: the writes made while no scenario was open, counted and
dropped (`/_fake/state` is unchanged by the recorder).

## Format and normalization

Header lines start with `# ` (`suite`, `scenario`, `writes`, `leftover`, `runs`) and are never compared. Every other line is one write:

```
<source> <METHOD> <normalized path>[ <graphql mutation field>] <status> <normalized body as compact JSON, or ->
```

The file holds the sorted, distinct lines: a set. The order of the writes and how often one repeats are not compared, because the sweep,
the plant ticks, the base-update loop and the transcript loop make both depend on timing.

Normalization (applied to the path and to every JSON string, recursively):

- Issue and pull request numbers are relative to the scenario's start: the first one the scenario creates is `{#1}`, one created before it
  `{#old}` (in `/issues/n`, `/pulls/n`, `factory-worker/n-...`, `I_n`, `PR_n`, `card_n`, `#n`, and JSON numbers named `number`, `item`,
  `issue`, `pr`, `pull_number`). Comment ids in `/issues/comments/n` become `{c}`; review, check, job, run, artifact, release and asset ids
  in paths `{id}`.
- `/repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders` becomes `/repos/{repo}`; the query string is dropped (except `name=` of an upload).
- A fenced `json` block inside a string (the factory-state comment) is parsed and normalized as JSON.
- Tokens (`w1.`, `r1.`, `b1.`, `i1.`, `eyJ...`, `fake-app-...`, `oat-...`, `API-...`) become `{token}`; session links `{session-link}`;
  Cursor and Copilot agent URLs `{agent-url}`; `caj-test-xxxxxx` `{exec}`; `task-`, `bc-`, `run-` ids `{task}`, `{agent}`, `{run}`;
  `ServerTasks-n`, `RunbookRuns-n` `ServerTasks-{n}`, `RunbookRuns-{n}`.
- ISO-8601 dates and times become `{time}`; GUIDs `{guid}`; 40-hex runs `{sha}`; 32-hex runs `{hex32}`; `127.0.0.1:port` and
  `localhost:port` `...:{port}`; durations (`12 s`, `3.5min`, ...) `{dur}`; then every remaining run of 4 or more digits and every decimal
  `{n}`. Integers of 1 to 3 digits stay (counts, exit codes, step numbers). Nothing inside a `{...}` placeholder is masked again.
- JSON numbers whose key ends in `seconds`, `ms`, `minutes`, `at`, `time`, `epoch`, `since`, `until`, `expires`, `ttl`, `reads`,
  `attempt(s)`, `size`, `len`, `length` or `bytes`, or whose key is `id`, become `{n}`; so does any other number of 4 or more digits or with
  a fraction. Object keys are sorted.
- A normalized string longer than 400 characters becomes `{len:L sha256:XXXXXXXX "first 80 characters…"}`.
- A write whose path names an item or pull request from before the scenario (`{#old}`) is left out of `.writes.txt` (it is in the raw file
  with `"leftover":true`): such writes come from earlier scenarios' background work and from group boundaries that move. Pull requests
  the fake seeds through `/_fake` (numbered from its own counter, starting at 1) also count as `{#old}`, so writes to them are not
  compared either.

## Comparison

`Compare-GoldenWrites` (lib.ps1), after each scenario, when `tests/scenarios/golden/<suite>/<slug>.writes.txt` exists. A golden line
without a prefix is required; a line starting `? ` is optional. The scenario passes the check `golden writes unchanged` when the record
holds every required line and no line that is neither required nor optional. On a difference the check fails the scenario (and the
suite) and prints up to 10 lines `- missing` / `+ unexpected`, then the paths of the golden file, the record and the raw file.

Modes, from `TEST_GOLDEN`:

| `TEST_GOLDEN` | Records | Compares |
|---|---|---|
| unset or `compare` (CI) | yes | where a golden file exists |
| `record` | yes | never |
| `off` | no (`FAKE_RECORD_DIR` is not set) | never |

Records go to `TEST_WRITES_DIR` when it is set (CI: `$RUNNER_TEMP/writes`), else to `writes/` in the suite's work directory.

## Recording a new baseline from CI

The writes of the same code differ a little between runs (background loops), so a baseline is made from two runs: a line of both is
required, a line of only one optional.

1. Push the change. Every Tier 1 job uploads its records as the artifact `writes-<job>-<attempt>` (always, passed or failed).
2. Re-run all jobs once for a second attempt: `gh run rerun <run-id>`.
3. Download both attempts, one directory per attempt:
   ```
   gh run download <run-id> --pattern 'writes-*-1' --dir /tmp/writes-1
   gh run download <run-id> --pattern 'writes-*-2' --dir /tmp/writes-2
   ```
4. Merge them into this directory (it searches each directory recursively; a scenario missing from a run is reported and left out):
   ```
   pwsh -NoProfile -c ". tests/scenarios/lib.ps1; Merge-GoldenWrites -From /tmp/writes-1,/tmp/writes-2 -Out tests/scenarios/golden"
   ```
5. Commit the golden files. The next CI run compares, and must be green on unchanged code.

A pull request that changes writes on purpose records again the same way and commits the golden diff for review.

Removing a scenario's golden file makes it record-only again: the escape for a scenario whose writes cannot be made stable. The removal
is visible in the pull request's diff.
