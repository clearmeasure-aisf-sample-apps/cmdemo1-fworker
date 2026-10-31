#!/usr/bin/env python3
"""Checks tests/scenarios/v2-claims.txt and its place in the v2 jobs of .github/workflows/tests.yml (W9.10d, #552).

usage: run-v2-claims.py <claims file> [--workflow <tests.yml>] [--pwsh pwsh] [--parse-only] [--require-complete]

The claims are RUN by `tests/scenarios/run-all.ps1 -Engine v2` (the matrix jobs "Tier 1 (v2 ...)" of tests.yml: the key `claims:` of an entry lists
suites and shards of suites, as `suites:` does for the v1 shards); this script is the static check of the list, and runs in the job "Every scenario is
in exactly one shard". It fails (exit 1) on:
  - a line that is not understood, a claim twice, a suite claimed whole and also by another claim, a v1-only declaration without a reason or for a suite that is claimed;
  - a `v1-only-scenarios <suite> | <regex> | <reason>` line (the scenarios of a suite claimed whole that stay on v1: run.ps1 -Engine v2 neither lists nor runs them) without a reason, for a suite that is not
    claimed whole, or whose regular expression matches no scenario of the suite;
  - (unless --parse-only; needs pwsh) a claim for a suite that run.ps1 -ListSuites does not list, and a claim that lists no scenario (run.ps1 -ListScenarios): a renamed suite
    or scenario cannot leave a claim empty and green;
  - (with --workflow) a claimed suite that no matrix entry of the v2 jobs runs, one that two entries run, a shard set that is not g = 0..n-1 of one n, a shard of a suite whose
    claims name scenarios (they are not sharded), and an entry for a suite nothing claims.
It reports, and with --require-complete (the precondition of W9.9c: the claim list equals run.ps1 -ListSuites less the declared v1-only suites) fails on: a suite of run.ps1
-ListSuites that is claimed whole nor declared v1-only (a suite claimed by regular expressions is not claimed whole).
"""
import re
import subprocess
import sys

PROFILES = ("plain", "saga-docs", "saga-feature")


def parse(path):
    claims, v1only, v1scen = [], [], []
    with open(path, encoding="utf-8") as fh:
        for no, raw in enumerate(fh, 1):
            line = raw.strip()
            if not line or line.startswith("#"):
                continue
            m = re.fullmatch(r"v1-only-scenarios\s+([a-z0-9-]+)\s*\|\s*(.+?)\s+\|\s+(.+)", line)
            if m:
                try:
                    re.compile(m.group(2))
                except re.error as e:
                    raise SystemExit(f"{path}:{no}: '{m.group(2)}' is not a regular expression: {e}")
                v1scen.append({"line": no, "suite": m.group(1), "only": m.group(2), "reason": m.group(3).strip()})
                continue
            if line.startswith("v1-only-scenarios"):
                raise SystemExit(f"{path}:{no}: a v1-only-scenarios declaration is 'v1-only-scenarios <suite> | <regex> | <reason>'")
            m = re.fullmatch(r"v1-only\s+([a-z0-9-]+)\s*\|\s*(.+)", line)
            if m:
                v1only.append({"line": no, "suite": m.group(1), "reason": m.group(2).strip()})
                continue
            if line.startswith("v1-only"):
                raise SystemExit(f"{path}:{no}: a v1-only declaration is 'v1-only <suite> | <reason>'")
            fields = [f.strip() for f in line.split(" | ")]
            suite = fields[0]
            if not re.fullmatch(r"[a-z0-9-]+", suite):
                raise SystemExit(f"{path}:{no}: '{suite}' is not a suite name (lower case letters, digits and -)")
            only, profile = "", "plain"
            for f in fields[1:]:
                if not f:
                    raise SystemExit(f"{path}:{no}: an empty field")
                if f.startswith("profile="):
                    profile = f[len("profile="):]
                    if profile not in PROFILES:
                        raise SystemExit(f"{path}:{no}: '{profile}' is not a profile of run.ps1 ({', '.join(PROFILES)})")
                else:
                    if only:
                        raise SystemExit(f"{path}:{no}: two regular expressions")
                    only = f
                    try:
                        re.compile(only)
                    except re.error as e:
                        raise SystemExit(f"{path}:{no}: '{only}' is not a regular expression: {e}")
            claims.append({"line": no, "suite": suite, "only": only, "profile": profile})
    if not claims:
        raise SystemExit(f"{path}: no claim (an empty list would make the job green without running anything)")
    return claims, v1only, v1scen


