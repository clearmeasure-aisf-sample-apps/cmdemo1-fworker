#!/usr/bin/env bash
# Installs a self-hosted fworker host: Docker, and the Octopus Linux Tentacle as a POLLING tentacle registered as a deployment target with
# target tag fworker-host in tdd of the cmdemo1 demo space. Idempotent: run it again to repair; an instance that is already configured is
# not registered twice. Debian/Ubuntu (apt) and RHEL/Fedora (dnf) are supported.
#
#   sudo OCTOPUS_SERVER_URL=https://clearmeasure.octopus.app OCTOPUS_SPACE=Spaces-355 OCTOPUS_ENVIRONMENT=tdd \
#        bash deploy/hosts/install-fworker-host.sh          # the registration API key is prompted (not echoed) when OCTOPUS_API_KEY is unset
#
# Inputs (env, or prompted when unset):
#   OCTOPUS_SERVER_URL   Octopus server URL                 OCTOPUS_SPACE        space name or id (Spaces-355)
#   OCTOPUS_ENVIRONMENT  environment (tdd)                  OCTOPUS_API_KEY      API key allowed to register targets (never stored, never printed)
#   FWORKER_HOST_NAME    machine name in Octopus (default: the host name); the Reconcile hosts runbook asks the gateway by this name
#   FWORKER_TARGET_TAG   target tag (fworker-host)          TENTACLE_INSTANCE    tentacle instance name (fworker)
#   TENTACLE_USER        service user, added to the docker group (octopus)
#   OCTOPUS_POLLING_PORT polling port of the server (10943)
#   FWORKER_CAPABILITIES comma separated capabilities of this host (dotnet,browser): each becomes a second target tag fworker-cap-<name> (the gateway reads
#                        the tags with the machine, runtimes.octopus.capabilityTagPrefix). Used when the instance is registered; for a machine that is
#                        registered already, add the tags in Octopus (Infrastructure > Deployment Targets > the machine > Roles).
set -euo pipefail

[ "$(id -u)" = 0 ] || { echo "Run as root (sudo)." >&2; exit 1; }

ask() { # ask VAR "prompt" [secret]
    local v=$1 p=$2 s=${3:-}
    if [ -z "${!v:-}" ]; then
        [ -t 0 ] || { echo "$v is not set and there is no terminal to ask." >&2; exit 1; }
        if [ -n "$s" ]; then read -r -s -p "$p: " "${v?}"; echo; else read -r -p "$p: " "${v?}"; fi
    fi
    [ -n "${!v:-}" ] || { echo "$v is required." >&2; exit 1; }
}

ask OCTOPUS_SERVER_URL "Octopus server URL (https://clearmeasure.octopus.app)"
ask OCTOPUS_SPACE "Space (Spaces-355)"
ask OCTOPUS_ENVIRONMENT "Environment (tdd)"
hostName=${FWORKER_HOST_NAME:-$(hostname -s)}
tag=${FWORKER_TARGET_TAG:-fworker-host}
instance=${TENTACLE_INSTANCE:-fworker}
tuser=${TENTACLE_USER:-octopus}
port=${OCTOPUS_POLLING_PORT:-10943}
[[ "$hostName" =~ ^[A-Za-z0-9._-]{1,100}$ ]] || { echo "FWORKER_HOST_NAME has characters outside [A-Za-z0-9._-]" >&2; exit 1; }
capabilityRoles=()
if [ -n "${FWORKER_CAPABILITIES:-}" ]; then
    IFS=, read -r -a caps <<< "$FWORKER_CAPABILITIES"
    for cap in "${caps[@]}"; do
        [[ "$cap" =~ ^[a-z0-9][a-z0-9-]{0,39}$ ]] || { echo "FWORKER_CAPABILITIES: $cap is not ^[a-z0-9][a-z0-9-]{0,39}\$" >&2; exit 1; }
        capabilityRoles+=(--role "fworker-cap-$cap")
    done
fi
[[ "$instance" =~ ^[A-Za-z0-9_-]+$ ]] || { echo "TENTACLE_INSTANCE has characters outside [A-Za-z0-9_-]" >&2; exit 1; }

if command -v apt-get > /dev/null; then pm=apt; elif command -v dnf > /dev/null; then pm=dnf; else echo "Neither apt-get nor dnf: unsupported distribution." >&2; exit 1; fi

