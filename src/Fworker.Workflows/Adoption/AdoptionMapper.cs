using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fworker.Contracts.Seams;
using Fworker.Domain;
using Fworker.Persistence;
using Fworker.Workflows.Capacity;
using Fworker.Workflows.Delivery;
using Fworker.Workflows.Qc;
using Fworker.Workflows.WorkItem;

namespace Fworker.Workflows.Adoption;

/// <summary>
/// W9.10m: the mapping from v1's state of one item to the state of its WorkItemSaga, as a pure function (docs/design/fworker-wolverine-implementation.md, "W9.10m as built" has the table, one row per case, each with a
/// Fworker.Workflow.Tests case in AdoptionMappingTests). v1's truth is the item's state comment (claim, park, verdict, merge, counters), the card's column, the issue and the pull request; the saga's is its row. The mapping
/// builds the row v1's truth stands for, the way the saga's own steps would have left it, so that the steps, the timers and the projections go on from it as from a saga that had run the item all along.
/// </summary>
public static class AdoptionMapper
{
    /// <summary>The codes of v1's parks that count a budget of the step the item is on.</summary>
    static readonly HashSet<string> RelaunchParks = ["watchdog-relaunch-budget", "session-relaunch-budget", "validation-no-verdict"];

    public const string AwaitingChecksPark = "development:awaiting-checks";

