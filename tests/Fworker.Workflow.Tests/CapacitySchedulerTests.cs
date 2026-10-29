using System.Text.Json;
using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Messaging;
using Fworker.Domain;
using Fworker.Workflows.Capacity;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W7.1 on an in-memory ledger: the ranking of waiting requests, the admission chain with D1's rules (runtime off, toolchain, capacity, day budget, generated last), the ledger (a slot is held until the session's end,
/// a start refused, or its time limit), the day roll, the sharing of a runtime's limits with the plant, the waiting code published once per change, and the idempotence the at-least-once delivery needs. The
/// file-overlap rule is in CapacityOverlapTests and the hosts in CapacityHostTests.
/// </summary>
public class CapacitySchedulerTests
{
    // ---- ranking ----------------------------------------------------------------------------------------------------------------------------

    /// <summary>Frees the one slot of cursor-cloud over and over and returns the items in the order they were granted it.</summary>
    static async Task<List<int>> DrainOrder(CapacityFixture fx, int holder)
    {
        var order = new List<int>();
        while (fx.State.Queue.Count > 0)
        {
            var r = await fx.End(holder);
            holder = r.Messages.OfType<SlotGranted>().Single().Item.Number;
            order.Add(holder);
        }
        return order;
    }

    [Fact]
    public async Task TheFreedSlot_GoesToTheItemsInRankOrder_PinnedResumePriorityThenTheOlderOne()
    {
        var fx = new CapacityFixture();
        await fx.Request(99);   // takes the only slot of cursor-cloud
        await fx.Request(1);
        await fx.Request(2, priority: "high");
        await fx.Request(3, priority: "top");
        await fx.Request(4, pinned: true);
        await fx.Request(5, resume: true);
        Assert.Equal([99], fx.Holders);
        Assert.Equal([4, 5, 3, 2, 1], await DrainOrder(fx, 99));
    }

    [Fact]
    public async Task OfEqualRank_TheDeeperColumnGoesFirst_ThenTheOlderRequest_ThenTheItem()
    {
        var fx = new CapacityFixture();
        await fx.Request(99);
        await fx.Request(6, depth: 1, requested: CapacityFixture.T0.AddMinutes(-30));
        await fx.Request(7, depth: 3, requested: CapacityFixture.T0.AddMinutes(-1));
        await fx.Request(8, depth: 1, requested: CapacityFixture.T0.AddMinutes(-60));
        await fx.Request(12, depth: 1, requested: CapacityFixture.T0.AddMinutes(-10));
        await fx.Request(11, depth: 1, requested: CapacityFixture.T0.AddMinutes(-10));
        Assert.Equal([7, 8, 6, 11, 12], await DrainOrder(fx, 99));
    }

    [Fact]
    public async Task PastIntakeWorkGoesBeforeNewWork_AsInV1sRanking()
    {
        var fx = new CapacityFixture();
        await fx.Request(99);
        await fx.Request(1, inIntake: true, requested: CapacityFixture.T0.AddMinutes(-90));
        await fx.Request(2, requested: CapacityFixture.T0);
        Assert.Equal([2, 1], await DrainOrder(fx, 99));
    }

    [Fact]
    public async Task ThePriorityOfTheLabel_BeatsTheAgeOfTheRequest_AndAResumeBeatsAPriority()
    {
        var fx = new CapacityFixture();
        await fx.Request(99);
        await fx.Request(1, requested: CapacityFixture.T0.AddHours(-5));
        await fx.Request(2, priority: "high", requested: CapacityFixture.T0);
        await fx.Request(3, resume: true, requested: CapacityFixture.T0);
        await fx.Request(4, priority: "top", requested: CapacityFixture.T0);
        Assert.Equal([3, 4, 2, 1], await DrainOrder(fx, 99));
    }

