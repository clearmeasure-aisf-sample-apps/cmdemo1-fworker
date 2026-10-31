using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Integrations.Commands;
using Fworker.Integrations.GitHub;
using Fworker.Workflows.WorkItem;
using Wolverine;

// W9.10i (docs/design/fworker-wolverine-implementation.md, W9.10i): the state comment projection, wired where the engine acts. It is the rollback path: `FACTORY_V2_OFF` or a revert hands every open item back to v1, which resumes
// from the item's state comment and the board. In the hybrid composition v1's verbs write the comment for what a session does (the design, the pull request, the verdict, the claim), and the saga's own transitions had no writer:
// the parks the saga makes (merge-blocked, fix-forward-exhausted, held, the master parks), the lift of a park, the merge the gateway makes (no `merge` record, so no settle), the end of a settle window (no `done_at`), the
// demotion of a closed pull request. Without them v1 would offer an item the saga had parked, merge again a PASS that was merged and never settle it.
//
// What the projection is: for every decision of the saga that changes something v1 reads (Wants), the item's state comment is brought to what the saga's state says (V1StateFormat.Apply), in the format v1's Get-State reads, by
// StateCommentProjection.Apply with the decision's sequence number as the version. The edit is a function of the saga's state as it is when the decision is handled (never older than the decision), so decisions handled late or
// twice give the same comment, and a comment that already says it is not written. It never overwrites what v1's verbs wrote: a park that names the same cause is left as it is, a merge record of the same commit is
// completed, not replaced. A fault is tried again a few times and then left (nothing is advanced, the next decision projects the whole of it again), as the card projection does.
// The delivery train's deployments are recorded by the executor (Integrations/GitHub/ProjectionPromotion.cs, DeliveryExecutorPort.cs).
// Not here: the claim ref and the claim of the owner (the plant's: ReleaseOwner), the `handoff` v1's verbs set, the journal lines of the settle (the saga's history is its journal), the labels.

/// <summary>What the projection needs of the configuration beside the plan's settings: how long a park lasts, how long a green master waits for its release candidate (timing.parkHours, timing.releaseCandidateTimeoutMinutes).</summary>
public sealed record V1StateSettings(TimeSpan ParkTime, TimeSpan ReleaseCandidateTimeout, string FinalEnvironment)
{
    public static V1StateSettings From(JsonNode? root, DocsPlanSettings plan)
    {
        double? Minutes(string name) { try { return root?["timing"]?[name] is JsonValue v && double.TryParse(v.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) && x > 0 ? x : null; } catch { return null; } }
        return new V1StateSettings(plan.Sweep?.ParkTime ?? TimeSpan.FromHours(24), TimeSpan.FromMinutes(Minutes("releaseCandidateTimeoutMinutes") ?? 20), plan.FinalEnvironment);
    }
}

/// <summary>The saga's state in the format v1 reads. Pure: the edit of one state comment's state, given the decision and the saga's state.</summary>
public static class V1StateFormat
{
    /// <summary>The decisions that change something v1 reads. The others (reviews, verdicts, merge requests, slots, leases) are v1's verbs' own writes or concern only the saga.</summary>
    static readonly HashSet<string> Wanted =
    [
        WorkItemActions.StepEntered, WorkItemActions.Parked, WorkItemActions.Resumed, WorkItemActions.ParkExpired, WorkItemActions.HoldCleared, WorkItemActions.MovedBack, WorkItemActions.Merged,
        WorkItemActions.Completed, WorkItemActions.HandedBack, WorkItemActions.Settling, WorkItemActions.Undelivered, WorkItemActions.MasterGreen, WorkItemActions.MasterBroken, WorkItemActions.MasterTimeout,
        WorkItemActions.SettleRearmed, WorkItemActions.Demoted, WorkItemActions.FixForward, WorkItemActions.ChecksGreen,
    ];

    /// <summary>The decisions after which the item is not parked any more, whatever v1's state comment still says about its park.</summary>
    static readonly HashSet<string> LiftsPark =
    [
        WorkItemActions.StepEntered, WorkItemActions.Resumed, WorkItemActions.ParkExpired, WorkItemActions.HoldCleared, WorkItemActions.MovedBack, WorkItemActions.Completed, WorkItemActions.HandedBack,
        WorkItemActions.Settling, WorkItemActions.Undelivered, WorkItemActions.MasterGreen, WorkItemActions.Demoted,
    ];

    /// <summary>The parks of the merge: v1 closes the PASS verdict (processed) when it parks the item for a failed merge, so that nothing merges it again.</summary>
    static readonly HashSet<string> MergeParks = ["merge-blocked", "merge-failed", "mechanical-exhausted"];