    /// <param name="generation">The generation of the instance the mapping stands for: 1 for an item the saga has no row for; W9.10n: the next one when a re-adoption after the kill switch opens a new instance (<see cref="AdoptionFence.GenerationFor"/>).
    /// It names the arm, the adopted slot and the slot request, and a slot the ledger holds for an earlier instance is not taken for this one's.</param>
    public static AdoptionMapping Map(AdoptionSnapshot s, AdoptionSettings cfg, DateTimeOffset now, int generation = 1)
    {
        // ---- is it the saga's? -----------------------------------------------------------------------------------------------------------------------
        if (!s.IssueOpen) return Skip("issue-closed");
        if (s.State is not { } st) return Skip("no-state-comment");
        var merge = V1State.Obj(st, "merge");
        var merged = V1State.Str(merge, "sha").Length > 0;
        if (merged && V1State.Str(merge, "done_at").Length > 0) return Skip("settled");
        var claim = V1State.Obj(st, "claim");
        var verdict = V1State.Obj(st, "verdict");
        var passed = V1State.Bool(verdict, "passed");
        var park = V1State.LivePark(st, now);
        var parkReason = park is null ? "" : V1State.Str(park, "reason");
        var waitsForChecks = parkReason == AwaitingChecksPark;   // (the gateway's own wait for CI, not a park of the item)
        var notes = new List<string>();

        // ---- the plan (row P) ---------------------------------------------------------------------------------------------------------------------------
        var plan = PlanOf(s, st, cfg, notes);
        if (!cfg.Policy.Carries(plan)) return Skip($"plan-not-carried: {plan}");
        if (merged && plan == WorkPlans.Docs) return Skip("docs-merged");

        // ---- the step (row S) ---------------------------------------------------------------------------------------------------------------------------
        StepKind step;
        if (merged) step = StepKind.Deliver;
        else
        {
            var column = s.Column ?? V1State.Str(claim, "column");
            if (cfg.Columns.IsHold(column)) return Skip($"hold-column: {column}");
            if (cfg.Columns.IsDone(column)) return Skip("card-done");
            if (cfg.Columns.StepOf(column) is not { } onCard) return Skip(string.IsNullOrWhiteSpace(column) ? "not-on-the-board" : $"column-of-no-step: {column}");
            step = onCard;
            // a PASS in the validation column is waiting for its merge, held or not (the merge happens in the validation column)
            if (step == StepKind.Verify && passed) step = StepKind.Merge;
            // Development done and waiting for CI (awaiting_checks): the saga enters Verify when Development completes, whether the checks are green or still running
            if (step == StepKind.Develop && V1State.Obj(st, "awaiting_checks") is not null) { step = StepKind.Verify; notes.Add("awaiting_checks: Development is done, the item is on Verify with the checks pending"); }
        }

        var rev = V1State.Rev(st);
        var item = s.Item;
        var state = new WorkItemState
        {
            Item = item.ToString(), Generation = generation, Plan = plan, Step = step, Status = WorkItemStatus.Open, ArmSeq = 1, ArmId = $"{generation}.1",
            StartedAt = StartedOf(st, now), Source = $"adopted from v1 (state revision {rev})", CommentRev = rev,
        };
        state.DesignMarker = V1State.NullableBool(V1State.Obj(st, "design"), "docs_only") ?? (step > StepKind.Design ? false : null);

        // ---- the pull request and the verdict ---------------------------------------------------------------------------------------------------------
        var pr = V1State.Obj(st, "pr");
        var awaiting = V1State.Obj(st, "awaiting_checks");
        state.PullRequest = FirstPositive(V1State.Int(pr, "number"), V1State.Int(awaiting, "pr"), s.PullRequest?.Number ?? 0, V1State.Int(verdict, "pr"), V1State.Int(merge, "pr"));
        state.Head = (s.PullRequest is { State: "open" } open && open.Head.Length > 0 ? open.Head : First(V1State.Str(awaiting, "sha"), V1State.Str(verdict, "head_sha"))).ToLowerInvariant();
        if (step == StepKind.Merge) { state.VerdictHead = V1State.Str(verdict, "head_sha").ToLowerInvariant(); state.VerdictSummary = V1State.Str(verdict, "summary"); }
        if (WorkPlans.IsCode(plan) && step == StepKind.Verify && awaiting is not null && state.Head.Length > 0) { state.ChecksHead = state.Head; state.ChecksState = "pending"; }
        if (s.PullRequest?.Files is { Count: > 0 } files && cfg.Policy.Schedules(plan)) { state.Footprint = [.. files]; state.FootprintFromPullRequest = true; }

        // ---- the counters and the budgets (row B) -----------------------------------------------------------------------------------------------------
        state.FixForwardRounds = V1State.Int(st, "fix_forward_rounds");
        state.Demotions = V1State.Int(st, "demotions");
        Reviews(st, state, cfg);
        Budgets(st, state, claim, park, parkReason, cfg);
        if (V1State.Has(claim, "session_launches")) notes.Add("claim.session_launches is not carried: the saga counts session trouble as failures of the phase");

        // ---- the park (row K) ----------------------------------------------------------------------------------------------------------------------
        if (park is not null && !waitsForChecks)
        {
            state.Parked = true; state.ParkReason = parkReason;
            if (cfg.Policy.Sweep is not null)
            {
                state.ParkUntil = V1State.At(park, "until") ?? now + cfg.Sweep.ParkTime;
                // a held PASS gives its owner up after idleReleaseMinutes (the saga's Park)
                if (step == StepKind.Merge && state.VerdictHead.Length > 0 && parkReason != SweepParks.PassWithoutAutoMerge) state.OwnerIdleDue = (V1State.At(park, "at") ?? now) + cfg.Sweep.IdleReleaseAfter;
            }
        }
        else if (waitsForChecks) notes.Add("park development:awaiting-checks is the gateway's wait for CI, not a park of the item");

        // ---- the claim, the lease and the slot (row L) --------------------------------------------------------------------------------------------
        AdoptionSlot? slot = null; AdoptionFlight? flight = null;
        var liveClaim = V1State.ClaimLive(claim, now);
        var owner = liveClaim && V1State.Bool(claim, "owner");
        if (!state.Parked && step is StepKind.Design or StepKind.Develop or StepKind.Verify) Session(s, cfg, state, claim, liveClaim, owner, now, notes, out slot);
        else if (!state.Parked && step == StepKind.Merge)
        {
            state.LeaseDue = now + (cfg.Policy.LeaseOf(StepKind.Merge) ?? TimeSpan.Zero);
            MergeRequest(state, cfg, now);
            if (cfg.Policy.Schedules(plan)) { state.SlotKnown = true; flight = new AdoptionFlight(item, state.Footprint, now, state.Generation); }
        }

        // ---- the merge (row M) and the delivery (row D) ----------------------------------------------------------------------------------------
        if (merged)
        {
            if (Merged(s, st, merge!, park, state, cfg, now, notes) is { } settled) return Skip(settled);
        }

        // ---- the rows beside the state (rows Q, G) -------------------------------------------------------------------------------------------------
        return new AdoptionMapping(null, state, slot, flight, QcRows(s, st), merged ? MergedRow(s, st, merge!, state, cfg, now) : null, notes);
    }

    static AdoptionMapping Skip(string reason) => new(reason, null, null, null, [], null, []);

    static string First(params string[] values) => values.FirstOrDefault(v => v.Length > 0) ?? "";
    static int FirstPositive(params int[] values) => values.FirstOrDefault(v => v > 0);