    [Fact]
    public async Task ARequestAgain_RefreshesTheRankFacts_ButKeepsTheAgeOfTheFirstRequest()
    {
        var fx = new CapacityFixture();
        await fx.Request(99);
        await fx.Request(1, requested: CapacityFixture.T0);
        await fx.Request(2, requested: CapacityFixture.T0.AddMinutes(1));
        fx.Now = fx.Now.AddMinutes(5);
        await fx.Request(2, priority: "top", requested: fx.Now);   // a label was added; the wait still began at the first request
        await fx.Request(1, requested: fx.Now);                    // asking again does not send #1 to the back
        Assert.Equal(CapacityFixture.T0, fx.State.Queue[CapacityFixture.I(1).ToString()].RequestedAt);
        Assert.Equal("top", fx.State.Queue[CapacityFixture.I(2).ToString()].Rank.Priority);
        Assert.Equal([2, 1], await DrainOrder(fx, 99));
    }

    [Fact]
    public async Task GeneratedItems_WaitWhileARealItemWaitsForASlot_AndTakeAFreeOneAfterIt()
    {
        var fx = new CapacityFixture();
        await fx.Report("h1");
        await fx.Request(99);   // cursor-cloud is full
        await fx.Request(1);    // a real item waits for cursor-cloud
        var r = await fx.Request(2, "octopus", generated: true);   // octopus is free, but a real item still waits for a slot
        Assert.Equal("generated-last", fx.Waiting(2));
        Assert.Contains(r.Messages, m => m is Held { Code: "generated-last" });
        // the real item gets the slot first; in the same pass nobody real is left waiting, so the generated item goes on octopus
        var freed = await fx.End(99);
        Assert.Equal([1, 2], freed.Messages.OfType<SlotGranted>().Select(g => g.Item.Number));
        Assert.Equal("octopus", freed.Messages.OfType<SlotGranted>().Last().Runtime);
    }

    [Fact]
    public async Task ARealItemThatWaitsForAReasonOtherThanASlot_DoesNotHoldBackTheGeneratedOnes()
    {
        var fx = new CapacityFixture();
        await fx.Request(1, "no-such-runtime");   // runtime-off: a free slot is of no use to it
        var r = await fx.Request(2, generated: true);
        Assert.Equal("runtime-off", fx.Waiting(1));
        Assert.Equal([2], r.Messages.OfType<SlotGranted>().Select(g => g.Item.Number));
    }

    // ---- the admission chain ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARuntimeThatIsNotConfigured_IsRuntimeOff()
    {
        var fx = new CapacityFixture();
        var r = await fx.Request(1, "cae-job");
        var held = Assert.Single(r.Messages.OfType<Held>());
        Assert.Equal(("runtime-off", "", "runtime-off"), (held.Code, held.Detail, held.Waiting));
        Assert.Empty(fx.State.Slots);
    }

    [Fact]
    public async Task ARuntimeAtItsLimit_IsAtCapacity_AndTheNextFreedSlotIsTheirs()
    {
        var fx = new CapacityFixture();
        await fx.Request(1);
        var r = await fx.Request(2);
        Assert.Equal("runtime-at-capacity", Assert.Single(r.Messages.OfType<Held>()).Waiting);
        Assert.Equal([1], fx.Holders);
        var freed = await fx.End(1);
        Assert.Equal(2, Assert.Single(freed.Messages.OfType<SlotGranted>()).Item.Number);
        Assert.Equal([2], fx.Holders);
    }

    [Fact]
    public async Task TheDayBudget_Counts_TheGrantsOfTheDay_AndStartsAgainAtMidnightUtc()
    {
        var fx = new CapacityFixture(c => c["api"]!["cursor"]!["maxExecutionsPerDay"] = 2);
        await fx.Request(1); await fx.End(1);
        await fx.Request(2); await fx.End(2);
        var r = await fx.Request(3);
        Assert.Equal("runtime-budget", Assert.Single(r.Messages.OfType<Held>()).Waiting);
        Assert.Equal(2, fx.State.ExecutionsToday["cursor-cloud"]);

        fx.Now = CapacityFixture.T0.AddDays(1);   // 2026-10-29 10:00 UTC
        var next = await fx.Wake();
        Assert.Equal(3, Assert.Single(next.Messages.OfType<SlotGranted>()).Item.Number);
        Assert.Equal(("2026-10-29", 1), (fx.State.Day, fx.State.ExecutionsToday["cursor-cloud"]));
    }

