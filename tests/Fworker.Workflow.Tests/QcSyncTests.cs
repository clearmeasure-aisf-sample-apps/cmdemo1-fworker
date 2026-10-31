using System.Text.Json.Nodes;
using Fworker.Workflows.Qc;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W9.1: qc-sync (Invoke-QcSync of Qc.ps1), the sweep's QC step: the offers, the withdrawals, and the teardowns and approvals it reports for the gateway to act on. It never creates a record or places a job.
/// </summary>
public sealed class QcSyncTests
{
    static QcHarness Validated(string mergePolicy = "auto", string humanApprovers = "[\"alice\"]")
    {
        var h = new QcHarness(mergePolicy, humanApprovers: humanApprovers);
        h.World.AddValidatedItem();
        return h;
    }

    static List<(int Item, int Pr, string Reason)> Teardowns(JsonObject o) =>
        ((JsonArray)o["teardowns"]!).Select(t => (QcJson.Int((JsonObject)t!, "item"), QcJson.Int((JsonObject)t!, "pr"), QcJson.Str((JsonObject)t!, "reason"))).ToList();

    static List<string> Actions(JsonObject o) => ((JsonArray)o["actions"]!).Select(a => QcJson.Str((JsonObject)a!, "action")).ToList();

    static void MoveCard(QcHarness h, string column)
    {
        h.World.BoardItems[0] = h.World.BoardItems[0] with { Column = column };
        h.World.Items[5].Info = h.World.Items[5].Info with { Column = column };
    }

    [Fact]
    public async Task WithoutOwnerModeAndASecretThereIsNoQc()
    {
        var h = new QcHarness(env: n => n == "FACTORY_OWNER_MODE" ? "1" : null);
        var (c, o) = await h.Call("qc-sync");
        Assert.Equal((30, "qc-disabled", "QC environments need the gateway in owner mode with FACTORY_TOKEN_SECRET"), (c, o["code"]!.ToString(), o["reason"]!.ToString()));
    }

    [Fact]
    public async Task AnEligibleItemIsOffered_OnceForTheHead()
    {
        var h = Validated();
        var (c, o) = await h.Call("qc-sync");
        Assert.Equal(0, c);
        Assert.Equal(["qc-offer"], Actions(o));
        var a = (JsonObject)((JsonArray)o["actions"]!)[0]!;
        Assert.Equal((5, 9, "posted"), (QcJson.Int(a, "item"), QcJson.Int(a, "pr"), QcJson.Str(a, "outcome")));
        Assert.Single(h.World.Offers(9)); Assert.Single(h.World.Offers(5));
        Assert.Empty((JsonArray)o["errors"]!); Assert.Empty((JsonArray)o["records"]!); Assert.Empty((JsonArray)o["teardowns"]!); Assert.Empty((JsonArray)o["approvals"]!);
        var (_, again) = await h.Call("qc-sync");
        Assert.Empty(Actions(again));
        Assert.Single(h.World.Journals, j => j.Event == "qc-offered");
    }

    [Fact]
    public async Task ADryRunOffersNothingAndWritesNothing()
    {
        var h = Validated();
        var (c, o) = await h.Call("qc-sync", new { dry = true });
        Assert.Equal(0, c);
        Assert.Empty(Actions(o));
        Assert.Empty(h.World.Offers(9));
        Assert.Empty(h.World.Writes);
    }

    [Fact]
    public async Task AnItemThatIsNotTheFactorysIsLeftAlone()
    {
        var h = new QcHarness();
        h.World.AddValidatedItem(5, 9, labels: ["something else"]);
        h.World.AddValidatedItem(6, 10, labels: ["factory worker", "AI Factory"]);     // the rival factory's label
        h.World.AddValidatedItem(7, 11, column: "Development");                       // not in validation, no live environment
        var (_, o) = await h.Call("qc-sync");
        Assert.Empty(Actions(o)); Assert.Empty(h.World.Writes);
    }