    // P: the design's decision (state.design.docs_only, or for a legacy item the body's marker) says docs; a pull request that changes anything but documentation says feature
    static string PlanOf(AdoptionSnapshot s, JsonObject st, AdoptionSettings cfg, List<string> notes)
    {
        var designFlag = V1State.NullableBool(V1State.Obj(st, "design"), "docs_only");
        var docsOnly = DocsOnly.ItemIsDocsOnly(designFlag, new DocsOnlyItem(s.Column, s.Body, s.CreatedAt, s.Labels), cfg.DocsOnly);
        if (!docsOnly) return WorkPlans.Feature;
        if (s.PullRequest?.Files is { Count: > 0 } files && !DocsOnly.FilesAreDocumentation(files, cfg.DocsOnly.Paths))
        {
            notes.Add("the design is docs-only but the pull request changes files that are not documentation: the feature plan");
            return WorkPlans.Feature;
        }
        return WorkPlans.Docs;
    }

    static DateTimeOffset StartedOf(JsonObject st, DateTimeOffset now)
    {
        if (V1State.Get(st, "flow") is JsonArray flow)
            foreach (var f in flow) if (f is JsonObject o && V1State.At(o, "at") is { } at) return at;
        return V1State.At(st, "updated") ?? now;
    }

    // B: the review rounds of the step's column, and the relaunch and failure budgets that parked the item (or that its claim has spent)
    static void Reviews(JsonObject st, WorkItemState state, AdoptionSettings cfg)
    {
        if (state.Step > StepKind.Verify && state.Step != StepKind.Merge) return;
        var column = cfg.Columns.ColumnOf(state.Step);
        JsonObject? rv = null;
        if (V1State.Obj(st, "reviews") is { } all) foreach (var kv in all) if (string.Equals(kv.Key, column, StringComparison.OrdinalIgnoreCase) && kv.Value is JsonObject o) rv = o;
        if (rv is null) return;
        var round = V1State.Int(rv, "round");
        if (round > 0) state.ReviewRounds[state.Step.ToString()] = round;
        if (V1State.Str(rv, "ref").Length > 0 || V1State.Str(rv, "last") == "COMPLETE") state.ReviewsComplete.Add(state.Step.ToString());
    }

    static void Budgets(JsonObject st, WorkItemState state, JsonObject? claim, JsonObject? park, string parkReason, AdoptionSettings cfg)
    {
        var phase = state.Step.ToString();
        if (RelaunchParks.Contains(parkReason))
        {
            var launches = V1State.Int(park, "launches");
            state.Launches[phase] = launches > 0 ? launches : Math.Max(1, cfg.Policy.MaxLaunchesPerPhase);
        }
        else if (parkReason == "worker-failure") state.Failures[phase] = Math.Max(1, cfg.Policy.Recovery.MaxFailuresPerPhase);
        else if (claim is not null)
        {
            // the launches the claim has spent in its column count against the step the item is on (the Merge step stands in the validation column)
            var launched = V1State.Int(claim, "launches");
            var claimStep = cfg.Columns.StepOf(V1State.Str(claim, "column"));
            if (launched > 0 && claimStep is { } c && (c == state.Step || (c == StepKind.Verify && state.Step == StepKind.Merge))) state.Launches[phase] = launched;
        }
    }

    // L: a live owner's session keeps its lease and holds a slot; an item with no live session asks for one (an item that was claimed before asks again as an owned item, rank 2)
    static void Session(AdoptionSnapshot s, AdoptionSettings cfg, WorkItemState state, JsonObject? claim, bool live, bool owner, DateTimeOffset now, List<string> notes, out AdoptionSlot? slot)
    {
        slot = null;
        var lease = cfg.Policy.LeaseOf(state.Step);
        if (!cfg.Policy.Schedules(state.Plan))
        {
            // the plan has no slots: the lease of the claim, else the lease a new arm gets
            state.LeaseDue = live ? V1State.At(claim, "lease_until") : lease is { } l ? now + l : null;
            return;
        }
        var phase = state.Step switch { StepKind.Design => "design", StepKind.Develop => "develop", _ => "verify" };
        if (live)
        {
            state.LeaseDue = V1State.At(claim, "lease_until");
            state.EverGranted = true;
            if (!owner) { notes.Add("a live claim of a session that is not an owner holds no slot: the lease only"); return; }
            // (a slot the ledger holds for an earlier instance of the item is that instance's: W9.10n clears it, it is not this one's)
            if (s.HeldSlot is { } held && (held.Request.Generation == 0 || held.Request.Generation == state.Generation))
            {
                state.SlotId = held.SlotId; state.SlotRuntime = held.Runtime; state.SlotHost = held.Host ?? "";
                state.SlotKnown = true;
                return;
            }
            var runtime = cfg.SlotRuntime;
            slot = new AdoptionSlot(AdoptedSlotId(state.ItemId.ToString(), state.Generation), state.ItemId, state.Generation, runtime, null, phase,
                V1State.At(claim, "started") ?? now, state.LeaseDue ?? now, state.Footprint);
            state.SlotId = slot.SlotId; state.SlotRuntime = runtime; state.SlotKnown = true;
            return;
        }
        state.EverGranted = claim is not null;
        state.SlotAsked = true; state.SlotKnown = true; state.AskSeq = 1;
        var depth = state.Step switch { StepKind.Design => 1, StepKind.Develop => 2, _ => 3 };
        state.Outbox.Add(new AskSlot($"ask|{state.Item}|{state.Generation}|1", state.ArmId, phase, state.EverGranted, depth, [.. state.Footprint], state.FootprintFromPullRequest, now, state.CurrentPlan.TopRank));
    }

