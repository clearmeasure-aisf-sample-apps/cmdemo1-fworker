using System.Text.Json.Nodes;
using Fworker.Workflows.Keeper;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W9.6: manager-action on v2 against plugin/scripts/lib/Verbs.Manager.ps1: one action per call, the refusals of each (exit and code), the state it changes, the journal line and the fields of the answer. The pipeline's part (mode,
/// hourly limit, name actions) is the gateway's and is not here.
/// </summary>
public sealed class ManagerCommandTests
{
    static KeeperWorld World(params string[] labels)
    {
        var w = new KeeperWorld();
        w.Add(5, "An item", "Body", "Technical Design", labels);
        return w;
    }

    static Dictionary<string, object?> Act(string action, object? extra = null, string policy = "park-resolved")
    {
        var a = new Dictionary<string, object?> { ["item"] = 5, ["action"] = action, ["policy"] = policy, ["reason"] = "the owner reworded criterion 2", ["by"] = "supervisor" };
        if (extra != null) foreach (var p in extra.GetType().GetProperties()) a[p.Name] = p.GetValue(extra);
        return a;
    }

    static void Park(KeeperWorld w, string code = "worker-failure") =>
        w.State(5)["park"] = new JsonObject { ["reason"] = code, ["at"] = "2026-10-29T11:00:00.000Z", ["until"] = "2026-10-29T15:00:00.000Z" };

    // ---- the checks every call makes ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryCallNeedsAKnownActionAndPolicy_AndACleanReason()
    {
        var w = World();
        await w.Refused("manager-action", Act("merge"), 2, "usage");
        await w.Refused("manager-action", Act("unpark", null, "no-such-policy"), 2, "unknown-policy");
        await w.Refused("manager-action", Act("unpark", new { reason = new string('r', 301) }), 2, "usage");
        await w.Refused("manager-action", new { item = 5, policy = "park-resolved" }, 2, "usage");
        await w.Refused("manager-action", new { action = "unpark", policy = "park-resolved" }, 2, "usage");
        Assert.Empty(w.Writes);
    }

    [Fact]
    public async Task TheOwnersHoldWins_OnEveryAction()
    {
        var w = World("hold:owner");
        Park(w);
        foreach (var action in ManagerCommands.ItemActions) await w.Refused("manager-action", Act(action), 10, "owner-hold");
        Assert.Empty(w.Writes);
    }

    [Fact]
    public async Task TheReasonIsCleanedForTheJournal_NoLinksMarkersOrMentions()
    {
        var w = World(); Park(w);
        await w.Run("manager-action", Act("unpark", new { reason = "see https://x.example/a <!-- b --> @bob [link](u)!  now", session = "https://github.com/acme/work/tasks/abc" }));
        var line = w.Journal.Single();
        Assert.EndsWith("reason=see https x.example/a -- b -- bob link(u) now", line);
        Assert.DoesNotContain("<", line); Assert.DoesNotContain("@", line); Assert.DoesNotContain("://", line);
    }

    // ---- unpark ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Unpark_ReleasesTheParkAndOffersTheItemAgain_AndTellsTheSaga()
    {
        var w = World(); Park(w); w.SagaOwned.Add(5);
        var o = await w.Run("manager-action", Act("unpark"));
        Assert.True(o["ok"]!.GetValue<bool>());
        Assert.Equal("unpark", o["action"]!.ToString()); Assert.Equal("park-resolved", o["policy"]!.ToString()); Assert.Equal("Technical Design", o["column"]!.ToString());
        Assert.Equal("worker-failure", o["park"]!["reason"]!.ToString());
        Assert.True(o["reoffer"]!.GetValue<bool>()); Assert.False(o["closed"]!.GetValue<bool>());
        Assert.True(o["journaled"]!.GetValue<bool>());
        Assert.Equal(1, o["actions_today"]!.GetValue<int>()); Assert.Equal(3, o["limit"]!.GetValue<int>());
        var s = w.States[5];
        Assert.Null(s["park"]);
        Assert.Equal("Technical Design", s["handoff"]!["column"]!.ToString());
        var done = Assert.Single(s["manager_actions"]!.AsArray());
        Assert.Equal("unpark", done!["action"]!.ToString()); Assert.Equal("supervisor", done["by"]!.ToString());
        Assert.Equal("5|manager-action|unpark|released the park `worker-failure`; the next sweep offers it again in **Technical Design**|by=supervisor|policy=park-resolved|reason=the owner reworded criterion 2", w.Journal.Single());
        Assert.Equal(["resumed 5"], w.SagaCalls);
    }

