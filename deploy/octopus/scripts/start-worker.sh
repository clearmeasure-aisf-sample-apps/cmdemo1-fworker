#!/usr/bin/env bash
# Runbook "Start worker" and "Start worker (overflow)" of Octopus project cmdemo1-fworker-runtime (deploy/octopus/main.tf).
# Starts one factory worker container fw-<Fworker.Name> from the fworker-host image (Dockerfile.fwrunner, runner/fwrunner.sh).
#   Fworker.Mode=detached (default): docker run -d --restart=no, wait up to Fworker.StartTimeoutSeconds (180) for the line
#     "nonce redeemed" (fwrunner.sh: the gateway accepted the worker), exit 0; no such line in time or the container ended: logs, exit 1.
#   Fworker.Mode=held (overflow, Hosted Ubuntu worker pool): docker run attached; the task ends with the container's exit code.
#   Host admission (W7.4, delivery-speed.md 4.2 "Start (host, authoritative)"; detached mode only): when Fworker.PerHostCapacity is set the script counts the
#     running fw-* containers itself, waiting up to Fworker.HostFullWaitSeconds (30) for one that is exiting, and refuses a start the host cannot take with the line
#     "host-full: <why>" and exit code 75. Up to PerHostCapacity a slot is guaranteed (no memory test). Above it, up to Fworker.PerHostBurst, a burst slot is
#     taken only when the headroom rule of the gateway holds: Fworker.FootprintMb is known, memAvailableMb - Fworker.ReserveMb (2048) >= FootprintMb,
#     load1 + Fworker.Cpus <= cpus, diskFreeMb >= Fworker.MinDiskMb (4096). Without Fworker.PerHostCapacity (every runbook before W7.4) nothing is checked.
# Values come from Octopus (get_octopusvariable) or, outside Octopus (tests/octopus), from env: Fworker.Name -> FWORKER_NAME and so on.
# Secrets reach docker through a mode-600 env file that is removed on exit; nothing here prints a secret.
set -euo pipefail

ov() {
    if declare -F get_octopusvariable > /dev/null; then get_octopusvariable "$1"
    else local n; n=$(printf '%s' "${1//./_}" | tr '[:lower:]' '[:upper:]'); printf '%s' "${!n:-}"; fi
}
fail() { echo "ERROR: $*" >&2; exit 1; }

name=$(ov Fworker.Name); item=$(ov Fworker.Item); phase=$(ov Fworker.Phase); profile=$(ov Fworker.ModelProfile)
nonce=$(ov Fworker.Nonce); agent=$(ov Fworker.Agent); mode=$(ov Fworker.Mode)
gateway=$(ov Fworker.GatewayUrl); image=$(ov Fworker.Image)
cpus=$(ov Fworker.Cpus); memory=$(ov Fworker.Memory); timeout=$(ov Fworker.StartTimeoutSeconds)
perHost=$(ov Fworker.PerHostCapacity); burst=$(ov Fworker.PerHostBurst); footprint=$(ov Fworker.FootprintMb)
reserve=$(ov Fworker.ReserveMb); minDisk=$(ov Fworker.MinDiskMb); fullWait=$(ov Fworker.HostFullWaitSeconds)
# the overflow runbook prepends FWORKER_MODE_FORCE=held (a dynamic worker must not leave a container behind)
[ "${FWORKER_MODE_FORCE:-}" = held ] && mode=held
agent=${agent:-claude}; mode=${mode:-detached}; cpus=${cpus:-2}; memory=${memory:-8g}; timeout=${timeout:-180}
burst=${burst:-$perHost}; reserve=${reserve:-2048}; minDisk=${minDisk:-4096}; fullWait=${fullWait:-30}

