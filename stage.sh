#!/bin/bash
# Copies the deployable's files from a clean git checkout of the factory-worker source into this clone.
# usage: ./stage.sh [source-dir] [--allow-dirty]   default source: ~/Projects/factory-worker
# Refuses a source with uncommitted changes (they would ship unreviewed), a target that is not a clone
# of cmdemo1-fworker, and a target on the default branch. Review `git diff` in this clone, then commit.
set -euo pipefail
SRC="$HOME/Projects/factory-worker"; ALLOW_DIRTY=0
for a in "$@"; do
  case "$a" in
    --allow-dirty) ALLOW_DIRTY=1 ;;
    -*) echo "stage.sh: unknown option $a" >&2; exit 1 ;;
    *) SRC="$a" ;;
  esac
done
DST="$(cd "$(dirname "$0")" && pwd)"

fail() { echo "stage.sh: $*" >&2; exit 1; }

git -C "$DST" rev-parse --is-inside-work-tree >/dev/null 2>&1 || fail "$DST is not a git clone of cmdemo1-fworker"
[ "$(git -C "$DST" rev-parse --show-toplevel)" = "$DST" ] || fail "$DST is not the top of its git work tree"
git -C "$DST" remote get-url origin 2>/dev/null | grep -Eq 'clearmeasure-aisf-sample-apps/cmdemo1-fworker(\.git)?$' || fail "origin of $DST is not clearmeasure-aisf-sample-apps/cmdemo1-fworker"
BRANCH="$(git -C "$DST" branch --show-current)"
DEFAULT="$(git -C "$DST" symbolic-ref --short refs/remotes/origin/HEAD 2>/dev/null | sed 's#^origin/##' || true)"
[ -n "$BRANCH" ] || fail "detached HEAD in $DST; check out a branch first"
[ "$BRANCH" != "${DEFAULT:-master}" ] || fail "on ${BRANCH}; create a branch first (git checkout -b <user>/<description>)"
[ -z "$(git -C "$DST" status --porcelain)" ] || fail "$DST has uncommitted changes; commit or stash them first"

git -C "$SRC" rev-parse --is-inside-work-tree >/dev/null 2>&1 || fail "$SRC is not a git checkout"
if [ "$ALLOW_DIRTY" -ne 1 ] && [ -n "$(git -C "$SRC" status --porcelain)" ]; then
  fail "$SRC has uncommitted changes (on $(git -C "$SRC" branch --show-current)); commit them or use a clean clone, or pass --allow-dirty"
fi

command -v rsync >/dev/null || fail "rsync is not installed"
command -v python3 >/dev/null || fail "python3 is not installed"
for p in gateway plugin config docs README.md; do [ -e "$SRC/$p" ] || fail "$SRC/$p is missing; $SRC is not the factory-worker root"; done

for d in gateway plugin config docs; do rm -rf "${DST:?}/$d"; mkdir -p "$DST/$d"; done
rsync -a --exclude bin --exclude obj "$SRC/gateway/" "$DST/gateway/"
rsync -a "$SRC/plugin/" "$DST/plugin/"
rsync -a "$SRC/config/" "$DST/config/"
rsync -a "$SRC/docs/" "$DST/docs/"
cp "$SRC/README.md" "$DST/README.md"
# the container always runs the plant manager (owner sessions); the queue depth is the tested default for code changes.
# Worker messaging and the supervisor are on for every registered worker (owner decision 2026-10-12); the source keeps them off by default.
python3 - "$DST/config/instance.json" <<'PY'
import json,sys
p=sys.argv[1]; c=json.load(open(p)); c["ownerSessions"]=True; c["mergeQueueDepth"]=2
c["messaging"]["enabled"]=True; c["messaging"]["allowedWorkers"]=["*"]
c["manager"]["enabled"]=True; c["manager"]["allowedAskers"]=["*"]
json.dump(c,open(p,"w"),indent=2)
PY
echo "staged from $SRC@$(git -C "$SRC" rev-parse --short HEAD) ($(git -C "$SRC" branch --show-current)) into $DST on $BRANCH"
git -C "$DST" status --short | head -40
