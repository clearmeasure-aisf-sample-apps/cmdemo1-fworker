using System.Text.Json.Nodes;
using Fworker.Workflows.Keeper;
using Fworker.Workflows.Reconcilers;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W9.10g (second pull request): the plant-wide steps of the sweep in C# (PlantSteps) against the behaviour of the rule scripts they replace (Invoke-ClaimRefReap, Invoke-BoardGuard, Invoke-Replenish and the count of
/// the item loop, in Verbs.Sweep.ps1 and Replenish.ps1): the same actions, the same calls, the same state and journal. The Tier 1 scenarios named in tests/scenarios/v2-claims.txt run the same steps against the real gateway.
/// </summary>
public sealed class PlantStepsTests
{
    const string RulesJson = """
        { "name": "factory-worker", "owner": "acme", "repo": "work", "project": 1, "label": "factory", "rivalLabel": "AI Factory", "autoMergeLabel": "Auto Merge", "testLabel": "test", "videoLabel": "video",
          "envScope": ["tdd"], "columns": { "ai": ["Technical Design", "Development"] }, "keeper": { "newItemColumn": "Technical Design" } }
        """;

    sealed class Rig
    {
        public readonly KeeperWorld World = new();
        public readonly InMemoryClaimOrphans Orphans = new();
        public bool May = true;
        public PlantSteps Steps;
        public VerbContext Cx;

        public Rig(ReplenishSettings? replenish = null, string rules = RulesJson, double lease = 30)
        {
            var r = FactoryRules.From(JsonNode.Parse(rules), n => n == "FACTORY_ENVIRONMENT" ? "tdd" : null);
            World.Rules = r;
            Cx = new VerbContext(r, World, World, World, World, () => World.Clock);
            Steps = new PlantSteps(Cx, World, Orphans, new PlantStepSettings(lease, replenish ?? ReplenishSettings.Off), () => May);
        }

        public KeeperWorld.Issue Add(int n, string title, string column = "Technical Design", bool open = true, params string[] labels)
        {
            var i = World.Add(n, title, "body", column, ["env:tdd", .. labels]); i.Open = open; return i;
        }

        public void Claim(int item, DateTime? until)
        {
            World.ClaimRefs.Add(item);
            if (until is { } u) World.State(item)["claim"] = new JsonObject { ["worker"] = "w1", ["lease_until"] = Ps.Iso(u) };
        }

        /// <summary>An item that reached Done a number of hours ago.</summary>
        public void Finished(int n, double hoursAgo = 1, string? replenished = null)
        {
            Add(n, $"finished {n}", "Done", open: false);
            var s = World.State(n);
            s["merge"] = new JsonObject { ["done_at"] = Ps.Iso(World.Clock.AddHours(-hoursAgo)) };
            if (replenished != null) s["replenished"] = new JsonObject { ["item"] = 1, ["at"] = replenished };
        }
    }

    static string[] ActionsOf(PlantStepResult r) => r.Actions.Select(a => $"{a!["item"]}:{a["action"]}{(a["reason"] is { } x ? ":" + x : "")}").ToArray();

    // ---- the claim-ref reap ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Reap_RemovesTheRefOfAClosedItemAtOnce_AndKeepsALiveClaimAndAYoungOrphan()
    {
        var r = new Rig();
        r.Add(1, "closed", "Done", open: false); r.Claim(1, null);
        r.Add(2, "live"); r.Claim(2, r.World.Clock.AddMinutes(20));
        r.Add(3, "young orphan"); r.Claim(3, null);
        var res = await r.Steps.ClaimRefReap(new HashSet<int>());
        Assert.Equal(["1:claim-ref-removed:item-closed", "3:orphan-claim"], ActionsOf(res));
        Assert.Equal([2, 3], r.World.ClaimRefs.OrderBy(n => n));
        Assert.Equal(Ps.Iso(r.World.Clock), res.Actions[1]!["since"]!.ToString());   // seen now: kept, and remembered
        Assert.Equal(r.World.Clock, r.Orphans.Load()[3]);
        Assert.DoesNotContain(2, r.Orphans.Load().Keys);
    }

    [Fact]
    public async Task Reap_RemovesAnOpenItemsOrphanOnceItIsOlderThanALease_ByTheTimeItWasFirstSeen()
    {
        var r = new Rig(lease: 30);
        r.Add(3, "orphan"); r.Claim(3, null);
        await r.Steps.ClaimRefReap(new HashSet<int>());
        r.World.Clock = r.World.Clock.AddMinutes(29);
        Assert.Equal(["3:orphan-claim"], ActionsOf(await r.Steps.ClaimRefReap(new HashSet<int>())));
        r.World.Clock = r.World.Clock.AddMinutes(2);
        var res = await r.Steps.ClaimRefReap(new HashSet<int>());
        Assert.Equal(["3:claim-ref-removed:orphan"], ActionsOf(res));
        Assert.Empty(r.World.ClaimRefs); Assert.Empty(r.Orphans.Load());
    }

