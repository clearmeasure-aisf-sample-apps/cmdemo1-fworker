#!/usr/bin/env bash
# Runbook "Start worker" and "Start worker (overflow)" of Octopus project cmdemo1-fworker-runtime (deploy/octopus/main.tf).
# Starts one factory worker container fw-<Fworker.Name> from the fworker-host image (Dockerfile.fwrunner, runner/fwrunner.sh).
#   Fworker.Mode=detached (default): docker run -d --restart=no, wait up to Fworker.StartTimeoutSeconds (180) for the line
#     "nonce redeemed" (fwrunner.sh: the gateway accepted the worker), exit 0; no such line in time or the container ended: logs, exit 1.
#   Fworker.Mode=held (overflow, Hosted Ubuntu worker pool): docker run attached; the task ends with the container's exit code.
# Values come from Octopus (get_octopusvariable) or, outside Octopus (tests/octopus), from env: Fworker.Name -> FWORKER_NAME and so on.
# Secrets reach docker through a mode-600 env file that is removed on exit; nothing here prints a secret.
set -euo pipefail

ov() {
    if declare -F get_octopusvariable > /dev/null; then get_octopusvariable "$1"
    else local n; n=$(printf '%s' "${1//./_}" | tr '[:lower:]' '[:upper:]'); printf '%s' "${!n:-}"; fi
}
fail() { echo "ERROR: $*" >&2; exit 1; }

name=$(ov Fworker.Name); item=$(ov Fworker.Item); phase=$(ov Fworker.Phase); profile=$(ov Fworker.ModelProfile)
nonce=$(ov Fworker.Nonce); agent=$(ov Fworker.Agent); mode=$(ov Fworker.Mode); role=$(ov Fworker.Role)
gateway=$(ov Fworker.GatewayUrl); image=$(ov Fworker.Image)
cpus=$(ov Fworker.Cpus); memory=$(ov Fworker.Memory); timeout=$(ov Fworker.StartTimeoutSeconds)
# the overflow runbook prepends FWORKER_MODE_FORCE=held (a dynamic worker must not leave a container behind)
[ "${FWORKER_MODE_FORCE:-}" = held ] && mode=held
agent=${agent:-claude}; mode=${mode:-detached}; cpus=${cpus:-2}; memory=${memory:-8g}; timeout=${timeout:-180}

[[ "$name" =~ ^[a-z0-9-]{1,40}$ ]] || fail "Fworker.Name must match ^[a-z0-9-]{1,40}\$"
[[ "$item" =~ ^[0-9]{1,9}$ ]] || fail "Fworker.Item must be a whole number"
[[ "$phase" =~ ^[A-Za-z0-9._-]{0,40}$ ]] || fail "Fworker.Phase has characters outside [A-Za-z0-9._-]"
[[ "$profile" =~ ^[A-Za-z0-9._-]{0,60}$ ]] || fail "Fworker.ModelProfile has characters outside [A-Za-z0-9._-]"
[[ "$role" =~ ^[a-z0-9-]{0,40}$ ]] || fail "Fworker.Role must match ^[a-z0-9-]{0,40}\$"
[[ "$nonce" =~ ^[A-Za-z0-9_.~-]{1,200}$ ]] || fail "Fworker.Nonce is missing or has characters outside [A-Za-z0-9_.~-]"
case "$agent" in claude) keyVar=ANTHROPIC_API_KEY; keyName=Fworker.AnthropicApiKey ;; cursor) keyVar=CURSOR_API_KEY; keyName=Fworker.CursorApiKey ;;
    codex) keyVar=OPENAI_API_KEY; keyName=Fworker.OpenAiApiKey ;; *) fail "Fworker.Agent must be claude, cursor or codex" ;; esac
case "$mode" in detached|held) ;; *) fail "Fworker.Mode must be detached or held" ;; esac
[[ "$gateway" =~ ^https?://[A-Za-z0-9.:-]+(/[A-Za-z0-9._/-]*)?$ ]] || fail "Fworker.GatewayUrl is missing or not a plain http(s) URL"
[[ "$image" =~ ^[a-z0-9./:_@-]+$ ]] || fail "Fworker.Image is missing or malformed"
[[ "$cpus" =~ ^[0-9]+(\.[0-9]+)?$ ]] || fail "Fworker.Cpus must be a number"
[[ "$memory" =~ ^[0-9]+[kmg]?$ ]] || fail "Fworker.Memory must be like 8g"
[[ "$timeout" =~ ^[0-9]+$ ]] || fail "Fworker.StartTimeoutSeconds must be whole seconds"
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

DOCKER_CONFIG="$pullConfig" docker pull --quiet "$image" > /dev/null || fail "docker pull of $image failed"
rm -rf "$pullConfig"; pullConfig=''

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
    printf 'FWRUNNER_AGENT=%s\nFWRUNNER_AUTH=key\nFWRUNNER_RUNTIME=octopus-host\nFWRUNNER_ROLE=%s\n' "$agent" "$role"
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