    public static bool Wants(string action) => Wanted.Contains(action);

    /// <summary>Brings <paramref name="state"/> (the state of the item's comment) to what the saga's <paramref name="saga"/> says after <paramref name="decision"/>. Changes nothing that is right already.</summary>
    public static void Apply(JsonObject state, WorkItemDecided decision, WorkItemState saga, DocsPlanSettings plan, V1StateSettings settings)
    {
        Park(state, decision, saga, settings);
        Counters(state, decision, saga);
        Step(state, decision, saga, plan);
        Verdict(state, decision, saga);
        Merge(state, decision, saga, settings);
    }

    // ---- the park --------------------------------------------------------------------------------------------------------------------------------------------

    static void Park(JsonObject s, WorkItemDecided d, WorkItemState saga, V1StateSettings cfg)
    {
        if (saga.Parked && saga.IsOpen)
        {
            var code = SweepParks.CodeOf(saga.ParkReason);
            // a park v1's verb wrote for the same cause is v1's own (its `until`, its counters of launches and criteria): it stays
            if (s["park"] is JsonObject current && SamePark(current["reason"]?.ToString() ?? "", saga.ParkReason, code)) return;
            var park = new JsonObject { ["reason"] = code, ["at"] = Iso(d.At), ["until"] = Iso(saga.ParkUntil ?? d.At + cfg.ParkTime) };
            // what v1 reads of these parks besides the reason (Verbs.Sweep.ps1: auto-unpark judges master-broken by park.failed and fix-forward-exhausted by the head it parked on)
            if (code == SweepParks.MasterBroken) { park["failed"] = new JsonArray([.. saga.MasterFailed.Select(f => (JsonNode?)f)]); park["sha"] = saga.MergeCommit; }
            if (code == "fix-forward-exhausted") { if (saga.PullRequest > 0) park["pr"] = saga.PullRequest; if (saga.Head.Length > 0) park["head"] = saga.Head; }
            s["park"] = park;
            return;
        }
        if (LiftsPark.Contains(d.Action)) s.Remove("park");
    }

    /// <summary>The saga's reason is "code" or "code: text"; v1's is the code, or a code with a colon of its own ("fv:release-candidate-timeout", "development:awaiting-checks").</summary>
    static bool SamePark(string v1Reason, string sagaReason, string sagaCode) =>
        v1Reason.Length > 0 && (v1Reason == sagaReason || v1Reason == sagaCode || sagaReason.StartsWith(v1Reason + ":", StringComparison.Ordinal) || v1Reason.StartsWith(sagaCode + ":", StringComparison.Ordinal));

    // ---- the counters the parks and the rounds are counted by --------------------------------------------------------------------------------------------

    static void Counters(JsonObject s, WorkItemDecided d, WorkItemState saga)
    {
        switch (d.Action)
        {
            // a round or a demotion is counted up; a park that parked at the limit is counted by v1's verb one past it, which stays
            case WorkItemActions.FixForward or WorkItemActions.Parked: Raise(s, "fix_forward_rounds", saga.FixForwardRounds); Raise(s, "demotions", saga.Demotions); break;
            case WorkItemActions.Demoted: Raise(s, "demotions", saga.Demotions); break;
            // green checks end the rounds (v1: Remove('fix_forward_rounds')); a lifted park starts the counts that parked the item again from 0 (unpark-item, Reset-ExpiredPark)
            case WorkItemActions.ChecksGreen: s.Remove("fix_forward_rounds"); break;
            case WorkItemActions.Resumed or WorkItemActions.ParkExpired: s.Remove("fix_forward_rounds"); s.Remove("demotions"); s.Remove("master_defect"); break;
        }
    }

    static void Raise(JsonObject s, string key, int count)
    {
        if (count > 0 && JournalJson.Int(s, key) < count) s[key] = count;
    }

    // ---- the step and the column -------------------------------------------------------------------------------------------------------------------------

    const int FlowKeep = 15;   // (Owner.ps1: $script:FlowKeep)