    [Fact]
    public async Task WhatThePlantRunsOutsideTheLedger_IsCountedAgainstTheSameLimits()
    {
        var fx = new CapacityFixture();
        fx.Outside["cursor-cloud"] = new RuntimeOutside(1, 0);   // v1's manager runs one execution of cursor-cloud
        var r = await fx.Request(1);
        Assert.Equal("runtime-at-capacity", Assert.Single(r.Messages.OfType<Held>()).Waiting);

        fx.Outside["cursor-cloud"] = new RuntimeOutside(0, 100);   // it has started the whole day's budget
        var b = await fx.Request(1);
        Assert.Equal("runtime-budget", Assert.Single(b.Messages.OfType<Held>()).Waiting);

        fx.Outside["cursor-cloud"] = new RuntimeOutside(0, 99);    // one execution of the day is left
        var ok = await fx.Request(1);
        Assert.Single(ok.Messages.OfType<SlotGranted>());
        var second = await fx.Request(2);
        Assert.Equal("runtime-at-capacity", Assert.Single(second.Messages.OfType<Held>()).Waiting);
    }

    [Fact]
    public async Task AReasonTheManagerGives_ThatIsNoShortageOfSlots_IsTheWaitingCode_AndDoesNotHoldBackTheGeneratedItems()
    {
        var fx = new CapacityFixture();
        fx.Outside["cursor-cloud"] = new RuntimeOutside(0, 0, "runtime-relisting");   // the plant's manager has not counted the day's executions yet
        var r = await fx.Request(1);
        Assert.Equal("runtime-relisting", Assert.Single(r.Messages.OfType<Held>()).Waiting);
        Assert.Empty(fx.State.Slots);
        var generated = await fx.Request(2, "copilot-cloud", generated: true);   // (a free slot of another runtime is of use to it: the reason is no lack of slots)
        Assert.Single(generated.Messages.OfType<SlotGranted>());
        fx.Outside["cursor-cloud"] = new RuntimeOutside(0, 0);
        fx.Now = fx.Now.AddMinutes(10);
        var woke = await fx.Wake();
        Assert.Equal(1, Assert.Single(woke.Messages.OfType<SlotGranted>()).Item.Number);
    }

    [Fact]
    public async Task TheToolchainsOfARuntime_AreCheckedAgainstWhatTheRepositoryNeeds_AndNoneConfiguredMeansNoCheck()
    {
        var fx = new CapacityFixture(c => c["scheduler"]!["toolchains"] = new JsonObject { ["copilot-cloud"] = new JsonArray("dotnet") });
        var r = await fx.Request(1, "copilot-cloud", toolchains: ["dotnet", "Node", "python"]);
        var held = Assert.Single(r.Messages.OfType<Held>());
        Assert.Equal(("toolchain", "node,python"), (held.Code, held.Detail));
        var ok = await fx.Request(2, "copilot-cloud", toolchains: ["DotNet"]);
        Assert.Single(ok.Messages.OfType<SlotGranted>());
        // cursor-cloud has no toolchain list in the configuration: it offers what the repository needs
        var any = await fx.Request(3, toolchains: ["anything"]);
        Assert.Single(any.Messages.OfType<SlotGranted>());
    }

    // ---- the ledger ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AGrant_NamesTheSlotItsRuntimeAndItsTimeLimit_AndCountsTheDaysExecution()
    {
        var fx = new CapacityFixture();
        var r = await fx.Request(1, phase: "develop");
        var g = Assert.Single(r.Messages.OfType<SlotGranted>());
        Assert.Equal(("acme/orders#1", "s1", "cursor-cloud", null, "guaranteed", "develop"), (g.Item.ToString(), g.SlotId, g.Runtime, g.Host, g.Kind, g.Phase));
        Assert.Equal(CapacityFixture.T0.AddMinutes(600), g.ExpiresAt);
        Assert.Equal(1, fx.State.ExecutionsToday["cursor-cloud"]);
        Assert.Empty(fx.State.Queue);
    }

    [Fact]
    public async Task AnItemThatHoldsASlot_AsksAgain_AndIsAnsweredWithTheSameSlot_NothingChanges()
    {
        var fx = new CapacityFixture();
        await fx.Request(1);
        var revision = fx.Store.RevisionOf(CapacityKeys.Factory);
        var again = await fx.Request(1);
        Assert.Equal(CapacityOutcome.Refused, again.Outcome);
        Assert.Equal("s1", Assert.Single(again.Messages.OfType<SlotGranted>()).SlotId);
        Assert.Equal(revision, fx.Store.RevisionOf(CapacityKeys.Factory));
        Assert.Equal(1, fx.State.ExecutionsToday["cursor-cloud"]);
    }

