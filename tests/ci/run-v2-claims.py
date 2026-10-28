#!/usr/bin/env python3
"""Runs the claims of tests/scenarios/v2-claims.txt on the v2 engine (the job "Tier 1 (v2)" of .github/workflows/tests.yml).

usage: run-v2-claims.py <claims file> <out dir> [--pwsh pwsh] [--parse-only]

Each claim is `<suite>` or `<suite> | <regex>` (comment lines start with #). Every claim is first listed (run.ps1 -ListScenarios -Suite s [-Only regex]):
a claim that lists no scenario fails the job, so a renamed suite or scenario cannot leave it empty and green. Then each claim runs as
`run.ps1 -Suite s [-Only regex] -Engine v2 -Tier full -NoBuild -Groups 1`, one after the other (a machine runs one run.ps1 at a time), its log and
result file in <out dir>. All claims run even when one fails; the exit code is 1 when any claim failed, lists nothing, ended without a result or
reports a failed check. FACTORY_TEST_SQL (the SQL Server of the job; the connection string is never printed) is read by run.ps1.
--parse-only prints the claims and exits (the check that the file is readable, no pwsh needed).
"""
import json
import os
import re
import subprocess
import sys
import time


def parse(path):
    claims = []
    with open(path, encoding="utf-8") as fh:
        for no, raw in enumerate(fh, 1):
            line = raw.strip()
            if not line or line.startswith("#"):
                continue
            suite, sep, only = (part.strip() for part in line.partition(" | "))
            if not re.fullmatch(r"[a-z0-9-]+", suite):
                raise SystemExit(f"{path}:{no}: '{suite}' is not a suite name (lower case letters, digits and -)")
            if sep and not only:
                raise SystemExit(f"{path}:{no}: '|' without a regular expression")
            if only:
                try:
                    re.compile(only)
                except re.error as e:
                    raise SystemExit(f"{path}:{no}: '{only}' is not a regular expression: {e}")
            claims.append({"line": no, "suite": suite, "only": only})
    if not claims:
        raise SystemExit(f"{path}: no claim (an empty list would make the job green without running anything)")
    seen = set()
    for c in claims:
        key = (c["suite"], c["only"])
        if key in seen:
            raise SystemExit(f"{path}:{c['line']}: claim '{c['suite']} | {c['only']}' is listed twice")
        seen.add(key)
    return claims


def label(c):
    return c["suite"] + (f" /{c['only']}/" if c["only"] else "")


def run_args(pwsh, c):
    here = os.path.dirname(os.path.abspath(__file__))
    run = os.path.join(here, "..", "scenarios", "run.ps1")
    args = [pwsh, "-NoProfile", "-File", run, "-Suite", c["suite"]]
    if c["only"]:
        args += ["-Only", c["only"]]
    return args


def list_scenarios(pwsh, c):
    out = subprocess.run(run_args(pwsh, c) + ["-Tier", "full", "-ListScenarios"], capture_output=True, text=True, check=False)
    if out.returncode != 0:
        sys.stdout.write(out.stdout + out.stderr)
        return None
    # (only the lines suite<TAB>scenario<TAB>tier: anything else a script prints is not a scenario)
    return [ln for ln in out.stdout.splitlines() if re.fullmatch(r"[^\t]+\t[^\t]+\t(fast|full)", ln)]


def main(argv):
    pwsh = "pwsh"
    parse_only = False
    rest = []
    it = iter(argv)
    for a in it:
        if a == "--pwsh":
            pwsh = next(it)
        elif a == "--parse-only":
            parse_only = True
        else:
            rest.append(a)
    if len(rest) != 2:
        raise SystemExit(__doc__)
    claims_file, out_dir = rest
    claims = parse(claims_file)
    if parse_only:
        for c in claims:
            print(f"line {c['line']}: suite {c['suite']}" + (f", scenarios matching /{c['only']}/" if c["only"] else ", every scenario"))
        return 0
    os.makedirs(out_dir, exist_ok=True)

    problems = []
    for c in claims:
        listed = list_scenarios(pwsh, c)
        if listed is None:
            problems.append(f"{label(c)}: the scenarios could not be listed (is it a suite of run.ps1 -ListSuites?)")
            c["skip"] = True
        elif not listed:
            problems.append(f"{label(c)}: the claim lists no scenario")
            c["skip"] = True
        else:
            c["scenarios"] = len(listed)
            print(f"claim {label(c)}: {len(listed)} scenarios")
    sys.stdout.flush()

    rows = []
    for n, c in enumerate(claims):
        if c.get("skip"):
            continue
        slug = re.sub(r"[^a-z0-9]+", "-", label(c).lower()).strip("-")[:60]
        result = os.path.join(out_dir, f"{n:02d}-{slug}.result.json")
        log = os.path.join(out_dir, f"{n:02d}-{slug}.log")
        args = run_args(pwsh, c) + ["-Engine", "v2", "-Tier", "full", "-NoBuild", "-Groups", "1", "-ResultFile", result]
        print(f"=== claim {label(c)} (v2) ===")
        sys.stdout.flush()
        t0 = time.time()
        with open(log, "w", encoding="utf-8") as lf:
            proc = subprocess.Popen(args, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, errors="replace")
            for line in proc.stdout:
                sys.stdout.write(line)
                lf.write(line)
            code = proc.wait()
        secs = time.time() - t0
        res = None
        try:
            with open(result, encoding="utf-8-sig") as fh:
                res = json.load(fh)
        except (OSError, ValueError):
            pass
        row = {"claim": label(c), "exit": code, "seconds": round(secs), "listed": c["scenarios"], "checks": 0, "failed": 0, "quarantined": 0, "ran": 0}
        if res is None:
            problems.append(f"{label(c)}: ended without a result file (exit {code})")
        else:
            row["checks"] = int(res.get("checks") or 0)
            row["failed"] = int(res.get("failed") or 0)
            q = res.get("quarantined") or []
            row["quarantined"] = len(q) if isinstance(q, list) else 1
            sc = res.get("scenarios") or []
            row["ran"] = len(sc) if isinstance(sc, list) else 1
            if row["failed"]:
                problems.append(f"{label(c)}: {row['failed']} failed checks")
            if row["checks"] == 0:
                problems.append(f"{label(c)}: no check ran")
            if row["ran"] != row["listed"]:
                problems.append(f"{label(c)}: {row['ran']} scenarios ran, {row['listed']} listed")
        if code != 0:
            problems.append(f"{label(c)}: run.ps1 exited {code}")
        rows.append(row)

    lines = ["### Tier 1 (v2): the claims of tests/scenarios/v2-claims.txt on the v2 engine", "",
             "| claim | scenarios listed | scenarios run | checks | failed | quarantined | exit | seconds |", "|---|---:|---:|---:|---:|---:|---:|---:|"]
    for r in rows:
        lines.append(f"| {r['claim']} | {r['listed']} | {r['ran']} | {r['checks']} | {r['failed']} | {r['quarantined']} | {r['exit']} | {r['seconds']} |")
    if problems:
        lines += ["", f"**{len(problems)} problems**", ""] + [f"- {p}" for p in problems]
    text = "\n".join(lines) + "\n"
    print(text)
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as fh:
            fh.write(text)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
