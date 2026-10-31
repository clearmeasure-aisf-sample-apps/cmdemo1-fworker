using System.Text.Json.Nodes;
using Fworker.Workflows.WorkItem;

namespace Fworker.Workflows.Adoption;

/// <summary>
/// W9.10m: the rule of the reconciliation. At every start, for every open item, the mapping of v1's state (<see cref="AdoptionMapper"/>) is an upsert into the saga's row in which the state comment wins when its revision is
/// newer than the one the row was last brought to (<see cref="WorkItemState.CommentRev"/>): an item with no row gets one; a row whose comment moved on since is brought to the comment, keeping what the saga alone knows (its
/// generation and decisions, the keys applied, the arm it is on while the step is the same, the slot it holds); a row the comment has not moved past is left alone. Two guards keep the rule from undoing the saga: a row with
/// decisions that the projection has not written to the comment yet (<c>projectedSequence</c> behind <see cref="WorkItemState.Sequence"/>) is the newer of the two whatever the comment's revision says, and a row whose card the
/// projection has not moved to the step yet (<c>cardProjectedSequence</c> behind <see cref="WorkItemState.CardSeq"/>) is not read from the card: the column the mapping reads is the step the saga has left, and the update would end the
/// live session of the step it is on. A revision that moved only because the saga's own projection (or v1's verb, or the train's writer) wrote the comment finds the same state in it and changes nothing but the revision it was read at:
/// <c>Keep</c> carries what the comment does not hold (the failure and launch budgets, the mechanical fixes, the idle follow-ups, the reviews of other steps, the release of an owner, the approver), so that the comment of a row the saga
/// ran is not a newer reading of it. It is idempotent: the same snapshot adopted twice gives the same row.
/// </summary>
public static class AdoptionReconciler
{
    /// <param name="existing">The saga's row of the item (the open instance or the last, completed one); null when it has none.</param>
    /// <param name="projectedSequence">The version the state comment's projection has applied for the item (ProjectionVersions, target "comment"): the sequence of the last decision written to the comment.</param>
    /// <param name="cardProjectedSequence">The version the card projection has applied for the item (ProjectionVersions, target "card"): the sequence of the last decision that moved the card. The card is where the saga wants it once this reaches <see cref="WorkItemState.CardSeq"/>.</param>
    public static AdoptionResult Reconcile(WorkItemState? existing, long projectedSequence, long cardProjectedSequence, AdoptionMapping mapping, DateTimeOffset now)
    {
        if (mapping.Skipped) return new AdoptionResult(AdoptionKind.Skipped, mapping.SkipReason!, null, null, [], [], null, null, [], null, mapping.Notes);
        var mapped = mapping.State!;
        if (existing is null) return Adopt(mapped, mapping, now);
        // the instance ended (W9.10n fences the re-adoption of an item that v1 reopened or finished meanwhile): it is not this rule's to bring back
        if (!existing.IsOpen) return Beside(AdoptionKind.Skipped, "the saga's instance of the item is completed", existing, mapping);
        if (mapped.CommentRev <= existing.CommentRev) return Beside(AdoptionKind.Kept, $"the state comment (revision {mapped.CommentRev}) is not newer than the one adopted (revision {existing.CommentRev})", existing, mapping);
        if (existing.Sequence > projectedSequence) return Beside(AdoptionKind.Kept, $"the saga has decisions the comment lacks (sequence {existing.Sequence}, projected {projectedSequence}): the projection writes them", existing, mapping);
        // the step is read from the card's column, and the card is a projection target of its own that lags the comment's: the saga entered a step the card does not show yet (a restart between the two writes). The column is
        // the step the saga has left; taking it would open a new arm and end the session of the step the item is on. (Verify and Merge share the validation column, and a merged item's step is the comment's, not the card's.)
        if (existing.CardSeq > cardProjectedSequence && mapped.Step != StepKind.Deliver && ColumnStep(mapped.Step) != ColumnStep(existing.Step))
            return Beside(AdoptionKind.Kept, $"the card is behind the saga (step entered at sequence {existing.CardSeq}, card projected {cardProjectedSequence}): its column is the step the saga has left", existing, mapping);
        return Update(existing, mapped, mapping, now);
    }

    // the step the card's column stands for: the validation column holds both Verify and Merge
    static StepKind ColumnStep(StepKind step) => step == StepKind.Merge ? StepKind.Verify : step;

