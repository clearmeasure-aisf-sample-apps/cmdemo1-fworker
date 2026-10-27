# ADR 0001: The gateway moves to Wolverine durable messaging and one saga per work item, release by release

- **Status:** Proposed. On 2026-10-25 the owner asked for "a phased refactoring plan that keeps fworker releases stable" and then
  "Save an adr with this plan"; this records the plan. It becomes Accepted with the decisions of the plan's section 7, the base
  included; nothing is built. Revised 2026-10-26 (section "Revision 2026-10-26"); still Proposed.
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

## Revision 2026-10-26

**Decision of the owner, 2026-10-26:**
1. uat and prod do not matter for this plan: if it works in tdd, it is accepted.
2. Green pull requests merge and deploy without approval stops.

**What changes in the decision above:**
- **Point 3, the stabilisation after stage C:** the gate is now the architecture test green, the golden writes unchanged, one measured
  batch in tdd with no regression against the 2026-10-25 delivery-speed baseline, `/ready` 200 with the leader acting and the plant
  restored after each release, and a 4-hour quiet observation window in tdd. It replaces two batches, the promotion to uat and prod,
  and two quiet weeks.
- **Point 4, the path:** v2 is built beside v1 as new projects in this repository, held to the same Tier 1 suites and golden-write
  oracle, and observed in tdd in shadow mode (it reads the board, its commands go to a recording sink). **tdd then switches engines
  as a whole,** once (`engine: { v2Environments, shadowEnvironments }`, tdd's value in `config/deploy.overrides.json`), not item by
  item. Three seams stay because they give the new code its shape: S1 the verb router, S2 the command port (with the recording sink),
  S3 the event ingress. S4 as a seam in v1, S5 and S6 go.
- **Point 5, the rules:** gone are the `engine:v2` label and every v1 path that skipped or forwarded v2 items, the per-capability
  flags, Solo mode on the lease holder (v2 runs Balanced with Wolverine's leader election and the session mailbox from R2), v1's side
  effects through the outbox, and expand and contract for uat's and prod's lag. Kept: Tier 1 as the contract, the database only where
  v2 runs, compatible answers for agents, `FACTORY_V2_OFF`.
- **Point 6, the environments:** fworker's uat and prod gateways are out of scope; no gate waits on them and nothing in the plan
  promotes to them. The tdd gateway still delivers the app to the app's uat and prod.
- **Gates** are tdd observation windows measured in hours (2 or 4), not soaks of days, shadow releases or batches to prod. Incidents
  (R8) come after the cutover.

**Consequences:**
- **Faster:** the side-by-side work (10 tasks removed or merged) and the soaks, shadow releases, promotions and quiet weeks are gone;
  the estimate is about 61 hours of clock time (55 to 75) at the observed 50 minutes per task with 2 to 3 in parallel
  (`fworker-wolverine-implementation.md` 5), against the earlier 53 to 56 weeks, which assumed tasks of days to weeks.
- **All or nothing per environment:** at the cutover v2 takes every item and capability in tdd at once, so a defect that Tier 1 on v2
  and the shadow windows missed reaches every item there. Accepted, because tdd is the only environment in scope.
- **Rollback by revert or kill switch:** a revert of the switch's pull request (a release), or `FACTORY_V2_OFF` (a new revision, no
  image change). v2 writes the state comments in the format v1 reads until v1 is removed, so v1 resumes every open item; the cutover
  rehearses this before its measured batch. After v1's removal, rollback is a revert of the removal.
- **uat and prod out of scope:** no promotion of fworker to them, no wait on cmdemo1-system#314, no `ui-auto-promote` path change.
- **No approval stops inside the repository:** protected paths and security-sensitive tasks merge on green after the reviewer
  subagent (with a security brief for the latter), recorded in the handoff. The actions outside the repository (Azure SQL, a replica,
  `FACTORY_V2_OFF` in cmdemo1-system, cmdemo1-aisf off board 679) remain.

## Alternatives considered

- **The AI Software Factory as the base**, with its seam extended and its Wolverine provider (PR #496) refreshed. The analysis
  recommends it; this ADR records the fworker path the owner asked to have planned, and the choice stays open (decision 1).
- **A native rewrite in one go.** It was rejected: it is what failed in the AI Software Factory's PR #409, with every saga rewritten
  and every change on master ported by hand.
- **NServiceBus.** It has SQL Server sagas and an outbox, and a free Community tier (3 endpoints, 10,000 messages a day) whose fit
  for the factory's volume has not been estimated. Not chosen because of what Wolverine adds for this design: recurring jobs,
  SQL-backed rate limiting, global partitioning, and an MIT licence without per-endpoint pricing.
- **A workflow engine** (Durable Task Scheduler, Temporal) came up in the research but was not analysed in the design documents.