    [Fact]
    public async Task AWithdrawnPullRequestStateIsReportedAsSkippedInTheJournal_ThenTheOfferIsWithdrawn()
    {
        var h = Validated();
        await h.Call("qc-sync");
        h.World.Pulls[9]["draft"] = true;
        var (_, o) = await h.Call("qc-sync");
        Assert.Equal(["qc-withdrawn"], Actions(o));
        var skipped = Assert.Single(h.World.Journals, j => j.Event == "skipped");
        Assert.Equal(("QC is not offered for PR #9: draft", "qc|9|draft"), (QcJson.Str(skipped.Fields, "reason"), skipped.Key));
        foreach (var n in new[] { 9, 5 }) Assert.Equal("QC environment for PR #9 is no longer offered (draft).\n<!-- qc link pr=9 sha=withdrawn exp=0 -->", Assert.Single(h.World.Offers(n)).Body);
        var writes = h.World.Writes.Count;
        var (_, again) = await h.Call("qc-sync");
        Assert.Equal(["qc-withdrawn"], Actions(again));      // (the script reports it at every sweep that finds the comment, and writes nothing more: the comment says so)
        Assert.Equal(writes, h.World.Writes.Count);
    }

    [Fact]
    public async Task TheReasonsThatNeedNoNoteAreNotJournaled()
    {
        var h = Validated();
        h.World.Files[9] = ["docs/a.md"];                       // docs-only
        var (_, o) = await h.Call("qc-sync");
        Assert.Empty(Actions(o));
        Assert.DoesNotContain(h.World.Journals, j => j.Event == "skipped");
    }

    [Fact]
    public async Task AnAutoMergeLabelAddedLaterEndsARunningEnvironment()
    {
        var h = Validated();
        await h.Start(); await h.Place(); await h.Ready();
        h.World.BoardItems[0] = h.World.BoardItems[0] with { Labels = ["factory worker", "Auto Merge"] };
        var (_, o) = await h.Call("qc-sync");
        Assert.Equal([(5, 9, "label-changed")], Teardowns(o));
        Assert.Equal("ready", QcJson.Str((JsonObject)((JsonArray)o["records"]!)[0]!, "state"));
    }

    [Fact]
    public async Task AClosedOrMergedPullRequestTearsItsEnvironmentDown()
    {
        var h = Validated();
        await h.Start(); await h.Place();
        h.World.Pulls[9]["state"] = "closed";
        var (_, o) = await h.Call("qc-sync");
        Assert.Equal([(5, 9, "closed")], Teardowns(o));
        h.World.State(5)["merge"] = new JsonObject { ["sha"] = "abc" };
        var (_, merged) = await h.Call("qc-sync");
        Assert.Equal([(5, 9, "merged")], Teardowns(merged));
    }

    [Fact]
    public async Task AnItemThatLeftValidationIsReached_ByTheIndexAndByTheTable()
    {
        var h = Validated();
        await h.Start(); await h.Place();
        MoveCard(h, "Development");
        var (_, o) = await h.Call("qc-sync");
        Assert.Equal([(5, 9, "left-validation")], Teardowns(o));
        Assert.Single((JsonArray)o["records"]!);
        // the index is a file of the state directory; the table holds the same fact
        var g = Validated();
        await g.Start(); await g.Place();
        g.Index.Update(5, false);
        MoveCard(g, "Development");
        var (_, o2) = await g.Call("qc-sync");
        Assert.Equal([(5, 9, "left-validation")], Teardowns(o2));
        // neither: an item outside validation with a record nobody indexed is not looked at
        var k = new QcHarness(useTable: false);
        k.World.AddValidatedItem();
        await k.Start(); k.Index.Update(5, false);
        MoveCard(k, "Development");
        var (_, o3) = await k.Call("qc-sync");
        Assert.Empty(Teardowns(o3));
    }

    [Fact]
    public async Task AMovedHeadMarksTheRecordStale_AndTheGraceDecidesTheTeardown()
    {
        var h = Validated();
        await h.Start(); await h.Place(); await h.Ready();
        h.World.Push(9, FakeQcWorld.Sha2);
        var (_, o) = await h.Call("qc-sync");
        Assert.Contains("qc-stale", Actions(o));
        Assert.Equal("stale", h.State());
        Assert.Empty(Teardowns(o));                             // (the grace counts from the mark)
        Assert.Equal("stale", QcJson.Str((JsonObject)((JsonArray)o["records"]!)[0]!, "state"));
        h.Now = h.Now.AddMinutes(29);
        var (_, early) = await h.Call("qc-sync");
        Assert.Empty(Teardowns(early)); Assert.DoesNotContain("qc-stale", Actions(early));
        h.Now = h.Now.AddMinutes(2);
        var (_, late) = await h.Call("qc-sync");
        Assert.Equal([(5, 9, "head-changed")], Teardowns(late));
    }

