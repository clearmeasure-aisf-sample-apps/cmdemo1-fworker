# Declared differences of v2 (`v2-differences.txt`)

The golden writes (`README.md` of this directory) are the oracle for both engines: v2 must write what v1 writes. Where v2 writes differently **on
purpose**, the difference is a block in `v2-differences.txt`, reviewed in a pull request like a golden file. The file is empty today: v2 has no
handlers yet (W2), so nothing compares. The v2 Tier 1 job and the comparison that reads this file are follow-ups (issue #313 and its sub-issues);
this file fixes the format they read.

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

## How the comparison reads it (for the v2 Tier 1 job)

For each scenario of a suite v2 claims, the golden file is taken, every `-` line of its block is removed and every `+` line added (a `=` caused line
at the position of the `-` line it replaces, an addition after the last `=` line of its object), and the record of the v2 run is compared with the
result by the rules of `README.md` (Comparison). A `-` line that is not in the golden file, or a block for a scenario with no golden file, fails the
check: the file must stay true to the baseline, and is re-checked whenever a baseline is re-recorded.

A difference that v2 makes without a block fails the scenario exactly as a v1 difference does.
