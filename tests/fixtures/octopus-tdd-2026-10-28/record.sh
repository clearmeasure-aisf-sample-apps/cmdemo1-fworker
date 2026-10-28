#!/usr/bin/env bash
# Records tdd's Octopus history (read only: every call is a GET) as the fixture of tests/Fworker.Workflow.Tests/RecordedOctopusHistoryTests.cs.
#
#   tests/fixtures/octopus-tdd-2026-10-28/record.sh [--days 30] [--list] [--out file]
#
# Needs curl and jq. The read key comes from OCTOPUS_API_KEY or the keyring (secret-tool, service demo-operator, kind octopus-read-key) inside this script and is never
# printed or written. Only what the watcher and the train read is kept: ids, versions, the commit of a release, created/completed times and states. No URLs, no user names,
# no release notes beyond the commit.
set -euo pipefail

DAYS=30; LIST=0
SERVER=${OCTOPUS_URL:-https://clearmeasure.octopus.app}; SPACE=${OCTOPUS_SPACE:-Spaces-355}
OUT="$(cd "$(dirname "$0")" && pwd)/octopus-history.json"
while [ $# -gt 0 ]; do case "$1" in --days) DAYS=$2; shift 2;; --list) LIST=1; shift;; --out) OUT=$2; shift 2;; *) echo "unknown $1" >&2; exit 2;; esac; done

KEY=${OCTOPUS_API_KEY:-$(secret-tool lookup service demo-operator kind octopus-read-key)}
[ -n "$KEY" ] || { echo "no Octopus read key" >&2; exit 1; }

get() { curl -sS --fail --max-time 60 -H "X-Octopus-ApiKey: $KEY" "$SERVER/$1"; }

CUTOFF=$(date -u -d "-$DAYS days" +%Y-%m-%dT%H:%M:%SZ)

# a collection page by page (newest first as Octopus lists it); with a time field ($2) only the items not older than the cutoff, and the walk ends at the first page that has an older one
TS='def ts: sub("\\.[0-9]+";"") | sub("[+-]00:00$";"Z") | fromdate;'
all() {
  local next=$1 field=${2:-} page
  while [ -n "$next" ] && [ "$next" != null ]; do
    page=$(get "${next#/}")
    if [ -n "$field" ]; then
      echo "$page" | jq -c --arg c "$CUTOFF" --arg f "$field" "$TS"'.Items[] | select((.[$f] | ts) >= ($c | ts))'
      if echo "$page" | jq -e --arg c "$CUTOFF" --arg f "$field" "$TS"'[.Items[] | select((.[$f] | ts) < ($c | ts))] | length > 0' >/dev/null; then return; fi
    else
      echo "$page" | jq -c '.Items[]'
    fi
    next=$(echo "$page" | jq -r '.Links["Page.Next"] // empty')
  done
}

if [ $LIST = 1 ]; then
  all "api/$SPACE/projects?take=100" | jq -r '"\(.Id) \(.Slug)"'
  get "api/$SPACE/environments?take=1000" | jq -r '.Items[] | "\(.Id) \(.Name)"'
  exit 0
fi

WORK=$(mktemp -d); trap 'rm -rf "$WORK"' EXIT   # (the answers are kept in files: they are too big for an argument list)
get "api/$SPACE/environments?take=1000" | jq -c '[.Items[] | {Id, Name}]' > "$WORK/envs.json"
PROJECTS=$(all "api/$SPACE/projects?take=100" | jq -r '.Id')

NOFRAC='if . then sub("\\.[0-9]+";"") else . end'
SLIM_RELEASE='{Id, Version, Assembled: (.Assembled | sub("\\.[0-9]+";"")),
  ReleaseNotes: ((([.BuildInformation[]?.VcsCommitNumber | select(test("^[0-9a-fA-F]{40}$"))][0]) // (.ReleaseNotes // "" | capture("(?<c>\\b[0-9a-fA-F]{40}\\b)").c?)) as $c | if $c then "built " + ($c | ascii_downcase) else "" end)}'
SLIM_TASK="{Id, State, IsCompleted, FinishedSuccessfully, QueueTime: (.QueueTime | $NOFRAC), StartTime: (.StartTime | $NOFRAC), CompletedTime: (.CompletedTime | $NOFRAC)}"

echo '[]' > "$WORK/projects.json"
for P in $PROJECTS; do
  all "api/$SPACE/projects/$P/releases?take=100" Assembled | jq -cs . > "$WORK/rel.json"
  all "api/$SPACE/deployments?projects=$P&take=100" Created | jq -cs . > "$WORK/dep.json"
  [ "$(jq -s 'map(length) | add' "$WORK/rel.json" "$WORK/dep.json")" != 0 ] || continue
  SLUG=$(get "api/$SPACE/projects/$P" | jq -r .Slug)
  # a deployment may name a release older than the window: the watcher reads it by id
  echo '[]' > "$WORK/older.json"
  for R in $(jq -r --slurpfile rel "$WORK/rel.json" '[.[].ReleaseId] | unique | .[] | select(. as $i | $rel[0] | map(.Id) | index($i) | not)' "$WORK/dep.json"); do
    get "api/$SPACE/releases/$R" | jq -c "$SLIM_RELEASE" > "$WORK/one.json"
    jq -c --slurpfile r "$WORK/one.json" '. + $r' "$WORK/older.json" > "$WORK/older2.json"; mv "$WORK/older2.json" "$WORK/older.json"
  done
  echo '{}' > "$WORK/tasks.json"; echo '{}' > "$WORK/ints.json"
  for T in $(jq -r '[.[].TaskId // empty] | unique | .[]' "$WORK/dep.json"); do
    get "api/$SPACE/tasks/$T" > "$WORK/task.json"
    jq -c "$SLIM_TASK" "$WORK/task.json" > "$WORK/slim.json"
    jq -c --arg t "$T" --slurpfile v "$WORK/slim.json" '.[$t] = $v[0]' "$WORK/tasks.json" > "$WORK/t2.json"; mv "$WORK/t2.json" "$WORK/tasks.json"
    if [ "$(jq -r .State "$WORK/task.json")" = Executing ]; then
      get "api/$SPACE/interruptions?regarding=$T&pendingOnly=true&take=20" | jq -c '[.Items[] | {Id, IsPending}]' > "$WORK/int.json"
      jq -c --arg t "$T" --slurpfile v "$WORK/int.json" '.[$t] = $v[0]' "$WORK/ints.json" > "$WORK/i2.json"; mv "$WORK/i2.json" "$WORK/ints.json"
    fi
  done
  jq -c --arg id "$P" --arg slug "$SLUG" --slurpfile rel "$WORK/rel.json" --slurpfile dep "$WORK/dep.json" --slurpfile older "$WORK/older.json" --slurpfile tasks "$WORK/tasks.json" --slurpfile ints "$WORK/ints.json" \
    ". + [{Id: \$id, Slug: \$slug, Releases: (\$rel[0] | map($SLIM_RELEASE)), OlderReleases: \$older[0], Deployments: (\$dep[0] | map({Id, TaskId, ReleaseId, EnvironmentId, Created: (.Created | $NOFRAC)})), Tasks: \$tasks[0], Interruptions: \$ints[0]}]" "$WORK/projects.json" > "$WORK/p2.json"
  mv "$WORK/p2.json" "$WORK/projects.json"
done
jq -n --arg at "$(date -u +%Y-%m-%dT%H:%M:%SZ)" --argjson days "$DAYS" --arg space "$SPACE" --slurpfile envs "$WORK/envs.json" --slurpfile projects "$WORK/projects.json" \
  '{recordedAt: $at, days: $days, space: $space, environments: $envs[0], projects: $projects[0]}' | jq -c . > "$OUT"
echo "recorded $(jq '.projects | length' "$OUT") projects to $OUT"
