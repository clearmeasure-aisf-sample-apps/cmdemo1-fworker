# Incident alert rules (W8.2)

What the factory's alert ingress (`POST /webhooks/azure-monitor`, `gateway/Endpoints/AlertsEndpoints.cs`) needs from Azure, and the alerts that
tell the owner when the factory itself is down. The rules are infrastructure of `clearmeasure-aisf-sample-apps/cmdemo1-system` (the infra
bicep, applied by the cmdemo1 session); this repository holds the gateway side and this specification. tdd only: no rule is created for uat or
prod (the gateway answers an alert of another environment with 202 and ignores it). The route exists only where the v2 engine acts
(`Engine.V2Acts`): in v1, in the shadow run (tdd, until the owner moves it to `v2Environments`) and under `FACTORY_V2_OFF` it answers 404. So create the
action group and the rules when tdd runs v2; before that Azure would get a 404 for every alert.

## 1. The two kinds of alert

| Kind | Goes to | Why |
|---|---|---|
| **App alerts**: the application deployed to tdd (deployable `ui`, Container App `ca-cmdemo1-tdd-ui`) | action group `ag-cmdemo1-tdd-incident`: the factory's webhook | they open or join an incident (`IncidentSaga`, W8.1) |
| **Factory alerts**: the factory itself (deployable `fworker`, Container App `ca-cmdemo1-tdd-fworker`, its database) | action group `ag-cmdemo1-tdd-factory-owner`: e-mail (and SMS) to the owner, **no webhook** | the factory's bus and database cannot carry the report of their own failure (`wolverine-factory.md` 3.3) |

A factory alert must never use the webhook action group. If one does, the gateway acknowledges it and drops it
(`factory-alert-is-out-of-band`) when the URI names deployable `fworker`.

## 2. The secret

One secret, 32 random bytes as hex (URL-safe: a `+` of base64 would be read as a space in the query), for the source `azure-monitor` (W4.0, `WebhookSecrets`).

- The gateway reads it from the environment variable **`FACTORY_WEBHOOK_SECRET_AZURE_MONITOR`** (a sensitive variable of the Octopus project
  `cmdemo1-fworker`, set on the Container App like `FACTORY_WEBHOOK_SECRET`). Unset or empty: every delivery is refused with 401.
- An action group cannot send a header, so the secret is the query parameter `secret` of the webhook URI (TLS only). The same value goes into the
  bicep as a `@secure()` parameter (`factoryAlertSecret`). A relay (Logic App, test with `curl`) may send it in the header `X-Azure-Monitor-Secret` instead.
- Rotate by setting the new value in both places in one release; deliveries signed with the old one are refused until the action group is updated.
- The gateway never logs the secret or the query. The Container Apps ingress and a proxy in front may log the URI: keep the log workspace
  readable by the owner only.

## 3. The webhook action group `ag-cmdemo1-tdd-incident`

```bicep
@secure() param factoryAlertSecret string
param tddFactoryHost string = 'ca-cmdemo1-tdd-fworker.graybush-19a6e13b.southcentralus.azurecontainerapps.io'

resource agIncident 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: 'ag-cmdemo1-tdd-incident'
  location: 'global'
  properties: {
    groupShortName: 'tddincident'      // 12 characters at most
    enabled: true
    webhookReceivers: [
      {
        name: 'fworker-tdd'
        // one action group per deployable and environment: the URI is the subject of the alert (the common schema does not name the factory's deployable)
        serviceUri: 'https://${tddFactoryHost}/webhooks/azure-monitor?deployable=ui&environment=tdd&secret=${factoryAlertSecret}'
        useCommonAlertSchema: true      // required: the gateway reads only azureMonitorCommonAlertSchema
        useAadAuth: false
      }
    ]
  }
}
```

The gateway answers 202 (stored, or ignored on purpose), 401 (secret), 400 (a delivery that is not usable: no retry), 503 with `Retry-After: 30` (the
inbox did not take it: Azure retries). One more deployable of the app would get its own action group with `deployable=<name>`; the name must be one of
`system.json`.

## 4. App alert rules (scope: Container App `ca-cmdemo1-tdd-ui`, action `agIncident`, `autoMitigate: true` so a Resolved reaches the factory)

All metric alerts: `evaluationFrequency: PT1M`, `windowSize` as listed, `targetResourceType: Microsoft.App/containerApps`.

