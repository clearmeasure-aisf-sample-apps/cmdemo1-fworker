# The Wolverine host (W2.2)

Task W2.2 of `docs/design/fworker-wolverine-implementation.md` (issue #311, which also carries the scope of #362). What the gateway does with
`FACTORY_DB_CONNECTION`, how the host runs, and what it costs.

## What is wired

- **Registration condition (W2.3, #464):** the host is registered only where there is a connection string AND `engine` names the environment in
  `shadowEnvironments` or `v2Environments` (`GatewayComposition.WolverineHost`). The kill switch `FACTORY_V2_OFF` makes the environment v1, so it also
  removes the host. A connection string with an unlisted environment registers nothing. Solo stays the default mode; Balanced is a separate setting.
- **No connection string (every test, uat, prod):** nothing is registered. No `DatabaseOptions`, no `WolverineHostOptions`, no `WolverineHostService`, no
  hosted service, no `wolverine` block in `/api/v1/status`, no database call. The gateway is v1 exactly as before.
- **With one (tdd):** `WolverineHostService` (`gateway/Composition/WolverineHostService.cs`) is a singleton and a hosted service, started right after
  `LeadershipService` (`AddGatewayBackground`). It runs in the background and never holds up the start or `/ready`:
  1. apply the migrations of `Fworker.Persistence` (W2.1, schema `factory`);
  2. build and start a second, nested host that runs Wolverine against SQL Server: durable inbox and outbox (schema `wolverine`), storage created
     by Wolverine, no handler registered (inert), `TypeLoadMode.Static` (no runtime compiler in the image);
  3. on any failure (database paused or down, no permission) log the exception type and the SQL error number only (never the message: a connection
     failure can carry the server's name), wait 5 s, 10 s, 20 s, 40 s, then 60 s between tries, and try again until it works or the process stops.
- The first connection after a pause can take about a minute: the connection string gets `Connect Timeout` 90 s at least (never lowered), and the
  background start waits for it.
- **Coherence with the gateway's lease (W9.10a, #550).** The lease decides who acts, and the Solo node runs only on the process that acts. In Solo mode every
  node is node 1, and a node that starts clears the node table and releases the ownership of every row of node 1; two nodes at once (the old and the new
  process of a release) therefore handled each other's rows twice. Now: the node does not start at `StartAsync` but when the process acts
  (`GatewayLeader.OnActing`: the lease taken, the predecessor's snapshot restored, the lease confirmed; with the lease off, at the start of the host, as before).
  It leaves FIRST in the hand-over (`GatewayLeader.BeforeHandOver`, called by `LeadershipService` when a newer process takes the lease and at the stop of the
  holder): the handlers in flight finish, the ownership is released, bounded by `stopSeconds` (10 s; the stop of the holder waits at most 10 s for it) and only
  then the final snapshot is written and the lease is released, so the successor's node never starts beside a running one. A start in progress is cancelled; a
  standby that takes the lease again starts the node again after its restore. At shutdown `StopAsync` is the same drain again and finds nothing to do. The
  stop order stays: node, hand-over (snapshot, lease), "stopping" line, transcripts' flush. `StopOrderTests` pins the order of the hand-over and the flush, also with the
  host registered; `NodeHandOverTests` (Fworker.Workflow.Tests) runs two nodes on one database in that order with a start and a merge in flight and shows one
  side effect each. Not yet done: the lease as a SQL application lock (W9.10b); until then the lease is the GitHub ref.
- `/api/v1/status` carries `wolverine` (state `starting`, `running` or `stopped`, mode, attempts, last failure type, the two main intervals) only
  where the host is registered.

## Shadow history (W2.3)

In an environment of `engine.shadowEnvironments` (kill switch off) with the host registered, the gateway records into `factory.ItemHistory`:

- every event that enters through the ingress (seam S3, `IEventIngress`): `Type` `ingress.webhook.<event>` (Detail: the event JSON the rule verb returned) or
  `ingress.sweep-requested` (Detail: the item); `ItemKey` `owner/repo#N`, or `board` for an event without an item;
- every journal line the plant posts (the callback that calls the rule verb `journal`): `Type` `v1.journal.<event>`, Detail `{event, key, posted, fields}`
  (`posted` false when v1's call failed). All rows are `Kind` `event`, `Generation` 0; a Detail is cut at 16000 characters.

How: v1 acts first (the ingress decorator `ShadowEventIngress` calls v1 and then records; the journal callback records after its rule-verb call). The
record is published on a background task to a durable Wolverine local queue (`shadow-history`, schema `wolverine`), whose handler
(`ShadowHistoryHandler`) inserts the row once per Wolverine message id. Best-effort: a host that is not up, a database that is down or paused (retried 2 s,
10 s, 60 s, then dead letter), more than 500 records in flight (the newest are dropped) or any exception loses the record, counted in
`/api/v1/status` `wolverine.shadowHistory` (`recorded`, `dropped`, `failed`, `inFlight`) and never reaches v1. Nothing in v2 acts: no command is sent, nothing is
written to GitHub, nothing is polled by this code.

Cost: a write on activity wakes the paused serverless database; that is expected (events and journal lines are the activity). The handler's code is generated by
Wolverine at the first record (`TypeLoadMode.Auto`, package `WolverineFx.RuntimeCompilation`), a one-time compile of about a second.

## Modes

Solo is the mode of every deployment. (When these figures were taken the tdd database was serverless and paused after 60 minutes without a session; since 2026-10-29 it is Azure SQL S0, which does not pause. Measured against SQL Server 2022 with the host running:)

| | Solo (default) | Balanced (`"mode": "balanced"`) |
|---|---|---|
| Wolverine's leader election | none (one node) | yes: `wolverine://leader/` is assigned to the node |
| Open session held for good | none | **yes: the leader holds an application lock (`sp_getapplock`) on a session of its own** |
| Polling | the durability agent's timers only | node agent (health check, node reassignment) and the durability agent |
| Lets the database pause | yes, once the start's connections are closed | **no**: a session stays open and the node agent polls |

Azure SQL serverless pauses only when there is no session and no CPU for the delay, so a Balanced node keeps the database awake (about 0.5 vCore
around the clock) however long its intervals are. Worse, with long intervals Wolverine does not elect a leader until the first poll (a node
agent with a 4-hour poll has no leader for 4 hours, then holds the lock for good), so long intervals do not make Balanced cheap, only late.
Therefore:

- **Default: Solo**, with every timer at 4 hours. This is the profile that satisfies the cost constraint while the host is inert.
- **Balanced** is complete and tested (`WolverineHostSqlServerTests`), selected by `config/instance.json`, block `wolverine`, `"mode": "balanced"`,
  with 30-second timers (Wolverine's own are 5 to 10 seconds). W2.3 (the 2-hour observation window, whose gate is "v2's node elected leader") sets
  it in the same change that puts tdd in `shadowEnvironments`; the cost is accepted there, explicitly, for the window or for good.

### Intervals

Block `wolverine` of `config/instance.json`, in seconds; a value below its floor or not a number leaves the default of the profile.

| Key | Solo (default) | Balanced | Floor |
|---|---|---|---|
| `healthCheckSeconds` (node heartbeat) | 14400 | 30 | 60 |
| `firstHealthCheckSeconds` | 3600 | 5 | 0 |
| `staleNodeSeconds` (must exceed the heartbeat; else 3 beats) | 43200 | 300 | 120 |
| `nodeReassignmentSeconds` | 14400 | 30 | 60 |
| `firstNodeReassignmentSeconds` | 3600 | 5 | 0 |
| `scheduledJobSeconds` (scheduled messages, inbox and outbox recovery) | 14400 | 60 | 60 |
| `firstScheduledJobSeconds` | 3600 | 5 | 0 |
| `orphanSweepSeconds` | 14400 | 300 | 60 |
| `cleanupSeconds` (handled-message clean-up) | 14400 | 600 | 60 |
| `deduplicationCleanupSeconds` | 14400 | 600 | 60 |
| `metricsSeconds` | 14400 | 300 | 60 |
| `connectTimeoutSeconds` | 90 | 90 | 5 |
| `retryFirstSeconds` / `retryMaxSeconds` | 5 / 60 | 5 / 60 | 1 |
| `stopSeconds` | 10 | 10 | 1 |

(The floors apply to values set in the file; the defaults of a profile are not checked against them.)

The one database wake-up an idle Solo gateway causes is at start (migrations, Wolverine's storage check, the node's registration); after that the next
poll is hours away.

## Not in this change

- OpenTelemetry and the Azure Monitor exporter for the host, and the dead-letter admin page (`gateway/Endpoints/Admin*`): follow-ups of #261.
- The session mailbox: its table is W2.1's; reading and writing it belongs with the messaging waves, because the host has no handler yet.
- Putting tdd in `shadowEnvironments` of `config/deploy.overrides.json`, and the switch to Balanced for the observation window: separate explicit steps.
- When a handler is added, its code is either pre-generated (`codegen write`, `TypeLoadMode.Static`) or the `WolverineFx.RuntimeCompilation` package is added.