    [Fact]
    public async Task Reap_ReadsAnItemOffTheBoardAndTreatsAGoneIssueAsClosed_ButNotAnotherEnvironmentsItem()
    {
        var r = new Rig();
        var off = r.Add(4, "no card"); off.Card = null; off.Open = false; r.Claim(4, null);   // not on the board: read as an issue, closed
        r.Claim(5, null);                                                                       // no such issue: gone
        r.World.Add(6, "other env", "x", "Development", "env:uat"); r.Claim(6, null);
        var res = await r.Steps.ClaimRefReap(new HashSet<int>());
        Assert.Equal(["4:claim-ref-removed:item-closed", "5:claim-ref-removed:item-closed"], ActionsOf(res));
        Assert.Equal([6], r.World.ClaimRefs);   // another environment's claim is not ours to reap
    }

    [Fact]
    public async Task Reap_LeavesABusyItemToALaterPass_KeepingTheTimeItWasFirstSeen_AndTheSagasItemsAlone()
    {
        var r = new Rig(lease: 30);
        r.Add(3, "orphan"); r.Claim(3, null);
        r.Add(7, "has a saga"); r.Claim(7, null);
        await r.Steps.ClaimRefReap(new HashSet<int> { 7 });
        var firstSeen = r.World.Clock;
        r.World.Clock = r.World.Clock.AddMinutes(40);
        r.World.BusyItems.Add(3);
        var res = await r.Steps.ClaimRefReap(new HashSet<int> { 7 });
        Assert.Equal(["3:skip:claim-ref-reap: item-busy"], ActionsOf(res));
        Assert.Equal(firstSeen, r.Orphans.Load()[3]);   // a skip does not restart the wait
        r.World.BusyItems.Clear();
        Assert.Equal(["3:claim-ref-removed:orphan"], ActionsOf(await r.Steps.ClaimRefReap(new HashSet<int> { 7 })));
        Assert.Equal([7], r.World.ClaimRefs);           // the saga's ref was never looked at
    }

    [Fact]
    public async Task AStepThatFails_IsAnErrorOfTheAnswer_NotAnException()
    {
        var r = new Rig();
        r.World.ListBoardFails = new VerbRejection(20, "github-rate-limited", "GitHub 403 on POST graphql");
        var res = await r.Steps.ClaimRefReap(new HashSet<int>());
        Assert.Empty(res.Actions);
        var e = Assert.Single(res.Errors);
        Assert.Equal("github-rate-limited", e!["code"]!.ToString());
        Assert.Equal("claim-ref-reap: GitHub 403 on POST graphql", e["reason"]!.ToString());
    }

    [Fact]
    public async Task ALeaseThatEnded_StopsAStepBeforeItsNextWrite()
    {
        var r = new Rig();
        r.Add(1, "closed", "Done", open: false); r.Claim(1, null);
        r.May = false;
        await Assert.ThrowsAsync<PlantStepFenced>(() => r.Steps.ClaimRefReap(new HashSet<int>()));
        Assert.DoesNotContain(r.World.Writes, w => w.StartsWith("DELETE"));
    }

    // ---- the board guard ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Guard_PutsAnIssueWithoutACardInTheIntakeColumn_RepairsACardWithoutAColumn_AndJournalsOnce()
    {
        var r = new Rig();
        r.Add(10, "placed", "Development");
        var none = r.Add(11, "no card"); none.Card = null; none.Column = null;
        var bare = r.Add(12, "card, no column"); bare.Column = null;
        var res = await r.Steps.BoardGuard();
        Assert.Equal(["12:board-added", "11:board-added"], ActionsOf(res));   // (GitHub lists the newest first)
        Assert.True((bool)res.Actions[0]!["repaired"]!); Assert.False((bool)res.Actions[1]!["repaired"]!);
        Assert.Equal("Technical Design", res.Actions[0]!["column"]!.ToString());
        Assert.Equal("Technical Design", r.World.Issues[11].Column); Assert.Equal("Technical Design", r.World.Issues[12].Column);
        Assert.Equal("Development", r.World.Issues[10].Column);
        Assert.Contains(r.World.Journal, j => j.StartsWith("11|board-added|11|")); Assert.Contains(r.World.Journal, j => j.StartsWith("12|board-added|12|"));
        Assert.Equal(2, r.World.IntakeLocks);   // each placement under the board-intake lock
        Assert.Empty((await r.Steps.BoardGuard()).Actions);   // a second pass has nothing to do
    }

