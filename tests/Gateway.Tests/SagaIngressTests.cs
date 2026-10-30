using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Domain;
using Fworker.Integrations.GitHub;
using Fworker.Workflows.WorkItem;
using Xunit;

namespace Gateway.Tests;

/// <summary>W9.10h: what a person does on GitHub reaches the saga: the issue closed or reopened, the card moved, a hold label. ProjectionEcho is consulted, so the projection's own writes are not news.</summary>
public sealed class SagaIngressTests
{
    static readonly RepoId Home = new("acme", "orders");
    static readonly ItemId Item = new(Home, 7);
    static readonly DateTimeOffset T0 = new(2026, 10, 30, 10, 0, 0, TimeSpan.Zero);
    static DocsPlanSettings Settings() => DocsPlanSettingsReader.From(JsonNode.Parse("""{"holdLabels":["Needs Human Review"],"columns":{"ai":["Technical Design","Development","Functional Testing"],"development":"Development","validation":"Functional Testing","done":"Done"},"requiredBodySection":{"Technical Design":"## Technical Design"}}"""));

    sealed class Issues : IItemIssueReader
    {
        public string[] Labels = [];
        public readonly List<int> Read = [];
        public Exception? Throw;
        public Task<ItemIssue?> ReadIssue(ItemId item, CancellationToken ct = default)
        {
            Read.Add(item.Number);
            return Throw is null ? Task.FromResult<ItemIssue?>(new ItemIssue(item, false, Labels)) : throw Throw;
        }
        public Task<IReadOnlyList<CommitCheck>?> ReadCheckRuns(RepoId repository, string commit, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CommitCheck>?>(null);
        public Task<IReadOnlyList<string>?> ReadPullRequestFiles(RepoId repository, int number, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>?>(null);
    }

    sealed class Merges : IMergeFactsReader
    {
        public MergedPullRequest? Closing;
        public Exception? Throw;
        public int Finds;
        public Task<MergedPullRequest?> ReadPullRequest(RepoId repository, int number, CancellationToken ct = default) => Task.FromResult<MergedPullRequest?>(null);
        public Task<MergedPullRequest?> FindMergedPullRequest(ItemId item, CancellationToken ct = default) { Finds++; return Throw is null ? Task.FromResult(Closing) : throw Throw; }
        public Task<ItemMergeFacts?> ReadItem(ItemId item, CancellationToken ct = default) => Task.FromResult<ItemMergeFacts?>(null);
    }

    sealed class Rig
    {
        public readonly InMemoryWorkItemStore Store = new();
        public readonly WorkItemService Svc;
        public readonly InMemoryProjectionVersions Versions = new();
        public readonly ProjectionEcho Echo;
        public readonly Issues Reader = new();
        public readonly Merges Merged = new();
        public readonly SagaIngress Ingress;
        public DateTimeOffset Now = T0;
        int seq;

        public Rig(bool withReader = true, bool withMerges = false)
        {
            Svc = new WorkItemService(Store, new WorkItemSaga(new DocsPort()));
            Echo = new ProjectionEcho(Versions, () => Now);
            Ingress = new SagaIngress(Store, Home, Settings(), Versions, Echo, withReader ? Reader : null, withMerges ? Merged : null);
        }

        public WorkItemState State => Store.States[Item.ToString()];
        public Task Start(string plan = "docs") => Svc.Handle(new StartWorkItem(Item, "intake", plan));

        /// <summary>Completes the step the item is on (the docs plan: a COMPLETE review, and for Development a pull request first).</summary>
        public async Task Next()
        {
            var s = State;
            if (s.Step == StepKind.Develop) await Svc.Handle(new PullRequestChanged(Item, 0, "pr", 11, "aaa1111", ["docs/a.md"]));
            await Svc.Handle(new ReviewRecorded(Item, s.Generation, s.ArmId, "r-" + s.ArmId, true));
            await Svc.Handle(new StepCompleted(Item, s.Generation, s.ArmId, "d-" + s.ArmId));
        }

        /// <summary>The item on its second step (Development), its card projected there (the projection applied the version of the step entry).</summary>
        public async Task OnDevelop()
        {
            await Start();
            await Next();
            await Projected("Development");
        }

        /// <summary>The projection moved the card for the step the item is on now: it applied the version of the step entry and recorded what it wrote.</summary>
        public async Task Projected(string column)
        {
            Echo.Record(Item.ToString(), ProjectionTargets.Card, State.CardSeq, ProjectionEcho.ColumnFingerprint(column));
            await Versions.Advance(Item.ToString(), ProjectionTargets.Card, State.CardSeq, Now);
        }

        public Task<IReadOnlyList<WorkItemInput>> Webhook(string name, int item = 7, Action<JsonObject>? edit = null) => Derive("webhook", name, item, edit);
        public Task<IReadOnlyList<WorkItemInput>> Poll(string name, int item = 7, Action<JsonObject>? edit = null) => Derive("poll", name, item, edit);

        Task<IReadOnlyList<WorkItemInput>> Derive(string source, string name, int item, Action<JsonObject>? edit)
        {
            var ev = new JsonObject { ["provider"] = "github", ["event"] = name, ["item"] = item, ["ref"] = source == "poll" ? $"poll:board:acme/orders#{item}:{++seq}" : $"delivery-{++seq}", ["at"] = Now.ToString("o") };
            edit?.Invoke(ev);
            return Ingress.Derive(source, ev.ToJsonString(), default);
        }
    }

    // ---- close and reopen ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AClosedIssue_OfAnItemWithASaga_IsTheSagasInput_FromTheWebhookAndFromThePoll()
    {
        var rig = new Rig(); await rig.Start();
        var hook = Assert.IsType<ItemClosed>(Assert.Single(await rig.Webhook("item.closed")));
        Assert.Equal((Item, "webhook"), (hook.Item, hook.Source));
        var poll = Assert.IsType<ItemClosed>(Assert.Single(await rig.Poll("board.card", edit: e => { e["open"] = false; e["column"] = "Done"; })));
        Assert.Equal("poll", poll.Source);
        Assert.NotEqual(hook.Key, poll.Key);
        var result = await rig.Svc.Handle(hook);
        Assert.Equal((WorkItemOutcome.Applied, WorkItemStatus.Completed), (result.Decision.Outcome, rig.State.Status));
    }

    static MergedPullRequest MergedPr() => new(Home, 11, true, "master", "factory/7-docs", "Docs", "Closes #7", new string('a', 40));

    [Theory]
    [InlineData("webhook")]
    [InlineData("poll")]
    public async Task TheCloseOfTheIssue_ThatAMergedPullRequestClosed_IsTheMerges_NotAnItemClosed_AndTheMergeIsApplied(string source)
    {
        // a person merges 'Closes #7' while the item is on Development: GitHub closes the issue at once, the merge derivation has still to read the pull request
        var rig = new Rig(withMerges: true); await rig.OnDevelop();
        rig.Merged.Closing = MergedPr();
        Assert.Empty(source == "webhook" ? await rig.Webhook("item.closed") : await rig.Poll("board.card", edit: e => { e["open"] = false; e["column"] = "Done"; }));
        Assert.Equal(1, rig.Merged.Finds);
        // the merge arrives later and finds the instance open: it is applied (the step is Merge, the commit recorded), not NotFound on a completed instance
        var seen = await rig.Svc.Handle(new MergeSeen(Item, "merged|" + new string('a', 40), new string('a', 40), 11));
        Assert.Equal(WorkItemOutcome.Applied, seen.Decision.Outcome);
        Assert.Equal(new string('a', 40), rig.State.MergeCommit);   // (the docs plan ends with its merge: the instance completes by the merge, and the commit is on it)
        Assert.Contains(seen.Decisions, d => d.Action == WorkItemActions.Merged);
        // the race this closes: with the close delivered first, the instance completes without a merge and the merge is refused
        var raced = new Rig(); await raced.OnDevelop();
        await raced.Svc.Handle(Assert.IsType<ItemClosed>(Assert.Single(await raced.Webhook("item.closed"))));
        Assert.Equal("", raced.State.MergeCommit);
        var late = await raced.Svc.Handle(new MergeSeen(Item, "merged|" + new string('a', 40), new string('a', 40), 11));
        Assert.Equal(WorkItemOutcome.NotFound, late.Decision.Outcome);
    }

    [Fact]
    public async Task ACloseWithoutAMergedPullRequest_IsStillAnItemClosed_AndAFailedReadIsRetried()
    {
        var rig = new Rig(withMerges: true); await rig.OnDevelop();
        Assert.IsType<ItemClosed>(Assert.Single(await rig.Webhook("item.closed")));   // (closed by hand: no merged pull request)
        rig.Merged.Throw = new HttpRequestException("503");
        await Assert.ThrowsAsync<HttpRequestException>(() => rig.Webhook("item.closed"));   // the message fails and is tried again; the close is neither lost nor sent unchecked
        // an item that was merged already costs no read: the saga refuses a close then
        await rig.Svc.Handle(new MergeSeen(Item, "merged|b", new string('b', 40), 11));
        rig.Merged.Throw = null; rig.Merged.Finds = 0;
        Assert.IsType<ItemClosed>(Assert.Single(await rig.Webhook("item.closed")));
        Assert.Equal(0, rig.Merged.Finds);
    }

    [Fact]
    public async Task AnEventOfAnItemWithoutASaga_OrOfAnotherKind_OrNotAnObject_MakesNoInput()
    {
        var rig = new Rig(); await rig.Start();
        Assert.Empty(await rig.Webhook("item.closed", item: 8));   // (v1's item)
        Assert.Empty(await rig.Webhook("pr.merged"));
        Assert.Empty(await rig.Webhook("build.branch_completed"));
        Assert.Empty(await rig.Webhook("item.closed", item: 0));
        Assert.Empty(await rig.Ingress.Derive("webhook", "[1,2]", default));
    }

    [Fact]
    public async Task AReopenedIssue_StartsTheNextGeneration_OnlyWhenTheInstanceCompleted_AndOnlyFromTheWebhook()
    {
        var rig = new Rig(); await rig.Start();
        Assert.Empty(await rig.Webhook("item.reopened"));   // (the instance is open: nothing to reopen)
        await rig.Svc.Handle(new ItemClosed(Item, "closed-1", "webhook"));
        // a poll reports every open card a process has not seen, after a restart too: it never reopens anything
        Assert.Empty(await rig.Poll("board.card", edit: e => { e["open"] = true; e["column"] = "Todo"; }));
        var reopen = Assert.IsType<ReopenWorkItem>(Assert.Single(await rig.Webhook("item.reopened")));
        Assert.Equal(Item, reopen.Item);
        var result = await rig.Svc.Handle(reopen);
        Assert.Equal((WorkItemOutcome.Applied, 2), (result.Decision.Outcome, result.Decision.Generation));
    }

    [Fact]
    public async Task TheKeysOfTheInputs_AreTheSameForTheSameEvent_AndDifferForAnother()
    {
        var rig = new Rig(); await rig.Start();
        const string ev = """{"provider":"github","event":"item.closed","item":7,"ref":"delivery-1","at":"2026-10-30T10:00:00.0000000+00:00"}""";
        var a = Assert.IsType<ItemClosed>(Assert.Single(await rig.Ingress.Derive("webhook", ev, default)));
        var b = Assert.IsType<ItemClosed>(Assert.Single(await rig.Ingress.Derive("webhook", ev, default)));
        var c = Assert.IsType<ItemClosed>(Assert.Single(await rig.Ingress.Derive("webhook", ev.Replace("delivery-1", "delivery-2"), default)));
        Assert.Equal(a.Key, b.Key);
        Assert.NotEqual(a.Key, c.Key);
    }

    // ---- the card --------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task APersonsMoveBack_IsTheSagasInput_WithTheStepOfTheColumn()
    {
        var rig = new Rig(); await rig.OnDevelop();
        var moved = Assert.IsType<CardMoved>(Assert.Single(await rig.Webhook("item.column_changed", edit: e => e["column"] = "Technical Design")));
        Assert.Equal((Item, rig.State.Generation, "Technical Design", StepKind.Design), (moved.Item, moved.Generation, moved.Column, moved.Step));
        var result = await rig.Svc.Handle(moved);
        Assert.Equal(WorkItemOutcome.Applied, result.Decision.Outcome);
        Assert.Equal(StepKind.Design, rig.State.Step);
    }

    [Fact]
    public async Task TheProjectionsOwnWrite_IsAnEcho_AndSoIsEveryEventBeforeTheProjectionAppliedTheStepsCard()
    {
        var rig = new Rig(); await rig.OnDevelop();
        // the event of the card the projection put in the column of the step: not news
        Assert.Empty(await rig.Webhook("item.column_changed", edit: e => e["column"] = "Development"));
        Assert.Empty(await rig.Poll("board.card", edit: e => { e["open"] = true; e["column"] = "Development"; }));
        // the saga entered Functional Testing and the projection has not applied it yet (a retry, or it gave up): the card still stands in Development, and an event about it is the old column
        await rig.Next();
        Assert.Equal(StepKind.Verify, rig.State.Step);
        Assert.True(rig.State.CardSeq > await rig.Versions.Applied(Item.ToString(), ProjectionTargets.Card));
        Assert.Empty(await rig.Poll("board.card", edit: e => { e["open"] = true; e["column"] = "Development"; }));
        // once the projection applied it, the same event is a person's move back
        await rig.Projected("Functional Testing");
        Assert.IsType<CardMoved>(Assert.Single(await rig.Poll("board.card", edit: e => { e["open"] = true; e["column"] = "Development"; })));
    }

    [Fact]
    public async Task TheWebhookOfAnEarlierWrite_IsStillTheProjections_ForTwoMinutes_ButAPollIsNot()
    {
        var rig = new Rig(); await rig.OnDevelop();
        // Development was written, then the item moved on and Functional Testing was written; the webhook of the first write arrives late
        await rig.Next();
        await rig.Projected("Functional Testing");
        rig.Now = T0.AddSeconds(30);
        Assert.Empty(await rig.Webhook("item.column_changed", edit: e => e["column"] = "Development"));
        // a poll reports the board as it is: the card is in Development although the projection wrote Functional Testing last, so somebody moved it
        Assert.IsType<CardMoved>(Assert.Single(await rig.Poll("board.card", edit: e => { e["open"] = true; e["column"] = "Development"; })));
        // and a webhook after the window is news too
        rig.Now = T0.AddMinutes(5);
        Assert.IsType<CardMoved>(Assert.Single(await rig.Webhook("item.column_changed", edit: e => e["column"] = "Development")));
    }

    [Fact]
    public async Task AColumnOfNoStep_AParkedItem_OrAnItemWhoseCardWasNotProjectedYet_MakeNoCardInput()
    {
        var rig = new Rig(); await rig.OnDevelop();
        Assert.Empty(await rig.Webhook("item.column_changed", edit: e => e["column"] = "Todo"));
        Assert.Empty(await rig.Webhook("item.column_changed", edit: e => e["column"] = "Done"));
        Assert.Empty(await rig.Webhook("item.column_changed"));   // (no column in the event)
        var fresh = new Rig(); await fresh.Start();   // (the projection has applied nothing yet)
        Assert.Empty(await fresh.Webhook("item.column_changed", edit: e => e["column"] = "Technical Design"));
        await rig.Svc.Handle(new ParkWorkItem(Item, 0, "park-1", "runtime-failed"));
        Assert.Empty(await rig.Webhook("item.column_changed", edit: e => e["column"] = "Technical Design"));
    }

    // ---- a hold label ----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AHoldLabelOnTheIssue_ParksTheItem_AndItsRemovalLiftsThatPark()
    {
        var rig = new Rig(); await rig.Start();
        rig.Reader.Labels = ["factory worker", "hold:owner"];
        var hold = Assert.IsType<HoldObserved>(Assert.Single(await rig.Webhook("item.labeled")));
        Assert.Equal((Item, "hold:owner"), (hold.Item, hold.Hold));
        await rig.Svc.Handle(hold);
        Assert.Equal((true, "hold: hold:owner"), (rig.State.Parked, rig.State.ParkReason));
        // the labels are read again, and the hold is still there: nothing to say
        Assert.Empty(await rig.Webhook("item.labeled"));
        rig.Reader.Labels = ["factory worker"];
        var lifted = Assert.IsType<HoldObserved>(Assert.Single(await rig.Webhook("item.labeled")));
        Assert.Null(lifted.Hold);
        await rig.Svc.Handle(lifted);
        Assert.False(rig.State.Parked);
    }

    [Fact]
    public async Task AConfiguredHoldLabel_CountsToo_AndACardEventLooksAtTheLabelsAsWell()
    {
        var rig = new Rig(); await rig.Start();
        rig.Reader.Labels = ["Needs Human Review"];
        Assert.Equal("Needs Human Review", Assert.IsType<HoldObserved>(Assert.Single(await rig.Poll("board.card", edit: e => { e["open"] = true; e["column"] = "Technical Design"; }))).Hold);
    }

    [Fact]
    public async Task APark_ForAnotherReason_IsNeitherSetNorLiftedByALabel_AndNothingIsReadForIt()
    {
        var rig = new Rig(); await rig.Start();
        await rig.Svc.Handle(new ParkWorkItem(Item, 0, "park-1", "runtime-failed"));
        rig.Reader.Labels = ["hold:owner"];
        Assert.Empty(await rig.Webhook("item.labeled"));
        rig.Reader.Labels = [];
        Assert.Empty(await rig.Webhook("item.labeled"));
        Assert.Empty(rig.Reader.Read);
    }

    [Fact]
    public async Task ALabelReadThatFails_IsLeftForTheNextEvent_AndWithoutAReaderNothingIsRead()
    {
        var rig = new Rig(); await rig.Start();
        rig.Reader.Throw = new HttpRequestException("boom");
        Assert.Empty(await rig.Webhook("item.labeled"));
        var none = new Rig(withReader: false); await none.Start();
        Assert.Empty(await none.Webhook("item.labeled"));
    }
}

/// <summary>W9.10h: MergeFactsRead after the merge. A feature item on its Deliver step is told what its merge commit's checks and its pull request's files say; the saga settles a docs-only merge as docs.</summary>
public sealed class MergeFactsPortTests
{
    static readonly RepoId Home = new("acme", "orders");
    static readonly ItemId Item = new(Home, 7);
    const string Commit = "abcdef0123456789abcdef0123456789abcdef01";