| Rule name | Signal | Condition | Window | Sev |
|---|---|---|---|---|
| `tdd-ui-replicas-zero` | metric `Replicas`, Minimum | `< 1` | PT5M | 1 |
| `tdd-ui-5xx` | metric `Requests`, Total, dimension `statusCodeCategory` = `5xx` | `>= 10` | PT5M | 2 |
| `tdd-ui-restarts` | metric `RestartCount`, Total | `>= 3` | PT15M | 2 |
| `tdd-ui-availability` | Application Insights standard web test of `https://ca-cmdemo1-tdd-ui.graybush-19a6e13b.southcentralus.azurecontainerapps.io/_healthcheck/detailed` (every 5 minutes from 5 locations, expect 200) with a metric alert on the web test (`Microsoft.Azure.Monitor.WebtestLocationAvailabilityCriteria`) | `failedLocationCount >= 2` | PT5M | 1 |
| `tdd-ui-exceptions` | scheduled query rule v2 on the Application Insights resource: `exceptions \| where timestamp > ago(5m) \| summarize count()` | count `>= 20` | PT5M | 2 |
| `tdd-ui-deadletter` (only where the app has a Service Bus namespace) | metric `DeadletteredMessages` of the namespace, Average | `> 0` | PT15M | 3 |

The thresholds are a starting point, tuned from the staged incident of the W8 gate. The confirmation (two failed locations, a five minute window) is the
rule that keeps the phantom incidents of 2026-09-13 away; the factory's own confirmed-Unhealthy trigger (`HealthCheckMonitor`'s rule) is a separate signal in W8.1.
Fired and Resolved are both sent; the IncidentSaga joins further alerts of an open incident and suppresses those that arrive while the environment is deploying.

## 5. Factory alert rules, out of band (scope: the factory's resources, action group `ag-cmdemo1-tdd-factory-owner`)

```bicep
param ownerEmail string
resource agOwner 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: 'ag-cmdemo1-tdd-factory-owner'
  location: 'global'
  properties: {
    groupShortName: 'factoryown'
    enabled: true
    emailReceivers: [ { name: 'owner', emailAddress: ownerEmail, useCommonAlertSchema: true } ]
    // smsReceivers: the owner's number, if wanted. Never a webhook receiver: the point is that nothing here depends on the factory.
  }
}
```

| Rule name | Signal | Condition | Window | Sev |
|---|---|---|---|---|
| `tdd-fworker-replicas-zero` | metric `Replicas`, Minimum, of `ca-cmdemo1-tdd-fworker` | `< 1` | PT5M | 1 |
| `tdd-fworker-not-ready` | Application Insights standard web test of `https://ca-cmdemo1-tdd-fworker.graybush-19a6e13b.southcentralus.azurecontainerapps.io/ready` (every 5 minutes from 5 locations, expect 200; `/ready` answers 503 while the schema, the revocations or the prompts are not ready, and 503 `gateway-replaced` for a process a newer one replaced) with the web test metric alert | `failedLocationCount >= 2` | PT10M | 1 |
| `tdd-fworker-5xx` | metric `Requests`, Total, `statusCodeCategory` = `5xx`, of the factory | `>= 10` | PT5M | 2 |
| `tdd-fworker-alerts-not-stored` | scheduled query rule v2 on the Container Apps environment's Log Analytics workspace: `ContainerAppConsoleLogs_CL \| where ContainerAppName_s == 'ca-cmdemo1-tdd-fworker' \| where Log_s has '"alertIngress":"not-stored"'` | count `>= 1` | PT5M | 1 |
| `tdd-fworker-db-connections` | metric `connection_failed` of the factory's Azure SQL database, Total | `>= 5` | PT5M | 2 |
| `tdd-fworker-db-paused-or-full` | metric `storage_percent`, Maximum, of the same database | `> 85` | PT15M | 3 |

`tdd-fworker-alerts-not-stored` is the one that the alert ingress itself feeds: the gateway writes one JSON line
`{"alertIngress":"not-stored",...}` whenever it could not put an alert into its inbox (and answers 503, so Azure retries), which means incidents of the
app are arriving at a factory that cannot take them.

## 6. After applying

1. In the portal, "Test action group" on `ag-cmdemo1-tdd-incident` sends a sample alert (its rule and ids are Azure's samples): expect the gateway log line
   `"alertIngress":"stored"` and a row of `ItemHistory` with item key `incident:ui/tdd` and type `ingress.inbox.azure-monitor.incident.alert`.
2. A wrong secret must give 401 (`"alertIngress":"bad-secret"`); `environment=prod` must give 202 with `"ignored":true`.
3. Test `ag-cmdemo1-tdd-factory-owner` the same way: the owner's mailbox gets the message, the factory's log shows nothing.
4. Stop the factory's Container App for six minutes in tdd (when the plant is quiet): `tdd-fworker-replicas-zero` and `tdd-fworker-not-ready` mail the owner
   while the app's incident path stays silent.