    [Fact]
    public async Task Guard_LeavesPullRequestsTheRivalFactorysIssuesAndOtherEnvironmentsAlone()
    {
        var r = new Rig();
        var pr = r.Add(20, "a pr"); pr.IsPull = true; pr.Card = null; pr.Column = null;
        var rival = r.Add(21, "rival", "Technical Design", true, "AI Factory"); rival.Card = null; rival.Column = null;
        var other = r.World.Add(22, "other env", "x", "Development", "env:uat"); other.Card = null; other.Column = null;
        var res = await r.Steps.BoardGuard();
        Assert.Empty(res.Actions); Assert.Empty(res.Errors);
        Assert.DoesNotContain(r.World.Writes, w => w.StartsWith("addCard") || w.StartsWith("moveCard"));
    }

    [Fact]
    public async Task Guard_AnIntakeLockThatIsBusy_IsASkip_NotAnAttemptOrAFailure()
    {
        var r = new Rig();
        for (var n = 30; n < 36; n++) { var i = r.Add(n, $"new {n}"); i.Card = null; i.Column = null; }
        r.World.IntakeBusy = true;
        var res = await r.Steps.BoardGuard();
        Assert.Equal(6, res.Actions.Count);   // not stopped by three failures: a busy lock is none
        Assert.All(res.Actions, a => Assert.Equal("board-guard: intake-busy", a!["reason"]!.ToString()));
        r.World.IntakeBusy = false;
        Assert.Equal(6, (await r.Steps.BoardGuard()).Actions.Count(a => a!["action"]!.ToString() == "board-added"));
    }

    [Fact]
    public async Task Guard_StopsAtTheLimitOfAttempts_AtThreeFailuresInARow_AndAtARateLimit()
    {
        var r = new Rig();
        for (var n = 40; n < 70; n++) { var i = r.Add(n, $"new {n}"); i.Card = null; i.Column = null; }
        Assert.Equal(20, (await r.Steps.BoardGuard()).Actions.Count);   // at most 20 attempts a pass; the rest follow
        Assert.Equal(10, (await r.Steps.BoardGuard()).Actions.Count);

        var f = new Rig();
        for (var n = 40; n < 50; n++) { var i = f.Add(n, $"new {n}"); i.Card = null; i.Column = null; }
        f.World.MoveFails = _ => new VerbRejection(10, "unknown-column", "board has no column");
        var failed = await f.Steps.BoardGuard();
        Assert.Equal(3, failed.Actions.Count); Assert.All(failed.Actions, a => Assert.Equal("board-add-failed", a!["action"]!.ToString()));

        var l = new Rig();
        for (var n = 40; n < 45; n++) { var i = l.Add(n, $"new {n}"); i.Card = null; i.Column = null; }
        l.World.MoveFails = _ => new VerbRejection(20, "github-rate-limited", "GitHub 403");
        Assert.Equal(["44:board-add-failed:rate limited: the pass ends"], ActionsOf(await l.Steps.BoardGuard()));
    }

    [Fact]
    public async Task Guard_APlacementThatTheJournalCannotRecord_IsStillAPlacement()
    {
        var r = new Rig();
        var i = r.Add(50, "new"); i.Card = null; i.Column = null;
        r.World.BusyItems.Add(50);   // the 3 second lock of the journal line stays busy
        var acts = ActionsOf(await r.Steps.BoardGuard());
        Assert.Equal("50:board-added", acts[0]);
        Assert.StartsWith("50:board-added-unjournalled", acts[1]);
        Assert.Equal("Technical Design", r.World.Issues[50].Column);
    }

    // ---- the keeper's count ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DesignWaiting_CountsTheOpenFactoryItemsInTheKeepersColumn()
    {
        var r = new Rig();
        r.Add(60, "a"); r.Add(61, "b"); r.Add(62, "closed", "Technical Design", open: false); r.Add(63, "elsewhere", "Development");
        var plain = r.World.Add(64, "unlabelled", "x", "Technical Design", "env:tdd"); plain.Labels.Remove("factory");
        Assert.Equal(2, (await r.Steps.DesignWaiting()).DesignWaiting);
    }

    // ---- the replenishment ----------------------------------------------------------------------------------------------------------------

