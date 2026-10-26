# ADR 0001: The gateway moves to Wolverine durable messaging and one saga per work item, release by release

- **Status:** Proposed. On 2026-10-25 the owner asked for "a phased refactoring plan that keeps fworker releases stable" and then
  "Save an adr with this plan"; this records the plan. It becomes Accepted with the decisions of the plan's section 7, the base
  included; nothing is built.
- **Date:** 2026-10-25
- **Plan:** `docs/design/fworker-wolverine-refactor.md` (phases R0 to R9, the rules that keep releases stable, the seams, the verb
  inventory).
- **Target design:** `docs/design/wolverine-factory.md` (sagas, coordinators, integration handlers, the gaps of the hypothesis).
- **Related:** `docs/design/delivery-speed.md` (draft PR #253: the speed rules built inside the phases; section 18, the state gaps of
  today's gateway); ClearMeasureLabs/AISoftwareFactory ADR-0053 (its pluggable messaging) and PR #409 (its abandoned native Wolverine
  rewrite).

## Context

The gateway is a hand-built process manager. GitHub is its database: item state comments, claim refs, the board, snapshot and lease
refs. A sweep every 180 s, run as PowerShell rule scripts (73 server verbs), compares timestamps for its timeouts and counters for its
retries; a leader lease and a plant snapshot carry it across releases. Reading it for `delivery-speed.md` 18 found the costs of that
design:
- a sweep still running keeps acting after its successor has taken over;
- state writes are blind PATCHes, with no compare-and-swap;
- several retry budgets live in process memory, so a restart grants extra rounds;
- the day budget is undercounted after a restart.

The owner's hypothesis (2026-10-25) was that every AI session invocation and every integration with GitHub, Octopus and other
dependencies could run as durable messages and handlers, with a saga per work item, different sagas for different kinds of item, and
incidents run by a saga too. The analysis (`wolverine-factory.md`) found that the hypothesis holds, with five refinements:
- a real database comes first;
- shared resources need coordinators of their own;
- one work-item saga type with a plan per kind of item, because an item's kind is often known only after design;
- handlers are idempotent, because delivery is at-least-once;
- decision verbs call the saga synchronously.

The analysis recommended the AI Software Factory as the base; the owner asked for the fworker path to be planned. Which base is
taken is decision 1 of the plan's section 7.

## Decision

1. **Wolverine, used directly**, with SQL Server persistence, EF Core saga storage and the transactional outbox and inbox. The
   gateway has no other hosts to keep compiling, so it needs no messaging seam of its own.
2. **The target** is `wolverine-factory.md` sections 2 to 4:
   - a `WorkItemSaga` with plans (feature, docs, hotfix);
   - the coordinators `CapacityScheduler`, `MergeTrainSaga` and `DeliveryTrainSaga`;
   - an `IncidentSaga`;
   - one idempotent integration command per external side effect;
   - an item history table as the audit trail;
   - GitHub as a versioned projection and as input, not as the store.
   Declared deviations while both engines run (plan rule 10): Solo mode on the lease holder until R9, the session mailbox only at R9,
   and v1's own side effects through the outbox in R3.
3. **First, stage C** (`gateway-composition.md`): `Program.cs` becomes a composition root (behaviour-neutral steps), and that
   release is stabilised (two measured batches, promotion to uat and prod, two quiet weeks) **before the enhancement plan starts**
   (owner, 2026-10-26). The sequence of work, with its parallel lanes, is `fworker-wolverine-implementation.md`.
4. **The path** is the strangler plan in `fworker-wolverine-refactor.md`. The old engine (v1) and Wolverine (v2) run in one gateway
   process behind six seams (verb router, command port, event ingress, item state store, timers, loopback), in ten phases, each with a
   gate:
   - R0 prerequisites and safety;
   - R1 structure and seams;
   - R2 the Wolverine host, inert;
   - R3 durable side effects;
   - R4 durable ingress and the board reconciler;
   - R5 the DeliveryTrainSaga;
   - R6 the docs plan;
   - R7 the feature plan;
   - R8 incidents;
   - R9 retiring v1.
