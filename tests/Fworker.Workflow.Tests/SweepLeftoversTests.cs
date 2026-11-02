using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Workflows.Keeper;
using Fworker.Workflows.Reconcilers;
using Fworker.Workflows.WorkItem;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// V1.4: the steps that only v1's sweep did, as rules of v2: the cap of new items a day in the keeper's commands (keeper.maxNewItemsPerDay), the gap renewal of the saga's owner leases (Invoke-GapRenew, issue 94) and the flag of
/// a Release Queue card without a merge record (issue 117).
/// </summary>
public class SweepLeftoversTests
{
    const string Criteria = "Does a thing.\n\n## Acceptance criteria\n\n- the thing is done and visible";

    static KeeperWorld WorldWithCap(int max, out KeeperDailyCap cap)
    {
        var w = new KeeperWorld();
        w.Add(5, "An item", "x\n\n## Acceptance criteria\n\n- the list loads in under 2 seconds", "Technical Design");
        cap = new KeeperDailyCap(() => max, () => w.Clock);
        w.Cx = new VerbContext(w.Rules, w, w, w, w, () => w.Clock, cap);
        w.Verbs = new KeeperVerbs(w.Cx);
        return w;
    }

    static object Create(string by = "backlog-keeper") => new { title = "A new piece of work", body = Criteria, policy = "backlog-low", reason = "thin backlog", by };

    static object[] Kids(int n) => Enumerable.Range(1, n).Select(i => (object)new { title = $"Part number {i}", body = "Does part of it.\n\n- part is done and visible" }).ToArray();

    // ---- keeper.maxNewItemsPerDay ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheKeeperMayCreateTheDaysItemsAndNoMore()
    {
        var w = WorldWithCap(2, out var cap);
        await w.Run("create-item", Create());
        await w.Run("create-item", Create());
        var refused = await w.Refused("create-item", Create(), 20, "daily-item-limit");
        Assert.Equal(3600, refused["retryAfterSeconds"]!.GetValue<int>());
        Assert.Contains("may create 2 new items in 24 hours; this would need 1 and 0 are left", refused["reason"]!.ToString());
        Assert.Equal(2, cap.Used);
        Assert.Equal(0, cap.Left);
        Assert.Equal(2, w.Issues.Values.Count(i => i.Number >= 100));   // (the refused call made no issue)
    }

    [Fact]
    public async Task TheLimitIsARollingTwentyFourHours()
    {
        var w = WorldWithCap(1, out _);
        await w.Run("create-item", Create());
        await w.Refused("create-item", Create(), 20, "daily-item-limit");
        w.Clock = w.Clock.AddHours(24).AddMinutes(1);
        await w.Run("create-item", Create());
    }

    [Fact]
    public async Task ASplitMakesAllItsChildrenOrNone()
    {
        var w = WorldWithCap(2, out var cap);
        await w.Refused("split-item", new { item = 5, children = Kids(3), policy = "oversized-item", reason = "too big", by = "backlog-keeper" }, 20, "daily-item-limit");
        Assert.DoesNotContain(w.Issues.Keys, k => k >= 100);   // (no child, and the parent is not closed)
        Assert.True(w.Issues[5].Open);
        Assert.Equal(0, cap.Used);
        await w.Run("split-item", new { item = 5, children = Kids(2), policy = "oversized-item", reason = "too big", by = "backlog-keeper" });
        Assert.Equal(2, cap.Used);
    }

    [Fact]
    public async Task AFollowUpOfAnAmendmentCountsAndAnAmendmentWithoutOneDoesNot()
    {
        var w = WorldWithCap(1, out var cap);
        var amend = new Dictionary<string, object?> { ["item"] = 5, ["criterion"] = "loads in under 2 seconds", ["replacement"] = "loads in under 5 seconds", ["policy"] = "criterion-dispute", ["reason"] = "measured", ["by"] = "backlog-keeper", ["follow_up"] = true };
        await w.Run("amend-criteria", amend);
        Assert.Equal(1, cap.Used);
        w.Add(6, "Another", "x\n\n## Acceptance criteria\n\n- the list loads in under 2 seconds", "Technical Design");
        await w.Refused("amend-criteria", new Dictionary<string, object?>(amend) { ["item"] = 6 }, 20, "daily-item-limit");
        Assert.Equal("x\n\n## Acceptance criteria\n\n- the list loads in under 2 seconds", w.Issues[6].Body);   // (nothing was written)
        await w.Run("amend-criteria", new Dictionary<string, object?>(amend) { ["item"] = 6, ["follow_up"] = false });
    }

    [Fact]
    public async Task TheOperatorsOwnCallsAreNotLimited()
    {
        var w = WorldWithCap(1, out var cap);
        await w.Run("create-item", Create("operator"));
        await w.Run("create-item", Create("operator"));
        Assert.Equal(0, cap.Used);
        await w.Run("create-item", Create());   // (the keeper's one is still allowed)
        await w.Refused("create-item", Create(), 20, "daily-item-limit");
    }