    static readonly ReplenishSettings On = new(true, 5, 5, 0, false);

    [Fact]
    public async Task Replenish_CreatesOneDocsOnlyItemForAFinishedItem_PutsItInTheKeepersColumn_AndRecordsItFirst()
    {
        var r = new Rig(On);
        r.Finished(70);
        var res = await r.Steps.Replenish(0, 1);
        var made = Assert.Single(res.Replenish!["created"]!.AsArray()).GetValue<int>();
        var issue = r.World.Issues[made];
        Assert.Equal("Docs: work order lifecycle guide", issue.Title);
        Assert.Equal("Technical Design", issue.Column);
        Assert.Equal(["enhancement", "Auto Merge", "factory", "env:tdd"], issue.Labels);
        Assert.Contains("<!-- keeper-replenish -->", issue.Body); Assert.Contains("work item #70 finished", issue.Body);
        Assert.Contains("- `docs/domain/work-order-lifecycle.md` exists and describes the lifecycle of a work order", issue.Body);
        Assert.StartsWith("Docs-only change. Add or extend `docs/domain/work-order-lifecycle.md` with a short, accurate page about the lifecycle", issue.Body);
        Assert.Equal(made, (int)r.World.State(70)["replenished"]!["item"]!);
        Assert.Contains(r.World.Journal, j => j.StartsWith("70|replenish||"));
        Assert.Equal(1, (int)res.Replenish["createdToday"]!);
        Assert.Equal(1, (int)res.Replenish["open"]!);   // the finished item is closed; the new one counted
        // the state comment is saved before the card is moved: a failure below must not create a second item
        Assert.True(r.World.Writes.IndexOf("SAVE state 70") < r.World.Writes.IndexOf($"moveCard {made} Technical Design"));
        Assert.Empty((await r.Steps.Replenish(0, 1)).Replenish!["created"]!.AsArray());   // the next pass does not repeat it
    }

    [Fact]
    public async Task Replenish_IsOffByDefault_AndAnswersNothing()
    {
        var r = new Rig();
        r.Finished(70);
        Assert.Null((await r.Steps.Replenish(0, 1)).Replenish);
        Assert.DoesNotContain(r.World.Writes, w => w.StartsWith("POST issues"));
    }

    [Fact]
    public async Task Replenish_NeverAboveTheTargetOfOpenItems_OrTheDaysLimit_AndSkipsOldOrAlreadyReplenishedItems()
    {
        var target = new Rig(new ReplenishSettings(true, 5, 2, 0, false));
        target.Finished(70); target.Finished(71); target.Finished(72);
        var res = await target.Steps.Replenish(0, 1);
        var created = res.Replenish!["created"]!.AsArray().Select(n => target.World.Issues[n!.GetValue<int>()]).ToList();
        Assert.Equal(2, created.Count);   // open 0 -> 2, then the target stops it
        Assert.Equal("target-open", res.Replenish["skipped"]!.ToString());
        Assert.Contains("work item #72 finished", created[0].Body); Assert.Contains("work item #71 finished", created[1].Body);   // the newest finished item first

        var day = new Rig(new ReplenishSettings(true, 1, 9, 0, false));
        day.Finished(70); day.Finished(71);
        var one = await day.Steps.Replenish(0, 1);
        Assert.Single(one.Replenish!["created"]!.AsArray());
        Assert.Equal("max-per-day", one.Replenish["skipped"]!.ToString());
        var again = await day.Steps.Replenish(0, 1);   // what was created within 24 hours counts, found in the repository's issues
        Assert.Empty(again.Replenish!["created"]!.AsArray());
        Assert.Equal("max-per-day", again.Replenish["skipped"]!.ToString());

        var old = new Rig(On);
        old.Finished(70, hoursAgo: 25); old.Finished(71, replenished: Ps.Iso(old.World.Clock));
        Assert.Empty((await old.Steps.Replenish(0, 1)).Replenish!["created"]!.AsArray());
    }