    static void Step(JsonObject s, WorkItemDecided d, WorkItemState saga, DocsPlanSettings plan)
    {
        if (d.Action != WorkItemActions.StepEntered) return;
        s.Remove("awaiting_checks");   // (the wait for CI of an earlier step: v1's sweep would move the item on once CI is green, a second time)
        if (d.Step is not { } name || !Enum.TryParse<StepKind>(name, out var step) || ColumnOf(plan, d.Plan, step) is not { Length: > 0 } column) return;
        // the stage entered (Add-FlowStamp): one entry per stage, not repeated when the verb of the session has stamped it
        var flow = s["flow"] as JsonArray ?? [];
        if (flow.LastOrDefault() is not JsonObject last || last["c"]?.ToString() != column)
        {
            var kept = flow.Select(x => x?.DeepClone()).ToList();
            kept.Add(new JsonObject { ["c"] = column, ["at"] = Iso(d.At) });
            s["flow"] = new JsonArray([.. kept.Skip(Math.Max(0, kept.Count - FlowKeep))]);
        }
        // the owner's claim follows the item to the column (Move-OwnerTo) when the saga moved it and the session did not: a demotion, a fix-forward round, a person's move back; the lease and the heartbeat are the plant's
        if (saga.IsOpen && !saga.Parked && saga.Step == step && s["claim"] is JsonObject claim && IsTrue(claim["owner"]) && claim["column"]?.ToString() != column) claim["column"] = column;
    }

    static string? ColumnOf(DocsPlanSettings plan, string planName, StepKind step) =>
        step == StepKind.Deliver ? (WorkPlans.IsCode(planName) && plan.ReleaseColumn.Length > 0 ? plan.ReleaseColumn : null) : plan.ColumnOf(step);

    // ---- the verdict -----------------------------------------------------------------------------------------------------------------------------------

    static void Verdict(JsonObject s, WorkItemDecided d, WorkItemState saga)
    {
        // a PASS that was merged, or that was parked for a failed merge, is processed: nothing merges it again (the merge verb, the sweep's PASS merge, base-update all read this)
        var closed = saga.MergeCommit.Length > 0 || (saga.Parked && saga.IsOpen && MergeParks.Contains(SweepParks.CodeOf(saga.ParkReason)));
        if (closed && s["verdict"] is JsonObject verdict && !IsTrue(verdict["processed"])) { verdict["processed"] = true; verdict.Remove("wait"); }
        // a PASS the saga voided (the head moved, CI was red on it, a FAIL, its pull request was closed) is no PASS (Invoke-Demote: Remove('verdict'), Remove('bot'))
        if (d.Action is WorkItemActions.Demoted or WorkItemActions.FixForward && saga.VerdictHead.Length == 0 && saga.MergeCommit.Length == 0) { s.Remove("verdict"); s.Remove("bot"); }
    }

    // ---- the merge record --------------------------------------------------------------------------------------------------------------------------------

    static void Merge(JsonObject s, WorkItemDecided d, WorkItemState saga, V1StateSettings cfg)
    {
        if (saga.MergeCommit.Length == 0) return;
        if (s["merge"] is not JsonObject m || !string.Equals(m["sha"]?.ToString(), saga.MergeCommit, StringComparison.OrdinalIgnoreCase))
        {
            // a merge v1 did not record (the gateway's own, the train's, a person's that the saga saw): the record the merge verb and the merge pickup write, and what they end (Invoke-MergePickup)
            s["merge"] = m = new JsonObject { ["sha"] = saga.MergeCommit, ["awaiting"] = "master-green", ["merged_at"] = Iso(d.At), ["pr"] = saga.PullRequest };
            foreach (var key in new[] { "handoff", "awaiting_checks", "bot" }) s.Remove(key);
            if (!saga.Parked) s.Remove("park");
        }
        else if (!JournalJson.Has(m, "pr") && saga.PullRequest > 0) m["pr"] = saga.PullRequest;

        if (!saga.IsOpen)
        {
            // the end of the settle (Invoke-Settle's finish): nothing awaited, done at
            if ((m["awaiting"]?.ToString() ?? "").Length > 0) m["awaiting"] = null;
            if (!JournalJson.Has(m, "done_at")) m["done_at"] = Iso(saga.CompletedAt ?? d.At);
            return;
        }
        m.Remove("done_at");   // (an item delivered and then undelivered, or a settle taken up again, is not done)
        var awaiting = m["awaiting"]?.ToString() ?? "";
        var rerun = saga.MasterRerunAt is not null && !IsTrue(m["rerun_attempted"]);
        if (saga.Status == WorkItemStatus.Settling)
        {
            if (awaiting != "release-queue")
            {
                m["awaiting"] = "release-queue"; m["queued_at"] = Iso(d.At);
                if (saga.Delivered.TryGetValue(cfg.FinalEnvironment.ToLowerInvariant(), out var release)) m["release"] = release;
            }
            return;
        }
        switch (saga.MasterState)
        {
            case "green":
                // v1 goes on from here by itself to the release queue (a release candidate that carries the commit) and its late states; it is only given the point it would have reached
                if (awaiting is not ("release-candidate" or "late-rc" or "release-queue"))
                {
                    m["awaiting"] = "release-candidate"; m["rc_deadline"] = Iso(d.At + cfg.ReleaseCandidateTimeout);
                    if (!JournalJson.Has(m, "green_at")) m["green_at"] = Iso(d.At);
                    if (saga.MasterGreenVia.Length > 0 && !string.Equals(saga.MasterGreenVia, saga.MergeCommit, StringComparison.OrdinalIgnoreCase)) m["green_via"] = saga.MasterGreenVia;
                }
                break;
            case "broken":
                if (awaiting != "master-broken") { m["awaiting"] = "master-broken"; m["broken_at"] = Iso(d.At); }
                break;
            case "timeout":
                if (awaiting != "master-timeout") m["awaiting"] = "master-timeout";
                break;
            default:   // waiting for master's CI, or no watch: v1's own wait after a merge
                if (awaiting != "master-green") m["awaiting"] = "master-green";
                break;
        }
        // the one re-run of the failed jobs (issue 79), so that v1 does not re-run them again and waits for the newer attempts
        if (rerun)
        {
            m["rerun_attempted"] = true; m["rerun_at"] = Iso(saga.MasterRerunAt!.Value);
            m["rerun_runs"] = new JsonArray([.. saga.MasterRerunRuns.Select(r => (JsonNode?)new JsonObject { ["id"] = r.Id, ["attempt"] = r.Attempt })]);
        }
    }