5. **Release stability rules** (plan section 2):
   - Tier 1 is the black-box contract, with a golden-write harness.
   - Every change goes in branch by abstraction.
   - Each capability has a flag in `instance.json`, defaulting to v1.
   - `FACTORY_V2_OFF` is the fast rollback: v2 work is frozen and parked, never handed to v1.
   - Migrations are additive and respect uat's and prod's lag behind tdd.
   - The database is required only where a capability is on v2.
   - One owner per item (the `engine:v2` label, checked at every v1 path) and per shared capability.
   - Agents get compatible answers.
   - Until R9 there is one acting Wolverine node: Solo mode on the lease holder.
6. **The tdd gateway does all v2 work.** It is the only acting gateway and delivers the app to tdd, uat and prod. The uat and prod
   gateways must start and stay ready with every capability on v1 and no database.

7. **Forward compatible with a system-level gateway** (`docs/design/system-gateway.md`, the owner's decisions of 2026-10-25): one
   acting gateway per system working items in any of the system's repositories; one repository per item, its issue living there;
   policy per repository and per deployable; toolchains matched by capability; `Depends on` across repositories gating delivery per
   environment; one queue with per-repository WIP limits. v1 stays home-repository-only; v2 is repository-aware from its first line.
   The refactoring builds the hooks now: qualified ids in v2, a system catalog behind an interface, coordinators keyed by repository
   and deployable, budgets per credential, the repository in the token, and isolation tests with a second repository. The catalog
   comes from cmdemo1-system's `system.json`, so adding a repository later is a cmdemo1-system change plus webhooks, the factory
   Apps, Octopus grants with `pipelineSignOff`, and worker images, not a gateway release or a redesign.

## Consequences

- **Gains when done:**
  - state that survives every restart, with versioned writes;
  - durable budgets and timeouts;
  - side effects that are retried, classified and recorded (the journal keeps the order #252 established);
  - webhooks as the primary input, with reconcilers as the safety net;
  - one place to see an item's whole history;
  - incident handling as a workflow.
- **Costs:**
  - a database per acting gateway;
  - new packages pinned to a library with one main maintainer and frequent releases;
  - a large port from PowerShell to C#;
  - two engines in one process from R1 to R8;
  - more CI time (SQL Server in the Tier 1 jobs).
- **Open decisions and approvals,** each asked before its phase (plan section 7):
  - the base: fworker (this plan) or the AI Software Factory;
  - the `system.json` fields for policy, toolchain and WIP limit, and the gateway's read access to cmdemo1-system
    (`system-gateway.md` 5);
  - cmdemo1-aisf off board 679;
  - `FACTORY_V2_OFF` kept in cmdemo1-system's infrastructure code;
  - at least one replica for the acting gateway;
  - Azure SQL for it;
  - `ui-auto-promote` application paths;
  - the packages;
  - the protected paths (Dockerfile, `deploy.overrides.json`) and SQL Server in CI;
  - `projects_v2_item` delivery;
  - the cross-repo pieces of the delivery-speed design;
  - the incident rollback policy in prod.
- **The delivery-speed design is built once,** inside R0 (safety and quick wins), R5 (evidence and train) and R7 (scheduler, Functional
  Testing with CI alongside, file-overlap hold, merge train), not first in v1 and again in v2.

## Alternatives considered

- **The AI Software Factory as the base**, with its seam extended and its Wolverine provider (PR #496) refreshed. The analysis
  recommends it; this ADR records the fworker path the owner asked to have planned, and the choice stays open (decision 1).
- **A native rewrite in one go.** It was rejected: it is what failed in the AI Software Factory's PR #409, with every saga rewritten
  and every change on master ported by hand.
- **NServiceBus.** It has SQL Server sagas and an outbox, and a free Community tier (3 endpoints, 10,000 messages a day) whose fit
  for the factory's volume has not been estimated. Not chosen because of what Wolverine adds for this design: recurring jobs,
  SQL-backed rate limiting, global partitioning, and an MIT licence without per-endpoint pricing.
- **A workflow engine** (Durable Task Scheduler, Temporal) came up in the research but was not analysed in the design documents.