    sealed class Reader : IItemIssueReader
    {
        public IReadOnlyList<CommitCheck>? Checks = [new CommitCheck("Build result", "in_progress", null)];
        public IReadOnlyList<string>? Files = ["docs/a.md"];
        public Exception? Throw;
        public int CheckReads, FileReads;
        public Task<ItemIssue?> ReadIssue(ItemId item, CancellationToken ct = default) => Task.FromResult<ItemIssue?>(null);
        public Task<IReadOnlyList<CommitCheck>?> ReadCheckRuns(RepoId repository, string commit, CancellationToken ct = default) { CheckReads++; return Throw is null ? Task.FromResult(Checks) : throw Throw; }
        public Task<IReadOnlyList<string>?> ReadPullRequestFiles(RepoId repository, int number, CancellationToken ct = default) { FileReads++; return Task.FromResult(Files); }
    }

    /// <summary>The docs-only port of these tests: a merge whose Build skipped its code checks is docs.</summary>
    sealed class SkippedBuild : IDocsOnlyPort
    {
        public bool ItemIsDocsOnly(bool designMarker) => designMarker;
        public bool? FilesAreDocumentation(IReadOnlyList<string> files) => true;
        public MergeDocsOnlyVerdict? MergeIsDocsOnly(IReadOnlyList<BuildCheck> checks, IReadOnlyList<string>? pullRequestFiles) =>
            checks.Count > 0 && checks.All(c => c.Status == "completed") && checks.Any(c => c.Conclusion == "skipped") ? new MergeDocsOnlyVerdict(true, "build") : null;
    }

