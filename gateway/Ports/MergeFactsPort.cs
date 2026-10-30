using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Workflows.WorkItem;
using Wolverine;

// W9.10h: MergeFactsRead after the merge. A feature item that has been merged waits on its Deliver step for the release; but a merge whose Build skipped its code checks (or whose pull request changed documentation only) has no
// release to wait for, and the saga settles such an item as docs (Verbs.Delivery.ps1 Get-MergeDocsOnly; WorkSteps.cs DeliverStep). The saga cannot read GitHub, so it has to be told what the merge commit's own checks and the
// pull request's files say: nothing sent that input, so a docs-only merge on the feature plan stayed on Deliver for ever (the audit of 2026-10-30).
// The step entry of a feature item on its Deliver step (the saga's decision "step-entered") starts a read of the merge commit's check runs. A Build that has not finished says nothing (the saga judges unknown checks as unknown), so the
// read is made again every minute until every check run has completed, at most MaxAttempts times; the last read is sent as it is (the files alone can decide). Each read that is sent has its own key, and the saga applies a
// repeated judgement as the nothing it is. A GitHub that does not answer is a read that found the checks not finished. Where the engine has no GitHub credential nothing is read and the item waits for its delivery as before.

/// <summary>One read of the facts of a merge, scheduled by the entry of the Deliver step and by the read before it. Public because Wolverine's generated code refers to it.</summary>
public sealed record MergeFactsDue(ItemId Item, int Generation, int Attempt);

/// <summary>What one read made: the input for the saga, if the facts are in (or the attempts are spent), and the next read, if they are not.</summary>
public sealed record MergeFactsOutcome(MergeFactsRead? Facts, MergeFactsDue? Again);

public sealed class MergeFactsPort(IWorkItemStore store, IItemIssueReader? reader)
{
    public const int MaxAttempts = 30;
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    /// <summary>Whether a decision of the saga starts the reads: a feature item that entered its Deliver step.</summary>
    public static bool Wants(WorkItemDecided d) =>
        d.Action == WorkItemActions.StepEntered && d.Step == nameof(StepKind.Deliver) && string.Equals(d.Plan, WorkPlans.Feature, StringComparison.OrdinalIgnoreCase);

    public async Task<MergeFactsOutcome> Run(MergeFactsDue due, CancellationToken ct)
    {
        var none = new MergeFactsOutcome(null, null);
        if (reader is null) return none;
        // the item may have moved on since the read was scheduled (delivered, completed, another generation): then there is nothing to settle
        if ((await store.Load(due.Item.ToString(), ct))?.State is not { IsOpen: true } state || state.Generation != due.Generation || state.Step != StepKind.Deliver
            || state.Status != WorkItemStatus.Open || state.MergeCommit.Length == 0 || !string.Equals(state.Plan, WorkPlans.Feature, StringComparison.OrdinalIgnoreCase)) return none;
        IReadOnlyList<CommitCheck>? checks = null;
        try { checks = await reader.ReadCheckRuns(due.Item.Repository, state.MergeCommit, ct); }
        catch (Exception e) when (e is not OperationCanceledException) { Console.Error.WriteLine($"merge facts of {due.Item}: {e.GetType().Name}"); }
        var finished = checks is { Count: > 0 } && checks.All(c => string.Equals(c.Status, "completed", StringComparison.OrdinalIgnoreCase));
        if (!finished && due.Attempt < MaxAttempts) return new MergeFactsOutcome(null, due with { Attempt = due.Attempt + 1 });
        IReadOnlyList<string>? files = null;
        if (state.PullRequest > 0)
            try { files = await reader.ReadPullRequestFiles(due.Item.Repository, state.PullRequest, ct); }
            catch (Exception e) when (e is not OperationCanceledException) { Console.Error.WriteLine($"merge facts of {due.Item}: files, {e.GetType().Name}"); }
        var build = (checks ?? []).Select(c => new BuildCheck(c.Name, c.Status, c.Conclusion)).ToList();
        return new MergeFactsOutcome(new MergeFactsRead(due.Item, state.Generation, $"facts|{state.Generation}|{state.MergeCommit}|{due.Attempt}", build, files), null);
    }
}

/// <summary>The handlers of the reads. Public because Wolverine's generated code refers to them.</summary>
public static class MergeFactsHandler
{
    public static OutgoingMessages Handle(WorkItemDecided d)
    {
        var o = new OutgoingMessages();
        if (MergeFactsPort.Wants(d)) o.Add(new MergeFactsDue(d.Item, d.Generation, 1));
        return o;
    }

    public static async Task<OutgoingMessages> Handle(MergeFactsDue due, MergeFactsPort port, CancellationToken ct)
    {
        var r = await port.Run(due, ct);
        var o = new OutgoingMessages();
        if (r.Facts is { } facts) o.Add(facts);
        if (r.Again is { } again) o.Add(again.ScheduledAt(DateTimeOffset.UtcNow + MergeFactsPort.Every));
        return o;
    }
}
