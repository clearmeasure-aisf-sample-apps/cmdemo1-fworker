#!/usr/bin/env python3
"""Runs one shard of a .NET test project: shard S (from 0) of N, by test class.

usage: shard-dotnet-tests.py <project dir> <shard> <shards> [extra dotnet test arguments]

The project must be built already (dotnet build -c Release). Every shard lists the project's tests the same way, takes the classes in
alphabetical order and keeps the ones whose position modulo N is S, so each class runs in exactly one shard and a new class needs no change
anywhere. Before it runs, the shard proves its vstest filter selects exactly the tests of its classes (a class whose name is also the end of
another class's name would select more, a name the filter cannot express fewer): any difference fails the shard instead of running
tests twice or not at all. The listing and the run use the same `dotnet test --no-build`, so the number of tests it prints is the one to add
up over the shards and compare with an unsharded run.
"""
import os
import re
import subprocess
import sys


def dotnet_test(project, extra):
    return ["dotnet", "test", project, "--configuration", "Release", "--no-build", *extra]


def list_tests(project, flt):
    args = dotnet_test(project, ["--list-tests"] + (["--filter", flt] if flt else []))
    out = subprocess.run(args, capture_output=True, text=True, check=False)
    if out.returncode != 0:
        sys.stdout.write(out.stdout + out.stderr)
        sys.exit(out.returncode)
    lines = out.stdout.splitlines()
    try:
        start = next(i for i, line in enumerate(lines) if line.strip() == "The following Tests are available:") + 1
    except StopIteration:
        return []  # nothing selected: the runner prints no list
    return [line.strip() for line in lines[start:] if line.startswith("    ") and line.strip()]


def class_of(display_name):
    # "Namespace.Class.Method(arg: ...)": the class is everything before the last dot ahead of the first parenthesis
    head = display_name.split("(", 1)[0]
    return head.rsplit(".", 1)[0]


def main():
    project, shard, shards = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
    extra = sys.argv[4:]
    if not 0 <= shard < shards:
        sys.exit(f"shard {shard} is outside 0..{shards - 1}")
    tests = list_tests(project, None)
    classes = sorted({class_of(t) for t in tests})
    for c in classes:
        if not re.fullmatch(r"[A-Za-z0-9_.]+", c):
            sys.exit(f"the class '{c}' has characters the shard filter does not express")
    mine = [c for i, c in enumerate(classes) if i % shards == shard]
    expected = [t for t in tests if class_of(t) in mine]
    print(f"{project}: {len(tests)} tests in {len(classes)} classes; shard {shard} of {shards}: {len(mine)} classes, {len(expected)} tests", flush=True)
    if not mine:
        print("this shard has no class")
        return
    flt = "|".join(f"FullyQualifiedName~{c}." for c in mine)
    selected = list_tests(project, flt)
    if sorted(selected) != sorted(expected):
        extra_tests = sorted(set(selected) - set(expected))[:5]
        missing_tests = sorted(set(expected) - set(selected))[:5]
        sys.exit(f"the filter of shard {shard} selects {len(selected)} tests, its classes have {len(expected)}: extra {extra_tests}, missing {missing_tests}")
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as fh:
            fh.write(f"{project}: shard {shard} of {shards} runs {len(expected)} of {len(tests)} tests ({len(mine)} of {len(classes)} classes)\n\n")
    result = subprocess.run(dotnet_test(project, ["--filter", flt, *extra]), check=False)
    sys.exit(result.returncode)


if __name__ == "__main__":
    main()