    sealed class Rig
    {
        public readonly InMemoryWorkItemStore Store = new();
        public readonly WorkItemService Svc;
        public readonly Reader Github = new();
        public readonly MergeFactsPort Port;

        public Rig(bool withReader = true)
        {
            Svc = new WorkItemService(Store, new WorkItemSaga(new SkippedBuild()));
            Port = new MergeFactsPort(Store, withReader ? Github : null);
        }

        public WorkItemState State => Store.States[Item.ToString()];

        public async Task<WorkItemResult> Merged()
        {
            await Svc.Handle(new StartWorkItem(Item, "intake", "feature"));
            await Svc.Handle(new PullRequestChanged(Item, 0, "pr", 11, "aaa1111", ["src/a.cs"]));
            return await Svc.Handle(new MergeSeen(Item, $"merged|{Commit}", Commit, 11));
        }
    }

    [Fact]
    public async Task OnlyTheEntryOfTheDeliverStep_OfAFeatureItem_StartsTheReads()
    {
        var rig = new Rig();
        var start = await rig.Svc.Handle(new StartWorkItem(Item, "intake", "feature"));
        Assert.DoesNotContain(start.Decisions, MergeFactsPort.Wants);
        await rig.Svc.Handle(new PullRequestChanged(Item, 0, "pr", 11, "aaa1111", ["src/a.cs"]));
        var merged = await rig.Svc.Handle(new MergeSeen(Item, $"merged|{Commit}", Commit, 11));
        var entered = Assert.Single(merged.Decisions.Where(MergeFactsPort.Wants));
        var due = Assert.IsType<MergeFactsDue>(Assert.Single(MergeFactsHandler.Handle(entered)));
        Assert.Equal((Item, 1, 1), (due.Item, due.Generation, due.Attempt));
        Assert.Empty(MergeFactsHandler.Handle(start.Decisions[0]));
        Assert.False(MergeFactsPort.Wants(entered with { Plan = "docs" }));
    }