    [Fact]
    public async Task AReopenedItem_AsksForItsSlotAgain_TheSlotOfTheEarlierInstanceIsNoneOfTheNewOnes()
    {
        var fx = new CapacityFixture();
        await fx.Request(1);   // generation 1 of #1 holds the only slot of cursor-cloud
        var reopened = await fx.Send(new RequestSlot(CapacityFixture.I(1), fx.K(), 2, "cursor-cloud", "design", default, false, false, 1, fx.Now, [], Footprint.Empty));
        var g = Assert.Single(reopened.Messages.OfType<SlotGranted>());
        Assert.Equal((2, "s2"), (g.Generation, g.SlotId));   // a new slot for the new instance; the old one is not echoed to it
        Assert.Equal([1], fx.Holders);
        // the same generation asking again is the same answer as before
        var same = await fx.Send(new RequestSlot(CapacityFixture.I(1), fx.K(), 2, "cursor-cloud", "design", default, false, false, 1, fx.Now, [], Footprint.Empty));
        Assert.Equal(CapacityOutcome.Refused, same.Outcome);
        Assert.Equal("s2", Assert.Single(same.Messages.OfType<SlotGranted>()).SlotId);
    }

    [Fact]
    public async Task TheSessionsEnd_FreesTheSlotAtOnce_AndAnEndOfAnotherSlotOrOfNoSlotChangesNothing()
    {
        var fx = new CapacityFixture();
        await fx.Request(1);
        await fx.Request(2);
        var revision = fx.Store.RevisionOf(CapacityKeys.Factory);

        var stale = await fx.End(1, slot: "s7");
        Assert.Equal(CapacityOutcome.Stale, stale.Outcome);
        var none = await fx.End(5);
        Assert.Equal(CapacityOutcome.NotFound, none.Outcome);
        Assert.Equal(revision, fx.Store.RevisionOf(CapacityKeys.Factory));
        Assert.Equal([1], fx.Holders);

        var ended = await fx.End(1, slot: "s1");
        Assert.Equal(CapacityOutcome.Applied, ended.Outcome);
        Assert.Equal([2], fx.Holders);   // the waiting item has the slot in the same transition
        Assert.Equal(["acme/orders#2"], ended.Messages.OfType<SlotGranted>().Select(g => g.Item.ToString()));
    }

    [Fact]
    public async Task ClearingAnItem_DoesNotFreeItsSlot_OnlyTheEndOfTheSessionDoes()
    {
        var fx = new CapacityFixture();
        await fx.Request(1, phase: "develop", files: ["src/a.cs"]);
        await fx.Request(2);
        await fx.Clear(1);   // merged or parked: it leaves the books, its session may still run
        Assert.Equal([1], fx.Holders);
        Assert.Empty(fx.State.InFlight);
        Assert.Equal("runtime-at-capacity", fx.Waiting(2));
        await fx.End(1);
        Assert.Equal([2], fx.Holders);
    }

    [Fact]
    public async Task ClearingAnItemThatWaits_DropsItsRequest_AndAnUnknownItemFindsNothing()
    {
        var fx = new CapacityFixture();
        await fx.Request(1); await fx.Request(2);
        Assert.Equal(CapacityOutcome.Applied, (await fx.Clear(2)).Outcome);
        Assert.Empty(fx.State.Queue);
        Assert.Equal(CapacityOutcome.NotFound, (await fx.Clear(2)).Outcome);
        await fx.End(1);
        Assert.Empty(fx.State.Slots);   // nobody waits for it
    }

