#!/bin/bash
# Copies the deployable's files from the working copy into this repository folder (files only, like the scorecard import).
# usage: ./stage.sh [source-dir]   default source: ~/Projects/factory-worker-next
set -euo pipefail
SRC="${1:-$HOME/Projects/factory-worker-next}"; DST="$(cd "$(dirname "$0")" && pwd)"
for d in gateway plugin config docs; do rm -rf "$DST/$d"; mkdir -p "$DST/$d"; done
rsync -a --exclude bin --exclude obj "$SRC/gateway/" "$DST/gateway/"
rsync -a "$SRC/plugin/" "$DST/plugin/"
rsync -a "$SRC/config/" "$DST/config/"
rsync -a "$SRC/docs/" "$DST/docs/"
cp "$SRC/README.md" "$DST/README.md"
# the container always runs the plant manager (owner sessions); the queue depth is the tested default for code changes
python3 - "$DST/config/instance.json" <<'PY'
import json,sys
p=sys.argv[1]; c=json.load(open(p)); c["ownerSessions"]=True; c["mergeQueueDepth"]=2; json.dump(c,open(p,"w"),indent=2)
PY
echo "staged from $SRC"
