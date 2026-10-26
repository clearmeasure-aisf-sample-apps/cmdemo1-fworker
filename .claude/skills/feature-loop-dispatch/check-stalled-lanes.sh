#!/usr/bin/env bash
# check-stalled-lanes.sh <owner/repo> [stale-minutes] [tasks-dir] [active-id ...]
# Feature-loop stall watchdog (bash port of the work-orders repository's Check-StalledLanes.ps1).
# Read-only against GitHub (REST). Exit 0 = no stalls, 2 = stalls found, 1 = the check itself failed.
#   GREEN_UNMERGED     every check-run success for more than stale-minutes, pull request still open
#   DIRTY              merge conflicts with the base
#   CI_FAILED          a check-run concluded failure, cancelled, timed_out or action_required
#   CI_STUCK           no check-runs, or the oldest one started more than 60 minutes ago and is unfinished
#   MERGED_ISSUE_OPEN  a merged pull request's "Closes #N" issue (or issue-N branch) is still open, no open children
#   LOCAL_STALL        an active sub-session's task output file untouched for 25 minutes (pre-PR stalls)
# Pull requests labelled hold:merge-window are skipped (the orchestrator holds them on purpose).
set -euo pipefail
repo=${1:?usage: check-stalled-lanes.sh owner/repo [stale-minutes] [tasks-dir] [active-id ...]}
stale=${2:-15}
tasks=${3:-}
shift $(( $# < 3 ? $# : 3 ))
export REPO="$repo" STALE="$stale" TASKS="$tasks" ACTIVE="$*"
python3 - <<'PY'
import json, os, re, subprocess, sys, time
from datetime import datetime, timezone

repo, stale = os.environ['REPO'], float(os.environ['STALE'])
tasks, active = os.environ.get('TASKS', ''), os.environ.get('ACTIVE', '').split()
now = datetime.now(timezone.utc)

def gh(*args):
    r = subprocess.run(['gh', *args], capture_output=True, text=True)
    if r.returncode != 0:
        print(f'check failed: gh {" ".join(args[:3])}: {r.stderr.strip()[:200]}', file=sys.stderr)
        sys.exit(1)
    return r.stdout

def lines(out):
    return [json.loads(l) for l in out.splitlines() if l.strip()]

def ts(s):
    return datetime.fromisoformat(s.replace('Z', '+00:00')) if s else None

stalls = []
prs = json.loads(gh('pr', 'list', '-R', repo, '--state', 'open', '--limit', '50',
                    '--json', 'number,headRefName,headRefOid,mergeStateStatus,labels'))
for pr in prs:
    if any(l['name'] == 'hold:merge-window' for l in pr['labels']):
        continue
    runs = lines(gh('api', f"repos/{repo}/commits/{pr['headRefOid']}/check-runs?per_page=100", '--paginate',
                    '--jq', '.check_runs[] | {name, status, conclusion, started_at, completed_at}'))
    if not runs:
        stalls.append(('CI_STUCK', pr, 'no check-runs on the head commit')); continue
    failed = [r['name'] for r in runs if r['conclusion'] in ('failure', 'cancelled', 'timed_out', 'action_required')]
    if failed:
        stalls.append(('CI_FAILED', pr, 'failed: ' + ', '.join(failed))); continue
    incomplete = [r for r in runs if r['status'] != 'completed']
    if incomplete:
        starts = [ts(r['started_at']) for r in runs if r['started_at']]
        if starts and (now - min(starts)).total_seconds() / 60 > 60:
            stalls.append(('CI_STUCK', pr, f'{len(incomplete)} job(s) unfinished after 60 min'))
        continue
    if pr['mergeStateStatus'] == 'DIRTY':
        stalls.append(('DIRTY', pr, 'merge conflicts with the base')); continue
    if all(r['conclusion'] in ('success', 'skipped') for r in runs):
        done = [ts(r['completed_at']) for r in runs if r['completed_at']]
        age = (now - max(done)).total_seconds() / 60 if done else 1e9
        if age > stale:
            stalls.append(('GREEN_UNMERGED', pr, f'all checks green for {age:.0f} min, not merged'))

merged = json.loads(gh('pr', 'list', '-R', repo, '--state', 'merged', '--limit', '30',
                       '--json', 'number,body,headRefName,mergedAt'))
for pr in merged:
    if (now - ts(pr['mergedAt'])).total_seconds() / 60 <= stale:
        continue
    nums = set(re.findall(r'(?i)\b(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\s+#(\d+)', pr.get('body') or ''))
    m = re.search(r'(?:issue|fix)[/\-](\d+)', pr['headRefName'])
    if m:
        nums.add(m.group(1))
    for n in nums:
        state = gh('api', f'repos/{repo}/issues/{n}', '--jq', '.state').strip()
        if state != 'open':
            continue
        open_children = gh('api', f'repos/{repo}/issues/{n}/sub_issues', '--jq', '[.[] | select(.state == "open")] | length').strip() or '0'
        if int(open_children) > 0:
            continue
        stalls.append(('MERGED_ISSUE_OPEN', pr, f'issue #{n} still open after the merge'))

if tasks and active:
    for a in active:
        f = os.path.join(tasks, f'{a}.output')
        if not os.path.exists(f):
            stalls.append(('LOCAL_STALL', {'number': 0, 'headRefName': a}, 'active sub-session has no task output file'))
        elif time.time() - os.path.getmtime(f) > 25 * 60:
            stalls.append(('LOCAL_STALL', {'number': 0, 'headRefName': a}, 'task output untouched for more than 25 min'))

stamp = now.strftime('%Y-%m-%d %H:%M:%SZ')
if not stalls:
    print(f'[{stamp}] OK - no stalled lanes in {repo}.'); sys.exit(0)
print(f'[{stamp}] {len(stalls)} stall(s) in {repo}:')
for kind, pr, detail in stalls:
    print(f"  {kind:<18} PR #{pr['number']} ({pr['headRefName']}): {detail}")
sys.exit(2)
PY
