using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Integrations.Correlation;
using Xunit;

namespace Fworker.Integrations.Tests;

/// <summary>W4.1 follow-up (#485): the business key must not swallow a CI re-run or an event without a commit.</summary>
public sealed class IngressBusinessKeyTests
{
    static JsonObject Build(string run, string attempt, string delivery, string conclusion = "failure") =>
        new() { ["provider"] = "github", ["event"] = "build.branch_completed", ["item"] = 7, ["sha"] = "ABC", ["conclusion"] = conclusion, ["workflow"] = "ci", ["run_id"] = run, ["run_attempt"] = attempt, ["ref"] = delivery };

    [Fact]
    public void AReRunOfTheSameCommit_IsANewFact_AndThePollOfTheSameRunIsADuplicate()
    {
        var p = new IngressInboxProcessor(new InMemoryIngressDedup(), new InMemoryCorrelationMap(), new RepoId("acme", "orders"));
        Assert.True(p.Process(new("webhook", Build("1", "1", "d-1").ToJsonString()), Guid.NewGuid()).Accepted);
        Assert.True(p.Process(new("webhook", Build("1", "2", "d-2").ToJsonString()), Guid.NewGuid()).Accepted);
        Assert.True(p.Process(new("webhook", Build("2", "1", "d-3", "success").ToJsonString()), Guid.NewGuid()).Accepted);
        Assert.Equal("duplicate-fact", p.Process(new("poll", Build("1", "2", "poll:x").ToJsonString()), Guid.NewGuid()).Reason);
    }

    [Fact]
    public void ACiEventWithoutItsRun_AndAPullRequestWithoutACommit_HaveNoFactKey()
    {
        var b = Build("1", "1", "d"); b.Remove("run_id");
        Assert.Null(IngressKeys.Business(b));
        Assert.Null(IngressKeys.Business(new JsonObject { ["event"] = "pr.drafted", ["item"] = 7, ["pr"] = 9 }));
        Assert.NotNull(IngressKeys.Business(Build("1", "1", "d")));
    }

    [Fact]
    public void TwoReviewsOfOneCommit_ByDifferentPeopleOrStates_AreTwoFacts()
    {
        static JsonObject Rv(string login, string state) => new() { ["event"] = "pr.reviewed", ["item"] = 7, ["pr"] = 9, ["sha"] = "abc", ["login"] = login, ["state"] = state };
        Assert.NotEqual(IngressKeys.Business(Rv("a", "APPROVED")), IngressKeys.Business(Rv("b", "APPROVED")));
        Assert.NotEqual(IngressKeys.Business(Rv("a", "APPROVED")), IngressKeys.Business(Rv("a", "CHANGES_REQUESTED")));
    }
}
