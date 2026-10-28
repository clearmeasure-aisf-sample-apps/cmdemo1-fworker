using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Domain;
using Fworker.Workflows.Delivery;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>W5.0: ItemMerged derived from the merge webhook and from the board reconciler's poll, on fakes (no database, no GitHub).</summary>
public class ItemMergedDeriverTests
{
    internal static readonly RepoId Home = new("acme", "orders");
    internal const string Sha = "0123456789abcdef0123456789abcdef01234567";
    static readonly ItemGateConfig Gates = new("Auto Merge", "video", ["hold:owner"]);

    internal sealed class FakeReader : IMergeFactsReader
    {
        public readonly Dictionary<int, MergedPullRequest> Pulls = [];
        public readonly Dictionary<int, ItemMergeFacts> Items = [];
        public int Reads;
        public Task<MergedPullRequest?> ReadPullRequest(RepoId repository, int number, CancellationToken ct = default) { Reads++; return Task.FromResult(Pulls.GetValueOrDefault(number)); }
        public Task<MergedPullRequest?> FindMergedPullRequest(ItemId item, CancellationToken ct = default) { Reads++; return Task.FromResult(Pulls.Values.FirstOrDefault(p => p.Merged && ContentGate.CandidateItems(p.Number, p.HeadRef, p.Title, p.Body).Contains(item.Number))); }
        public Task<ItemMergeFacts?> ReadItem(ItemId item, CancellationToken ct = default) { Reads++; return Task.FromResult(Items.GetValueOrDefault(item.Number)); }
    }

    internal static FakeReader Reader()
    {
        var r = new FakeReader();
        r.Pulls[9] = new MergedPullRequest(Home, 9, true, "master", "bot/7-add-thing", "Add the thing (#9)", "Closes #7", Sha);
        r.Items[7] = Facts(7, labels: ["Auto Merge"], body: "Depends on #5 and other/repo#12, #7", passed: true);
        return r;
    }

    internal static ItemMergeFacts Facts(int n, string[]? labels = null, string? body = null, bool passed = false, bool factory = true, string? by = "gateway", bool video = false)
        => new(new ItemId(Home, n), factory, labels ?? [], body, passed, passed ? 9 : null, passed ? 9 : null, by, video);

    internal static string BaseMerged(string delivery = "d-1", string sha = Sha) => new JsonObject { ["provider"] = "github", ["event"] = "base.merged", ["ref"] = delivery, ["pr"] = 9, ["sha"] = sha }.ToJsonString();
    internal static string PrMerged(string delivery = "d-2") => new JsonObject { ["provider"] = "github", ["event"] = "pr.merged", ["item"] = 7, ["ref"] = delivery, ["pr"] = 9, ["sha"] = "headsha" }.ToJsonString();
    internal static string ClosedCard() => new JsonObject { ["provider"] = "github", ["event"] = "board.card", ["item"] = 7, ["repository"] = "acme/orders", ["column"] = "Done", ["open"] = false, ["ref"] = "poll:board:acme/orders#7:Done:closed" }.ToJsonString();

