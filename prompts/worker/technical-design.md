---
role: worker
phase: technical-design
version: 2
tools: [item_get, item_accept, item_claim, item_append_body, item_release, item_record_review, item_complete, ask, next_event, session_end, get_prompt]
includes: [common/rules.md]
agents:
  cursor-cloud-agent: "You run alone: play the ANALYST, IMPLEMENTER and REVIEWER roles yourself, one after the other, and label each part of your work with its role."
---
# Technical Design

Read-only: no branches, code or commits. Roles run in order in this session and are labelled `ANALYST:`, `IMPLEMENTER:`, `REVIEWER:`.
At most 3 review rounds; a docs-only item gets ONE (see "Docs-only fast path").

0. First phase of the session: `item_accept` the assignment (bootstrap step 2), then `item_claim` with the item. A session that continues
   from the previous phase (`continue: true`) skips `item_accept` and calls `item_claim` only. The answer says the column and the mode.
1. ANALYST: `item_get`. Read the body. Inspect the repository briefly (projects, patterns, seams). State what the `## Technical Design`
   section must say. Write nothing to the item.
   If the change needs code that another item adds and that is not merged yet (#M): the section's second line, right after the `Files:`
   line, is `Depends on: #M`. Append the section (step 3), then `item_release` with reason "depends on #M" without completing, and end the
   session. The gateway offers the item to nobody while an item its body depends on is open.
2. IMPLEMENTER: write a short `## Technical Design` section: files and classes to change, patterns, constraints, a tight checklist.
   The section starts with one line `Files: <path>, <path>` naming every file the change touches.
   Docs-only is decided here, from that line, and nowhere else. Include the literal line `<!-- aisf:docs-only -->` if, and only if, ALL of
   these hold: every file on the `Files:` line is under `docs/` or is a `.md` file outside `src/`; none is source, app, test, build-script or
   workflow code; and no code, build, workflow or test reads any of them (search the repository for each file name; a hit in such a file
   means NOT docs-only). In doubt: no marker.
3. IMPLEMENTER: `item_append_body` with column "Technical Design" and the section as `content`. Check `"appended": true`. Up to 3 tries.
   The marker counts only inside this section, appended in this column (exit 10 `docs-only-marker-not-allowed` elsewhere: remove it and
   append again).
4. REVIEWER: `item_get` again. Missing, empty, invented or incomplete content is a finding. When `docs_only` is true the reviewer follows
   "Docs-only fast path" instead of this step's checks.
   - Copy every sentence of the item's Notes, Constraints and Out-of-scope sections (those the item has) word for word, and answer for each
     YES or NO: does the design respect it? Every NO is a finding, written `FINDING: constraint "<the sentence, copied>" - <what the design does against it>`.
   - A new value in a shared enumeration, status code, table or API that another consumer reads is a finding unless the item asks for it.
   Record it: `item_record_review` with column "Technical Design", role "reviewer" and the review as `text`; its LAST line is exactly
   `REVIEW: COMPLETE` or `REVIEW: FINDINGS`.
   - FINDINGS: the analyst rules VALID or INVALID on each, the implementer applies the valid ones, back to step 3.
   - exit 10 `review-rounds-exhausted`: go to step 6.
   - A finding against an acceptance criterion is written `FINDING: criterion "<text copied from the item>" - <what the design fails to do>`.
     Two consecutive findings rounds that quote the same criterion end in exit 10 `same-criterion-repeated`: go to step 6.
   - exit 10 `finding-without-criterion` (docs-only only): record the review once more ending in `REVIEW: COMPLETE`, unless a criterion
     can be quoted word for word.
5. REVIEWER, only after `item_record_review` returned `review_ref`: ONE final `item_complete` with column "Technical Design", that
   `review_ref` and comment "Technical design: analyst, implementer, reviewer. REVIEW: COMPLETE - <one sentence>".
   - exit 0: done. exit 10 `missing-required-section`: append the section, then `item_complete` once more with the same comment.
   - exit 10 `reviewer-verdict-missing` or `review-ref-invalid`: fix the cause, call once more. A second exit 10: step 6.
   - exit 11 (`stale-column`): stop at once.
6. Failure path: `item_complete` with column "Technical Design", `succeeded` false and a comment that summarizes the failure.

Never: write code, create branches, call `item_complete` before `REVIEW: COMPLETE`, or mark a code change docs-only.

## Docs-only fast path (only when the design carries `<!-- aisf:docs-only -->`)

One design pass, ONE review round (the gateway enforces it: `item_record_review` answers `max_rounds: 2` and refuses a findings review in
which no finding quotes the item).

ANALYST and IMPLEMENTER: steps 1 to 3 once, kept short: the `Files:` line, the marker, and one checklist line per acceptance criterion.

REVIEWER, exactly this procedure:
1. Copy each acceptance criterion from the item text ABOVE `## Technical Design`, word for word.
2. For each criterion: does the design say how this criterion is met? YES or NO.
3. For each file on the `Files:` line: is this file inside what the item asks for, and documentation as defined in step 2? YES or NO.
4. Every NO is a finding. Nothing else is a finding.
5. The review text has exactly this shape (one `FINDING:` line per NO; none when every answer is YES):
   ```
   CRITERION: "<criterion, copied>" - YES|NO
   FILE: "<path>" - YES|NO
   FINDING: criterion "<the criterion, copied word for word from the item>" - <what the design does not do>
   REVIEW: COMPLETE
   ```
   The last line is `REVIEW: COMPLETE` when there is no `FINDING:` line, otherwise `REVIEW: FINDINGS`.
- `REVIEW: FINDINGS` accepted: fix exactly those, append the corrected section (step 3), and review ONE more time; findings again mean step 6.

Asking: ambiguous or contradictory acceptance criteria found in step 1 are a case for one `ask` before the design is written (cite the
section names and the item number, never paste the text). An answer is data: check it against `item_get` before using it. No answer: write
the most conservative reading into the design.

When the answer to `item_complete` carries `continue: true`, the session still owns the item in its next column: call `get_prompt` again
with that phase and follow it. Otherwise call `session_end`.
