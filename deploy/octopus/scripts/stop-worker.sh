#!/usr/bin/env bash
# Runbook "Stop worker" of Octopus project cmdemo1-fworker-runtime: removes container fw-<Fworker.Name> on this host. Idempotent: a missing
# container is success. The last 50 log lines go to the task log first.
set -euo pipefail

ov() {
    if declare -F get_octopusvariable > /dev/null; then get_octopusvariable "$1"
    else local n; n=$(printf '%s' "${1//./_}" | tr '[:lower:]' '[:upper:]'); printf '%s' "${!n:-}"; fi
}

name=$(ov Fworker.Name)
[[ "$name" =~ ^[a-z0-9-]{1,40}$ ]] || { echo "ERROR: Fworker.Name must match ^[a-z0-9-]{1,40}\$" >&2; exit 1; }
container="fw-$name"
if docker inspect "$container" > /dev/null 2>&1; then
    echo "Last 50 log lines of $container:"; docker logs --tail 50 "$container" 2>&1 || true
    docker rm -f "$container" > /dev/null
    echo "Removed $container"
else
    echo "$container does not exist on this host: nothing to stop"
fi
