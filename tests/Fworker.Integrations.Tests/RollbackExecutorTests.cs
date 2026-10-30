using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Fworker.Contracts.Commands;
using Fworker.Integrations.Commands;
using Fworker.Integrations.Octopus;
using Xunit;

namespace Fworker.Integrations.Tests;

/// <summary>
/// W8.3: the executor of RollbackRelease, the one deployment the gateway creates in tdd. It refuses every other environment, reads before it writes, creates no deployment when tdd's newest is of the release already
/// (a replay never deploys twice), answers a replay of a settled key from the ledger, and writes only as the leader.
/// </summary>
public class RollbackExecutorTests
{
    sealed class Octo : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, string Path, string Body, string? Key)> Calls = [];
        readonly Dictionary<string, (int Status, string Body)> answers = new(StringComparer.Ordinal);
        public int Posts => Calls.Count(c => c.Method == HttpMethod.Post);

        public Octo On(HttpMethod method, string path, int status, JsonNode? body = null) { answers[$"{method.Method} {path}"] = (status, body?.ToJsonString() ?? "{}"); return this; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery.TrimStart('/');
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.Method, path, body, request.Headers.TryGetValues("X-Octopus-ApiKey", out var k) ? k.First() : null));
            var (status, text) = answers.TryGetValue($"{request.Method.Method} {path}", out var a) ? a : (404, "{}");
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }

    const string NewestPath = "api/Spaces-1/deployments?projects=Projects-1&environments=E1&take=1";
    const string CreatePath = "api/Spaces-1/deployments/create/untenanted/v1";
    static readonly RollbackRelease Command = new("RollbackRelease|orders|inc-1|1.0.1|tdd", "orders", "orders-ui", "1.0.1", "tdd", "inc-1");

    /// <summary>Octopus where 1.0.1 is Releases-1 and tdd's newest deployment is of <paramref name="newestRelease"/>.</summary>
    static Octo Known(string newestRelease = "Releases-2")
        => new Octo()
            .On(HttpMethod.Get, "api/Spaces-1/projects/orders-ui", 200, new JsonObject { ["Id"] = "Projects-1" })
            .On(HttpMethod.Get, "api/Spaces-1/projects/Projects-1/releases/1.0.1", 200, new JsonObject { ["Id"] = "Releases-1" })
            .On(HttpMethod.Get, "api/Spaces-1/environments?take=1000", 200, new JsonObject { ["Items"] = new JsonArray(new JsonObject { ["Id"] = "E1", ["Name"] = "Tdd" }, new JsonObject { ["Id"] = "E2", ["Name"] = "UAT" }) })
            .On(HttpMethod.Get, NewestPath, 200, new JsonObject { ["Items"] = new JsonArray(new JsonObject { ["ReleaseId"] = newestRelease, ["TaskId"] = "ServerTasks-9" }) });

    static RollbackExecutor Make(Octo octo, string? key = "k-runbook", Func<bool>? may = null)
        => new(new HttpClient(octo) { BaseAddress = new Uri("https://octopus.example/") }, new RollbackSettings("Spaces-1", new Dictionary<string, string> { ["tdd"] = "Tdd", ["uat"] = "UAT" }, () => key, may));

    static Octo WithCreate(Octo o) => o.On(HttpMethod.Post, CreatePath, 200, new JsonObject { ["DeploymentServerTasks"] = new JsonArray(new JsonObject { ["ServerTaskId"] = "ServerTasks-77", ["DeploymentId"] = "Deployments-9" }) });

    [Fact]
    public async Task ItDeploysTheLastGoodRelease_AfterReadingEverything_EvenThoughAnEarlierDeploymentOfItExists()
    {
        var octo = WithCreate(Known());
        var r = await Make(octo).Rollback(Command, CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Equal("ServerTasks-77", r.Execution);
        var post = octo.Calls.Single(c => c.Method == HttpMethod.Post);
        Assert.Equal("k-runbook", post.Key);
        var body = JsonNode.Parse(post.Body)!;
        Assert.Equal(("Spaces-1", "orders-ui", "1.0.1", "Tdd"), (body["SpaceIdOrName"]!.ToString(), body["ProjectName"]!.ToString(), body["ReleaseVersion"]!.ToString(), body["EnvironmentNames"]![0]!.ToString()));
        Assert.True(octo.Calls.Take(octo.Calls.Count - 1).All(c => c.Method == HttpMethod.Get));   // everything is read before the one write
        Assert.Contains(octo.Calls, c => c.Path == NewestPath);
    }

    [Fact]
    public async Task WhenTddsNewestDeploymentIsOfTheReleaseAlready_NoSecondOneIsCreated()
    {
        var octo = Known("Releases-1");
        var r = await Make(octo).Rollback(Command, CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Equal("ServerTasks-9", r.Execution);
        Assert.Equal(0, octo.Posts);
    }

    [Theory]
    [InlineData("uat")]
    [InlineData("prod")]
    [InlineData("")]
    public async Task EveryEnvironmentButTddIsRefusedForGood_WithoutAnyCall(string environment)
    {
        var octo = WithCreate(Known());
        var r = await Make(octo).Rollback(Command with { Environment = environment }, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(FailureKind.Permanent, r.Failure);
        Assert.Contains("tdd only", r.Error);
        Assert.Empty(octo.Calls);
    }

    [Fact]
    public async Task OnlyTheLeaderWrites()
    {
        var octo = WithCreate(Known());
        var r = await Make(octo, may: () => false).Rollback(Command, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(FailureKind.Transient, r.Failure);
        Assert.Contains("leader lease", r.Error);
        Assert.Equal(0, octo.Posts);
    }

    [Fact]
    public async Task WithoutAKey_ItIsRefusedForGood()
    {
        var r = await Make(WithCreate(Known()), key: null).Rollback(Command, CancellationToken.None);
        Assert.Equal(FailureKind.Permanent, r.Failure);
        Assert.Contains("OCTOPUS_RUNBOOK_API_KEY", r.Error);
    }

    [Theory]
    [InlineData(403, FailureKind.Permanent)]
    [InlineData(400, FailureKind.Permanent)]
    [InlineData(422, FailureKind.Permanent)]
    [InlineData(500, FailureKind.Transient)]
    [InlineData(429, FailureKind.RateLimited)]
    public async Task ARefusedCreateIsClassified(int status, FailureKind kind)
    {
        var octo = Known().On(HttpMethod.Post, CreatePath, status, new JsonObject { ["ErrorMessage"] = status == 403 ? "Missing permission: DeploymentCreate" : "no" });
        var r = await Make(octo).Rollback(Command, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(kind, r.Failure);
        if (status == 403) Assert.Contains("missing permission DeploymentCreate", r.Error);
    }

    [Fact]
    public async Task AReleaseOrProjectOctopusDoesNotHave_IsRefusedForGood()
    {
        var noRelease = Known().On(HttpMethod.Get, "api/Spaces-1/projects/Projects-1/releases/1.0.1", 404);
        var r = await Make(noRelease).Rollback(Command, CancellationToken.None);
        Assert.Equal(FailureKind.Permanent, r.Failure);
        Assert.Contains("release 1.0.1 of orders-ui is not in Octopus", r.Error);
        var noProject = new Octo();
        r = await Make(noProject).Rollback(Command, CancellationToken.None);
        Assert.Equal(FailureKind.Permanent, r.Failure);
        Assert.Contains("project 'orders-ui' not found", r.Error);
    }

    [Fact]
    public async Task AnEnvironmentThatIsNotInTheSpace_IsRefusedForGood()
    {
        var octo = Known().On(HttpMethod.Get, "api/Spaces-1/environments?take=1000", 200, new JsonObject { ["Items"] = new JsonArray(new JsonObject { ["Id"] = "E2", ["Name"] = "UAT" }) });
        var r = await Make(octo).Rollback(Command, CancellationToken.None);
        Assert.Equal(FailureKind.Permanent, r.Failure);
        Assert.Contains("environment 'Tdd' not found", r.Error);
    }

    [Fact]
    public async Task TheHandlerAnswersAReplayOfASettledKeyFromTheLedger_SoTheDeploymentHappensOnce()
    {
        var octo = WithCreate(Known());
        var handler = new RollbackReleaseHandler(new InMemoryIdempotencyLedger(), Make(octo));
        Assert.True((await handler.Handle(Command)).Ok);
        var callsAfterFirst = octo.Calls.Count;
        var again = await handler.Handle(Command);
        Assert.True(again.Ok);
        Assert.Equal("ServerTasks-77", again.Execution);
        Assert.Equal(callsAfterFirst, octo.Calls.Count);   // nothing was asked of Octopus the second time
        Assert.Equal(1, octo.Posts);
    }

    [Fact]
    public async Task ATransientFailureIsNotRemembered_SoTheSameKeyRunsAgain()
    {
        var octo = Known().On(HttpMethod.Post, CreatePath, 500, new JsonObject { ["ErrorMessage"] = "busy" });
        var handler = new RollbackReleaseHandler(new InMemoryIdempotencyLedger(), Make(octo));
        Assert.Equal(FailureKind.Transient, (await handler.Handle(Command)).Failure);
        WithCreate(octo);
        Assert.True((await handler.Handle(Command)).Ok);
    }
}