def structure(path, claims, v1only, v1scen):
    problems = []
    seen = set()
    for c in claims:
        key = (c["suite"], c["only"], c["profile"])
        if key in seen:
            problems.append(f"{path}:{c['line']}: claim '{c['suite']} | {c['only']}' is listed twice")
        seen.add(key)
    by_suite = {}
    for c in claims:
        by_suite.setdefault(c["suite"], []).append(c)
    for suite, mine in by_suite.items():
        whole = [c for c in mine if not c["only"]]
        if len(whole) > 1 or (whole and len(mine) > 1):
            problems.append(f"{path}:{mine[0]['line']}: the suite {suite} is claimed whole and also by another claim (claim it whole once, or claim scenarios by regular expressions)")
        if len(mine) > 10:
            problems.append(f"{path}: the suite {suite} has {len(mine)} claims; at most 10 (each has a port range of 100)")
    for v in v1only:
        if v["suite"] in by_suite:
            problems.append(f"{path}:{v['line']}: {v['suite']} is declared v1-only and is claimed")
    for x in v1scen:
        if not any(not c["only"] for c in by_suite.get(x["suite"], [])):
            problems.append(f"{path}:{x['line']}: {x['suite']} has scenarios declared v1-only and is not claimed whole (claim the suite whole, or leave the scenario out of the claims by their regular expressions)")
    return by_suite, problems


def run(args):
    return subprocess.run(args, capture_output=True, text=True, check=False)


def suites_of(pwsh, run_ps1):
    out = run([pwsh, "-NoProfile", "-File", run_ps1, "-ListSuites"])
    if out.returncode != 0:
        sys.stdout.write(out.stdout + out.stderr)
        raise SystemExit("run.ps1 -ListSuites failed")
    return [ln.strip() for ln in out.stdout.splitlines() if re.fullmatch(r"[a-z0-9-]+", ln.strip())]


def listed(pwsh, run_ps1, c):
    args = [pwsh, "-NoProfile", "-File", run_ps1, "-ListScenarios", "-Suite", c["suite"], "-Tier", "full"]
    if c["only"]:
        args += ["-Only", c["only"]]
    out = run(args)
    if out.returncode != 0:
        return None
    return [ln for ln in out.stdout.splitlines() if re.fullmatch(r"[^\t]+\t[^\t]+\t(fast|full)", ln)]


def matrix_specs(workflow):
    """The `claims:` entries of the matrix of the v2 jobs: [(suite, group, n)] with group None for a whole suite, 'all' for @all/n."""
    specs = []
    with open(workflow, encoding="utf-8") as fh:
        for raw in fh:
            m = re.match(r"^\s+claims:\s+(\S+)\s*$", raw)
            if not m:
                continue
            for spec in m.group(1).split(","):
                sm = re.fullmatch(r"([a-z0-9-]+)(?:@(all|\d+)/(\d+))?", spec)
                if not sm:
                    raise SystemExit(f"{workflow}: '{spec}' is not a suite, name@g/n or name@all/n")
                specs.append((sm.group(1), sm.group(2), int(sm.group(3)) if sm.group(3) else None))
    return specs


def matrix_problems(workflow, by_suite):
    problems = []
    specs = matrix_specs(workflow)
    for suite in by_suite:
        mine = [s for s in specs if s[0] == suite]
        whole_claim = any(not c["only"] for c in by_suite[suite])
        if not mine:
            problems.append(f"{workflow}: the suite {suite} is claimed and no v2 job runs it (an entry with `claims: {suite}`)")
            continue
        plain = [s for s in mine if s[1] is None or s[1] == "all"]
        shards = [s for s in mine if s[1] not in (None, "all")]
        if plain and shards:
            problems.append(f"{workflow}: {suite} is listed whole and as shards")
        elif len(plain) > 1:
            problems.append(f"{workflow}: {suite} is listed {len(plain)} times")
        elif shards:
            if not whole_claim:
                problems.append(f"{workflow}: {suite} is sharded, and its claims name scenarios (they cannot be sharded)")
            ns = {s[2] for s in shards}
            gs = sorted(int(s[1]) for s in shards)
            if len(ns) != 1 or gs != list(range(next(iter(ns)))):
                problems.append(f"{workflow}: the shards of {suite} are not g = 0..n-1 of one n, each once: {gs} of {sorted(ns)}")
        elif plain and plain[0][1] == "all" and not whole_claim:
            problems.append(f"{workflow}: {suite}@all/n, and its claims name scenarios")
    for suite in {s[0] for s in specs}:
        if suite not in by_suite:
            problems.append(f"{workflow}: a v2 job runs {suite}, which nothing claims")
    return problems


