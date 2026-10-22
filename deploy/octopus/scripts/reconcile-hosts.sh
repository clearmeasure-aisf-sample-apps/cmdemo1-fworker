#!/usr/bin/env bash
# Runbook "Reconcile hosts" of Octopus project cmdemo1-fworker-runtime, run every 5 minutes on each target with tag fworker-host:
#   1. GET {Fworker.GatewayUrl}/api/v1/runtime/octopus/desired?host=<machine>  ->  {"workers":["name",...]}
#   2. remove every fw-* container that is not desired or has exited (its last 50 log lines go to the task log first);
#   3. POST {Fworker.GatewayUrl}/api/v1/runtime/octopus/report  {host, containers:[{name,item,state,startedAt}]} of the fw-* containers left.
# Both calls carry Authorization: Bearer <Fworker.HostToken>, sent from a file descriptor, never on a command line or in the log.
# The desired list is not trusted blindly: an unreachable gateway or an unparsable answer stops the run before anything is removed.
set -euo pipefail

ov() {
    if declare -F get_octopusvariable > /dev/null; then get_octopusvariable "$1"
    else local n; n=$(printf '%s' "${1//./_}" | tr '[:lower:]' '[:upper:]'); printf '%s' "${!n:-}"; fi
}
fail() { echo "ERROR: $*" >&2; exit 1; }

gateway=$(ov Fworker.GatewayUrl); gateway=${gateway%/}
hostName=$(ov Octopus.Machine.Name); hostName=${hostName:-$(hostname)}
token=$(ov Fworker.HostToken)
[[ "$gateway" =~ ^https?://[A-Za-z0-9.:-]+(/[A-Za-z0-9._/-]*)?$ ]] || fail "Fworker.GatewayUrl is missing or not a plain http(s) URL"
[[ "$hostName" =~ ^[A-Za-z0-9._-]{1,100}$ ]] || fail "the machine name has characters outside [A-Za-z0-9._-]"
[ -n "$token" ] || fail "Fworker.HostToken is not set"
auth() { printf 'Authorization: Bearer %s\n' "$token"; }

desired=$(curl -fsS --max-time 30 -H @<(auth) "$gateway/api/v1/runtime/octopus/desired?host=$hostName") || fail "desired: the gateway did not answer"
compact=$(printf '%s' "$desired" | tr -d ' \t\r\n')
[[ "$compact" =~ \"workers\":\[([^]]*)\] ]] || fail "desired: no workers array in the answer"
list=${BASH_REMATCH[1]}
wanted=" "
if [ -n "$list" ]; then
    IFS=, read -r -a names <<< "$list"
    for q in "${names[@]}"; do
        n=${q#\"}; n=${n%\"}
        [[ "$n" =~ ^[a-z0-9-]{1,40}$ ]] || fail "desired: worker name $q is not ^[a-z0-9-]{1,40}\$"
        wanted+="$n "
    done
fi
echo "Desired on $hostName:${wanted% }"

removed=0
while IFS=$'\t' read -r cname state; do
    [ -n "$cname" ] || continue
    wname=${cname#fw-}
    if [ "$state" = running ] && [[ "$wanted" == *" $wname "* ]]; then continue; fi
    echo "Removing $cname ($state, $([[ "$wanted" == *" $wname "* ]] && echo desired || echo 'not desired')); last 50 log lines:"
    docker logs --tail 50 "$cname" 2>&1 | sed 's/^/    /' || true
    docker rm -f "$cname" > /dev/null || echo "WARNING: could not remove $cname" >&2
    removed=$((removed + 1))
done < <(docker ps -a --filter name='^fw-' --format '{{.Names}}\t{{.State}}')

entries=''
while IFS=$'\t' read -r cname item state started; do
    [ -n "$cname" ] || continue
    [[ "$item" =~ ^[0-9]+$ ]] || item=0
    entries+="${entries:+,}{\"name\":\"${cname#fw-}\",\"item\":$item,\"state\":\"$state\",\"startedAt\":\"$started\"}"
done < <(docker ps -a --filter name='^fw-' --format '{{.Names}}' | while read -r c; do
    docker inspect --format '{{.Name}}{{"\t"}}{{index .Config.Labels "fworker.item"}}{{"\t"}}{{.State.Status}}{{"\t"}}{{.State.StartedAt}}' "$c" | sed 's#^/##'
done)
body=$(printf '{"host":"%s","containers":[%s]}' "$hostName" "$entries")
curl -fsS --max-time 30 -X POST -H @<(auth) -H 'Content-Type: application/json' --data-binary @- \
    "$gateway/api/v1/runtime/octopus/report" <<< "$body" > /dev/null || fail "report: the gateway did not accept it"
echo "Reported $(printf '%s' "$entries" | grep -o '"name"' | wc -l) container(s); removed $removed"