    static ItemMergedDeriver Deriver(FakeReader r, InMemoryMergedItemStore s) => new(r, s, Home, () => "master", Gates, () => new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task TheMergeWebhook_GivesItemMerged_WithCommitGatesAndQualifiedDependencies()
    {
        var d = Deriver(Reader(), new InMemoryMergedItemStore());
        var r = await d.Derive("webhook", BaseMerged(), Guid.NewGuid(), default);
        var m = Assert.Single(r.Merged);
        Assert.Equal(("acme/orders#7", Sha, 9, "webhook"), (m.Item.ToString(), m.Commit, m.PullRequest, m.Source));
        Assert.Equal(["acme/orders#5", "other/repo#12"], m.Dependencies.Select(x => x.ToString()));   // its own #7 is no dependency; the foreign one stays qualified
        Assert.True(m.Gates.AutoMerge);
        Assert.True(m.Gates.PassMerged);
        Assert.Equal("", m.Gates.Hold);
        Assert.True(m.Gates.Qualifies);
    }

    [Fact]
    public async Task TheGates_AreReadFromTheItem_AndTheGapsAreV1sTexts()
    {
        var reader = Reader();
        reader.Items[7] = Facts(7, labels: ["hold:owner", "video"], passed: false);
        var m = Assert.Single((await Deriver(reader, new InMemoryMergedItemStore()).Derive("webhook", BaseMerged(), Guid.NewGuid(), default)).Merged);
        Assert.False(m.Gates.AutoMerge);
        Assert.Equal("hold:owner", m.Gates.Hold);
        Assert.False(m.Gates.PassMerged);
        Assert.True(m.Gates.VideoRequired);
        Assert.False(m.Gates.Qualifies);
        Assert.Contains("#7 has no 'Auto Merge' label", m.Gates.Gaps);
        Assert.Contains("#7 carries the 'hold:owner' label", m.Gates.Gaps);
        Assert.Contains("#7 has no pull request merged after a Functional Testing PASS verdict", m.Gates.Gaps);
        Assert.Contains(m.Gates.Gaps, g => g.Contains("no demo video published"));
    }

    [Fact]
    public async Task TheReconcilersClosedCard_GivesTheSameMessage()
    {
        var r = await Deriver(Reader(), new InMemoryMergedItemStore()).Derive("poll", ClosedCard(), Guid.NewGuid(), default);
        var m = Assert.Single(r.Merged);
        Assert.Equal(("acme/orders#7", Sha, "poll"), (m.Item.ToString(), m.Commit, m.Source));
    }

    [Fact]
    public async Task WebhookAndReconciler_AreOneMessage_WhicheverComesFirst()
    {
        foreach (var (first, second) in new[] { (("webhook", BaseMerged()), ("poll", ClosedCard())), (("poll", ClosedCard()), ("webhook", BaseMerged())), (("webhook", BaseMerged()), ("webhook", PrMerged())) })
        {
            var store = new InMemoryMergedItemStore(); var d = Deriver(Reader(), store);
            Assert.Single((await d.Derive(first.Item1, first.Item2, Guid.NewGuid(), default)).Merged);
            var again = await d.Derive(second.Item1, second.Item2, Guid.NewGuid(), default);
            Assert.Empty(again.Merged);
            Assert.Single(store.Rows);
        }
    }

    [Fact]
    public async Task ARedeliveryOfTheSameMessage_DerivesAgain_ButAnotherMessageDoesNot()
    {
        var store = new InMemoryMergedItemStore(); var d = Deriver(Reader(), store); var id = Guid.NewGuid();
        Assert.Single((await d.Derive("webhook", BaseMerged(), id, default)).Merged);
        Assert.Single((await d.Derive("webhook", BaseMerged(), id, default)).Merged);   // the first attempt crashed before the message went out
        Assert.Empty((await d.Derive("webhook", BaseMerged("d-9"), Guid.NewGuid(), default)).Merged);
    }

    [Fact]
    public async Task ACommitAnotherMessageTook_NeedsNoGitHubRead()
    {
        var reader = Reader(); var store = new InMemoryMergedItemStore(); var d = Deriver(reader, store);
        await d.Derive("webhook", BaseMerged(), Guid.NewGuid(), default);
        reader.Reads = 0;
        var r = await d.Derive("webhook", BaseMerged("d-2"), Guid.NewGuid(), default);
        Assert.Equal("duplicate-commit", r.Reason);
        Assert.Equal(0, reader.Reads);
    }

    [Fact]
    public async Task OneMergeCommit_ClosingTwoItems_GivesOneMessageEach_AndANonFactoryNumberIsLeftOut()
    {
        var reader = Reader();
        reader.Pulls[9] = reader.Pulls[9] with { Body = "Closes #7\nFixes #8\nCloses #99" };
        reader.Items[8] = Facts(8);
        reader.Items[99] = Facts(99, factory: false);   // an issue without the factory's state comment
        var r = await Deriver(reader, new InMemoryMergedItemStore()).Derive("webhook", BaseMerged(), Guid.NewGuid(), default);
        Assert.Equal(["acme/orders#7", "acme/orders#8"], r.Merged.Select(m => m.Item.ToString()).Order());
    }

    [Theory]
    [InlineData("not-merged")]
    [InlineData("other-branch")]
    [InlineData("no-commit")]
    [InlineData("unknown-pr")]
    public async Task WhatIsNotAMergeIntoTheDefaultBranch_GivesNothing(string how)
    {
        var reader = Reader();
        if (how == "not-merged") reader.Pulls[9] = reader.Pulls[9] with { Merged = false };
        if (how == "other-branch") reader.Pulls[9] = reader.Pulls[9] with { BaseRef = "release" };
        if (how == "no-commit") reader.Pulls[9] = reader.Pulls[9] with { MergeCommit = null };
        if (how == "unknown-pr") reader.Pulls.Remove(9);
        var store = new InMemoryMergedItemStore();
        Assert.Empty((await Deriver(reader, store).Derive("webhook", PrMerged(), Guid.NewGuid(), default)).Merged);
        Assert.Empty(store.Rows);
    }

    [Theory]
    [InlineData("pr.opened")]
    [InlineData("build.master_completed")]
    public async Task OtherEvents_AreNotMerges(string name)
    {
        var reader = Reader();
        var r = await Deriver(reader, new InMemoryMergedItemStore()).Derive("webhook", new JsonObject { ["event"] = name, ["item"] = 7, ["pr"] = 9, ["sha"] = Sha }.ToJsonString(), Guid.NewGuid(), default);
        Assert.Equal(("not-a-merge", 0), (r.Reason, reader.Reads));
        // a card that is still open is not a merge either
        var open = new JsonObject { ["event"] = "board.card", ["item"] = 7, ["open"] = true }.ToJsonString();
        Assert.Equal("not-a-merge", (await Deriver(reader, new InMemoryMergedItemStore()).Derive("poll", open, Guid.NewGuid(), default)).Reason);
    }

    [Theory]
    [InlineData("Depends on #5", "acme/orders#5")]
    [InlineData("depends on: #5, #6 and #5", "acme/orders#5,acme/orders#6")]
    [InlineData("Depends on shop#4 and Other/Repo#8", "acme/shop#4,other/repo#8")]
    [InlineData("Depends on Acme/Orders#7 and #3", "acme/orders#3")]
    [InlineData("see #5\nDepends on #2\nnot a dependency #6", "acme/orders#2")]
    [InlineData("nothing here", "")]
    public void TheDependencyParser_IsV1sWithQualifiedForeignRefs(string body, string expected)
        => Assert.Equal(expected, string.Join(",", ItemMergedDependencies.Parse(new ItemId(Home, 7), body).Select(d => d.ToString())));
}
