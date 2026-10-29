# fworker hosts

Self-hosted machines that run factory worker containers for the Octopus project `cmdemo1-fworker-runtime` (deploy/octopus).
Each host is an Octopus deployment target in `tdd` with target tag `fworker-host`, reached through a polling Tentacle (no inbound port).

## Linux

```bash
sudo OCTOPUS_SERVER_URL=https://clearmeasure.octopus.app OCTOPUS_SPACE=Spaces-355 OCTOPUS_ENVIRONMENT=tdd \
     bash deploy/hosts/install-fworker-host.sh
```

The registration API key is prompted without echo (or taken from `OCTOPUS_API_KEY`); it is never stored by the script.
The script installs Docker if missing, the Tentacle from Octopus's package repository, creates the service user `octopus`
(member of `docker`), registers the polling instance `fworker` once, and runs the service as that user. Running it again is safe.

Capabilities (W7.4): `FWORKER_CAPABILITIES=dotnet,browser` registers the host with the extra target tags `fworker-cap-dotnet` and `fworker-cap-browser`.
A host registered earlier gets its tags in Octopus (Infrastructure > Deployment Targets > the machine > Roles, or the machines API). The runbooks still
target `fworker-host` only; the gateway reads the capability tags together with the machine and shows them in `/api/v1/status` under the host.

The machine name in Octopus (`FWORKER_HOST_NAME`, default the short host name) is the `host` the gateway's desired list is keyed by.

Size: each worker container gets `Fworker.Cpus` (2) and `Fworker.Memory` (8g) by default; plan the host for the number of concurrent
workers the gateway assigns to it. Images are pulled from private GHCR with `Fworker.GhcrUsername` / `Fworker.GhcrToken`.

Security: membership of the `docker` group on a host equals root on it and access to the agents' API keys (`docker inspect` of a
worker container shows its environment, `ANTHROPIC_API_KEY`, `OPENAI_API_KEY` or `CURSOR_API_KEY` included). Add no other user to
`docker` on a fworker host; only the Tentacle's service user belongs there.

## Windows (outline)

- Install Docker Desktop with the WSL 2 backend, set to Linux containers, started at sign-in of a dedicated account (or Docker Engine in WSL).
- Install the Tentacle MSI, configure it as a polling Tentacle in Tentacle Manager (or `Tentacle.exe register-with ... --comms-style TentacleActive`),
  environment `tdd`, target tag `fworker-host`, service running as the account that owns Docker.
- The runbook scripts are bash: give the target a bash (Git for Windows or WSL) on the PATH, or add PowerShell twins of
  `deploy/octopus/scripts/*.sh` before using Windows hosts.

## macOS (outline)

- Docker Desktop (or Colima) for Linux containers; Apple silicon needs a linux/amd64 build or a multi-arch image (the image is amd64 today).
- Octopus has no macOS Tentacle: register the Mac as an SSH target (deployment target over SSH, Mono-free Calamari for osx-x64/arm64)
  with tag `fworker-host`, or run a Linux VM with the Linux installer above.
