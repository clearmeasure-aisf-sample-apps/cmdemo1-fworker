# One work item per session, and the launcher loop

Owner decision of 2026-10-14: a worker session must not carry the context of one work item into the next. When its item ends, the session ends,
and a new Claude session started from the connect prompt takes the next item.

## How it works

| Part | What it does |
|---|---|
| Gateway (`gateway/Plant.cs`) | Setting `plant.oneItemPerSession` (default `true` in `config/instance.json`; `FACTORY_ONE_ITEM_PER_SESSION=1` or `0` overrides it). A session that has accepted a work item (an `own-item`), or has an open offer of one, is offered no other: not while it holds the item, not after the item ended. A pinned item that waits for such a session has the waiting code `session-spent`. Bot jobs (inspector) and QC jobs are not work items and are not limited. |
| Session identity | A stream opened without a cursor (no `Last-Event-ID`) is a new session of that name; a reconnect of the same client sends its cursor and stays the same session. A new session starts with the items the name still owns (it resumes that item and is offered nothing else) and no longer drains. |
| Status | `GET /api/v1/status`: `plant.oneItemPerSession`, and for each worker `session` (a number per name), `sessionStarted`, `sessionItems`, `sessionSpent` and `itemsPerSession` (the last 10 sessions of the name, each with its items). The state is in memory: a gateway restart starts every name again at session 1. |
| Worker skill (`plugin/skills/factory-owner-session`, section "One item per session") | When the item ends (complete, released, demoted or reassigned) the session runs `factory.ps1 end-session --name N --marker <BASE>/work/session-ended`, stops its event stream and ends. It never accepts a second item. |
| `factory.ps1 end-session` | Drains the name at the gateway with the session's own credential and writes the end marker (`FACTORY_SESSION_END_FILE` when a launcher set it, else `--marker`). |
| `session.end` (issue 72) | The gateway's tick sends it on the stream of a session that is spent and has owned nothing (no owned item, no accepted or open offer of its item, no QC or bot job held or offered) for `timing.sessionEndGraceSeconds` (default 90; `FACTORY_TIMING_sessionEndGraceSeconds`), once per session (again after a reconnect). The stream client (`subscribe --name`) then writes `FACTORY_SESSION_END_FILE` itself, so a session whose item was taken back is ended by the launcher even when the model never runs `end-session`. A take-back is seen when the gateway drops the owner (release, complete, or the sweep no longer finding the holder). |
| `session.restart` | The plant manager's `restart-session` (`docs/design/plant-manager.md` 10e) sends it on the stream of a connected session. The stream client writes `FACTORY_SESSION_END_FILE` as for `session.end` (`markerWritten` on the printed line); the launcher ends the session and starts the next one, which is resent the items the name owns and resumes them. Without a launcher the line tells the session to end. |
| Launcher (`plugin/launcher/`) | Starts `claude` with the connect prompt, waits for the end marker, ends that session and starts the next one after a pause. |

Without a launcher (a cloud session, Cursor) the session says it is finished and stops; a person starts the next session from the connect prompt.
The gateway rule holds either way.

Limits: a session's identity is its stream. If the session restarts its stream command (a Monitor that expired) after its item ended and before
`end-session`, the gateway sees a new session and may offer it another item; the skill's rule (never accept a second item) is the guard then, and
the offer lapses to another session after `assignAckSeconds`. `sessionSpent` in the status counts accepted items only, not open offers.

## The launchers

Both do the same thing and need nothing but their shell and `claude` on the PATH. They are in the plugin (`<BASE>/plugin/launcher/` once the
plugin is downloaded) and in this repository.

```bash
# Linux, macOS (bash)
bash <BASE>/plugin/launcher/fworker-loop.sh --dir <BASE> --prompt-file <BASE>/start-prompt.txt -- --model claude-sonnet-5-5 --effort low
```

```powershell
# any system with PowerShell 7 (Windows included)
pwsh -File <BASE>/plugin/launcher/fworker-loop.ps1 -Dir <BASE> -PromptFile <BASE>/start-prompt.txt -ClaudeArgs '--model claude-sonnet-5-5 --effort low'
```

- `<BASE>` is the worker's directory of the connect prompt (`<home>/factory-worker/<name>`); the prompt file holds the connect prompt. The prompt
  contains a credential: keep the file readable by its user only and never commit it.
- Everything after `--` (bash) or in `-ClaudeArgs` goes to `claude` before the prompt (model, effort, permission mode, remote-control name). Do not
  end it with an option that takes several values (`--add-dir`, `--allowed-tools`): it would take the prompt as one. The prompt is a process argument,
  visible to other users of the machine in the process list: run workers on a machine or account of their own.
- Each round: the end marker `<BASE>/work/session-ended` is removed, `FACTORY_SESSION_END_FILE` is set to it, and `claude` starts with the prompt.
  When the marker appears the launcher waits 5 s, ends the session (TERM, then KILL after 10 s) and starts the next after `--backoff` seconds
  (default 10). A session that exits without the marker (a crash, or a person quit it) is started again after a pause that doubles up to
  `--max-backoff` (default 300).
- Stop: create `<BASE>/work/STOP` (or the file of `--stop-file`). A running session finishes its item first; no new one starts. Delete the file
  before starting the launcher again. Ctrl-C stops the launcher during the pause between sessions; while a session runs, the keys go to that session.
- On Windows an npm-installed `claude` is a `.cmd` shim that cannot take a multi-line argument: the PowerShell launcher then gives the session a short
  first message that names the prompt file instead of the prompt itself.

## Tests

`tests/scenarios/owner.ps1`, scenario "owner: one item per session": a session with capacity 5 is offered one item at a time, is offered nothing
after it accepted one and after it released it, `end-session` drains it and writes the marker, and the next registration of the name is a new
session that is offered an item again, with `itemsPerSession` keeping the earlier session's one item. The other suites run with the rule off
(`tests/scenarios/lib.ps1`), because their scripted sessions take many items each.
