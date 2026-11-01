using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Fworker.Contracts.Commands;
using Fworker.Contracts.Ids;
using Fworker.Integrations.Commands;
using Fworker.Integrations.GitHub;
using Xunit;

namespace Fworker.Integrations.Tests;

/// <summary>The close of a finished item's issue (v1's settle: PATCH state closed, state_reason completed, no comment), against a fake GitHub.</summary>
public class CloseIssueTests
{
    static readonly RepoId Repo = new("acme", "orders");
    static readonly ItemId Item = new(Repo, 556);
    const string IssuePath = "repos/acme/orders/issues/556";

    sealed record Sent(string Method, string Path, string Body);

    sealed class Fake(Func<HttpRequestMessage, (HttpStatusCode, string)> answer) : HttpMessageHandler
    {
        public readonly List<Sent> Requests = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new(request.Method.Method, request.RequestUri!.AbsolutePath.TrimStart('/'), body));
            var (code, json) = answer(request);
            return new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    static (CloseIssueHandler, InMemoryIdempotencyLedger) Make(Fake fake)
    {
        var ledger = new InMemoryIdempotencyLedger();
        var factory = new GitHubClientFactory(new HttpClient(fake) { BaseAddress = new Uri("https://api.example/") }, new StaticCredentialSource([], new GitHubCredential("acme-app", "x")), new GitHubBudget());
        return (new CloseIssueHandler(ledger, factory), ledger);
    }

    static CloseWorkItemIssue Cmd(int generation = 1) => new(CloseWorkItemIssue.KeyFor(Item, generation), Item, generation);

    // an open issue becomes closed once PATCHed
    static Fake OpenIssue() { var state = "open"; return new Fake(r => { if (r.Method == HttpMethod.Patch) { state = "closed"; return (HttpStatusCode.OK, """{"state":"closed"}"""); } return (HttpStatusCode.OK, $$"""{"state":"{{state}}"}"""); }); }

    [Fact]
    public async Task AnOpenIssue_IsClosedAsCompleted_WithNothingElseSent()
    {
        var fake = OpenIssue();
        var (handler, _) = Make(fake);
        var r = await handler.Process(Cmd());
        Assert.True(r.Ok); Assert.Equal("closed", r.Execution);
        var patch = Assert.Single(fake.Requests, q => q.Method == "PATCH");
        Assert.Equal(IssuePath, patch.Path);
        // the body is v1's (RuleKit.CloseIssue, Close-Issue): state and state_reason, and no comment is written
        var body = JsonNode.Parse(patch.Body)!.AsObject();
        Assert.Equal(["state", "state_reason"], body.Select(p => p.Key).ToArray());
        Assert.Equal(("closed", "completed"), (body["state"]!.ToString(), body["state_reason"]!.ToString()));
        Assert.DoesNotContain(fake.Requests, q => q.Path.EndsWith("/comments", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnIssueThatIsClosedAlready_IsLeftAlone()
    {
        var fake = new Fake(_ => (HttpStatusCode.OK, """{"state":"closed","state_reason":"completed"}"""));
        var (handler, _) = Make(fake);
        var r = await handler.Process(Cmd());
        Assert.True(r.Ok); Assert.Equal("already-closed", r.Execution);
        Assert.DoesNotContain(fake.Requests, q => q.Method == "PATCH");
    }

    [Fact]
    public async Task ARepeatedKey_IsAnsweredFromTheLedger_WithoutACall()
    {
        var fake = OpenIssue();
        var (handler, _) = Make(fake);
        await handler.Process(Cmd());
        var calls = fake.Requests.Count;
        Assert.True((await handler.Process(Cmd())).Ok);
        Assert.Equal(calls, fake.Requests.Count);
        // the next generation of the item (reopened, finished again) is another key: it reads the issue and closes it if it is open again
        Assert.NotEqual(Cmd(1).IdempotencyKey, Cmd(2).IdempotencyKey);
    }

    [Fact]
    public async Task AFaultOfGitHub_IsTransient_AndTheSameKeyClosesTheIssueTheNextTime()
    {
        var down = true;
        var state = "open";
        var fake = new Fake(r =>
        {
            if (down) return (HttpStatusCode.BadGateway, """{"message":"bad gateway"}""");
            if (r.Method == HttpMethod.Patch) { state = "closed"; return (HttpStatusCode.OK, """{"state":"closed"}"""); }
            return (HttpStatusCode.OK, $$"""{"state":"{{state}}"}""");
        });
        var (handler, _) = Make(fake);
        var first = await handler.Process(Cmd());
        Assert.False(first.Ok); Assert.Equal(FailureKind.Transient, first.Failure);
        down = false;
        var second = await handler.Process(Cmd());
        Assert.True(second.Ok); Assert.Equal("closed", second.Execution);
    }

    [Fact]
    public async Task ARefusalForGood_IsPermanent_AndRecorded()
    {
        var fake = new Fake(r => r.Method == HttpMethod.Patch ? (HttpStatusCode.Forbidden, """{"message":"Resource not accessible by integration"}""") : (HttpStatusCode.OK, """{"state":"open"}"""));
        var (handler, _) = Make(fake);
        var r = await handler.Process(Cmd());
        Assert.False(r.Ok); Assert.Equal(FailureKind.Permanent, r.Failure);
        var calls = fake.Requests.Count;
        Assert.False((await handler.Process(Cmd())).Ok);   // (the ledger holds the refusal: a retry says the same)
        Assert.Equal(calls, fake.Requests.Count);
    }
}