[[ "$name" =~ ^[a-z0-9-]{1,40}$ ]] || fail "Fworker.Name must match ^[a-z0-9-]{1,40}\$"
[[ "$item" =~ ^[0-9]{1,9}$ ]] || fail "Fworker.Item must be a whole number"
[[ "$phase" =~ ^[A-Za-z0-9._-]+( [A-Za-z0-9._-]+)*$|^$ ]] && [ ${#phase} -le 40 ] || fail "Fworker.Phase must be words of [A-Za-z0-9._-] separated by single spaces (at most 40)"
[[ "$profile" =~ ^[A-Za-z0-9._-]+( [A-Za-z0-9._-]+)*$|^$ ]] && [ ${#profile} -le 60 ] || fail "Fworker.ModelProfile must be words of [A-Za-z0-9._-] separated by single spaces (at most 60)"
[[ "$nonce" =~ ^[A-Za-z0-9_.~-]{1,200}$ ]] || fail "Fworker.Nonce is missing or has characters outside [A-Za-z0-9_.~-]"
case "$agent" in claude) keyVar=ANTHROPIC_API_KEY; keyName=Fworker.AnthropicApiKey ;; cursor) keyVar=CURSOR_API_KEY; keyName=Fworker.CursorApiKey ;;
    codex) keyVar=OPENAI_API_KEY; keyName=Fworker.OpenAiApiKey ;; *) fail "Fworker.Agent must be claude, cursor or codex" ;; esac