    static AdoptionResult Adopt(WorkItemState state, AdoptionMapping mapping, DateTimeOffset now)
    {
        state.Sequence = 0;
        var d = Decide(state, now, $"from v1's state, revision {state.CommentRev}", $"{state.Plan} plan, {state.Step}{(state.Parked ? $", parked: {state.ParkReason}" : "")}");
        state.LastKey = $"adopt|{state.CommentRev}"; state.Emitted = [d];
        return Result(AdoptionKind.Adopted, $"adopted on the {state.Plan} plan at {state.Step}", state, d, mapping, withSlot: true);
    }

    static AdoptionResult Update(WorkItemState existing, WorkItemState mapped, AdoptionMapping mapping, DateTimeOffset now)
    {
        var next = WorkItemState.FromJson(mapped.ToJson());
        Keep(existing, next, now);
        // the comment's revision is not part of the comparison: only whether the state changed
        var probe = WorkItemState.FromJson(next.ToJson()); probe.CommentRev = existing.CommentRev;
        var changed = Changes(existing, probe);
        if (changed.Count == 0)
        {
            var same = WorkItemState.FromJson(existing.ToJson()); same.CommentRev = mapped.CommentRev;
            return Result(AdoptionKind.Unchanged, $"the state comment (revision {mapped.CommentRev}) says what the saga's row says", same, null, mapping, withSlot: false);
        }
        var d = Decide(next, now, $"from v1's state, revision {next.CommentRev}", string.Join(", ", changed.Take(8)) + (changed.Count > 8 ? ", ..." : ""));
        next.LastKey = $"adopt|{next.CommentRev}"; next.Emitted = [d];
        return Result(AdoptionKind.Updated, $"the state comment (revision {next.CommentRev}) won: {string.Join(", ", changed.Take(8))}", next, d, mapping, withSlot: true);
    }

