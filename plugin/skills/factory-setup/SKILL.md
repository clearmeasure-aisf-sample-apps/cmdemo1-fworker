---
name: factory-setup
description: Use when asked to set up, verify or troubleshoot the factory worker on this machine: client configuration, gateway reachability, secrets check, tools needed to build.
---

# Setup

The machine holds no GitHub or Octopus secret. It needs `FACTORY_URL` and `FACTORY_TOKEN` (or `~/.config/factory-worker/client.json`).
1. `factory.ps1 check-secrets` (the gateway runs the read-only probes; names and pass/fail only).
2. Local tools: `pwsh`, `git`, `claude`, and whatever the target repo builds with (`dotnet`, Playwright, ffmpeg for video items).
3. Subscribe: start `pwsh .../factory.ps1 subscribe` under the Monitor tool (one event per line), or `subscribe --spawn` for headless workers.
Never print token values.