    // Another writer saves the item between the sync's read and its write (a verb, the sweep's other steps, a webhook): the sync reads again and does its step, and the sweep's answer has no error. It was
    // answered as {"code":"error","reason":"qc: the state of item N changed since it was read ..."} and the suites that fail on a sweep error failed with it.
    [Fact]
    public async Task AStateThatMovesUnderTheSync_IsReadAgain_AndTheStepIsDone_WithNoError()
    {
        var h = Validated();
        await h.Start(); await h.Place(); await h.Ready();
        h.World.Push(9, FakeQcWorld.Sha2);
        var moved = 0;
        h.World.BeforeSave = item => { if (moved++ < 2) h.World.Items[item].Revision++; };   // two writers in a row win the race; the third attempt finds the revision it read
        var (c, o) = await h.Call("qc-sync");
        Assert.Equal(0, c);
        Assert.Empty((JsonArray)o["errors"]!);
        Assert.Single(Actions(o), a => a == "qc-stale");        // once, from the attempt that saved (not once for each attempt)
        Assert.Equal("stale", h.State());
        Assert.Single((JsonArray)o["records"]!);                // and the record once, not once for each attempt
    }

    [Fact]
    public async Task AStateThatKeepsMoving_IsAnErrorOfTheItem_AfterThreeAttempts()
    {
        var h = Validated();
        await h.Start(); await h.Place(); await h.Ready();
        h.World.Push(9, FakeQcWorld.Sha2);
        var tries = 0;
        h.World.BeforeSave = item => { tries++; h.World.Items[item].Revision++; };
        var (c, o) = await h.Call("qc-sync");
        Assert.Equal(0, c);
        Assert.Equal(3, tries);
        var e = Assert.Single(((JsonArray)o["errors"]!).Select(x => (JsonObject)x!));
        Assert.Contains("changed since it was read", QcJson.Str(e, "reason"));
        Assert.Equal("ready", h.State());
    }

    [Fact]
    public async Task ARunningEnvironmentPastItsLifetimeIsTornDown()
    {
        var h = Validated();
        await h.Start(); await h.Place(); await h.Ready();
        h.Now = h.Now.AddMinutes(479);
        Assert.Empty(Teardowns((await h.Call("qc-sync")).Out));
        h.Now = h.Now.AddMinutes(2);
        Assert.Equal([(5, 9, "lifetime")], Teardowns((await h.Call("qc-sync")).Out));
    }

    [Fact]
    public async Task AnItemTheSweepFindsBusyIsSkippedForThisSweep()
    {
        var h = Validated();
        await h.Start(); await h.Place(); await h.Ready();
        h.World.Push(9, FakeQcWorld.Sha2);
        var gate = new TaskCompletionSource(); var inside = new TaskCompletionSource(); var first = true;
        h.World.OnRead = async _ => { if (first) { first = false; inside.SetResult(); await gate.Task; } };
        var verb = h.Call("qc-teardown", new { item = 5, pr = 9, reason = "person" });     // holds the item's lock inside its first read
        await inside.Task;
        var (_, o) = await h.Call("qc-sync");
        var skip = (JsonObject)((JsonArray)o["actions"]!)[0]!;
        Assert.Equal(("qc-skip", "qc-stale: item-busy"), (QcJson.Str(skip, "action"), QcJson.Str(skip, "reason")));
        Assert.Equal("ready", h.State());          // nothing was marked
        gate.SetResult(); await verb;
    }

    [Fact]
    public async Task APersonsApprovalOfTheCurrentHeadIsReported_AndTheCopyOnTheRecordFollowsIt()
    {
        var h = Validated("peer-approved", "[\"carol\"]");
        await h.Start(); await h.Place(); await h.Ready();
        h.World.ReviewsOf[9] = [new JsonObject { ["user"] = new JsonObject { ["login"] = "carol" }, ["state"] = "APPROVED", ["commit_id"] = FakeQcWorld.Sha1 }];
        var (_, o) = await h.Call("qc-sync");
        var ap = (JsonObject)Assert.Single((JsonArray)o["approvals"]!)!;
        Assert.Equal((5, 9, FakeQcWorld.Sha1, "carol", true), (QcJson.Int(ap, "item"), QcJson.Int(ap, "pr"), QcJson.Str(ap, "sha"), QcJson.Str(ap, "by"), QcJson.Bool(ap, "ran")));
        Assert.Equal("carol", QcJson.Str((JsonObject)h.World.Rec(5, 9)!["approved"]!, "by"));
        // dismissed: the copy is cleared and nothing is reported
        h.World.ReviewsOf[9].Add(new JsonObject { ["user"] = new JsonObject { ["login"] = "carol" }, ["state"] = "DISMISSED", ["commit_id"] = FakeQcWorld.Sha1 });
        var (_, o2) = await h.Call("qc-sync");
        Assert.Empty((JsonArray)o2["approvals"]!);
        Assert.False(QcJson.Has(h.World.Rec(5, 9), "approved"));
    }