    [Fact]
    public async Task Unpark_OfAClosedItemOrOneWithALiveClaimOffersNothing()
    {
        var closed = World(); Park(closed); closed.Issues[5].Open = false;
        var c = await closed.Run("manager-action", Act("unpark"));
        Assert.False(c["reoffer"]!.GetValue<bool>()); Assert.True(c["closed"]!.GetValue<bool>());
        Assert.Null(closed.States[5]["handoff"]);
        var owned = World(); Park(owned); owned.State(5)["claim"] = new JsonObject { ["holder"] = "alice", ["owner"] = true, ["lease_until"] = "2026-10-29T13:00:00.000Z" };
        Assert.False((await owned.Run("manager-action", Act("unpark")))["reoffer"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Unpark_RefusesWhatOnlyAPersonOrTheGatewayEnds()
    {
        var w = World();
        await w.Refused("manager-action", Act("unpark"), 10, "not-parked");
        Park(w, "development:awaiting-checks"); await w.Refused("manager-action", Act("unpark"), 10, "gateway-wait");
        Park(w, "inspector-escalated"); await w.Refused("manager-action", Act("unpark"), 10, "needs-approval");
        Park(w); w.State(5)["verdict"] = new JsonObject { ["passed"] = true }; await w.Refused("manager-action", Act("unpark"), 10, "needs-merge");
        Assert.Empty(w.SagaCalls);
        Assert.NotNull(w.States[5]["park"]);
    }

    // ---- release-owner, requeue -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ReleaseOwner_EndsTheLeaseNowAndMarksItTakenBack()
    {
        var w = World();
        await w.Refused("manager-action", Act("release-owner", null, "stalled-owner"), 10, "no-owner");
        w.State(5)["claim"] = new JsonObject { ["holder"] = "alice", ["owner"] = true, ["lease_until"] = "2026-10-29T13:00:00.000Z" };
        var o = await w.Run("manager-action", Act("release-owner", null, "stalled-owner"));
        Assert.Equal("alice", o["holder"]!.ToString());
        var claim = w.States[5]["claim"]!;
        Assert.Equal("2026-10-29T11:59:59.000Z", claim["lease_until"]!.ToString());
        Assert.Equal("released-by-plant-manager", claim["taken_back"]!.ToString());
        Assert.Contains("ended the lease of **alice**; the item is offered again in resume mode", w.Journal.Single());
        // a lease that has run out has no owner to release
        w.State(5)["claim"]!["lease_until"] = "2026-10-29T11:00:00.000Z";
        await w.Refused("manager-action", Act("release-owner", null, "stalled-owner"), 10, "no-owner");
    }

    // W9.10k: an item the saga owns hears of the manager's release of its owner and of its requeue, as it hears of the unpark (the saga is what offers the item, so it must know)
    [Fact]
    public async Task ReleaseOwnerAndRequeue_TellTheSagaOfAnItemItOwns_AndNoOneElse()
    {
        var released = World(); released.SagaOwned.Add(5);
        released.State(5)["claim"] = new JsonObject { ["holder"] = "alice", ["owner"] = true, ["lease_until"] = "2026-10-29T13:00:00.000Z" };
        await released.Run("manager-action", Act("release-owner", null, "stalled-owner"));
        Assert.Equal(["owner-released 5"], released.SagaCalls);

        var requeued = World(); requeued.SagaOwned.Add(5);
        await requeued.Run("manager-action", Act("requeue", null, "stuck-offer"));
        Assert.Equal(["requeued 5"], requeued.SagaCalls);

        // an item without a saga: v1's, nothing is said; a refused call says nothing either
        var v1 = World(); v1.State(5)["claim"] = new JsonObject { ["holder"] = "alice", ["owner"] = true, ["lease_until"] = "2026-10-29T13:00:00.000Z" };
        await v1.Run("manager-action", Act("release-owner", null, "stalled-owner"));
        var v1Requeue = World(); await v1Requeue.Run("manager-action", Act("requeue", null, "stuck-offer"));
        var refused = World(); refused.SagaOwned.Add(5);
        await refused.Refused("manager-action", Act("release-owner", null, "stalled-owner"), 10, "no-owner");
        Assert.Empty(v1.SagaCalls); Assert.Empty(v1Requeue.SagaCalls); Assert.Empty(refused.SagaCalls);
    }

    [Fact]
    public async Task Requeue_OffersTheItemAtOnce_AndRefusesWhatIsNotToBeOffered()
    {
        var w = World();
        var o = await w.Run("manager-action", Act("requeue", null, "stuck-offer"));
        Assert.True(o["reoffer"]!.GetValue<bool>());
        Assert.Equal("Technical Design", w.States[5]["handoff"]!["column"]!.ToString());

        var notAi = World(); notAi.Issues[5].Column = "Todo";
        await notAi.Refused("manager-action", Act("requeue", null, "stuck-offer"), 10, "not-ai-column");
        var owned = World(); owned.State(5)["claim"] = new JsonObject { ["holder"] = "alice", ["lease_until"] = "2026-10-29T13:00:00.000Z" };
        await owned.Refused("manager-action", Act("requeue", null, "stuck-offer"), 10, "owned");
        var parked = World(); Park(parked);
        await parked.Refused("manager-action", Act("requeue", null, "stuck-offer"), 10, "parked");
        var expired = World(); expired.State(5)["park"] = new JsonObject { ["reason"] = "x", ["until"] = "2026-10-29T11:00:00.000Z" };
        await expired.Run("manager-action", Act("requeue", null, "stuck-offer"));   // a park that has run out is no park
        var verdict = World(); verdict.State(5)["verdict"] = new JsonObject { ["passed"] = true, ["processed"] = false };
        await verdict.Refused("manager-action", Act("requeue", null, "stuck-offer"), 10, "gateway-merge");
        var merged = World(); merged.State(5)["merge"] = new JsonObject { ["awaiting"] = true };
        await merged.Refused("manager-action", Act("requeue", null, "stuck-offer"), 10, "settling");
        var closed = World(); closed.Issues[5].Open = false;
        await closed.Refused("manager-action", Act("requeue", null, "stuck-offer"), 11, "item-closed");
    }

    // ---- pin, unpin, prioritize -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Pin_MovesThePinToTheWorker_AndRefusesAReservedNameAndTheSame()
    {
        var w = World("worker:ghost");
        var o = await w.Run("manager-action", Act("pin", new { worker = "carol" }, "pinned-worker-absent"));
        Assert.Equal("carol", o["worker"]!.ToString()); Assert.Equal(["ghost"], o["was"]!.AsArray().Select(x => x!.ToString()));
        Assert.Equal(["factory", "worker:carol"], w.Issues[5].Labels);
        Assert.Contains("DELETE issues/5/labels/worker:ghost", w.Writes);
        Assert.Contains("pinned it to **carol** (was ghost)", w.Journal.Single());
        await w.Refused("manager-action", Act("pin", new { worker = "carol" }, "pinned-worker-absent"), 10, "already-pinned");
        foreach (var bad in new[] { "operator", "supervisor", "plant-manager", "has space", "" }) await w.Refused("manager-action", Act("pin", new { worker = bad }, "pinned-worker-absent"), 2, "usage");
        await w.Refused("manager-action", Act("pin", null, "pinned-worker-absent"), 2, "usage");
    }

    [Fact]
    public async Task Pin_OfAnUnpinnedItem_AddsTheLabelAndHasNoWas()
    {
        var w = World();
        var o = await w.Run("manager-action", Act("pin", new { worker = "carol" }, "load-balance"));
        Assert.Empty(o["was"]!.AsArray());
        Assert.Equal(["factory", "worker:carol"], w.Issues[5].Labels);
        Assert.Equal("5|manager-action|pin|pinned it to **carol**|by=supervisor|policy=load-balance|reason=the owner reworded criterion 2", w.Journal.Single());
    }

    [Fact]
    public async Task Unpin_RemovesEveryPin_AndRefusesAnItemThatHasNone()
    {
        var w = World("worker:a", "worker:b");
        var o = await w.Run("manager-action", Act("unpin", null, "pinned-worker-absent"));
        Assert.Equal(["a", "b"], o["was"]!.AsArray().Select(x => x!.ToString()));
        Assert.Equal(["factory"], w.Issues[5].Labels);
        await w.Refused("manager-action", Act("unpin", null, "pinned-worker-absent"), 10, "not-pinned");
    }

    [Fact]
    public async Task Prioritize_DefaultsToTopAndRefusesTheLevelThatIsSet()
    {
        var w = World("priority:high");
        var o = await w.Run("manager-action", Act("prioritize", null, "backlog-order"));
        Assert.Equal("top", o["level"]!.ToString()); Assert.Equal("high", o["was"]!.ToString());
        Assert.Equal(["factory", "priority:top"], w.Issues[5].Labels);
        Assert.Contains("set its priority to **top** (was high)", w.Journal.Single());
        await w.Refused("manager-action", Act("prioritize", null, "backlog-order"), 10, "already");
        await w.Refused("manager-action", Act("prioritize", new { level = "urgent" }, "backlog-order"), 2, "usage");
        await World().Refused("manager-action", Act("prioritize", new { level = "normal" }, "backlog-order"), 10, "already");
    }

    // ---- rerun-failed ---------------------------------------------------------------------------------------------------------------------

    const string Head = "0123456789abcdef0123456789abcdef01234567";

    static KeeperWorld WithPr()
    {
        var w = World();
        w.Pulls.Add(KeeperWorld.Pull(40, "factory-worker/5-work", Head));
        return w;
    }

    [Fact]
    public async Task RerunFailed_ReRunsTheFailedRunsOfTheHeadOnce()
    {
        var w = WithPr();
        w.Runs.Add(KeeperWorld.Run(11, Head, "completed", "failure")); w.Runs.Add(KeeperWorld.Run(12, Head, "completed", "timed_out")); w.Runs.Add(KeeperWorld.Run(13, Head, "completed", "success"));
        w.Runs.Add(KeeperWorld.Run(14, Head, "completed", "failure", "push")); w.Runs.Add(KeeperWorld.Run(15, "other", "completed", "failure"));
        var o = await w.Run("manager-action", Act("rerun-failed", null, "flaky-check"));
        Assert.Equal(40, o["pr"]!.GetValue<int>()); Assert.Equal(Head, o["sha"]!.ToString());
        Assert.Equal([11L, 12L], o["runs"]!.AsArray().Select(x => x!.GetValue<long>()));
        Assert.Empty(o["refused"]!.AsArray());
        Assert.Equal(["POST actions/runs/11/rerun-failed-jobs", "POST actions/runs/12/rerun-failed-jobs"], w.Writes.Where(x => x.StartsWith("POST actions")));
        Assert.Contains("re-ran the failed jobs of PR #40 at `0123456` (workflow run 11, 12), once for this head", w.Journal.Single());
        Assert.Equal([11L, 12L], w.States[5]["manager_reruns"]![Head]!["runs"]!.AsArray().Select(x => x!.GetValue<long>()));
        // the second time for the same head is not a flake
        await w.Refused("manager-action", Act("rerun-failed", null, "flaky-check"), 10, "already-rerun");
    }

    [Fact]
    public async Task RerunFailed_RefusesWhatCannotBeReRun_AndAHeadReRunOnceBefore()
    {
        var none = World(); await none.Refused("manager-action", Act("rerun-failed", null, "flaky-check"), 10, "no-pr");
        var green = WithPr(); green.Runs.Add(KeeperWorld.Run(11, Head, "completed", "success"));
        await green.Refused("manager-action", Act("rerun-failed", null, "flaky-check"), 10, "nothing-failed");
        var running = WithPr(); running.Runs.Add(KeeperWorld.Run(11, Head, "in_progress", null));
        var r = await running.Refused("manager-action", Act("rerun-failed", null, "flaky-check"), 10, "run-in-progress");
        Assert.Equal(120, r["retryAfterSeconds"]!.GetValue<int>());
        var refused = WithPr(); refused.Runs.Add(KeeperWorld.Run(11, Head, "completed", "failure")); refused.RerunStatus[11] = 403;
        var x = await refused.Refused("manager-action", Act("rerun-failed", null, "flaky-check"), 20, "rerun-refused");
        Assert.Equal(60, x["retryAfterSeconds"]!.GetValue<int>());
        Assert.Contains("11: 403", x["reason"]!.ToString());
        Assert.Null(refused.States.GetValueOrDefault(5)?["manager_reruns"]);   // nothing is recorded for a re-run GitHub refused
    }

    [Fact]
    public async Task RerunFailed_TakesThePullRequestOfTheStateWhenItIsOpen_AndTheLastTenHeadsAreKept()
    {
        var w = World();
        w.Pulls.Add(KeeperWorld.Pull(77, "somebody/else", Head));
        w.State(5)["pr"] = new JsonObject { ["number"] = 77 };
        w.Runs.Add(KeeperWorld.Run(11, Head, "completed", "failure"));
        var o = await w.Run("manager-action", Act("rerun-failed", null, "flaky-check"));
        Assert.Equal(77, o["pr"]!.GetValue<int>());
        // a state with eleven heads already: the oldest goes
        var heads = new JsonObject();
        for (var k = 0; k < 10; k++) heads[$"{k:D40}"] = new JsonObject { ["at"] = $"2026-10-2{k % 9}T10:00:00.000Z", ["runs"] = new JsonArray() };
        var w2 = WithPr(); w2.State(5)["manager_reruns"] = heads; w2.Runs.Add(KeeperWorld.Run(11, Head, "completed", "failure"));
        await w2.Run("manager-action", Act("rerun-failed", null, "flaky-check"));
        var kept = w2.States[5]["manager_reruns"]!.AsObject();
        Assert.Equal(10, kept.Count); Assert.True(kept.ContainsKey(Head));
        Assert.False(kept.ContainsKey($"{0:D40}"));
    }

    // ---- the limit per item per day --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheFourthActionOfADayOnOneItemIsRefused_WithWhenToAsk()
    {
        var w = World();
        await w.Run("manager-action", Act("prioritize", new { level = "top" }, "backlog-order"));
        w.Clock = w.Clock.AddHours(1);
        await w.Run("manager-action", Act("prioritize", new { level = "high" }, "backlog-order"));
        w.Clock = w.Clock.AddHours(1);
        var third = await w.Run("manager-action", Act("prioritize", new { level = "top" }, "backlog-order"));
        Assert.Equal(3, third["actions_today"]!.GetValue<int>());
        w.Clock = w.Clock.AddHours(1);
        var o = await w.Refused("manager-action", Act("prioritize", new { level = "high" }, "backlog-order"), 10, "item-action-limit");
        Assert.Equal(3, o["actions_today"]!.GetValue<int>()); Assert.Equal(3, o["limit"]!.GetValue<int>());
        // the first action was 3 hours ago: 21 hours to wait
        Assert.Equal(21 * 3600, o["retryAfterSeconds"]!.GetValue<int>());
        Assert.Contains("acted on item #5 3 times in 24 hours (limit 3)", o["reason"]!.ToString());
        // a day after the first, it may act again
        w.Clock = w.Clock.AddHours(21);
        await w.Run("manager-action", Act("prioritize", new { level = "high" }, "backlog-order"));
    }

    [Fact]
    public async Task ActionsOfAnotherItemAreNotCounted_AndTheListKeepsTheLastTen()
    {
        var w = World();
        var many = new JsonArray();
        for (var k = 0; k < 12; k++) many.Add(new JsonObject { ["at"] = "2026-10-20T10:00:00.000Z", ["action"] = "x", ["policy"] = "y", ["by"] = "z" });   // older than a day: none counts
        w.State(5)["manager_actions"] = many;
        var o = await w.Run("manager-action", Act("prioritize", null, "backlog-order"));
        Assert.Equal(1, o["actions_today"]!.GetValue<int>());
        Assert.Single(w.States[5]["manager_actions"]!.AsArray());
    }
}
