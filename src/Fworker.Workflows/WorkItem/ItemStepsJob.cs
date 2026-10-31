using Fworker.Contracts.Ids;
using Fworker.Workflows.Delivery;

namespace Fworker.Workflows.WorkItem;

// W9.10f: the watcher of the sweep's item steps (a recurring job, every minute, on the leader only), the producer of the inputs SweepFlow takes that no timer and no event of the outside brings:
//  - master's CI for the merge commit of every code item that waits for it (waiting, broken or timed out): green, red (after the one re-run), or the re-run just started, told as MasterObserved;
//  - a merged code item that has no watch (a state saved before the watch existed): RearmSettle (issue 114);
//  - the pull request of every PASS that waits for its merge: closed without merging is PullRequestClosed (the PASS is void), and for a PASS held by the inspector an approval of its head by a human is ApprovalSeen.
// Everything it tells carries a business key, so a repeat is a no-op in the saga, and a verdict that was told for the same state is not told again (the entry is dropped when the item leaves the state it was told for). A read that
// fails is tried again at the next run and never reported as a state; at most PerRun items of each kind are read in a run, the longest unread first.

public sealed class ItemStepsJob(IWorkItemStore store, RepoId home, IMasterChecks master, IItemPullRequests pulls, Func<WorkItemInput, Task<bool>> publish, Func<bool> mayAct, Func<bool> budgetLow, bool masterWatch = true, Func<DateTimeOffset>? clock = null)
{
    public const int PerRun = 8;
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    readonly Dictionary<string, string> told = [];
    readonly Dictionary<string, DateTimeOffset> looked = [];

    public async Task<string> Run(CancellationToken ct)
    {
        if (!mayAct()) return "not-leader";
        if (budgetLow()) return "budget-low";
        var owned = (await store.OwnedStates(ct)).Where(s => s.Status == WorkItemStatus.Open && ItemId.TryParse(s.Item, out var id) && id.Repository == home).ToList();
        var watching = masterWatch ? owned.Where(s => WorkPlans.IsCode(s.Plan) && s.Step == StepKind.Deliver && s.MergeCommit.Length > 0 && s.MasterState is "waiting" or "broken" or "timeout").ToList() : [];
        var rearm = masterWatch ? owned.Where(s => WorkPlans.IsCode(s.Plan) && s.Step == StepKind.Deliver && s.MergeCommit.Length > 0 && s.MasterState.Length == 0).ToList() : [];
        var passes = owned.Where(s => s.Step == StepKind.Merge && s.VerdictHead.Length > 0 && s.MergeCommit.Length == 0 && s.PullRequest > 0).ToList();
        int reads = 0, told1 = 0;
        foreach (var state in rearm)
        {
            if (!mayAct()) return "not-leader";
            if (await Tell(state, new RearmSettle(state.ItemId, state.Generation, $"rearm|{state.MergeCommit}", "merged with no settle step"), $"rearm|{state.MergeCommit}", ct)) told1++;
        }
        foreach (var state in Due(watching))
        {
            if (!mayAct()) return "not-leader";
            reads++;
            try { if (await WatchMaster(state, ct)) told1++; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) { Console.Error.WriteLine($"item steps, master for {state.Item}: {e.GetType().Name}; read again at the next run"); }
            looked[state.Item] = now();
        }
        foreach (var state in Due(passes))
        {
            if (!mayAct()) return "not-leader";
            reads++;
            try { if (await WatchPass(state, ct)) told1++; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) { Console.Error.WriteLine($"item steps, pull request for {state.Item}: {e.GetType().Name}; read again at the next run"); }
            looked[state.Item] = now();
        }
        // what was told for an item that has left the state it was told in is forgotten
        var live = watching.Concat(rearm).Concat(passes).Select(s => s.Item).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in told.Keys.Where(k => !live.Contains(k.Split('|')[0])).ToList()) told.Remove(gone);
        foreach (var gone in looked.Keys.Where(k => !live.Contains(k)).ToList()) looked.Remove(gone);
        return reads + rearm.Count == 0 ? "none" : $"{reads} read, {rearm.Count} to re-arm, {told1} told";
    }

    IEnumerable<WorkItemState> Due(List<WorkItemState> states) =>
        states.OrderBy(s => looked.GetValueOrDefault(s.Item)).ThenBy(s => s.Item, StringComparer.Ordinal).Take(PerRun);

    async Task<bool> WatchMaster(WorkItemState state, CancellationToken ct)
    {
        var read = await master.Read(state, ct);
        var id = state.ItemId; var commit = state.MergeCommit;
        // what the same reading of the same state says again is not told again (the park changes the state, so a green after the park's end is told once more)
        var stamp = $"{state.MasterState}|{state.Parked}|{state.ParkUntil?.UtcTicks}";
        switch (read.Verdict)
        {
            case MasterVerdict.Pending when read.Rerun is { Count: > 0 }:
                return await Tell(state, new MasterObserved(id, state.Generation, $"master|{commit}|rerun|{string.Join(',', read.Rerun.Select(r => r.Id))}", commit, MasterVerdict.Pending, [], "", read.Rerun), $"master|{stamp}|rerun", ct);
            case MasterVerdict.Pending: return false;
            case MasterVerdict.Green:
                return await Tell(state, new MasterObserved(id, state.Generation, $"master|{commit}|green|{read.Via}|{stamp}", commit, MasterVerdict.Green, [], read.Via), $"master|{stamp}|green|{read.Via}", ct);
            default:
                return await Tell(state, new MasterObserved(id, state.Generation, $"master|{commit}|red|{string.Join(',', read.Failed.Take(10))}", commit, MasterVerdict.Red, read.Failed), $"master|{stamp}|red", ct);
        }
    }

    async Task<bool> WatchPass(WorkItemState state, CancellationToken ct)
    {
        var id = state.ItemId;
        var facts = await pulls.State(state.PullRequest, ct);
        if (facts is null) return false;
        if (facts.ClosedWithoutMerge) return await Tell(state, new PullRequestClosed(id, state.Generation, $"closed|{state.PullRequest}|{state.VerdictHead}", state.PullRequest), "closed", ct);
        if (facts.Merged || !state.Parked || SweepParks.CodeOf(state.ParkReason) != SweepParks.InspectorEscalated) return false;
        // a hold the inspector put on the PASS ends with a human's approval of the head the PASS covers (a push since then makes the approval another head's)
        if (await pulls.Approver(state.PullRequest, state.VerdictHead, ct) is not { Length: > 0 } by) return false;
        return await Tell(state, new ApprovalSeen(id, state.Generation, $"approval|{state.PullRequest}|{state.VerdictHead}|{by}", state.PullRequest, state.VerdictHead, by), $"approval|{by}", ct);
    }

    async Task<bool> Tell(WorkItemState state, WorkItemInput input, string what, CancellationToken ct)
    {
        var entry = $"{state.Item}|{what}";
        if (told.ContainsKey(entry)) return false;
        if (!await publish(input)) throw new InvalidOperationException("the Wolverine host did not take the input");
        told[entry] = "";
        return true;
    }
}