    [Fact]
    public async Task ABuildThatHasNotFinished_IsReadAgainAMinuteLater_AndSendsNothingYet()
    {
        var rig = new Rig(); await rig.Merged();
        var r = await rig.Port.Run(new MergeFactsDue(Item, 1, 1), default);
        Assert.Null(r.Facts);
        Assert.Equal(new MergeFactsDue(Item, 1, 2), r.Again);
        Assert.Equal(0, rig.Github.FileReads);
    }

    [Fact]
    public async Task ABuildThatSkippedItsCodeChecks_IsSentWithTheFiles_AndTheSagaSettlesTheItemAsDocs()
    {
        var rig = new Rig(); await rig.Merged();
        Assert.Equal(StepKind.Deliver, rig.State.Step);
        rig.Github.Checks = [new CommitCheck("Build result", "completed", "success"), new CommitCheck("Integration Build (SQL container)", "completed", "skipped")];
        var r = await rig.Port.Run(new MergeFactsDue(Item, 1, 3), default);
        Assert.Null(r.Again);
        var facts = Assert.IsType<MergeFactsRead>(r.Facts);
        Assert.Equal((Item, 1, $"facts|1|{Commit}|3"), (facts.Item, facts.Generation, facts.Key));
        Assert.Equal(["Build result", "Integration Build (SQL container)"], facts.Checks.Select(c => c.Name));
        Assert.Equal(["docs/a.md"], facts.PullRequestFiles);
        var settled = await rig.Svc.Handle(facts);
        Assert.Equal([WorkItemActions.PlanSwitched, WorkItemActions.Completed], settled.Decisions.Select(d => d.Action));
        Assert.Equal(("docs", WorkItemStatus.Completed), (rig.State.Plan, rig.State.Status));
    }

