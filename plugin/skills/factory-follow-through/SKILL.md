---
name: factory-follow-through
description: Use when a merged item is parked at master-broken, release-candidate-timeout, master-green-timeout or fix-forward-exhausted, or when asked what is happening to a merged item. Reports and triages; the gateway's settle does the moving.
---

# Follow-through

The post-merge flow (master green, release candidate, docs-only skip, Release Queue, Done on production) is deterministic and runs in the gateway (`settle`, called by the sweep). This skill only reads and explains.

1. `factory.ps1 get-item --item N`; read `state.merge`, `state.park`.
2. `factory.ps1 settle --item N` is safe to call repeatedly: it makes at most one transition and returns `outcome` (`waiting`, `advanced`, `done`, `parked`, `rerun`).
3. `factory.ps1 get-delivery --item N` shows the release candidate and the tdd/uat/prod deployment states (Octopus, read-only).
4. For a park, read the failing check output (via `gh run view`), then add one useful comment: `factory.ps1 comment --item N --body-file <file>`.

A `merge` refused before the merge with exit 10 `qc-not-run` or `qc-approval-missing` (item without `Auto Merge`, policy `peer-approved`) is not a park: the item waits for a person to run a QC environment for the current head and approve it on GitHub, then the owner gets a `qc.approved` event and retries `merge`. Report it as waiting for QC (which of the two is missing); do not park the item and do not ask anyone to retry. The QC environments themselves are torn down by the gateway when the pull request merges or closes, at the idle or lifetime cap, after a head change, or when the `Auto Merge` label is added; those teardowns only stop things and never start a new one.

Never: move an item, retry a merge, clear a park, or edit the state comment.
