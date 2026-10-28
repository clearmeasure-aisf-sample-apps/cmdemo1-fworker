using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Integrations.Correlation;
using Xunit;

namespace Fworker.Integrations.Tests;

/// <summary>W4.1: the keys, the deduplication in two layers and the correlation writers, on the in-memory stores (the SQL ones run in the Workflow tests against a server).</summary>
public sealed class IngressInboxTests
{
    static readonly RepoId Home = new("acme", "orders");

    static string Ev(string name, int item, int pr = 0, string sha = "", string delivery = "d-1") =>
        new JsonObject { ["provider"] = "github", ["event"] = name, ["item"] = item, ["ref"] = delivery, ["pr"] = pr > 0 ? pr : null, ["sha"] = sha == "" ? null : sha }.ToJsonString();

    static (IngressInboxProcessor P, InMemoryCorrelationMap Map) Make() { var map = new InMemoryCorrelationMap(); return (new IngressInboxProcessor(new InMemoryIngressDedup(), map, Home), map); }

    [Fact]
    public void TheSameDeliveryTwice_IsTakenInOnce()
    {
        var (p, _) = Make();
        Assert.True(p.Process(new("webhook", Ev("pr.opened", 7, 9, "abc")), Guid.NewGuid()).Accepted);
        var again = p.Process(new("webhook", Ev("pr.opened", 7, 9, "abc")), Guid.NewGuid());
        Assert.False(again.Accepted);
        Assert.Equal("duplicate-delivery", again.Reason);
    }

    [Fact]
    public void ThePollSeeingTheSameFactAfterTheWebhook_IsADuplicateByBusinessKey()
    {
        var (p, _) = Make();
        Assert.True(p.Process(new("webhook", Ev("pr.head_changed", 7, 9, "ABC", "delivery-1")), Guid.NewGuid()).Accepted);
        var poll = p.Process(new("poll", Ev("pr.head_changed", 7, 9, "abc", "poll:board-1")), Guid.NewGuid());
        Assert.False(poll.Accepted);
        Assert.Equal("duplicate-fact", poll.Reason);
    }

    [Fact]
    public void ANewCommitOfTheSamePullRequest_IsANewFact()
    {
        var (p, _) = Make();
        Assert.True(p.Process(new("webhook", Ev("pr.head_changed", 7, 9, "aaa", "d-1")), Guid.NewGuid()).Accepted);
        Assert.True(p.Process(new("webhook", Ev("pr.head_changed", 7, 9, "bbb", "d-2")), Guid.NewGuid()).Accepted);
    }

    [Fact]
    public void AnEventWithoutPullRequestOrCommit_IsDeduplicatedByDeliveryOnly()
    {
        var (p, _) = Make();
        Assert.Null(IngressKeys.Business(JsonNode.Parse(Ev("item.complete", 7))!.AsObject()));
        Assert.True(p.Process(new("webhook", Ev("item.complete", 7, delivery: "d-1")), Guid.NewGuid()).Accepted);
        Assert.True(p.Process(new("webhook", Ev("item.complete", 7, delivery: "d-2")), Guid.NewGuid()).Accepted);
    }

    [Fact]
    public void ARedeliveryOfTheSameMessage_IsAcceptedAgain_AndWritesNoSecondCorrelation()
    {
        var (p, map) = Make(); var id = Guid.NewGuid();
        var first = p.Process(new("webhook", Ev("pr.opened", 7, 9, "abc")), id);
        var second = p.Process(new("webhook", Ev("pr.opened", 7, 9, "abc")), id);
        Assert.True(first.Accepted && second.Accepted);
        Assert.Equal((2, 0), (first.CorrelationsAdded, second.CorrelationsAdded));
        Assert.Equal("acme/orders#7", map.Resolve("pr", "acme/orders!9"));
        Assert.Equal("acme/orders#7", map.Resolve("commit", "acme/orders@abc"));
    }

    [Fact]
    public void ACorrelationIsNeverOverwritten()
    {
        var map = new InMemoryCorrelationMap();
        Assert.True(map.TryAdd(new("pr", "acme/orders!9", "acme/orders#7", 0, "verb:x")));
        Assert.False(map.TryAdd(new("pr", "acme/orders!9", "acme/orders#8", 0, "webhook")));
        Assert.Equal("acme/orders#7", map.Resolve("pr", "acme/orders!9"));
    }

    [Fact]
    public void VerbAndRuntimeWriters_NameTheirThings()
    {
        var v = CorrelationWriters.FromVerb("complete", 7, new JsonObject { ["pr"] = 12, ["branch"] = "acme/7-fix" }, Home);
        Assert.Equal(["pr|acme/orders!12", "branch|acme/orders:acme/7-fix"], v.Select(r => r.Kind + "|" + r.Key).ToArray());
        Assert.All(v, r => Assert.Equal(("acme/orders#7", "verb:complete"), (r.ItemKey, r.Source)));
        Assert.Empty(CorrelationWriters.FromVerb("complete", 0, new JsonObject { ["pr"] = 12 }, Home));
        Assert.Equal("agent", CorrelationWriters.FromRuntime("cursor", "bc-1", 7, Home).Single().Kind);
        Assert.Equal("task", CorrelationWriters.FromRuntime("copilot", "t-1", 7, Home).Single().Kind);
        Assert.Empty(CorrelationWriters.FromRuntime("copilot", "", 7, Home));
    }

    [Fact]
    public void AnEventWithoutAnItem_WritesNoCorrelation() =>
        Assert.Empty(CorrelationWriters.FromEvent(JsonNode.Parse(Ev("base.merged", 0, 9, "abc"))!.AsObject(), Home, "webhook"));
}
