# Health checks of the fworker hosts (Octopus machine policy)

Why: on 2026-11-01 the tdd host omarchy (Machines-641, space Spaces-355) lost its Tentacle connection a few times between 18:31Z and 20:22Z. Octopus marked it
Unavailable and did not check it again soon. The runbook Reconcile hosts (Runbooks-388, every 5 minutes) targets healthy machines only, so it skipped omarchy
for 6 hours; with no host report the CapacityScheduler granted no slot (`host-headroom: no host reported` after 3 x `scheduler.hostReportMaxMinutes`) and feature
work stalled until an operator ran a health check by hand.

The prevention: a machine policy `fworker-host` whose health-check interval is 10 minutes (connectivity only), assigned to the fworker-host targets. An
Unavailable host then clears itself within one interval after its Tentacle is back. `deploy/octopus/main.tf` holds the same policy as
`octopusdeploy_machine_policy.fworker_host` (variables `host_machine_policy_name`, `host_health_check_minutes`); the live project has no Terraform state, so
the policy is created through the API as below. An operator or a session with an Octopus key of a space manager runs the steps; the key is never printed or stored.

```bash
export OCTOPUS_URL=https://clearmeasure.octopus.app OCTOPUS_APIKEY=...      # a space manager's key, from the keyring
SPACE=Spaces-355
TDD_URL=...; OPERATOR_TOKEN=...                                             # the tdd gateway's url and the operator token (keyring), step 4 only
H=(-H "X-Octopus-ApiKey: $OCTOPUS_APIKEY" -H 'Content-Type: application/json')

# 1. Create the policy as a copy of the Default Machine Policy with the short interval (the rest stays as it is).
curl -fsS "${H[@]}" "$OCTOPUS_URL/api/$SPACE/machinepolicies/all" | jq -r '.[] | [.Id, .Name, .IsDefault] | @tsv'      # find the default's id
DEFAULT_ID=...                                                                                                         # the row with IsDefault true
curl -fsS "${H[@]}" "$OCTOPUS_URL/api/$SPACE/machinepolicies/$DEFAULT_ID" \
  | jq 'del(.Id, .Links, .LastModifiedOn, .LastModifiedBy)
        | .Name = "fworker-host" | .IsDefault = false
        | .Description = "fworker-host targets: health check every 10 minutes (connectivity only) so an Unavailable host recovers by itself."
        | .MachineHealthCheckPolicy.HealthCheckInterval = "00:10:00"
        | .MachineHealthCheckPolicy.HealthCheckType = "OnlyConnectivity"
        | del(.MachineHealthCheckPolicy.HealthCheckCron, .MachineHealthCheckPolicy.HealthCheckCronTimezone)' > /tmp/fworker-host-policy.json
POLICY_ID=$(curl -fsS "${H[@]}" -X POST "$OCTOPUS_URL/api/$SPACE/machinepolicies" -d @/tmp/fworker-host-policy.json | jq -r .Id)
echo "$POLICY_ID"

# 2. Assign Machines-641 (and every other target with the tag fworker-host).
curl -fsS "${H[@]}" "$OCTOPUS_URL/api/$SPACE/machines/Machines-641" | jq --arg p "$POLICY_ID" '.MachinePolicyId = $p' > /tmp/m641.json
curl -fsS "${H[@]}" -X PUT "$OCTOPUS_URL/api/$SPACE/machines/Machines-641" -d @/tmp/m641.json | jq '{Name, Status, MachinePolicyId}'
# other hosts: GET /api/$SPACE/machines?roles=fworker-host, then the same GET, set, PUT per machine.

# 3. Clear a stale Unavailable now (this is also the manual fix of the incident).
curl -fsS "${H[@]}" -X POST "$OCTOPUS_URL/api/$SPACE/tasks" \
  -d '{"Name":"Health","Description":"fworker host health check","Arguments":{"Timeout":"00:05:00","MachineIds":["Machines-641"]},"SpaceId":"Spaces-355"}' | jq -r '.Id, .State'

# 4. Verify: the policy is assigned, the status is Healthy, and the gateway sees a host report (age under 10 minutes).
curl -fsS "${H[@]}" "$OCTOPUS_URL/api/$SPACE/machines/Machines-641" | jq '{Status, StatusSummary, MachinePolicyId}'
curl -fsS -H "Authorization: Bearer $OPERATOR_TOKEN" "$TDD_URL/api/v1/status" | jq '.v2.capacity.hostReports'
rm -f /tmp/fworker-host-policy.json /tmp/m641.json
```

Read the default policy's JSON before step 1's POST and check that no other field names a schedule type. A host that is switched off on purpose keeps
`noHostReported` true (the ledger never forgets a host), so the alert repeats until the host reports again or the gateway's ledger is reset.

New hosts: `deploy/hosts/install-fworker-host.sh` registers with the policy named by `FWORKER_HOST_POLICY` (default `Default Machine Policy`; set
`FWORKER_HOST_POLICY=fworker-host` after step 1). Rollback: assign the Default policy again (its `MachinePolicyId`) and delete the policy.

## What the gateway shows

`/api/v1/status` `v2.capacity`:

- `waiting`: the waiting codes of the queued requests, counted, with the oldest wait (`host-headroom`, `runtime-budget`, `file-overlap`, ...; `unjudged` before the first decision).
- `hostReports.hosts[]`: per host `reportedAt`, `reportAgeSeconds` and `state` (`fresh`; `stale` past `hostReportMaxMinutes`: no burst; `gone` past three times that: no slot).
- `hostReports.freshestAgeSeconds` and `hostReports.noHostReported`: true when the ledger knows hosts and none reported for 30 minutes.

Every 5 minutes the acting gateway writes the line `{"hostReports":"none",...}` to its console while `noHostReported` holds; the Azure Monitor rule
`tdd-fworker-no-host-report` (docs/deploy/incident-alert-rules.md 5) counts it and mails the owner.