    static bool IsTrue(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    static string Iso(DateTimeOffset at) => JournalJson.Iso(at.UtcDateTime);
}

/// <summary>
/// The state comment follows the saga: a decision that changes what v1 reads brings the item's comment to what the saga's state says. The decision's sequence number is the version of the write, so a late decision never undoes
/// a newer one. A refusal for good (the state does not fit its budget) is logged and left; a fault is tried again a few times and then left, nothing remembered as failed: the next decision projects the whole state again, and the
/// card projection (DocsProjection) leaves an item behind the same way.
/// </summary>
public sealed class SagaStateProjection(Func<StateCommentProjection?> commentOf, IWorkItemStore store, DocsPlanSettings plan, V1StateSettings settings, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    static readonly TimeSpan[] Pauses = [TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8)];
    readonly Func<TimeSpan, CancellationToken, Task> wait = delay ?? ((d, ct) => Task.Delay(d, ct));

    public static bool Wants(WorkItemDecided d) => V1StateFormat.Wants(d.Action);

    /// <summary>Projects the decision. False when it gave up after the tries (the next decision projects again); true when the comment says what the saga says, or the decision is not this projection's.</summary>
    public async Task<bool> Project(WorkItemDecided d, CancellationToken ct)
    {
        if (!Wants(d) || commentOf() is not { } comment) return true;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                // the state as it is now, which is never older than the decision; a later generation (the item was reopened) projects by its own decisions
                if ((await store.Load(d.Item.ToString(), ct))?.State is not { } saga || saga.Generation != d.Generation) return true;
                await comment.Apply(d.Item, d.Sequence, s => V1StateFormat.Apply(s, d, saga, plan, settings), ct);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (JournalRefusedException e) { Console.Error.WriteLine($"state comment of {d.Item} after '{d.Action}' refused: {e.Message}"); return true; }   // for good: a retry says the same
            catch (Exception e) when (attempt < Pauses.Length)
            {
                Console.Error.WriteLine($"state comment of {d.Item} after '{d.Action}': {e.GetType().Name}; trying again");
                await wait(Pauses[attempt], ct);
            }
            catch (Exception e) { Console.Error.WriteLine($"state comment of {d.Item} after '{d.Action}' gave up: {e.GetType().Name}"); return false; }
        }
    }
}

/// <summary>A decision of the saga for the state comment's projection, on a queue of its own: GitHub being slow or down holds up this queue and nothing else (the history of the decision, the card, the merge facts).</summary>
[Fworker.Contracts.Messaging.MessageIdentity("workitem.state-projection")]
public sealed record ProjectStateComment(WorkItemDecided Decision);

/// <summary>The handlers of the saga's decisions for the state comment. Public because Wolverine's generated code refers to it.</summary>
public static class SagaStateProjectionHandler
{
    public const string Queue = "state-projection";

    /// <summary>The decisions that change what v1 reads go on to the queue of the projection; the others end here.</summary>
    public static OutgoingMessages Handle(WorkItemDecided m)
    {
        var o = new OutgoingMessages();
        if (SagaStateProjection.Wants(m)) o.Add(new ProjectStateComment(m));
        return o;
    }

    public static Task Handle(ProjectStateComment m, SagaStateProjection projection, CancellationToken ct) => projection.Project(m.Decision, ct);
}