    /// <summary>The id of the slot an adopted session holds in the ledger: opaque to the scheduler, fixed by the item so that adopting again finds the same one.</summary>
    public static string AdoptedSlotId(string item, int generation) => $"adopted|{item}|{generation}";

    // M (the Merge step): the PASS asks for its merge as the step's entry does (the docs plan asks at once, the code plans wait for green checks of the head the PASS covers)
    static void MergeRequest(WorkItemState state, AdoptionSettings cfg, DateTimeOffset now)
    {
        if (state.VerdictHead.Length == 0) return;
        if (WorkPlans.IsCode(state.Plan) && cfg.Policy.HasFeatureSteps) { state.MergeAwaitingChecks = true; return; }
        state.MergeAttempt = 1;
        state.MergeRequest = new MergeRequestState { Attempt = 1, PullRequest = state.PullRequest, Head = state.VerdictHead };
        state.MergeRequestEmitted = true; state.TrainEntered = true;
        state.MergeRetryAt = null;
    }

    // M and D: a merged item is on its Deliver step; the wait for master's CI, the release candidate and the release queue are v1's `merge.awaiting`; what the delivery trains show is delivered. Returns a reason when the item is settled already.
    static string? Merged(AdoptionSnapshot s, JsonObject st, JsonObject merge, JsonObject? park, WorkItemState state, AdoptionSettings cfg, DateTimeOffset now, List<string> notes)
    {
        state.MergeCommit = V1State.Str(merge, "sha").ToLowerInvariant();
        if (V1State.Int(merge, "pr") > 0) state.PullRequest = V1State.Int(merge, "pr");
        state.LeaseDue = null; state.MergeRequest = null; state.MergeRetryAt = null; state.MergeAwaitingChecks = false; state.TrainEntered = false;
        var mergedAt = V1State.At(merge, "merged_at");
        var awaiting = V1State.Str(merge, "awaiting");
        if (cfg.Policy.WatchesMaster(state.Plan))
        {
            switch (awaiting)
            {
                case "master-broken":
                    state.MasterState = "broken"; state.MasterSince = mergedAt ?? now;
                    state.MasterFailed = V1State.Strings(park, "failed");
                    break;
                case "master-timeout":
                    state.MasterState = "timeout"; state.MasterSince = mergedAt ?? now;
                    break;
                case "release-candidate" or "late-rc" or "release-queue":
                    state.MasterState = "green"; state.MasterSince = mergedAt ?? now; state.MasterGreenVia = V1State.Str(merge, "green_via");
                    break;
                default:   // master-green, or a merge record with no wait: the settle's wait for master, counted from the merge (from the re-run when the failed jobs were re-run, issue 79)
                    state.MasterState = "waiting";
                    state.MasterRerunAt = V1State.At(merge, "rerun_at");
                    state.MasterSince = state.MasterRerunAt ?? mergedAt ?? now;
                    state.MasterDue = state.MasterSince + cfg.Sweep.MasterTimeout;
                    state.MasterRerunRuns = RerunRuns(merge);
                    break;
            }
        }
        // what the delivery trains show: the item is delivered to an environment when its merge is in the release the train delivered there
        DateTimeOffset? finalFinished = null;
        foreach (var train in s.Trains ?? [])
        {
            if (DeliveredRelease(train, state.Item, state.MergeCommit, V1State.Str(merge, "release")) is not { } release) continue;
            var env = train.Environment.ToLowerInvariant();
            state.Delivered[env] = release;
            if (env == cfg.Policy.FinalEnvironment.ToLowerInvariant()) finalFinished = train.DeploymentOf(release, train.Environment)?.FinishedAt ?? now;
        }
        if (finalFinished is { } finished)
        {
            var until = finished + cfg.Policy.Settle;
            if (until <= now) return "delivered-and-settled";
            // the settle window runs under its own arm, as the saga's Settle starts it
            state.Status = WorkItemStatus.Settling; state.ArmSeq = 2; state.ArmId = $"{state.Generation}.2";
            state.SettleUntil = until; state.MasterState = state.MasterState.Length > 0 ? "green" : "";
            state.MasterDue = null;
            notes.Add($"delivered to {cfg.Policy.FinalEnvironment}: the settle window runs until {until:O}");
        }
        return null;
    }