    [Fact]
    public async Task ASlotWhoseEndSignalWasLost_ExpiresAtItsTimeLimit_AndTheItemsSagaIsTold()
    {
        var fx = new CapacityFixture(c => c["scheduler"]!["slotMaxMinutes"] = 60);
        var first = await fx.Request(1);
        Assert.Equal(CapacityFixture.T0.AddMinutes(60), first.WakeAt);   // the timer is asked for with the grant
        var second = await fx.Request(2);
        Assert.Equal(CapacityFixture.T0.AddMinutes(10), second.WakeAt);  // (a request that waits is looked at every ten minutes, too)

        fx.Now = CapacityFixture.T0.AddMinutes(60);
        var r = await fx.Wake(CapacityFixture.T0.AddMinutes(60));
        var expired = Assert.Single(r.Messages.OfType<SlotExpired>());
        Assert.Equal(("acme/orders#1", "s1"), (expired.Item.ToString(), expired.SlotId));
        Assert.Equal([2], fx.Holders);   // the waiting item takes the capacity
        Assert.Equal(2, fx.State.ExecutionsToday["cursor-cloud"]);   // (the first execution stays used)
        Assert.Single(r.Messages.OfType<SlotGranted>());
    }

    [Fact]
    public async Task ASlotThatExpiredWhileAnotherInputWasHandled_IsFreedByThatInput()
    {
        var fx = new CapacityFixture(c => c["scheduler"]!["slotMaxMinutes"] = 60);
        await fx.Request(1);
        fx.Now = CapacityFixture.T0.AddMinutes(61);
        var r = await fx.Request(2);   // the timer has not come yet
        Assert.Single(r.Messages.OfType<SlotExpired>());
        Assert.Equal([2], fx.Holders);
    }

    [Fact]
    public async Task ATimerThatIsNotThePendingOne_OnlyPlansAgain_AndWritesNothingWhenNothingChanged()
    {
        var fx = new CapacityFixture();
        await fx.Request(1);
        var revision = fx.Store.RevisionOf(CapacityKeys.Factory);
        var r = await fx.Wake(CapacityFixture.T0.AddMinutes(3));
        Assert.Equal(CapacityOutcome.Applied, r.Outcome);
        Assert.Empty(r.Messages);
        Assert.Equal(revision, fx.Store.RevisionOf(CapacityKeys.Factory));
    }

    [Fact]
    public async Task TheWaitingCode_IsPublishedWhenItChanges_AndNotRepeated()
    {
        var fx = new CapacityFixture();
        await fx.Request(1);
        await fx.Request(2);
        await fx.Request(2);                       // asks again: still waiting for the same reason
        fx.Now = fx.Now.AddMinutes(10);
        await fx.Wake();                          // a look: nothing changed
        Assert.Equal(["runtime-at-capacity"], fx.HeldMessages.Select(h => h.Waiting));
        fx.Outside["cursor-cloud"] = new RuntimeOutside(0, 100);   // the day budget of the plant is spent: the reason changes, the item still waits
        await fx.End(1);
        Assert.Equal(["runtime-at-capacity", "runtime-budget"], fx.HeldMessages.Select(h => h.Waiting));
    }

    [Fact]
    public async Task AWaitingRequestIsLookedAtAgain_TheTimerIsOnlyAskedForWhenItIsEarlier()
    {
        var fx = new CapacityFixture();
        var a = await fx.Request(1);
        var b = await fx.Request(2);
        Assert.Equal(2, fx.Wakes.Count);                        // the slot's limit, then the safety look of the waiting request
        Assert.True(b.WakeAt < a.WakeAt);
        var c = await fx.Request(3);
        Assert.Null(c.WakeAt);                                  // the pending look is earlier or the same
        fx.Now = fx.Now.AddMinutes(10);
        var w = await fx.Wake(b.WakeAt);                        // the look is made; the next one is asked for
        Assert.Equal(CapacityFixture.T0.AddMinutes(20), w.WakeAt);
    }

    // ---- idempotence, redelivery, concurrency, faults ------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARedeliveredInput_PublishesWhatItPublishedBefore_AndChangesNothing()
    {
        var fx = new CapacityFixture();
        var input = new RequestSlot(CapacityFixture.I(1), "key-1", 1, "cursor-cloud", "design", default, false, false, 1, CapacityFixture.T0, [], Footprint.Empty);
        var first = await fx.Send(input);
        var revision = fx.Store.RevisionOf(CapacityKeys.Factory);
        var again = await fx.Send(input);   // the handler died between the save and the publish; Wolverine runs the message again
        Assert.Equal(revision, fx.Store.RevisionOf(CapacityKeys.Factory));
        Assert.Equal(first.Messages.OfType<SlotGranted>().Single(), again.Messages.OfType<SlotGranted>().Single());
        Assert.Equal(first.WakeAt, again.WakeAt);   // and the timer it asked for
        Assert.Equal(1, fx.State.ExecutionsToday["cursor-cloud"]);
    }