case "$mode" in detached|held) ;; *) fail "Fworker.Mode must be detached or held" ;; esac
[[ "$gateway" =~ ^https?://[A-Za-z0-9.:-]+(/[A-Za-z0-9._/-]*)?$ ]] || fail "Fworker.GatewayUrl is missing or not a plain http(s) URL"
[[ "$image" =~ ^[a-z0-9./:_@-]+$ ]] || fail "Fworker.Image is missing or malformed"
[[ "$cpus" =~ ^[0-9]+(\.[0-9]+)?$ ]] || fail "Fworker.Cpus must be a number"
[[ "$memory" =~ ^[0-9]+[kmg]?$ ]] || fail "Fworker.Memory must be like 8g"
[[ "$timeout" =~ ^[0-9]+$ ]] || fail "Fworker.StartTimeoutSeconds must be whole seconds"
[[ "$perHost" =~ ^[0-9]*$ ]] || fail "Fworker.PerHostCapacity must be a whole number"
for v in "$burst" "$footprint" "$reserve" "$minDisk" "$fullWait"; do [[ "$v" =~ ^[0-9]*$ ]] || fail "Fworker.PerHostBurst, FootprintMb, ReserveMb, MinDiskMb and HostFullWaitSeconds must be whole numbers"; done
key=$(ov "$keyName"); [ -n "$key" ] || fail "$keyName is not set (agent $agent)"

container="fw-$name"

# one trap removes the throwaway docker config (registry login) and the env file (the API key), whatever the exit
pullConfig=''; envFile=''
trap 'rm -rf "$pullConfig"; rm -f "$envFile"' EXIT

# registry login (private GHCR) when credentials are configured: the token goes on stdin only, into a throwaway DOCKER_CONFIG used
# for the login and the pull alone, so it never lands in ~/.docker/config.json; docker run uses the default config
ghcrUser=$(ov Fworker.GhcrUsername); ghcrToken=$(ov Fworker.GhcrToken)
pullConfig=$(mktemp -d); chmod 700 "$pullConfig"
if [ -n "$ghcrUser" ] && [ -n "$ghcrToken" ]; then
    printf '%s' "$ghcrToken" | DOCKER_CONFIG="$pullConfig" docker login ghcr.io --username "$ghcrUser" --password-stdin > /dev/null || fail "docker login ghcr.io failed"
fi
unset ghcrToken

# disk first: an ended worker keeps its clone and build output (several GB) until something removes it, and the new one needs the room.
# Ended fw-* containers go (their last 20 log lines into this task's log), then images no container uses (the previous fworker-host
# versions left by each pull of :latest)
while IFS=$'\t' read -r old oldState; do
    [ -n "$old" ] || continue
    case "$oldState" in running|restarting|paused) continue ;; esac   # never a live one, whatever the filter returned
    echo "Removing ended $old ($oldState); last 20 log lines:"
    docker logs --tail 20 "$old" 2>&1 | sed 's/^/    /' || true
    docker rm -f "$old" > /dev/null || echo "WARNING: could not remove $old" >&2
done < <(docker ps -a --filter name='^fw-' --filter status=exited --filter status=dead --format '{{.Names}}\t{{.State}}')

# ---- host admission (W7.4). The same readings are in reconcile-hosts.sh (the scripts are pasted into Octopus steps, so they cannot share a file).
# FWORKER_PROC_DIR moves them to a fixture in the tests; on a host it is /proc.
procDir=${FWORKER_PROC_DIR:-/proc}
running_count() {   # every running fw-* container, one that is exiting included
    local n=0 cname cstate
    while IFS=$'\t' read -r cname cstate; do
        case "$cstate" in running|restarting|paused) n=$((n + 1)) ;; esac
    done < <(docker ps -a --filter name='^fw-' --format '{{.Names}}\t{{.State}}')
    printf '%s' "$n"
}
milli() {   # 1.5 -> 1500: bash has no decimals
    local w=${1%%.*} f=''
    [[ "$1" == *.* ]] && f=${1#*.}
    f="${f}000"; printf '%s' "$((10#${w:-0} * 1000 + 10#${f:0:3}))"
}
host_full() { echo "host-full: $*"; exit 75; }
admit() {
    [ "$mode" = detached ] && [ -n "$perHost" ] || return 0
    local waited=0 running limit memAvail='' hostCpus='' load1='' rest='' k v dir dfLine avail='' root
    limit=$(( burst > perHost ? burst : perHost ))   # the limit is max(capacity, burst): a burst below the capacity never lowers it
    running=$(running_count)
    while [ "$((running + 1))" -gt "$limit" ] && [ "$waited" -lt "$fullWait" ]; do
        [ "$waited" = 0 ] && echo "$running running fw-* container(s) on this host, the limit is $limit; waiting up to $fullWait s for one to exit"
        sleep 2; waited=$((waited + 2)); running=$(running_count)
    done
    [ "$((running + 1))" -le "$limit" ] || host_full "$running running fw-* container(s), the limit is $limit"
    if [ "$((running + 1))" -le "$perHost" ]; then echo "Slot $((running + 1)) of $perHost (guaranteed): no headroom test"; return 0; fi
    # a burst slot: the gateway's rule (Fworker.Domain BurstAdmission.Check) again, on this host's own readings
    [ -n "$footprint" ] || host_full "slot $((running + 1)) is above the $perHost guaranteed and no footprint is known"
    if [ -r "$procDir/meminfo" ]; then
        while read -r k v _; do case "$k" in MemAvailable:) memAvail=$v ;; esac; done < "$procDir/meminfo"
    fi
    [[ "$memAvail" =~ ^[0-9]+$ ]] || host_full "burst slot: MemAvailable cannot be read"
    [ "$((memAvail / 1024 - reserve))" -ge "$footprint" ] || host_full "burst slot: $((memAvail / 1024)) MB available, $reserve MB reserved, the footprint is $footprint MB"
    if [ -r "$procDir/cpuinfo" ]; then hostCpus=$(grep -c '^processor' "$procDir/cpuinfo" || true); fi
    if [ -r "$procDir/loadavg" ]; then read -r load1 rest < "$procDir/loadavg" || true; fi
    [[ "$hostCpus" =~ ^[0-9]+$ ]] && [[ "$load1" =~ ^[0-9]+(\.[0-9]+)?$ ]] || host_full "burst slot: cpus or load cannot be read"
    [ "$(( $(milli "$load1") + $(milli "$cpus") ))" -le "$((hostCpus * 1000))" ] || host_full "burst slot: load $load1 + $cpus cpu(s) is over the host's $hostCpus"
    root=$(docker info --format '{{.DockerRootDir}}' 2> /dev/null || true)
    for dir in "$root" /var/lib/docker /; do
        [ -n "$dir" ] || continue
        dfLine=$(df -Pm "$dir" 2> /dev/null | sed -n '2p') || continue
        [ -n "$dfLine" ] || continue
        read -r _ _ _ avail _ <<< "$dfLine"
        [[ "$avail" =~ ^[0-9]+$ ]] && break
        avail=''
    done
    [[ "$avail" =~ ^[0-9]+$ ]] || host_full "burst slot: free disk cannot be read"
    [ "$avail" -ge "$minDisk" ] || host_full "burst slot: $avail MB of disk free, at least $minDisk MB needed"
    echo "Slot $((running + 1)) (burst): headroom holds"
}
admit

