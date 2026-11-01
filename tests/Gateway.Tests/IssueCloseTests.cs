using System.Net;
using System.Text;
using Fworker.Contracts.Commands;
using Fworker.Contracts.Ids;
using Fworker.Integrations.Commands;
using Fworker.Integrations.GitHub;
using Fworker.Workflows.WorkItem;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;

namespace Gateway.Tests;

/// <summary>The close of a finished item's issue in the gateway: the Completed decision becomes one command, a fault is scheduled again for ever, a refusal for good is logged and left.</summary>
public class IssueCloseTests
{
    static readonly ItemId Item = new(new RepoId("acme", "orders"), 556);

    sealed class Fake(Func<HttpRequestMessage, (HttpStatusCode, string)> answer) : HttpMessageHandler
    {
        public int Patches;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Patch) Patches++;
            var (code, json) = answer(request);
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    static CloseIssueHandler Handler(Fake fake) =>
        new(new InMemoryIdempotencyLedger(), new GitHubClientFactory(new HttpClient(fake) { BaseAddress = new Uri("https://api.example/") }, new StaticCredentialSource([], new GitHubCredential("acme-app", "x")), new Fworker.Integrations.GitHub.GitHubBudget()));

    static WorkItemDecided Decided(string action, string plan = "docs") =>
        new(Item, 1, 9, action, plan, null, null, ["why"], DateTimeOffset.UnixEpoch);

    static DocsProjection Projection(string plan = "docs") =>
        new(() => null, DocsPlanSettingsReader.From(null) with { CarriedPlans = [plan, WorkPlans.Docs], DoneColumn = "Done" });

    [Fact]
    public void TheCompletionOfADocsItem_BecomesTheCloseCommand_AndNoOtherDecisionDoes()
    {
        var made = IssueCloseHandler.Handle(Decided(WorkItemActions.Completed), Projection());
        var close = Assert.IsType<CloseWorkItemIssue>(Assert.Single(made));
        Assert.Equal((Item, 1, CloseWorkItemIssue.KeyFor(Item, 1)), (close.Item, close.Generation, close.IdempotencyKey));
        foreach (var other in new[] { WorkItemActions.Merged, WorkItemActions.Started, WorkItemActions.HandedBack, WorkItemActions.Parked })
            Assert.Empty(IssueCloseHandler.Handle(Decided(other), Projection()));
    }

    [Fact]
    public async Task AnOpenIssue_IsClosed_ThroughTheService()
    {
        var state = "open";
        var fake = new Fake(r => { if (r.Method == HttpMethod.Patch) { state = "closed"; return (HttpStatusCode.OK, """{"state":"closed"}"""); } return (HttpStatusCode.OK, $$"""{"state":"{{state}}"}"""); });
        await new IssueCloseService(Handler(fake)).Run(new CloseWorkItemIssue(CloseWorkItemIssue.KeyFor(Item, 1), Item, 1), default);
        Assert.Equal(1, fake.Patches);
        Assert.Equal("closed", state);
    }

    [Fact]
    public async Task AFaultOfGitHub_OrAGatewayWithoutACredential_ThrowsSoThatTheMessageIsScheduledAgain()
    {
        var c = new CloseWorkItemIssue(CloseWorkItemIssue.KeyFor(Item, 1), Item, 1);
        await Assert.ThrowsAsync<IssueCloseRetryException>(() => new IssueCloseService(null).Run(c, default));
        var down = new Fake(_ => (HttpStatusCode.ServiceUnavailable, """{"message":"down"}"""));
        await Assert.ThrowsAsync<IssueCloseRetryException>(() => new IssueCloseService(Handler(down)).Run(c, default));
        Assert.Equal(0, down.Patches);
    }

    [Fact]
    public async Task ARefusalForGood_IsLoggedAndLeft()
    {
        var log = new List<string>();
        var refused = new Fake(r => r.Method == HttpMethod.Patch ? (HttpStatusCode.Forbidden, """{"message":"no"}""") : (HttpStatusCode.OK, """{"state":"open"}"""));
        await new IssueCloseService(Handler(refused), log.Add).Run(new CloseWorkItemIssue(CloseWorkItemIssue.KeyFor(Item, 1), Item, 1), default);
        Assert.Contains("not closed", Assert.Single(log));
    }

    [Fact]
    public async Task TheMessageIsScheduledAgain_ThenEveryHour_NeverTheDeadLetters()
    {
        using var host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder().UseWolverine(o =>
        {
            o.Discovery.DisableConventionalDiscovery().IncludeType(typeof(IssueCloseHandler));
            o.Durability.Mode = DurabilityMode.MediatorOnly;
        }).Build();
        await host.StartAsync();
        var graph = host.Services.GetRequiredService<Wolverine.Runtime.Handlers.HandlerGraph>();
        var chain = graph.Chains.Single(c => c.MessageType == typeof(CloseWorkItemIssue));
        Assert.Empty(chain.Failures);
        IssueCloseHandler.Configure(chain);   // (Wolverine applies it while it compiles a chain, which a host that has handled nothing has not done)
        var failure = new IssueCloseRetryException(new CloseWorkItemIssue(CloseWorkItemIssue.KeyFor(Item, 1), Item, 1), "down");
        var text = chain.Failures.Single(r => r.Match.Matches(failure)).ToString();
        Assert.Contains("attempt 1: Schedule Retry in 30 seconds; attempt 2: Schedule Retry in 120 seconds; attempt 3: Schedule Retry in 600 seconds; attempt 4: Schedule Retry in 3600 seconds", text);
        Assert.Contains("repeat: Schedule Retry in 3600 seconds", text);
        Assert.DoesNotContain("error queue", text);
    }
}