    [Fact]
    public async Task AKeyThatWasAppliedBeforeAnotherInput_IsADuplicate_AndPublishesNothing()
    {
        var fx = new CapacityFixture();
        var input = new RequestSlot(CapacityFixture.I(1), "key-1", 1, "cursor-cloud", "design", default, false, false, 1, CapacityFixture.T0, [], Footprint.Empty);
        await fx.Send(input);
        await fx.End(1);
        var again = await fx.Send(input);
        Assert.Equal(CapacityOutcome.Duplicate, again.Outcome);
        Assert.Empty(again.Messages);
        Assert.Empty(fx.State.Slots);   // it did not take a slot again
    }

    [Fact]
    public async Task TheKeysKept_AreBounded()
    {
        var fx = new CapacityFixture(c => c["api"]!["cursor"]!["maxExecutionsPerDay"] = 100000);
        for (var i = 1; i <= CapacitySettings.DefaultKeepKeys + 20; i++) { await fx.Request(1); await fx.End(1); }
        Assert.Equal(CapacitySettings.DefaultKeepKeys, fx.State.Keys.Count);
    }

    [Fact]
    public async Task AWriterThatLostTheRevision_AppliesItsInputAgainToTheStateThatWon()
    {
        var fx = new CapacityFixture();
        await fx.Request(9);
        var moved = false;
        fx.Store.BeforeSave = key =>
        {
            if (moved) return;
            moved = true;
            // another node's host report lands between this writer's read and its save
            var rec = fx.Store.Load(key, default).Result!;
            rec.State.Hosts["h9"] = new LedgerHost { Name = "h9", Runtime = "octopus", Metrics = CapacityFixture.Roomy, ReportedAt = fx.Now };
            Assert.True(fx.Store.Save(key, rec.State, rec.Revision, default).Result);
        };
        await fx.End(9);
        await fx.Request(1, "octopus");
        Assert.Equal(["h9"], fx.State.Hosts.Keys);   // both writes are in the state
        Assert.Equal("h9", fx.SlotOf(1)!.Host);
    }

    [Fact]
    public async Task AnInputThatKeepsLosingTheRevision_EndsInAConflict_ForWolverineToRetry()
    {
        var fx = new CapacityFixture();
        await fx.Request(1);
        var inside = false;
        fx.Store.BeforeSave = key =>
        {
            if (inside) return;
            inside = true;
            var rec = fx.Store.Load(key, default).Result!;
            Assert.True(fx.Store.Save(key, rec.State, rec.Revision, default).Result);   // another writer is first, every time
            inside = false;
        };
        await Assert.ThrowsAsync<CapacityConflictException>(() => fx.End(1));
    }

    [Fact]
    public async Task ADatabaseThatDoesNotAnswer_IsNoAnswer_NothingIsKept_AndTheNextAttemptWorks()
    {
        FlakyCapacityStore? flaky = null;
        var fx = new CapacityFixture(wrap: inner => flaky = new FlakyCapacityStore(inner));
        await fx.Request(1);

        flaky!.FailLoads = 1;   // the read fails: the request is not turned into "the runtime is full" or any other answer
        await Assert.ThrowsAsync<TimeoutException>(() => fx.Request(2));
        flaky.FailSaves = 1;    // the save fails: it is no revision conflict, and nothing of the attempt is left
        await Assert.ThrowsAsync<TimeoutException>(() => fx.Request(2));
        Assert.DoesNotContain("acme/orders#2", fx.State.Queue.Keys);
        Assert.Equal([1], fx.Holders);

        // the database is back: the same message, run again by Wolverine, is handled as if nothing had happened
        var r = await fx.Request(2, key: "again");
        Assert.Equal("runtime-at-capacity", Assert.Single(r.Messages.OfType<Held>()).Waiting);
        await fx.End(1);
        Assert.Equal([2], fx.Holders);
    }