DOCKER_CONFIG="$pullConfig" docker pull --quiet "$image" > /dev/null || fail "docker pull of $image failed"
rm -rf "$pullConfig"; pullConfig=''
docker image prune -f > /dev/null 2>&1 || true

# a container of the same name: a running one is the same worker started twice (refused); an ended one is removed
if state=$(docker inspect --format '{{.State.Status}}' "$container" 2> /dev/null); then
    [ "$state" = running ] && fail "$container is already running"
    docker rm -f "$container" > /dev/null
fi

envFile=$(mktemp); chmod 600 "$envFile"
{
    printf 'FWORKER_NONCE=%s\n' "$nonce"
    printf 'FWORKER_NAME=%s\nFWORKER_ITEM=%s\nFWORKER_PHASE=%s\nFWORKER_MODEL_PROFILE=%s\n' "$name" "$item" "$phase" "$profile"
    printf 'FWORKER_RUNTIME=octopus\nFWORKER_GATEWAY_URL=%s\n' "$gateway"
    printf 'FWRUNNER_AGENT=%s\nFWRUNNER_AUTH=key\nFWRUNNER_RUNTIME=octopus-host\n' "$agent"
    printf '%s=%s\n' "$keyVar" "$key"
} > "$envFile"
# the label carries the nonce so the gateway can tell this start from an older one; the nonce is single use and spent at the redeem
args=(run --name "$container" --restart=no --cpus "$cpus" --memory "$memory" --env-file "$envFile"
    --label "fworker.name=$name" --label "fworker.item=$item" --label "fworker.nonce=$nonce" --label "fworker.mode=$mode")
unset key nonce

if [ "$mode" = held ]; then
    echo "Starting $container attached (held mode, agent $agent, item $item)"
    set +e; docker "${args[@]}" "$image"; code=$?; set -e
    docker rm -f "$container" > /dev/null 2>&1 || true
    echo "$container exited with $code"
    exit "$code"
fi

docker "${args[@]}" -d "$image" > /dev/null || fail "docker run of $container failed"
echo "Started $container (agent $agent, item $item); waiting up to $timeout s for the gateway registration"
deadline=$(( $(date +%s) + timeout ))
while :; do
    if docker logs "$container" 2>&1 | grep -q 'nonce redeemed'; then echo "$container registered at the gateway"; exit 0; fi
    st=$(docker inspect --format '{{.State.Status}}' "$container" 2> /dev/null || echo gone)
    if [ "$st" != running ] && [ "$st" != created ]; then
        echo "$container ended ($st) before it registered; its log:" >&2; docker logs --tail 200 "$container" >&2 2>&1 || true; exit 1
    fi
    if [ "$(date +%s)" -ge "$deadline" ]; then
        echo "$container did not register within $timeout s; its log:" >&2; docker logs --tail 200 "$container" >&2 2>&1 || true
        docker rm -f "$container" > /dev/null 2>&1 || true
        exit 1
    fi
    sleep 2
done
