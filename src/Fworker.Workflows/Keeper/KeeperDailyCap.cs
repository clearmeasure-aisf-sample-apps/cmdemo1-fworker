namespace Fworker.Workflows.Keeper;

/// <summary>
/// V1.4: <c>keeper.maxNewItemsPerDay</c> as a rule of the keeper's commands. The backlog keeper may create this many new items (create-item, the children of split-item, the follow-up of amend-criteria) in a rolling 24 hours.
/// The gateway's verb pipeline (VerbPipeline) refuses a call that would need more before it runs, as it always did; the commands refuse it again at the item they would create, so the limit stays with v2 when the pipeline's
/// check goes with v1 (V3.2). An item is counted once by its issue number, whichever of the two records it. The record is in the process (a restart starts the count again; the items themselves are on GitHub), as before.
/// Only the keeper's calls are counted: the operator's own create-item (<c>by</c> "operator") has no limit.
/// </summary>
public sealed class KeeperDailyCap(Func<int> max, Func<DateTime>? clock = null)
{
    readonly object gate = new();
    readonly List<(DateTime At, int Item)> made = [];
    readonly Func<DateTime> now = clock ?? (() => DateTime.UtcNow);

    /// <summary>The limit (<c>keeper.maxNewItemsPerDay</c>).</summary>
    public int Max => max();

    void Prune() { var t = now(); made.RemoveAll(x => (t - x.At).TotalHours >= 24); }

    /// <summary>How many new items the keeper made in the last 24 hours.</summary>
    public int Used { get { lock (gate) { Prune(); return made.Count; } } }

    /// <summary>How many it may still make.</summary>
    public int Left { get { lock (gate) { Prune(); return Math.Max(0, Max - made.Count); } } }

    /// <summary>Records the issues the keeper created; one already recorded is not counted again.</summary>
    public void Record(IEnumerable<int> issues)
    {
        lock (gate)
        {
            Prune();
            foreach (var n in issues) if (n > 0 && !made.Any(x => x.Item == n)) made.Add((now(), n));
        }
    }

    /// <summary>Whether the call is the keeper's (the operator's calls carry <c>by</c> "operator" and are not limited).</summary>
    public static bool Counts(KeeperCall call) => !string.Equals(call.By, "operator", StringComparison.Ordinal);

    /// <summary>The refusal of a call that needs <paramref name="need"/> new items when fewer are left (the pipeline's answer: 429, exit 20, retry in an hour).</summary>
    public VerbRejection? Refusal(int need) => need > 0 && Left < need
        ? VerbRejection.Retry(20, "daily-item-limit", $"the backlog keeper may create {Max} new items in 24 hours; this would need {need} and {Left} are left", 3600)
        : null;
}
