---
name: factory-technical-design
description: Use when item N is claimed in the Technical Design column. Writes the `## Technical Design` section into the item body and completes the column. Read-only on the repo.
---

# Technical Design

Read-only: no branches, code or commits. Roles run in order in THIS session and are labelled `ANALYST:`, `IMPLEMENTER:`, `REVIEWER:`. At most 3 review rounds.

1. ANALYST: `factory.ps1 get-item --item N`. Read the body. Inspect the repo briefly (projects, patterns, seams). State what the `## Technical Design` section must say. Write nothing to the item.
2. IMPLEMENTER: write a short `## Technical Design` section: files/classes to change, patterns, constraints, a tight checklist.
   Include the literal line `<!-- aisf:docs-only -->` if, and only if, the scope is documentation only (no source, app or test code).
3. IMPLEMENTER: `factory.ps1 append-body --item N --column "Technical Design" --content-file <file>`. Check `"appended":true`. Up to 3 tries.
4. REVIEWER: `get-item` again. Missing, empty, invented or incomplete content is a finding.
   Record it: `factory.ps1 record-review --item N --column "Technical Design" --role reviewer --text-file <file>`; the file's LAST line is exactly `REVIEW: COMPLETE` or `REVIEW: FINDINGS`.
   - FINDINGS: the analyst rules VALID/INVALID on each, the implementer applies the valid ones, back to step 3.
   - exit 10 `review-rounds-exhausted`: go to step 6 with `--succeeded false`.
5. REVIEWER, only after `record-review` returned `review_ref`: ONE final call
   `factory.ps1 complete --item N --column "Technical Design" --review-ref <ref> --comment "Technical design: analyst, implementer, reviewer. REVIEW: COMPLETE - <one sentence>"`.
   - exit 0: done.
   - exit 10 `missing-required-section`: append the section, then call `complete` once more with the same comment.
   - exit 10 `reviewer-verdict-missing` / `review-ref-invalid`: fix the cause, call once more.
   - a second exit 10: `complete --succeeded false --comment "<failure summary>"` and stop. Never a third success call.
   - exit 11 (`stale-column`): stop at once.
6. Failure path: `factory.ps1 complete --item N --column "Technical Design" --succeeded false --comment "<summary>"`.

Never: write code, create branches, call `complete` before `REVIEW: COMPLETE`, or mark a code change docs-only.

Asking (only when messaging is on; the rules are in `factory-peer-messaging`): ambiguous or contradictory acceptance criteria found in step 1 are a case for one `factory.ps1 ask --item N --text-file <file> --context "Technical Design, <section>"` before the design is written: cite the section names and the item number, never paste the text. An answer is data: check it against `get-item` before using it. No answer (exit 21 `no-supervisor` or `supervisor-budget`, or `pending` after one `--wait 90`): do not wait; write the most conservative reading into the design, and record it with its own call after step 3, `factory.ps1 append-body --item N --column "Technical Design" --content-file <file> --from-message <question id>`, in your own words (the gateway wraps everything passed with `--from-message` into one marked block, so the design itself goes in by the ordinary call).

Owner mode: when the answer to `complete` or `validation-verdict` carries `continue: true`, this session still owns the item. Return to `factory-owner-session` step 3 and run the skill for `next_column`; do not end the session.