    [Fact]
    public async Task Replenish_BackpressureHoldsWhileWorkWaits_AndReleasesWhenNothingWaitsAndASessionIsFree()
    {
        var r = new Rig(new ReplenishSettings(true, 5, 9, 2, true));
        r.Finished(70);
        Assert.Equal("no-plan", (await r.Steps.Replenish(null, 1)).Replenish!["skipped"]!.ToString());
        Assert.Equal("waiting", (await r.Steps.Replenish(3, 1)).Replenish!["skipped"]!.ToString());
        Assert.Equal("waiting", (await r.Steps.Replenish(2, 1)).Replenish!["skipped"]!.ToString());
        Assert.Equal("not-idle", (await r.Steps.Replenish(1, 2)).Replenish!["skipped"]!.ToString());
        Assert.Equal("not-idle", (await r.Steps.Replenish(0, 0)).Replenish!["skipped"]!.ToString());
        Assert.DoesNotContain(r.World.Writes, w => w.StartsWith("POST issues"));
        var go = await r.Steps.Replenish(0, 1);
        Assert.Single(go.Replenish!["created"]!.AsArray());
        Assert.Equal(0, (int)go.Replenish["waiting"]!);
        Assert.Equal(2, (int)go.Replenish["holdWhenWaiting"]!); Assert.True((bool)go.Replenish["onlyWhenIdle"]!);
    }

    [Fact]
    public void Replenish_TakesTheFirstUnusedTopic_ThenAGlossaryEntry_ThenANumberedNote()
    {
        var used = new HashSet<string>();
        var first = ReplenishTopics.Next(used);
        Assert.Equal(("Docs: work order lifecycle guide", "docs/domain/work-order-lifecycle.md"), (first.Title, first.File));
        used.Add(first.Title.ToLowerInvariant());
        Assert.Equal("Docs: work order statuses reference", ReplenishTopics.Next(used).Title);
        while (ReplenishTopics.Next(used).Title.StartsWith("Docs: glossary") == false) used.Add(ReplenishTopics.Next(used).Title.ToLowerInvariant());   // the rest of the catalogue
        var glossary = ReplenishTopics.Next(used);
        Assert.Equal(("Docs: glossary entry for Work order", "docs/glossary/work-order.md"), (glossary.Title, glossary.File));
        used.Add(glossary.Title.ToLowerInvariant());
        Assert.Equal("docs/glossary/draft.md", ReplenishTopics.Next(used).File);
        for (var i = 0; i < 33; i++) used.Add(ReplenishTopics.Next(used).Title.ToLowerInvariant());   // the 34 terms
        var note = ReplenishTopics.Next(used);
        Assert.Equal(("Docs: note 1", "docs/notes/note-1.md"), (note.Title, note.File));
    }

    [Fact]
    public async Task Replenish_AvoidsTheTitlesOfEveryFactoryIssue_OpenOrClosed()
    {
        var r = new Rig(On);
        r.Finished(70);
        r.World.Add(80, "Docs: work order lifecycle guide", "x", "Done").Open = false;
        r.World.Add(81, "DOCS: WORK ORDER STATUSES REFERENCE", "x", "Done");
        var res = await r.Steps.Replenish(0, 1);
        Assert.Equal("Docs: waiting on vendor substate explained", r.World.Issues[res.Replenish!["created"]![0]!.GetValue<int>()].Title);
    }

    [Fact]
    public void TheSettingsAreTheScripts_Defaults_Limits_AndEnvironments()
    {
        string? Env(string k) => k == "FACTORY_ENVIRONMENT" ? "uat" : null;
        Assert.False(ReplenishSettings.From(JsonNode.Parse("""{ "keeper": { } }"""), Env).Enabled);
        Assert.False(ReplenishSettings.From(JsonNode.Parse("""{ "keeper": { "replenish": { "enabled": true, "environments": ["tdd"] } } }"""), Env).Enabled);   // another environment's
        var on = ReplenishSettings.From(JsonNode.Parse("""{ "keeper": { "replenish": { "enabled": true, "environments": ["tdd", "UAT"], "maxPerDay": 9999, "targetOpen": 3, "holdWhenWaiting": "4", "onlyWhenIdle": true } } }"""), Env);
        Assert.Equal(new ReplenishSettings(true, 500, 3, 4, true), on);   // capped like the script's [Math]::Min
        Assert.Equal(new ReplenishSettings(true, 30, 10, 0, false), ReplenishSettings.From(JsonNode.Parse("""{ "keeper": { "replenish": { "enabled": true } } }"""), Env));
        Assert.Equal(45, PlantStepSettings.From(JsonNode.Parse("""{ "timing": { "leaseMinutes": 45 } }"""), Env).LeaseMinutes);
        Assert.Equal(30, PlantStepSettings.From(JsonNode.Parse("""{ }"""), Env).LeaseMinutes);
        Assert.Equal(0.5, PlantStepSettings.From(JsonNode.Parse("""{ "timing": { "leaseMinutes": 45 } }"""), k => k == "FACTORY_TIMING_leaseMinutes" ? "0.5" : null).LeaseMinutes);   // the suites' override, which the scripts read too
    }
}