    static List<RerunRun> RerunRuns(JsonObject merge)
    {
        var runs = new List<RerunRun>();
        if (V1State.Get(merge, "rerun_runs") is JsonArray a)
            foreach (var x in a) if (x is JsonObject o && V1State.Int(o, "id") is var id && id > 0) runs.Add(new RerunRun { Id = id, Attempt = V1State.Int(o, "attempt") });
        return runs;
    }

    /// <summary>The release of the train that carries the item: the train says it delivered "item|commit" (and in which release), or the release v1 queued the item for has a successful deployment (itself or the release that superseded it).</summary>
    static string? DeliveredRelease(TrainState train, string item, string commit, string queuedRelease)
    {
        if (train.DeliveredItems.Contains($"{item}|{commit}", StringComparer.OrdinalIgnoreCase))
        {
            foreach (var kv in train.DeliveredBy) if (kv.Value.Any(c => string.Equals(c.Item, item, StringComparison.OrdinalIgnoreCase))) return kv.Key;
            if (queuedRelease.Length > 0 && Carrying(train, queuedRelease) is { } carried) return carried;
            return train.Current;
        }
        return queuedRelease.Length > 0 ? Carrying(train, queuedRelease) : null;
    }

    // the release itself when it succeeded in the train's environment, else the release that superseded it (and so carried its items) when that did
    static string? Carrying(TrainState train, string release)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var r = release; r.Length > 0 && seen.Add(r); r = train.Superseded.GetValueOrDefault(r) ?? "")
            if (train.DeploymentOf(r, train.Environment) is { Succeeded: true, Rollback: false }) return r;
        return null;
    }

    // Q: the records of the state comment's qc map, for the table of QC environments
    static IReadOnlyList<AdoptionQcRow> QcRows(AdoptionSnapshot s, JsonObject st)
    {
        var rows = new List<AdoptionQcRow>();
        if (V1State.Obj(st, "qc") is not { } map) return rows;
        foreach (var kv in map)
        {
            if (kv.Value is not JsonObject record || !int.TryParse(kv.Key, out var pr) || pr < 1) continue;
            rows.Add(new AdoptionQcRow(QcKeys.For(s.Item.Repository.Owner, s.Item.Repository.Repo, pr), s.Item.Repository.ToString(), s.Item.ToString(), (JsonObject)record.DeepClone()));
        }
        return rows;
    }

    // G: the row of the merged-items table (the claim that makes the webhook's and the reconciler's derivations one), with the gates the item's labels, verdict and merge show
    static MergedItem MergedRow(AdoptionSnapshot s, JsonObject st, JsonObject merge, WorkItemState state, AdoptionSettings cfg, DateTimeOffset now)
    {
        var verdict = V1State.Obj(st, "verdict");
        var facts = new ItemMergeFacts(s.Item, true, s.Labels, s.Body, V1State.Bool(verdict, "passed"), V1State.Has(verdict, "pr") ? V1State.Int(verdict, "pr") : null,
            V1State.Has(merge, "pr") ? V1State.Int(merge, "pr") : null, V1State.Str(merge, "by") is { Length: > 0 } by ? by : null, s.DemoVideoPublished);
        var message = ItemMergedDeriver.GatesOf(facts, s.Item.Number, cfg.Gates);
        return new MergedItem
        {
            ItemKey = s.Item.ToString(), Commit = state.MergeCommit, PullRequest = state.PullRequest, Source = "adopted", MergedAt = V1State.At(merge, "merged_at") ?? now,
            MessageId = AdoptionMessageId(s.Item.ToString(), state.MergeCommit),
            GatesJson = JsonSerializer.Serialize(message), DependenciesJson = JsonSerializer.Serialize(ItemMergedDependencies.Parse(s.Item, s.Body).Select(d => d.ToString())),
        };
    }

    /// <summary>The message id of an adopted row: fixed by the item and the commit, so adopting again records the same row once (the derivation of the webhook or the reconciler finds it taken).</summary>
    public static Guid AdoptionMessageId(string item, string commit) => new(MD5.HashData(Encoding.UTF8.GetBytes($"adopt|{item}|{commit}")));
}