# 1. Docker (the convenience script of docker.com installs the engine and enables the service)
if ! command -v docker > /dev/null; then
    echo "Installing Docker"
    curl -fsSL https://get.docker.com -o /tmp/get-docker.sh
    sh /tmp/get-docker.sh
    rm -f /tmp/get-docker.sh
fi
systemctl enable --now docker

# 2. The Tentacle package (Octopus's apt / rpm repository)
tentacle=/opt/octopus/tentacle/Tentacle
if [ ! -x "$tentacle" ]; then
    echo "Installing the Octopus Tentacle"
    if [ "$pm" = apt ]; then
        apt-get update -q && apt-get install -y -q --no-install-recommends gnupg curl ca-certificates
        install -d -m 0755 /etc/apt/keyrings
        curl -fsSL https://apt.octopus.com/public.key | gpg --dearmor --yes -o /etc/apt/keyrings/octopus.gpg
        echo "deb [signed-by=/etc/apt/keyrings/octopus.gpg] https://apt.octopus.com/ stable main" > /etc/apt/sources.list.d/octopus.com.list
        apt-get update -q && apt-get install -y -q tentacle
    else
        curl -fsSL https://rpm.octopus.com/tentacle.repo -o /etc/yum.repos.d/tentacle.repo
        dnf install -y tentacle
    fi
fi

# 3. The service user, allowed to run docker
id "$tuser" > /dev/null 2>&1 || useradd --system --create-home --shell /usr/sbin/nologin "$tuser"
usermod -aG docker "$tuser"

# 4. The polling instance, registered once
config="/etc/octopus/$instance/tentacle-$instance.config"
appDir="/home/Octopus/Applications"
if [ ! -f "$config" ]; then
    ask OCTOPUS_API_KEY "Octopus API key for the registration (not shown)" secret
    echo "Creating tentacle instance $instance"
    "$tentacle" create-instance --instance "$instance" --config "$config"
    "$tentacle" new-certificate --instance "$instance" --if-blank
    "$tentacle" configure --instance "$instance" --app "$appDir" --noListen "True" --reset-trust
    echo "Registering $hostName in $OCTOPUS_ENVIRONMENT with target tag $tag${capabilityRoles[*]:+ and $(printf '%s' "${capabilityRoles[*]}" | sed 's/--role //g')} (polling)"
    # the key goes to the Tentacle on its command line only for the duration of this call; it is not written to any file here
    "$tentacle" register-with --instance "$instance" --server "$OCTOPUS_SERVER_URL" --apiKey "$OCTOPUS_API_KEY" --space "$OCTOPUS_SPACE" \
        --name "$hostName" --comms-style "TentacleActive" --server-comms-port "$port" --env "$OCTOPUS_ENVIRONMENT" --role "$tag" "${capabilityRoles[@]}" --policy "Default Machine Policy"
    unset OCTOPUS_API_KEY
else
    echo "Tentacle instance $instance is already configured ($config): registration skipped"
fi
mkdir -p "$appDir"
chown -R "$tuser" "/etc/octopus/$instance" "$appDir"

# 5. The service, as the service user
# only "already installed" is tolerated; any other failure stops the install
if ! out=$("$tentacle" service --install --instance "$instance" 2>&1); then
    if grep -qi 'already installed\|already exists' <<< "$out"; then echo "Tentacle service of $instance is already installed"
    else echo "$out" >&2; echo "tentacle service --install failed" >&2; exit 1; fi
fi
unit=$(systemctl list-unit-files --type=service --no-legend | awk '{print $1}' | grep -ix "${instance}.service" | head -n 1 || true)
if [ -n "$unit" ]; then
    mkdir -p "/etc/systemd/system/$unit.d"
    printf '[Service]\nUser=%s\nGroup=%s\nSupplementaryGroups=docker\n' "$tuser" "$tuser" > "/etc/systemd/system/$unit.d/fworker-user.conf"
    systemctl daemon-reload
    systemctl enable "$unit" > /dev/null 2>&1 || true
    systemctl restart "$unit"
else
    echo "WARNING: no systemd unit $instance.service found; started through the Tentacle as root" >&2
    "$tentacle" service --start --instance "$instance"
fi

echo "Done: $hostName polls $OCTOPUS_SERVER_URL ($OCTOPUS_SPACE, $OCTOPUS_ENVIRONMENT, tag $tag); $tuser can run docker."
