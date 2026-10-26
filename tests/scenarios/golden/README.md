# Golden writes

The baseline of what each Tier 1 scenario writes to GitHub (and to the fake Octopus, ARM, Cursor and Copilot APIs). During the stage C
refactoring of the gateway (docs/design/gateway-composition.md), a change that is meant to be behaviour-neutral must leave every scenario's
writes as they are; a difference fails the scenario.

## Layout

`<suite>/<slug>.writes.txt`, one file per scenario. `<suite>` is the suite's name in `tests/scenarios/run.ps1`; `<slug>` is
`Get-ScenarioSlug` of `tests/scenarios/lib.ps1`: the scenario's name without its ` [full]` tag (so a change of tier keeps the file),
lowercase, every run of other characters one `-`, at most 80 characters, then `-` and the first 8 hex digits of the SHA-256 of that name.

A scenario without a file here is recorded only, never compared. The baseline is made from two runs or more of the same product code
(below): a scenario whose caused writes (below) differ between the runs gets no file.

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

`Run-Scenario` calls `/_fake/scenario-begin` right after `/_fake/reset` and `/_fake/scenario-end` after the body, once `Wait-RecordSettled`
has let the gateway's tail work go quiet: it closes the record once a sweep has finished since the body (the gateway's `lastSweep` moved;
not waited for when the gateway is down or never swept) and `/_fake/quiet` says no compared write has come for 2.2 s (the timing writes do
not count), at most 5 s after the body in all. So a tail write the scenario set off lands in the record however fast the runner was. The
end writes
`<FAKE_RECORD_DIR>/<suite>/<slug>.writes.txt` (normalized, compared) and `<slug>.raw.jsonl` (every write as sent, for reading a
difference). `GET /_fake/writes` shows the open record (each write with its `cause`, the request's `X-Fake-Cause` or null, and the
scenario's `nonce`), and `unlabelledWrites`: the writes
made while no scenario was open, counted (they are in no record, but the group check below reads them; `/_fake/state` is unchanged by the
recorder).

### Caused and background writes

Every compared write is either caused by the scenario or background. Which one is decided by where the write came from, never by when it
came:

- Cause ids: `/_fake/scenario-begin` makes a nonce for the scenario (8 random lowercase hex digits) and answers it as `cause`; the harness
  keeps it (`$script:CauseNonce` of lib.ps1) until the body ends. While the record is open, `F` (lib.ps1, the scripted worker's call of the
  real client) gives each call the id `<nonce>.<n>`, n counted from 1 in the scenario, and passes it to the client as the environment
  variable `FACTORY_TEST_CAUSE` of that call only (the runner host's call environment, or set around the spawned `pwsh` and removed right
  after it; the harness's own process never keeps it, and a gateway the harness starts never has it).
- The client (`plugin/scripts/factory.ps1`, client mode) reads `FACTORY_TEST_CAUSE` once, removes it from its process environment (so a
  session, a spawned worker or another client it starts does not inherit it; the `--max-chars` child gets it back explicitly, since it runs
  the verb) and sends it as the header `X-Fake-Cause` with every request of the call to the gateway. Without the variable it sends no such
  header and nothing else changes. The thread jobs and long-lived stream loops of `subscribe` carry no id.
- The gateway, only when it runs with `FACTORY_TEST_CAUSE_HEADER=1` (`Start-TestEnv` sets it when the golden mode is not `off`; every
  gateway a suite starts inherits it), holds a request's `X-Fake-Cause` in a request-scoped holder while it serves that request
  (`gateway/TestCause.cs`: an `AsyncLocal` box, set by a middleware and cleared when the request ends). Every rule-script run it starts
  meanwhile, pooled or spawned (`Runner.RunProcessAsync`), gets the id as the field `_cause` of its JSON arguments, when the id is 1 to 80
  characters of `[A-Za-z0-9._-]`. Without the variable the gateway reads no header and adds nothing.
- The rule script (`factory.ps1 --server`), with `FACTORY_TEST_CAUSE_HEADER=1`, takes `_cause` out of its arguments before the verb runs
  and sends it as `X-Fake-Cause` with every write: the GitHub calls of `Invoke-GhRaw` (lib/Core.ps1, REST and GraphQL), the release asset
  upload of `publish-demo` (lib/Verbs.Pr.ps1) and the Octopus calls of `Invoke-OctoPost` (lib/Verbs.Promote.ps1). Without the variable the
  key is left as it is.
- The fake keeps each write's `X-Fake-Cause` and counts the write as carrying the scenario's id when the id is the open scenario's nonce or
  starts with the nonce and `.`, decided when the write comes; an id of an earlier scenario never counts.

So the writes of a call are those of the verb's own rule-script run and of every rule-script run the gateway starts while serving that
request, however late in the call they come; a write of the sweep, the plant's ticks or any other loop carries no id, even when it comes
while a call runs. Work that outlives the request (a task the request set off that starts a rule script after the answer has gone) carries
no id: its writes are background.

Not tagged (`$script:CauseSkipVerbs`): the client verbs that only wait or stream (`subscribe`, `wait-event`, `wait-for-checks`), whose
long requests would tag whatever the gateway starts while they wait, and those that only read (`inbox`, `message-status`, `presence`,
`get-item`, `peers`, `model-for`, `model-tier`, `questions`, `qc-list`, `pr-checks`, `inspect-facts`, `plant-digest`), which scenarios
poll; nor the role verbs `operator`, `keeper` and `supervisor` with the actions `status`, `metrics`, `act-log` and `release-check`. They
carry no id, so a write one of them sets off is background.

- Caused (`= `): every harness write, and a `rule` write whose request carried the scenario's id and is not of a background kind.
- Background (`+ ` settled, `~ ` tail, below): every other write: one that carried no id of the scenario (the sweep, the plant's ticks,
  the base-update, transcript and release loops, the runtimes, and whatever the scenario's own direct requests to the gateway set off:
  only `F` tags its calls), and one of a background kind whatever it carried. The background kinds (`WriteRecorder.BackgroundKinds`):
  `plant` (a body with `<!-- plant event=`), `state` (a factory-state comment), `merge-watch` (a journal comment, `<!-- factory-journal:`,
  of the sweep's merge watch in `plugin/scripts/lib/Verbs.Delivery.ps1`: "master is GREEN at", "master checks failed at", "master is
  BROKEN", "release candidate **V** carries", "no release candidate" (also "published no release candidate"), "Delivered to **Prod**"),
  `claims`, `transcripts`, `snapshot` (`plant-snapshot`), `leader` (`gateway-leader`) and `revoked` (a path or `ref` with `/claims/`,
  `/transcripts/`, `plant-snapshot`, `gateway-leader`, `/revoked/`; a git commit whose message is a sealed transcript `fwt1:...`, a plant
  snapshot or the leader lease counts as its ref's kind), and `gateway` (every write of the gateway's own C# clients: the Cursor, Copilot,
  ARM and Octopus runtimes, which send no id).

The background kinds stay background because what the gateway's own loops write can carry a call's id too: a plant tick, a snapshot or a
state rewrite that a verb's request sets off runs while that request is served, and the merge watch's lines come whenever a sweep next
looks at a merged item, so whether they carry an id follows the timing. The limit that remains: a rule-script run that a fire-and-forget
task starts while the request is still being served carries the id (the task holds the request's holder until the request ends), so its
writes of another kind count as caused. When that happens in one run and not in another, the scenario's caused writes differ between the
runs, and the baseline gets no file for it (below).

### Settled and tail lines

A background line is settled when its first write in the scenario came at or before the scenario's last caused write (the largest `ms`
of a write of class `cause`), and tail otherwise; when the scenario caused no write, every background line is tail. A line written more
than once counts by its first write, so a line settled by one write and tail by another is settled. A `STATE` line (below) stands for
every accepted background write of its (source, state comment): its first write is the earliest of those. A settled line was written
while the scenario was still acting, so it lands in the scenario's own record in every run; a tail line is written after the scenario's
last action (a late promotion sign-off, a plant `launched` or `offered` line, a claim's DELETE, "master is GREEN", a QC comment rewrite),
and whether it comes before the record closes is timing.

### Tail lines: the group check

A tail line need not land in its own scenario's record, but it must be written by the end of the fake process (one suite's run, or one
group's), after a final settle. The fake keeps every write of the process, also those made while no scenario was open, and for each
closed scenario its normalizer (its counters and deployment ranks), its own lines and where the process's writes stood at its end.

`Assert-NoServerErrors` (lib.ps1, at the end of every suite or group process) calls `Assert-GoldenTails`: `Wait-RecordSettled`, then
`POST /_fake/group-check {"goldenDir": ...}` (the golden directory in compare mode, else empty). For every closed scenario the fake
normalizes every later write of the process against that scenario, exactly as a write of the scenario itself (its items stay `{#k}`; a
factory-state write gives its `STATE` line when accepted, its marker line when refused), and keeps the lines that name one of the
scenario's items or pull requests (`{#k}`, `{#prk}`); a later write that is leftover or timing relative to the scenario, or whose text
holds none of the scenario's numbers, is skipped. It writes them as `~ ` lines to `<FAKE_RECORD_DIR>/<suite>/<slug>.later.txt`. Where
the scenario has a golden file, every `~ ` line of it must be in the scenario's own record (any section) or among those later lines:
each line missing is a quarantined miss (Comparison, below), `golden tail line written by no write of the group: <scenario>` (one
passing check counts the scenarios checked when none misses). A tail line that names no item or pull request of the scenario (an
Octopus sign-off without one) can only be found in the scenario's own record.

What the later lines can and cannot catch: the next scenario begins with `/_fake/reset`, which removes the issues, pull requests, refs and
releases of the scenario before. A tail write that misses its own record therefore usually comes after that reset: a write to a removed
item is refused (its line has another status, `404`, and does not match the golden's), and often the gateway does not make it at all,
because it finds the item gone. So in practice a scenario's `~ ` lines are found in its own record (which `Wait-RecordSettled` makes the
common case) or, for the last scenario of a process, among its later lines. Two smaller limits: deployment ranks and the pull-request
names of a `pr` key come from the scenario's own writes, so a deployment or pull request named only by a later write stays raw
(`Deployments-NNN`, `{#old}`) in the later lines and does not match; and when the process wrote more than the fake keeps (400,000 writes),
the check `golden tail writes: the fake kept every write of the process` fails and says how many were not kept.

`raw.jsonl` keeps for each write `cause` (the request's `X-Fake-Cause`, or null), `kind` (a background kind, `commit` or `verb`) and `class` (`cause`,
`background`, `leftover` or `timing`).

## Format and normalization

Header lines start with `# ` and are never compared: `suite`, `scenario`, `runs` (golden files); `writes` (the writes compared),
`leftover`, `timing` (the writes left out, below), `bases` and `ends` (the fake's counters at the scenario's start and at its end,
`number=` of `bases` being the first item number the scenario can create; for reading a record offline), `normalize-errors` (below,
only when there was one), `calls` (how many distinct cause ids of the scenario caused a write of the record), `cause`, `background` and `tail` (the number of `= `, `+ ` and
`~ ` lines) in records. Every other line is one write in one of three sections:

```
= <line>      a caused write: every one, in the order the fake answered them
+ <line>      a settled background line: the distinct lines, sorted (ordinal)
~ <line>      a tail background line: the distinct lines, sorted (ordinal)
```

A `<line>` is

```
<source> <METHOD> <normalized path>[ <graphql mutation field>] <status> <normalized body as compact JSON, or ->
```

or, for an accepted write (status below 300) of an item's factory-state comment, `<source> STATE <kind> #{#k}` without the body (below).
The `= ` lines come first, then the `+ ` lines, then the `~ ` lines. In a golden file, `+ ` lines must be in the scenario's own record
and `~ ` lines by the end of the group (Comparison). A golden file never has a line of another prefix (`? ` and unprefixed lines are
of older formats).

A path first has `/repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders` replaced by `/repos/{repo}` and review, check, job, run,
artifact, release and asset ids replaced by `{id}`; its query string is dropped (except `name=` of an upload). Then the path and every JSON
string value, recursively, are normalized in this order:

1. A fenced `json` block inside a string (the factory-state comment) is parsed and normalized as JSON; its top-level `journal` (the state's
   copy of the journal: counts, folds, keys, which follow the plant's timing) is removed.
2. Tokens become `{token}`: `w1.`, `r1.`, `b1.`, `i1.`, `n1.` (nonces), `c1.` (connect codes), `eyJ...`, `fake-app-...`, `oat-...`,
   `API-...`. Session links become `{session-link}`; Cursor and Copilot agent URLs `{agent-url}`; `caj-test-xxxxxx` `{exec}`; `task-`,
   `bc-`, `run-` ids `{task}`, `{agent}`, `{run}`; `ServerTasks-n`, `RunbookRuns-n`, `Interruptions-n` `ServerTasks-{n}`,
   `RunbookRuns-{n}`, `Interruptions-{n}`; session names `cae-`, `oct-`, `cur-`, `cop-` (with an optional `mgr-` and item number before
   the 6 hex digits) `<prefix>-{session}`; `fwt1:<base64>` (sealed transcripts) `fwt1:{blob}`; assignment ids `a-xxxxx` `a-{id}`; review
   refs `rv-xxxxxxxxxx` `rv-{sha}`; a hex run of 7 to 40 characters in backticks `` `{sha}` ``. A gateway's lease holder
   `<host>:<pid>:<6 hex>` becomes `{host}:{n}:{hex}`, and that host name (the runner's) anywhere in the record `{host}`. A deployment
   `Deployments-[P]n` becomes `Deployments-{dep:k}`, k its rank among the deployments the scenario's compared writes name (not the
   leftover and timing writes below; the lowest `{dep:1}`;
   one numbered before the fake's octo counter at the start `{dep:old}`): that counter also counts runbook runs and releases.
3. Issue and pull request numbers are the scenario's own, never where the fake's counters stood: the fake passes its counters at the
   scenario's start (`bases`) and at its end (`ends`). A number the fake's item counter handed out between the two is `{#k}`, k counted
   from the start (the first one the scenario creates is `{#1}`); a pull request the fake seeded in the scenario (`/_fake/merged-pr`,
   its own pr counter) is `{#prk}`. In an early scenario of a fresh fake both can hold the same number (item 1 and seeded pull request
   1): where only a pull request stands (`/pulls/n`, `/pull/n`, `PR_n`, `PR #n`, and the `pr` and `pull_number` keys) the seeded one is
   taken first, anywhere else the scenario's item. Any other number is not the scenario's: `{#far}` when 1000 or more above the start
   (a fixed number of the harness), else `{#old}` (created before the scenario, or a fixed small number of the harness, such as the
   `pr = 7` of promote.ps1). Under a pull request's key (`pr`, `pull_number`) a number of the scenario's range is `{#k}` only when the
   scenario's writes name it as a pull request (`/pulls/n`, `/pull/n`, `PR_n`, `PR #n`), so a fixed number there is never taken for the
   scenario's item that happens to have it. So the same scenario gives the same numbers in any grouping of the scenarios into jobs.
   This applies in `/issues/n`, `/pulls/n`, `/pull/n`, `/claims/n`, `/qc/n`, `factory-worker/n-...`, `I_n`, `PR_n`, `card_n`, `#n` (not
   `}#n`, a session's ordinal after `{task}`), after `Trial `, `Back-merge `, `copilot/<words>-` (`copilot/thing-7`,
   `copilot/thing-fix-7`, `copilot/par2-7`), `demo-`, `--item `, `--pr `, `pr=`, `K-` (the keeper's decision ids `K-<item>-<n>`),
   `"item":`, `"issue":`, `"pr":`, `"number":`, `"pull_number":` (JSON inside a string), in the keys of plant journal lines that name
   one (`<!-- plant event=E key=K -->`, a key without spaces, the part of 1 to 6 digits: of a `qc-...` event the first, `<pr>|<sha>|
   <attempt>`; of `skipped` the second of `qc|<pr>|...`; of `reassigned` the second of `back|<item>|<ticks>`; of `board-added` the key
   `<item>`; every other key's digits, such as the workflow run id of `ci-release-refused`, are masked as below), to JSON numbers named `number`, `item`, `issue`, `pr`, `pull_number`, and to the string values of
   `Fworker.Item` and of the `FWORKER_ITEM` environment entry. Comment ids in `/issues/comments/n` become `{c}`.
4. ISO-8601 dates and times become `{time}`; GUIDs `{guid}`; 40-hex runs `{sha}`; 32-hex runs `{hex32}`; `127.0.0.1:port` and
   `localhost:port` `...:{port}`.
5. Outside `{...}` placeholders: durations (`12 s`, `3.5min`, ...) become `{dur}`; decimals `{n}`; a run of 7 or more lowercase hex characters with
   a digit and a letter, or of exactly 7 digits, `{sha}` (short SHAs, before their digits could be masked); then every remaining run of 4
   or more digits `{n}`. Integers of 1 to 3 digits stay (counts, exit codes, step numbers).
6. Comma lists of check names, whose order varies, are sorted: the list that opens a parenthesis (`(B, A; ...)`, `(B, A)`; one nested
   level of parentheses inside an item is allowed) and the list of `with B, A passing`. JSON arrays named `failed` and
   `failed_checks` are sorted too.

JSON numbers whose key ends in `seconds`, `ms`, `minutes`, `at`, `time`, `epoch`, `since`, `until`, `expires`, `ttl`, `reads`,
`attempt(s)`, `size`, `len`, `length` or `bytes`, or whose key is `id`, become `{n}` (except an `epoch` below 1000: a lease's or a plant
snapshot's generation, kept); so does any other number of 4 or more digits or with a fraction. Object keys are sorted. A string that is a
JSON object (the lease's and the snapshots' commit messages) is normalized as JSON, its keys sorted (its writer's key order is not stable);
a plant snapshot (an object with `assignments` and `owners`) becomes `{plant-snapshot epoch:E final:F}`: which states of the plant a
snapshot captures follows its timing. A normalized string longer than 400 characters becomes `{len:L sha256:XXXXXXXX "first 80
characters…"}`; the hash is of the normalized text, after every rule above (the raw file has the whole text).

A write whose line cannot be built (a regex past its 1 s timeout, an odd body) becomes the line `<source> <METHOD> {normalize-error}
<exception type>`, counted in the header `normalize-errors`: one such write never loses the record.

Writes that are kept in the raw file but not compared:

- Leftover (`"leftover":true`, header `leftover`): a write whose path, or whose factory-state comment, names an item or pull request from
  before the scenario, or one that is not the scenario's (`{#old}`). Such writes come from earlier scenarios' background work and from
  group boundaries that move. A pull request the fake seeded in the scenario is the scenario's (`{#prk}`, compared).
- Timing (`"timing":true`, header `timing`): the plant's journal comments of its sweep observations (`<!-- plant event=matched` and
  `<!-- plant event=waiting`), every fold edit of a repeated journal line (its marker carries `fold=N`), and every rewrite (PATCH) of a
  session's transcript ref (`refs/factory/<instance>/transcripts/...`; the ref's creation and the transcript commits are compared).
  Whether the sweep sees an item in a column before the scenario ends, how often a line repeats and how often the transcript loop flushes
  depend on the sweep's, the plant's and that loop's timing. Every other plant journal line (offered, assigned, launched, reassigned,
  released, qc-..., promoted, ...) is compared, as a background line.

Factory-state comments: the gateway writes an item's state comment once and then rewrites it many times; how many snapshots there are,
and so whether the last one is the comment's POST or a later PATCH, depends on timing. They are background (kind `state`): the accepted
writes (status below 300) of one (source, item) state comment give a single `<source> STATE <kind> #{#k}` background line, without a body: the
last snapshot is not compared either, because where the background loops have got to when the scenario ends is timing too (the raw file
has every snapshot). A refused write (status 300 or more) keeps its own line, with the body `{"body":"{factory-state <kind> #{#k}}"}`.
The harness's own state writes (`Set-ItemState`) are caused: each accepted one is its own `= harness STATE <kind> #{#k}` line (how many
it makes is the scenario's; the state it edits is the gateway's, so its body is not compared), a refused one its marker line.

## Comparison

`Compare-GoldenWrites` (lib.ps1), after each scenario, when `tests/scenarios/golden/<suite>/<slug>.writes.txt` exists
(`Compare-WritesFiles` does the comparison). The scenario passes the check `golden writes unchanged` when:

1. Caused writes: the record's `= ` lines are the golden file's exactly, as sequences per object. A line's object is its first
   `{#k}` or `{#prk}` (an item or pull request of the scenario), `*` for a line that names none; for every object, the record's lines of
   that object must be the golden's lines of that object, the same lines in the same order and as many. How the writes on two objects
   interleave is not compared (a verb on `{#1}` and an answer on its pull request `{#2}` can race).
2. Settled background writes: every `+ ` line of the golden file is a `+ ` or `~ ` line of the record. Background lines the golden file
   does not hold are no difference: what else the gateway's loops wrote before the scenario ended is timing.
3. Both files hold only `= `, `+ ` and `~ ` lines besides the headers (a golden file of an older format fails the check: record again).

The golden's `~ ` lines are not checked here but at the end of the group (Tail lines, above).

A difference of 1 or 3 fails the check, the scenario and the suite; it prints the first object whose caused writes differ (the position,
up to 2 lines before it, the golden's and the record's line there, `(no more writes)` where one side ends), then the paths of the golden
file, the record and the raw file.

A miss of 2, or of a tail line at the end of the group, is a quarantined check (`CheckQuarantined` of lib.ps1, issue #386): each missing
line is its own check, `golden settled background line missing from the record: <scenario>` or `golden tail line written by no write of
the group: <scenario>`, printed as `QUARANTINED (failed)` with the line (`+ ` or `~ `, normalized) and the golden file's path, counted in
the quarantined total of the suite summary, run-all and the "Tests result" summary, and it does not fail the suite. Whether a background
line lands in time is the timing of the gateway's loops; a missing caused write never is. Every miss is also appended as
`<+ or ~> <suite>/<slug> <line>` to `golden-misses.txt` in the records' directory, so it is in the job's `writes-<job>-<attempt>` artifact.

The repeat rule, for a stage C pull request: when a run has a quarantined golden miss, re-run that shard once (`gh run rerun <run-id>
--job <job-id>`). A line missing again in the same scenario on the re-run is a regression that blocks the pull request; a line that
was written on the re-run was timing. The check is a reviewer step: a re-run hides the earlier attempt's artifacts from the run's
artifact list, so download the shard's `writes-<job>-<attempt>` artifact before the re-run, then the new attempt's, and compare their
`golden-misses.txt` files: a line in both is the regression (`comm -12 <(sort a/golden-misses.txt) <(sort b/golden-misses.txt)`).

Modes, from `TEST_GOLDEN`:

| `TEST_GOLDEN` | Records | Compares |
|---|---|---|
| unset or `compare` (CI) | yes | where a golden file exists |
| `record` | yes | never |
| `off` | no (`FAKE_RECORD_DIR` and `FACTORY_TEST_CAUSE_HEADER` are not set; no cause ids) | never |

Records go to `TEST_WRITES_DIR` when it is set (CI: `$RUNNER_TEMP/writes`), else to `writes/` in the suite's work directory.

## Recording a new baseline from CI

A baseline is made from two runs or more of the same code. For each scenario: its caused writes must be the same in every run (compared
as above); then the golden file gets them in the order of the first run; as `+ ` the lines settled in every run (the intersection of
the records' `+ ` sets); and as `~ ` the lines of every run's group set (its record's `+ ` and `~ ` lines and its `<slug>.later.txt`
lines; the intersection) that are neither `+ ` nor `= ` lines. A line that is settled in some runs and tail in others is then a `~ `
line, guarded at the end of the group; a background line that is missing from some run's group set is not guarded at all. A
scenario whose caused writes differ between the runs is reported unstable and gets no golden file (an existing one in the output
directory is removed), so it is recorded only until it is made stable. The numbers do not depend on how the scenarios are grouped into
jobs (above), so runs of different groupings can be merged.

1. Push the change. Every Tier 1 job uploads its records (`*.writes.txt`, `*.raw.jsonl`, `*.later.txt`) as the artifact
   `writes-<job>-<attempt>` (always, passed or failed).
2. Re-run all jobs once for a second attempt: `gh run rerun <run-id>`.
3. Download both attempts, one directory per attempt:
   ```
   gh run download <run-id> --pattern 'writes-*-1' --dir /tmp/writes-1
   gh run download <run-id> --pattern 'writes-*-2' --dir /tmp/writes-2
   ```
4. Merge them into this directory with the fake's command (`tests/FakeGitHub/GoldenMerge.cs`; build the fake first). It searches each
   directory recursively; a scenario missing from a run is reported and left out; more runs are more directories, comma-separated or in
   more `--from`:
   ```
   dotnet tests/FakeGitHub/bin/Release/net10.0/FakeGitHub.dll merge-golden --from /tmp/writes-1,/tmp/writes-2 --out tests/scenarios/golden
   ```
   `Merge-GoldenWrites -From ... -Out ...` of lib.ps1 runs the same command.
   For a full re-baseline, remove the old golden files first (`git rm -q 'tests/scenarios/golden/*/*.writes.txt'`), so that a scenario
   left out or unstable keeps none.
5. Commit the golden files. The next CI run compares, and must be green on unchanged code.

A pull request that changes writes on purpose records again the same way and commits the golden diff for review.

Removing a scenario's golden file makes it record-only again: the escape for a scenario whose caused writes cannot be made stable. The
removal is visible in the pull request's diff.
