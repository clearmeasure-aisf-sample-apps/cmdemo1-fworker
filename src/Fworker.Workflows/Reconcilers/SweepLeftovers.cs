using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Workflows.Keeper;
using Fworker.Workflows.WorkItem;

namespace Fworker.Workflows.Reconcilers;

// V1.4 (docs/design/fworker-wolverine-implementation.md, row V1.4): the two steps of v1's sweep that only the sweep did, as rules of v2.
//   SagaGapRenew          Invoke-GapRenew (Verbs.Sweep.ps1, issue 94): an owner's claim whose lease ran out while no gateway was up is renewed, not offered again as a relaunch;
//   ReleaseQueueFlags     the flag of a Release Queue card that has no merge record (Verbs.Sweep.ps1, issue 117).
// The other two leftovers need no new rule: the base update fallback (BaseWatch.AfterSweep) becomes the job base-watch-fallback, the cap of new items a day is KeeperDailyCap in the keeper's commands.

/// <summary>One saga item whose owner's lease ended inside the gap: the owner, and when the lease had run out.</summary>
public sealed record GapRenewal(WorkItemState State, string Holder, DateTimeOffset Lapsed)
{
    public int Item => State.ItemId.Number;
}

/// <summary><c>Invoke-GapRenew</c> for the items that have a saga.</summary>
public static class SagaGapRenew
{
    /// <summary>
    /// The items to renew: open, not parked, with an owner (the plant's record of the accept: an item with a lease and nobody holding it is a step that waits for a session, which the saga's own timer relaunches) and a lease that
    /// ended inside the gap, from <paramref name="since"/> (the predecessor's last renewal or release) to <paramref name="until"/> (now). A lease that ended before the gap ran out while a gateway was up, and is the saga's to handle;
    /// one still running needs nothing.
    /// </summary>
    public static IReadOnlyList<GapRenewal> Due(IEnumerable<WorkItemState> states, DateTimeOffset since, DateTimeOffset until, Func<int, string?> ownerOf)
    {
        var due = new List<GapRenewal>();
        foreach (var s in states)
        {
            if (s.Status != WorkItemStatus.Open || s.Parked || s.LeaseDue is not { } lease || lease < since || lease > until) continue;
            if (ownerOf(s.ItemId.Number) is not { Length: > 0 } holder) continue;
            due.Add(new GapRenewal(s, holder, lease));
        }
        return due;
    }

    /// <summary>
    /// Sends <see cref="LeaseRenewed"/> for each item due (the saga gives the step a new lease from now, as the script did, and arms its timer; the timer of the lapsed lease is stale then). The key names the lapsed lease, so a pass that
    /// is repeated renews once. Returns the items renewed.
    /// </summary>
    public static async Task<IReadOnlyList<int>> Run(IEnumerable<WorkItemState> states, DateTimeOffset since, DateTimeOffset until, Func<int, string?> ownerOf, Func<LeaseRenewed, Task> send)
    {
        var renewed = new List<int>();
        foreach (var d in Due(states, since, until, ownerOf))
        {
            await send(new LeaseRenewed(d.State.ItemId, d.State.Generation, d.State.ArmId, $"gap|{d.Lapsed.UtcTicks}"));
            renewed.Add(d.Item);
        }
        return renewed;
    }
}

/// <summary>The step the job calls (the job's tests stand in for it).</summary>
public interface IReleaseQueueFlags
{
    Task<IReadOnlyList<int>> Flag(IEnumerable<(int Item, string Column)> candidates);
}

/// <summary>
/// The flag of a Release Queue card that has no merge record. Only a merge moves a card into the Release Queue (the settle, or the saga's projection); an open item there without a merge record got there some other way (issue 290, 117),
/// so a journal line says so once (key <c>rq-no-merge</c>) and the operator hears of it. Nothing moves the card: a person moves it back.
/// </summary>
public sealed class ReleaseQueueFlags(VerbContext cx) : IReleaseQueueFlags
{
    public const string Code = "release-queue-without-merge";

    /// <summary>The candidates are the open items whose card is in a hold column and whose saga (if any) has no merge commit. The state comment is read again under the item's lock: a merge record there clears the flag. Returns the items that have no merge record (the journal line is written once, whichever pass finds it first).</summary>
    public async Task<IReadOnlyList<int>> Flag(IEnumerable<(int Item, string Column)> candidates)
    {
        var lacking = new List<int>();
        foreach (var (n, column) in candidates)
        {
            try
            {
                await using (await cx.Locks.Item(n, TimeSpan.FromSeconds(3)))
                {
                    var st = await cx.State.Read(n);
                    if (Ps.Has(st.State, "merge") && st.State["merge"] is JsonObject merge && Ps.Has(merge, "sha") && Ps.Truthy(merge["sha"])) continue;
                    var wrote = await cx.State.Journal(n, st, "flagged", new JsonObject
                    {
                        ["column"] = column, ["code"] = Code,
                        ["reason"] = "the card is in the Release Queue but the item has no merge record (its pull request is not merged by the factory); a person moves it back",
                    }, "rq-no-merge");
                    if (wrote) await cx.State.Save(n, st);
                    lacking.Add(n);
                }
            }
            catch (VerbRejection r) when (r.Code is "item-busy" or "state-conflict") { }   // (a later pass looks again)
        }
        return lacking;
    }
}