    [Fact]
    public async Task ABuildThatRanItsCodeChecks_IsSentToo_AndTheItemWaitsForItsRelease()
    {
        var rig = new Rig(); await rig.Merged();
        rig.Github.Checks = [new CommitCheck("Build result", "completed", "success")];
        var facts = Assert.IsType<MergeFactsRead>((await rig.Port.Run(new MergeFactsDue(Item, 1, 1), default)).Facts);
        await rig.Svc.Handle(facts);
        Assert.Equal(("feature", StepKind.Deliver, WorkItemStatus.Open), (rig.State.Plan, rig.State.Step, rig.State.Status));
    }

    [Fact]
    public async Task WhenTheAttemptsAreSpent_TheLastReadIsSentAsItIs()
    {
        var rig = new Rig(); await rig.Merged();
        var r = await rig.Port.Run(new MergeFactsDue(Item, 1, MergeFactsPort.MaxAttempts), default);
        Assert.Null(r.Again);
        Assert.Equal("in_progress", Assert.Single(Assert.IsType<MergeFactsRead>(r.Facts).Checks).Status);
    }

    [Fact]
    public async Task AGitHubThatDoesNotAnswer_IsABuildNotFinished_AndNoReaderMeansNoReads()
    {
        var rig = new Rig(); await rig.Merged();
        rig.Github.Throw = new HttpRequestException("boom");
        var r = await rig.Port.Run(new MergeFactsDue(Item, 1, 1), default);
        Assert.Null(r.Facts);
        Assert.Equal(new MergeFactsDue(Item, 1, 2), r.Again);
        var none = new Rig(withReader: false); await none.Merged();
        var nothing = await none.Port.Run(new MergeFactsDue(Item, 1, 1), default);
        Assert.Null(nothing.Facts); Assert.Null(nothing.Again);
    }

    [Fact]
    public async Task AnItemThatMovedOn_IsNotReadFor()
    {
        var rig = new Rig(); await rig.Merged();
        await rig.Svc.Handle(new DeliverySeen(Item, "delivered|prod|1.0.5", "prod", "1.0.5"));
        Assert.Equal(WorkItemStatus.Settling, rig.State.Status);
        var settling = await rig.Port.Run(new MergeFactsDue(Item, 1, 1), default);
        Assert.Null(settling.Facts); Assert.Null(settling.Again);
        var otherGeneration = await rig.Port.Run(new MergeFactsDue(Item, 9, 1), default);
        Assert.Null(otherGeneration.Facts); Assert.Null(otherGeneration.Again);
        Assert.Equal(0, rig.Github.CheckReads);
        var unknown = await new Rig().Port.Run(new MergeFactsDue(new ItemId(Home, 99), 1, 1), default);
        Assert.Null(unknown.Facts);
    }
}
