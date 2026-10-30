# Declared differences of v2 (`v2-differences.txt`)

The golden writes (`README.md` of this directory) are the oracle for both engines: v2 must write what v1 writes. Where v2 writes differently **on
purpose**, the difference is a block in `v2-differences.txt`, reviewed in a pull request like a golden file. The file holds the declared differences of the claimed scenarios (today: the transcript of the octopus scenarios, a database row on v2). The comparison that reads it is part of the v2 Tier 1 job (`tests.yml`, `run.ps1 -Engine v2`; issue #468);
`tests/scenarios/v2-differences.ps1` has its functions and `lib.ps1` (`Initialize-V2Golden`, `Get-GoldenDir`) uses them.

## Format

Blocks separated by blank lines. A block names one scenario and the lines of its golden file that v2 does not write as they are:

```
@ <suite>/<slug>
- <golden line as it stands in <suite>/<slug>.writes.txt, prefix and all>
+ <the line v2 writes instead, same prefix; absent when v2 writes nothing there>
# why: <one sentence>; issue: #<n>
```

- `@` opens a block. `<suite>/<slug>` is the path of the golden file without `.writes.txt`, so the block is found by the name the golden uses.
- `-` is a line of the golden file (an `= `, `+ ` or `~ ` line: the prefix is part of the line) that v2 does not write. `+` is a line v2 writes that the
  golden does not hold. A change is a `-` followed by a `+` with the same prefix; a removal has a `-` only; an addition a `+` only.
- `# why:` is required: the reason and the issue or design section that decided it. A block without it is invalid.
- Lines starting with `#` other than `# why:` are comments. Blank lines separate blocks.
- Lines are in the normalized form of the golden files (`README.md`, Format and normalization), so `{#1}`, `{time}` and the other placeholders are used as
  they are there.

## How the comparison reads it

Only a run with `-Engine v2` reads the file; a v1 run compares with the golden files as they are. At the start of each suite of such a run
(`Initialize-V2Golden`) the golden directory is copied to a temporary *effective golden directory*, and in the copy every block is applied to the
golden file it names: every `-` line is removed and every `+` line added (a change, a `-` followed by a `+` with the same prefix, takes the place of
the line it replaces; an addition goes after the last line of its own section, which for a `=` line is after the last caused line of its object).
Then the run compares as a v1 run does (`README.md`, Comparison), with the effective files: the caused writes (`=`) of the record against the
effective file, the settled background lines (`+`) in the record, and, at the end of the group, the tail lines (`~`) against the later writes of the
process (the group check reads the effective directory too). With no block the golden directory itself is used and nothing is copied.

So a caused (`=`) line that v2 writes differently and that no block declares fails the scenario exactly as a v1 difference does: the effective
file still holds the golden's line there. Nothing is excused unless a block says so.

The file must stay true to the baseline. The check `v2 declared differences: valid and true to the golden files` (once per suite of a v2 run) and
the smoke test (`tests/smoke.ps1`, on every pull request, v1 too) fail on: a `-` line that is not in the golden file, a block for a scenario with no
golden file, a block without `# why:` or without a `-` or `+` line, a second block for the same scenario, a name that is not `<suite>/<slug>`, a `+`
line equal to its `-` line, and any line that is not understood. All problems are listed at once; a block with a problem is applied to nothing (its
scenario compares with the plain golden file, so it fails there if v2 really differs). Re-recording a baseline therefore also means re-checking this
file: a block whose golden line changed or whose golden file was removed fails until the block is updated or deleted.

A difference that v2 makes without a block fails the scenario exactly as a v1 difference does.