    // what the saga alone knows survives the comment: the identity and the history of the instance, the keys, the arm while the step is the same, the slot it holds
    static void Keep(WorkItemState existing, WorkItemState next, DateTimeOffset now)
    {
        next.Generation = existing.Generation; next.Sequence = existing.Sequence; next.Keys = [.. existing.Keys]; next.StartedAt = existing.StartedAt; next.Source = existing.Source; next.CardSeq = existing.CardSeq;
        next.AutoUnparks = new Dictionary<string, int>(existing.AutoUnparks);
        next.DesignMarker ??= existing.DesignMarker;
        if (next.Footprint.Count == 0) { next.Footprint = [.. existing.Footprint]; next.FootprintFromPullRequest = existing.FootprintFromPullRequest; }
        next.EverGranted |= existing.EverGranted;
        if (next.ChecksHead.Length == 0 && existing.ChecksHead.Length > 0 && string.Equals(existing.ChecksHead, next.Head, StringComparison.OrdinalIgnoreCase)) { next.ChecksHead = existing.ChecksHead; next.ChecksState = existing.ChecksState; next.ChecksFailed = [.. existing.ChecksFailed]; }
        foreach (var kv in existing.Delivered) next.Delivered.TryAdd(kv.Key, kv.Value);   // (a delivery the saga saw; the trains may not show it any more)
        next.LastKey = existing.LastKey; next.Emitted = [.. existing.Emitted];
        if (next.MasterState.Length > 0 && next.MasterState == existing.MasterState)
        {
            // the wait for master the saga is in: its clock, re-run and verdict are the watcher's, not the comment's
            next.MasterSince = existing.MasterSince; next.MasterDue = existing.MasterDue; next.MasterRerunAt = existing.MasterRerunAt; next.MasterRerunRuns = [.. existing.MasterRerunRuns];
            next.MasterFailed = existing.MasterFailed.Count > 0 ? [.. existing.MasterFailed] : next.MasterFailed; next.MasterGreenVia = existing.MasterGreenVia.Length > 0 ? existing.MasterGreenVia : next.MasterGreenVia;
        }

        // the same arm: the same plan, step and status, parked or not, and for a PASS the same head (a new verdict is new work)
        var sameArm = existing.Step == next.Step && existing.Parked == next.Parked && existing.Status == next.Status && existing.Plan == next.Plan
            && (next.Step != StepKind.Merge || string.Equals(existing.VerdictHead, next.VerdictHead, StringComparison.OrdinalIgnoreCase));
        KeepWhatTheCommentDoesNotHold(existing, next, sameArm);
        if (sameArm)
        {
            next.ArmSeq = existing.ArmSeq; next.ArmId = existing.ArmId; next.Outbox = [.. existing.Outbox];
            if (next.Status == WorkItemStatus.Settling && existing.SettleUntil is { } until) next.SettleUntil = until;
            // a renewed lease is the later of the two; the slot the saga holds is the scheduler's grant, not the claim's
            if (existing.LeaseDue is { } lease && next.LeaseDue is { } claimLease) next.LeaseDue = lease > claimLease ? lease : claimLease;
            if (existing.SlotId.Length > 0) { next.SlotId = existing.SlotId; next.SlotRuntime = existing.SlotRuntime; next.SlotHost = existing.SlotHost; next.SlotAsked = false; next.SlotWait = ""; next.SlotKnown = true; next.AskSeq = existing.AskSeq; }
            else if (existing.SlotAsked && next.SlotAsked) { next.AskSeq = existing.AskSeq; next.SlotWait = existing.SlotWait; }
            next.SlotStamp = existing.SlotStamp;
            if (existing.Step == StepKind.Merge)
            {
                // the merge calls the saga has made on this verdict are the saga's own
                next.MergeAttempt = existing.MergeAttempt; next.MergeRequest = existing.MergeRequest; next.MergeAnswers = existing.MergeAnswers; next.MergeUpdates = existing.MergeUpdates; next.MergeUpdatedFrom = existing.MergeUpdatedFrom;
                next.MergeBlockedReason = existing.MergeBlockedReason; next.MergeRetryAt = existing.MergeRetryAt; next.MergeAwaitingChecks = existing.MergeAwaitingChecks; next.MergeRequestEmitted = existing.MergeRequestEmitted; next.TrainEntered = existing.TrainEntered;
                next.ChecksHead = existing.ChecksHead; next.ChecksState = existing.ChecksState; next.ChecksFailed = [.. existing.ChecksFailed];
            }
            return;
        }
        // another step, another park, another plan: a new arm, as the saga's own Enter makes it, and the old arm's session and place in the line go back
        next.ArmSeq = existing.ArmSeq + 1; next.ArmId = $"{next.Generation}.{next.ArmSeq}";
        next.SlotStamp = existing.SlotStamp; next.AskSeq = existing.AskSeq;
        var outbox = new List<StateCommand>();
        if (existing.SlotId.Length > 0 && existing.SlotId != next.SlotId) outbox.Add(new EndSession($"end|{existing.Item}|{existing.SlotId}", existing.SlotId));
        if (existing.SlotKnown && !next.SlotKnown) outbox.Add(new ClearSlot($"clear|{existing.Item}|{existing.Generation}|adopted|{existing.Sequence}", "the state comment moved the item"));
        if (existing.TrainEntered && !next.TrainEntered && next.MergeCommit.Length == 0) outbox.Add(new LeaveTrain($"leave|{existing.Item}|{existing.Generation}|{existing.Sequence}", "the state comment moved the item"));
        if (next.SlotAsked)
        {
            next.AskSeq = existing.AskSeq + 1;
            var phase = next.Step switch { StepKind.Design => "design", StepKind.Develop => "develop", _ => "verify" };
            var depth = next.Step switch { StepKind.Design => 1, StepKind.Develop => 2, _ => 3 };
            outbox.Add(new AskSlot($"ask|{next.Item}|{next.Generation}|{next.AskSeq}", next.ArmId, phase, next.EverGranted, depth, [.. next.Footprint], next.FootprintFromPullRequest, now, next.CurrentPlan.TopRank));
        }
        next.Outbox = outbox;
    }

