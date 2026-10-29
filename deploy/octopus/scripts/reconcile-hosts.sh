#!/usr/bin/env bash
# Runbook "Reconcile hosts" of Octopus project cmdemo1-fworker-runtime, run every 5 minutes on each target with tag fworker-host:
#   1. POST {Fworker.GatewayUrl}/api/v1/runtime/octopus/report  {host, containers:[{name,item,state,startedAt,memPeakMb?}], memTotalMb?, memAvailableMb?,
#      cpus?, load1?, diskFreeMb?} of the fw-* containers and of the host (W7.4, delivery-speed.md 4.2: the gateway's headroom rule reads them). A metric that
#      cannot be read is left out, never guessed, and a gateway that predates the fields ignores them. memPeakMb is the container's cgroup memory.peak in MB
#      (running containers only: the cgroup of an exited one is gone);
#      (first, so a gateway that has just restarted adopts the running ones before it is asked what it wants);
#   2. GET {Fworker.GatewayUrl}/api/v1/runtime/octopus/desired?host=<machine>  ->  {"workers":["name",...]}; without a workers array
#      (the gateway has no report of this host yet) nothing is removed;
#   3. remove every fw-* container that is not desired or has exited (its last 50 log lines go to the task log first);
#   4. report again, the containers left.
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

# ---- host metrics (W7.4). The same reading is in start-worker.sh (the scripts are pasted into Octopus steps, so they cannot share a file).
# FWORKER_PROC_DIR and FWORKER_CGROUP_DIR move the readings to a fixture in the tests; on a host they are /proc and /sys/fs/cgroup.
procDir=${FWORKER_PROC_DIR:-/proc}; cgroupDir=${FWORKER_CGROUP_DIR:-/sys/fs/cgroup}
# host_metrics: prints ,"memTotalMb":N,"memAvailableMb":N,"cpus":N,"load1":N.NN,"diskFreeMb":N with the values that could be read
host_metrics() {
    local k v memTotal='' memAvail='' cpus='' load1='' rest='' root dir dfLine avail out=''
    if [ -r "$procDir/meminfo" ]; then
        while read -r k v _; do
            case "$k" in MemTotal:) memTotal=$v ;; MemAvailable:) memAvail=$v ;; esac
        done < "$procDir/meminfo"
    fi
    if [[ "$memTotal" =~ ^[0-9]+$ ]]; then out+=",\"memTotalMb\":$((memTotal / 1024))"; fi
    if [[ "$memAvail" =~ ^[0-9]+$ ]]; then out+=",\"memAvailableMb\":$((memAvail / 1024))"; fi
    if [ -r "$procDir/cpuinfo" ]; then cpus=$(grep -c '^processor' "$procDir/cpuinfo" || true); fi
    if [[ "$cpus" =~ ^[0-9]+$ ]] && [ "$cpus" -gt 0 ]; then out+=",\"cpus\":$cpus"; fi
    if [ -r "$procDir/loadavg" ]; then read -r load1 rest < "$procDir/loadavg" || true; fi
    if [[ "$load1" =~ ^[0-9]+(\.[0-9]+)?$ ]]; then out+=",\"load1\":$load1"; fi
    # the filesystem the containers' clones and build output are written to: docker's data root, else /
    root=$(docker info --format '{{.DockerRootDir}}' 2> /dev/null || true)
    for dir in "$root" /var/lib/docker /; do
        [ -n "$dir" ] || continue
        dfLine=$(df -Pm "$dir" 2> /dev/null | sed -n '2p') || continue
        [ -n "$dfLine" ] || continue
        read -r _ _ _ avail _ <<< "$dfLine"
        if [[ "$avail" =~ ^[0-9]+$ ]]; then out+=",\"diskFreeMb\":$avail"; break; fi
    done
    printf '%s' "$out"
}
# peak_mb ID: the container's peak memory in MB from its cgroup (v2 memory.peak; a docker stats sample misses peaks); nothing when the kernel or the cgroup driver has no such file
peak_mb() {
    local f bytes
    for f in "$cgroupDir/system.slice/docker-$1.scope/memory.peak" "$cgroupDir/docker/$1/memory.peak"; do
        [ -r "$f" ] || continue
        read -r bytes < "$f" || continue
        if [[ "$bytes" =~ ^[0-9]+$ ]]; then printf '%s' "$((bytes / 1048576))"; return 0; fi
    done
    return 0
}

report() {
    entries=''
    # the fields are tab separated and none is empty (a container without the item label reads as 0): read would merge adjacent tabs and shift the fields
    while IFS=$'\t' read -r cname item state started cid; do
        [ -n "$cname" ] || continue
        [[ "$item" =~ ^[0-9]+$ ]] || item=0
        peak=''; if [ "$state" = running ] && [ -n "${cid:-}" ]; then peak=$(peak_mb "$cid"); fi
        entries+="${entries:+,}{\"name\":\"${cname#fw-}\",\"item\":$item,\"state\":\"$state\",\"startedAt\":\"$started\"${peak:+,\"memPeakMb\":$peak}}"
    done < <(docker ps -a --filter name='^fw-' --format '{{.Names}}' | while read -r c; do
        docker inspect --format '{{.Name}}{{"\t"}}{{or (index .Config.Labels "fworker.item") "0"}}{{"\t"}}{{.State.Status}}{{"\t"}}{{.State.StartedAt}}{{"\t"}}{{.Id}}' "$c" | sed 's#^/##'
    done)
    body=$(printf '{"host":"%s","containers":[%s]%s}' "$hostName" "$entries" "$(host_metrics)")
    curl -fsS --max-time 30 -X POST -H @<(auth) -H 'Content-Type: application/json' --data-binary @- \
        "$gateway/api/v1/runtime/octopus/report" <<< "$body" > /dev/null || fail "report: the gateway did not accept it"
    echo "Reported $(printf '%s' "$entries" | grep -o '"name"' | wc -l) container(s); removed $removed"
}

removed=0
report

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
report