def main(argv):
    pwsh, workflow = "pwsh", None
    parse_only = complete = False
    rest = []
    it = iter(argv)
    for a in it:
        if a == "--pwsh":
            pwsh = next(it)
        elif a == "--workflow":
            workflow = next(it)
        elif a == "--parse-only":
            parse_only = True
        elif a == "--require-complete":
            complete = True
        else:
            rest.append(a)
    if len(rest) != 1:
        raise SystemExit(__doc__)
    path = rest[0]
    claims, v1only, v1scen = parse(path)
    by_suite, problems = structure(path, claims, v1only, v1scen)
    if parse_only:
        for c in claims:
            print(f"line {c['line']}: suite {c['suite']}" + (f", scenarios matching /{c['only']}/" if c["only"] else ", every scenario") + f", profile {c['profile']}")
        for v in v1only:
            print(f"line {v['line']}: suite {v['suite']} stays on v1: {v['reason']}")
        for x in v1scen:
            print(f"line {x['line']}: suite {x['suite']}, scenarios matching /{x['only']}/ stay on v1: {x['reason']}")
        for p in problems:
            print("problem:", p)
        return 1 if problems else 0

    import os
    run_ps1 = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "scenarios", "run.ps1")
    known = suites_of(pwsh, run_ps1)
    covered = {}
    for c in claims:
        if c["suite"] not in known:
            problems.append(f"{path}:{c['line']}: {c['suite']} is not a suite of run.ps1 -ListSuites")
            continue
        found = listed(pwsh, run_ps1, c)
        label = c["suite"] + (f" /{c['only']}/" if c["only"] else "")
        if found is None:
            problems.append(f"{label}: the scenarios could not be listed")
        elif not found:
            problems.append(f"{label}: the claim lists no scenario")
        else:
            print(f"claim {label}: {len(found)} scenarios, profile {c['profile']}")
            covered.setdefault(c["suite"], set()).update(found)
    for v in v1only:
        if v["suite"] not in known:
            problems.append(f"{path}:{v['line']}: {v['suite']} is not a suite of run.ps1 -ListSuites")
    # a v1-only scenario line names a suite of run.ps1 and matches at least one scenario of it: a renamed scenario cannot leave the line empty (and its scenario running on v2 unnoticed)
    for x in v1scen:
        if x["suite"] not in known:
            problems.append(f"{path}:{x['line']}: {x['suite']} is not a suite of run.ps1 -ListSuites")
            continue
        found = listed(pwsh, run_ps1, {"suite": x["suite"], "only": x["only"]})
        if not found:
            problems.append(f"{path}:{x['line']}: /{x['only']}/ matches no scenario of {x['suite']}")
        else:
            print(f"v1-only scenarios {x['suite']} /{x['only']}/: {len(found)} scenarios")
    if workflow:
        problems += matrix_problems(workflow, by_suite)
    whole = {s for s, mine in by_suite.items() if any(not c["only"] for c in mine)}
    # a suite whose claims by scenarios together list every scenario of it (the suite saga: one claim per profile) is claimed whole all the same
    for suite in [s for s in by_suite if s not in whole and s in covered]:
        everything = listed(pwsh, run_ps1, {"suite": suite, "only": ""})
        if everything and set(everything) <= covered[suite]:
            whole.add(suite)
    declared = {v["suite"] for v in v1only}
    open_suites = [s for s in known if s not in whole and s not in declared]
    print(f"claimed whole: {len(whole)}, claimed by scenarios: {len([s for s in by_suite if s not in whole])}, v1-only: {len(declared)}, of {len(known)} suites")
    if open_suites:
        text = f"suites neither claimed whole nor declared v1-only ({len(open_suites)}): {', '.join(open_suites)}"
        if complete:
            problems.append(text)
        else:
            print("not yet complete:", text)
    for p in problems:
        print("problem:", p)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