    // What the comment does not hold, or holds less of than the saga knows. The comment is written by the projection (park, flow, claim.column, verdict.processed, merge, the rounds), by v1's verbs and by the train's writer, and it
    // moves its revision with each of them; a row the saga ran must come out of the next start as it went in, or every restart would reset the budgets and the holds below. So the budgets only grow here (a stale count is the safe
    // error: the item parks sooner, it never loops), and the rest is carried when the comment says nothing of it.
    static void KeepWhatTheCommentDoesNotHold(WorkItemState existing, WorkItemState next, bool sameArm)
    {
        next.MechanicalFixes = Math.Max(existing.MechanicalFixes, next.MechanicalFixes);
        next.IdleFollowUps = Math.Max(existing.IdleFollowUps, next.IdleFollowUps);
        foreach (var kv in existing.Failures) next.Failures[kv.Key] = Math.Max(kv.Value, next.Failures.GetValueOrDefault(kv.Key));
        foreach (var kv in existing.Launches) next.Launches[kv.Key] = Math.Max(kv.Value, next.Launches.GetValueOrDefault(kv.Key));

        // the reviews of the steps the item is not on are the history of the item; the step it is on starts again with a new arm (entering a step starts its rounds again)
        var current = next.Step.ToString();
        foreach (var kv in existing.ReviewRounds)
            if (kv.Key != current || sameArm) next.ReviewRounds[kv.Key] = Math.Max(kv.Value, next.ReviewRounds.GetValueOrDefault(kv.Key));
        foreach (var done in existing.ReviewsComplete)
            if ((done != current || sameArm) && !next.ReviewsComplete.Contains(done)) next.ReviewsComplete.Add(done);

        // the park the projection wrote is the saga's own by its code: the saga's reason is "code" or "code: text", the comment's the code
        var samePark = existing.Parked && next.Parked && (existing.ParkReason == next.ParkReason || SweepParks.CodeOf(existing.ParkReason) == next.ParkReason);
        if (samePark)
        {
            next.ParkReason = existing.ParkReason;
            // (the comment keeps milliseconds, the saga's clock more)
            if (existing.ParkUntil is { } until && next.ParkUntil is { } written && (until - written).Duration() < TimeSpan.FromMilliseconds(1)) next.ParkUntil = until;
            next.OwnerReleased = existing.OwnerReleased;
            if (existing.OwnerIdleDue is { } idle && next.OwnerIdleDue is not null) next.OwnerIdleDue = idle;
        }
        if (existing.Step == next.Step)
        {
            next.ApprovedBy = existing.ApprovedBy;
            if (next.PullRequest == 0) next.PullRequest = existing.PullRequest;
            if (next.Head.Length == 0 && next.PullRequest == existing.PullRequest) next.Head = existing.Head;
        }
    }

    // the names of the fields that differ, from the two rows' JSON
    static List<string> Changes(WorkItemState before, WorkItemState after)
    {
        var a = JsonNode.Parse(before.ToJson()) as JsonObject ?? []; var b = JsonNode.Parse(after.ToJson()) as JsonObject ?? [];
        var names = new List<string>();
        foreach (var kv in b) if (!a.TryGetPropertyValue(kv.Key, out var old) || Canon(old) != Canon(kv.Value)) names.Add(kv.Key);
        foreach (var kv in a) if (!b.ContainsKey(kv.Key)) names.Add(kv.Key);
        return names;
    }

    // a value's JSON with the properties of its objects in key order: a dictionary that was filled in another order is the same dictionary
    static string? Canon(JsonNode? node) => node is null ? null : Sorted(node).ToJsonString();

    static JsonNode Sorted(JsonNode node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create(kv.Key, kv.Value is null ? null : Sorted(kv.Value)))),
        JsonArray a => new JsonArray([.. a.Select(x => x is null ? null : Sorted(x))]),
        _ => node.DeepClone(),
    };

    static WorkItemDecided Decide(WorkItemState state, DateTimeOffset now, params string[] reasons) =>
        new(state.ItemId, state.Generation, ++state.Sequence, WorkItemActions.Adopted, state.Plan, state.Status == WorkItemStatus.Completed ? null : state.Step.ToString(), state.ArmId.Length == 0 ? null : state.ArmId, reasons, now);

    static AdoptionResult Result(AdoptionKind kind, string reason, WorkItemState state, WorkItemDecided? decision, AdoptionMapping mapping, bool withSlot)
    {
        // the timers and the commands are the saga's own readings of the state; an unchanged row asks for none
        var timers = decision is null ? [] : WorkItemSaga.TimersOf(state);
        var commands = decision is null ? [] : WorkItemSaga.CommandsOf(state);
        // the ledger rows only for the slot the row holds (a row that kept the scheduler's own slot adopts none)
        var slot = withSlot && mapping.Slot is { } s && s.SlotId == state.SlotId ? s : null;
        return new AdoptionResult(kind, reason, state, decision, timers, commands, slot, withSlot ? mapping.Flight : null, mapping.QcRows, mapping.MergedRow, mapping.Notes);
    }

    // a row that is left as it is (or skipped): the rows beside it are still the mapping's, they are idempotent
    static AdoptionResult Beside(AdoptionKind kind, string reason, WorkItemState existing, AdoptionMapping mapping) =>
        new(kind, reason, existing, null, [], [], null, null, mapping.QcRows, mapping.MergedRow, mapping.Notes);
}