    [Fact]
    public async Task APlantReadThatFails_IsNoAnswerEither()
    {
        var calls = 0;
        var cfg = (JsonObject)JsonNode.Parse(CapacityFixture.DefaultConfig)!;
        var scheduler = new CapacityScheduler(CapacitySettings.From(cfg, "tdd"), _ => ++calls == 1 ? throw new InvalidOperationException("the manager is starting") : new RuntimeOutside(0, 0));
        var store = new InMemoryCapacityStore();
        var svc = new CapacityService(store, scheduler, () => CapacityFixture.T0);
        var request = new RequestSlot(CapacityFixture.I(1), "k", 1, "cursor-cloud", "design", default, false, false, 1, CapacityFixture.T0, [], Footprint.Empty);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.Handle(request));
        Assert.Null(store.State(CapacityKeys.Factory));
        Assert.Single((await svc.Handle(request)).Messages.OfType<SlotGranted>());
    }

    // ---- the messages -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void EveryMessage_HasAStableIdentity_UniqueAndInTheCapacityFamily()
    {
        var types = typeof(CapacityInput).Assembly.GetTypes().Where(t => t.Namespace == "Fworker.Workflows.Capacity" && !t.IsAbstract && (typeof(CapacityInput).IsAssignableFrom(t) || typeof(IEvent).IsAssignableFrom(t))).ToList();
        Assert.Equal(10, types.Count);   // seven inputs and three events
        var names = types.Select(MessageIdentity.NameOf).ToList();
        Assert.All(names, n => Assert.StartsWith("capacity.", n));
        Assert.Equal(names.Count, names.Distinct().Count());
        // every input is routed to the scheduler's queue
        Assert.Equal(7, CapacityTopology.Inputs.Count());
    }

    [Fact]
    public void TheMessages_SurviveTheSerializerOfTheWire_AndTheStateTheSerializerOfTheTable()
    {
        var request = new RequestSlot(CapacityFixture.I(1), "k", 2, "octopus", "develop", new PlanRank(true, "top", false), true, true, 3, CapacityFixture.T0, ["dotnet"], new Footprint(["src/a.cs", "docs/**"], FootprintSource.Design));
        var json = JsonSerializer.Serialize(request);
        var back = JsonSerializer.Deserialize<RequestSlot>(json)!;
        Assert.Equal(json, JsonSerializer.Serialize(back));
        Assert.Equal(("acme/orders#1", "top", FootprintSource.Design), (back.Item.ToString(), back.Rank.Priority, back.Footprint.Source));
        Assert.Equal(["src/a.cs", "docs/**"], back.Footprint.Entries);
        var report = JsonSerializer.Deserialize<HostReported>(JsonSerializer.Serialize(new HostReported("k", "h1", "octopus", 2, CapacityFixture.Roomy, CapacityFixture.T0)))!;
        Assert.Equal(CapacityFixture.Roomy, report.Metrics);
        var held = JsonSerializer.Deserialize<Held>(JsonSerializer.Serialize(new Held(CapacityFixture.I(2), 1, "file-overlap", "acme/orders#1", CapacityFixture.I(1), CapacityFixture.T0)))!;
        Assert.Equal(("acme/orders#1", "file-overlap acme/orders#1"), (held.By!.Value.ToString(), held.Waiting));
    }

    [Fact]
    public async Task TheState_RoundTripsThroughItsJson_WithEveryPartFilled()
    {
        var fx = new CapacityFixture();
        await fx.Report("h1");
        await fx.Request(1, "octopus", phase: "develop", files: ["src/a.cs"], toolchains: ["dotnet"]);
        await fx.Request(2, "octopus", phase: "develop", files: ["src/a.cs"]);   // held by #1
        await fx.Request(3);
        var json = fx.State.ToJson();
        var again = CapacityState.FromJson(json);
        Assert.Equal(json, again.ToJson());
        Assert.NotEmpty(again.Slots); Assert.NotEmpty(again.Queue); Assert.NotEmpty(again.InFlight); Assert.NotEmpty(again.Holds); Assert.NotEmpty(again.Hosts); Assert.NotEmpty(again.Emitted);
        Assert.Equal(["src/a.cs"], again.InFlight["acme/orders#1"].Footprint.Entries);
    }
}