    [Fact]
    public async Task AnApprovalIsNotLookedForWithoutTheGate_OrAfterTheVerdictWasProcessed()
    {
        var h = Validated("auto", "[\"carol\"]");
        await h.Start(); await h.Place(); await h.Ready();
        h.World.ReviewsOf[9] = [new JsonObject { ["user"] = new JsonObject { ["login"] = "carol" }, ["state"] = "APPROVED", ["commit_id"] = FakeQcWorld.Sha1 }];
        Assert.Empty((JsonArray)(await h.Call("qc-sync")).Out["approvals"]!);
        var g = Validated("peer-approved", "[\"carol\"]");
        await g.Start(); await g.Place(); await g.Ready();
        g.World.ReviewsOf[9] = h.World.ReviewsOf[9];
        g.World.State(5)["verdict"]!["processed"] = true;
        Assert.Empty((JsonArray)(await g.Call("qc-sync")).Out["approvals"]!);
    }

    [Fact]
    public async Task AnInspectedItemWithoutARecordReportsTheApprovalToo_AndSavesNothing()
    {
        var h = Validated("peer-approved", "[\"carol\"]");
        h.World.State(5)["approval"] = new JsonObject { ["sha"] = FakeQcWorld.Sha1, ["by"] = "inspector" };
        h.World.ReviewsOf[9] = [new JsonObject { ["user"] = new JsonObject { ["login"] = "carol" }, ["state"] = "APPROVED", ["commit_id"] = FakeQcWorld.Sha1 }];
        var (_, o) = await h.Call("qc-sync");
        var ap = (JsonObject)Assert.Single((JsonArray)o["approvals"]!)!;
        Assert.False(QcJson.Bool(ap, "ran"));
        Assert.Null(h.World.Rec(5, 9));
    }

    [Fact]
    public async Task ADraftOfACopilotItemIsMarkedReadyAndThenOffered()
    {
        var h = Validated();
        h.World.Pulls[9]["draft"] = true;
        h.World.State(5)["copilot_task"] = new JsonObject { ["branch"] = "copilot/x" };
        var (_, o) = await h.Call("qc-sync");
        Assert.Contains("READY 9", h.World.Writes);
        Assert.Equal(["qc-offer"], Actions(o));
        var (_, other) = await new QcHarness().Call("qc-sync");
        Assert.Empty(Actions(other));
    }

    [Fact]
    public async Task AnItemThatVanishedIsSkipped_AnyOtherFaultIsAnErrorOfThatItemAndTheSweepGoesOn()
    {
        var h = Validated();
        h.World.AddValidatedItem(6, 10, FakeQcWorld.Sha2);
        h.World.AddValidatedItem(7, 11);
        h.World.Items.Remove(5);                                                         // its issue is gone: the board still lists the card
        h.World.OnRead = item => item == 7 ? throw new InvalidOperationException("boom") : Task.CompletedTask;
        var (c, o) = await h.Call("qc-sync");
        Assert.Equal(0, c);
        var skip = ((JsonArray)o["actions"]!).Select(a => (JsonObject)a!).First(a => QcJson.Str(a, "action") == "qc-skip");
        Assert.Equal((5, "vanished-during-sweep"), (QcJson.Int(skip, "item"), QcJson.Str(skip, "reason")));
        var error = (JsonObject)Assert.Single((JsonArray)o["errors"]!)!;
        Assert.Equal((7, "error", "qc: boom"), (QcJson.Int(error, "item"), QcJson.Str(error, "code"), QcJson.Str(error, "reason")));
        Assert.Single(h.World.Offers(10));     // item 6 was offered all the same
    }
}