    [Fact]
    public void AnIssueIsCountedOnceWhoeverRecordsIt()
    {
        var cap = new KeeperDailyCap(() => 3);
        cap.Record([100, 101]);
        cap.Record([101, 102, 0]);   // (the pipeline records what the answer names; the command recorded it first)
        Assert.Equal(3, cap.Used);
        Assert.Equal(0, cap.Left);
        Assert.NotNull(cap.Refusal(1));
        Assert.Null(cap.Refusal(0));
    }

    [Fact]
    public async Task WithoutACapTheCommandsAreAsTheyWere()
    {
        var w = new KeeperWorld();
        for (var i = 0; i < 12; i++) await w.Run("create-item", Create());
    }

    // ---- the gap renewal of the saga's owner leases ----------------------------------------------------------------------------------------

    static readonly DateTimeOffset Since = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset Until = Since.AddMinutes(20);

    static WorkItemState Item(int n, DateTimeOffset? lease, bool parked = false, string status = WorkItemStatus.Open) =>
        new() { Item = $"acme/work#{n}", Generation = 2, Status = status, Step = StepKind.Develop, ArmId = $"2.{n}", LeaseDue = lease, Parked = parked };

    [Fact]
    public async Task AnOwnersLeaseThatEndedInTheGapIsRenewed()
    {
        var states = new[]
        {
            Item(1, Since.AddMinutes(5)),                 // ended in the gap, owned: renewed
            Item(2, Since.AddMinutes(-1)),                // ended before the gap: a gateway was up, the saga's own timer decides
            Item(3, Until.AddMinutes(10)),                // still running
            Item(4, Since.AddMinutes(5), parked: true),   // parked: no lease to renew
            Item(5, Since.AddMinutes(5)),                 // no owner: a step that waits for a session
            Item(6, null),                                // no lease
            Item(7, Since.AddMinutes(5), status: WorkItemStatus.Completed),
        };
        var sent = new List<LeaseRenewed>();
        var owners = new Dictionary<int, string> { [1] = "alice", [2] = "bob", [3] = "carol", [4] = "dave", [6] = "erin", [7] = "frank" };
        var renewed = await SagaGapRenew.Run(states, Since, Until, n => owners.GetValueOrDefault(n), m => { sent.Add(m); return Task.CompletedTask; });
        Assert.Equal([1], renewed);
        var one = Assert.Single(sent);
        Assert.Equal(ItemId.Parse("acme/work#1"), one.Item);
        Assert.Equal(2, one.Generation);
        Assert.Equal("2.1", one.ArmId);
        Assert.Equal($"gap|{Since.AddMinutes(5).UtcTicks}", one.Key);   // (the lapsed lease names the key: a repeated pass renews once)
    }

    [Fact]
    public void TheEdgesOfTheGapCount()
    {
        var states = new[] { Item(1, Since), Item(2, Until) };
        var due = SagaGapRenew.Due(states, Since, Until, _ => "alice");
        Assert.Equal([1, 2], due.Select(d => d.Item));
        Assert.Equal("alice", due[0].Holder);
        Assert.Equal(Since, due[0].Lapsed);
    }

    // ---- the Release Queue card without a merge record --------------------------------------------------------------------------------------

    static KeeperWorld Queue() { var w = new KeeperWorld(); w.Add(9, "Moved by hand", "x", "Release Queue"); w.Add(10, "Merged", "x", "Release Queue"); return w; }

    [Fact]
    public async Task ACardWithNoMergeRecordIsJournaledOnceAndReported()
    {
        var w = Queue();
        w.State(10)["merge"] = new JsonObject { ["sha"] = "abc123" };
        var step = new ReleaseQueueFlags(w.Cx);
        var lacking = await step.Flag([(9, "Release Queue"), (10, "Release Queue")]);
        Assert.Equal([9], lacking);
        var line = Assert.Single(w.Journal, j => j.Contains("|flagged|"));
        Assert.StartsWith("9|flagged|rq-no-merge|", line);
        Assert.Contains("SAVE state 9", w.Writes);
        Assert.DoesNotContain(w.Writes, x => x == "SAVE state 10");
    }

    [Fact]
    public async Task ABusyItemIsLeftToALaterPass()
    {
        var w = Queue();
        w.BusyItems.Add(9);
        var lacking = await new ReleaseQueueFlags(w.Cx).Flag([(9, "Release Queue")]);
        Assert.Empty(lacking);
        Assert.DoesNotContain(w.Journal, j => j.Contains("|flagged|"));
        w.BusyItems.Clear();
        Assert.Equal([9], await new ReleaseQueueFlags(w.Cx).Flag([(9, "Release Queue")]));
    }
}
