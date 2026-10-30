using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Fworker.Contracts.Commands;
using Fworker.Integrations.Commands;
using Fworker.Integrations.Octopus;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>W9.4: the executor of PromoteRelease and AnswerSignOff (Verbs.Promote.ps1: the create call of Invoke-AutoPromoteInner, Resolve-SignOff, Invoke-OctoPost).</summary>
public class OctopusPromoterTests
{
    sealed class Octo : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, string Path, string Body, string? Key)> Calls = [];
        public readonly Dictionary<string, (int Status, string Body)> Answers = new(StringComparer.Ordinal);
        public (int Status, string Body) Default = (404, "{}");

        public Octo On(HttpMethod method, string path, int status, JsonNode? body = null) { Answers[$"{method.Method} {path}"] = (status, body?.ToJsonString() ?? "{}"); return this; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery.TrimStart('/');
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.Method, path, body, request.Headers.TryGetValues("X-Octopus-ApiKey", out var k) ? k.First() : null));
            var (status, text) = Answers.TryGetValue($"{request.Method.Method} {path}", out var a) ? a : Default;
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }

    static OctopusPromoter Make(Octo octo, string? key = "k-runbook", Func<bool>? may = null) =>
        new(new HttpClient(octo) { BaseAddress = new Uri("https://octopus.example/") }, new OctopusPromoteSettings("Spaces-1", new Dictionary<string, string> { ["uat"] = "UAT", ["prod"] = "Prod" }, () => key, may));

    static readonly PromoteRelease Promote = new("PromoteRelease|orders|1.0.5|uat", "orders", "orders-ui", "1.0.5", "uat");

    static Octo Known(string[]? existingEnvs = null)
    {
        var o = new Octo()
            .On(HttpMethod.Get, "api/Spaces-1/projects/orders-ui", 200, new JsonObject { ["Id"] = "Projects-1" })
            .On(HttpMethod.Get, "api/Spaces-1/projects/Projects-1/releases/1.0.5", 200, new JsonObject { ["Id"] = "Releases-5" })
            .On(HttpMethod.Get, "api/Spaces-1/environments?take=1000", 200, new JsonObject { ["Items"] = new JsonArray(new JsonObject { ["Id"] = "E1", ["Name"] = "tdd" }, new JsonObject { ["Id"] = "E2", ["Name"] = "UAT" }) })
            .On(HttpMethod.Get, "api/Spaces-1/releases/Releases-5/deployments?take=100", 200,
                new JsonObject { ["Items"] = new JsonArray((existingEnvs ?? ["E1"]).Select(e => (JsonNode?)new JsonObject { ["EnvironmentId"] = e, ["TaskId"] = "ServerTasks-" + e }).ToArray()) });
        return o;
    }

    [Fact]
    public async Task Promote_CreatesTheDeploymentWithTheExecutionsApiRoute_AfterReadingWhatExists()
    {
        var octo = Known().On(HttpMethod.Post, "api/Spaces-1/deployments/create/untenanted/v1", 200, new JsonObject { ["DeploymentServerTasks"] = new JsonArray(new JsonObject { ["ServerTaskId"] = "ServerTasks-77", ["DeploymentId"] = "Deployments-9" }) });
        var r = await Make(octo).Promote(Promote, CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Equal("ServerTasks-77", r.Execution);
        var post = octo.Calls.Single(c => c.Method == HttpMethod.Post);
        Assert.Equal("k-runbook", post.Key);
        var body = JsonNode.Parse(post.Body)!;
        Assert.Equal(("Spaces-1", "orders-ui", "1.0.5", "UAT"), (body["SpaceIdOrName"]!.ToString(), body["ProjectName"]!.ToString(), body["ReleaseVersion"]!.ToString(), body["EnvironmentNames"]![0]!.ToString()));
        Assert.True(octo.Calls.Take(octo.Calls.Count - 1).All(c => c.Method == HttpMethod.Get));   // everything is read before the one write
    }

    [Fact]
    public async Task Promote_ADeploymentOfThatReleaseToThatEnvironmentIsNeverDoubled()
    {
        var octo = Known(["E1", "E2"]);
        var r = await Make(octo).Promote(Promote, CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Equal("ServerTasks-E2", r.Execution);
        Assert.DoesNotContain(octo.Calls, c => c.Method == HttpMethod.Post);
    }

    [Theory]
    [InlineData(403, FailureKind.Permanent, "Octopus refused to create the deployment (403, missing permission DeploymentCreate); the owner grants it to the gateway's Octopus account")]
    [InlineData(500, FailureKind.Transient, "Octopus answered 500 to POST /api/Spaces-1/deployments/create/untenanted/v1: the release is broken; the gateway tries again")]
    [InlineData(429, FailureKind.RateLimited, "Octopus answered 429 to POST /api/Spaces-1/deployments/create/untenanted/v1: the release is broken; the gateway tries again")]
    public async Task Promote_ARefusedCreateIsClassified(int status, FailureKind kind, string error)
    {
        var octo = Known().On(HttpMethod.Post, "api/Spaces-1/deployments/create/untenanted/v1", status, new JsonObject { ["ErrorMessage"] = status == 403 ? "Missing permission: DeploymentCreate" : "the release is broken" });
        var r = await Make(octo).Promote(Promote, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal((kind, error), (r.Failure, r.Error));
    }

    [Fact]
    public async Task Promote_ACreateAnswerWithoutATaskIsNotASuccess()
    {
        var octo = Known().On(HttpMethod.Post, "api/Spaces-1/deployments/create/untenanted/v1", 200, new JsonObject());
        var r = await Make(octo).Promote(Promote, CancellationToken.None);
        Assert.Equal(FailureKind.Transient, r.Failure);
    }

    [Fact]
    public async Task Promote_WithoutAKeyAProjectOrAReleaseOrALease_NothingIsCreated()
    {
        var octo = Known();
        var none = await Make(octo, key: null).Promote(Promote, CancellationToken.None);
        Assert.Equal((FailureKind.Permanent, "neither OCTOPUS_RUNBOOK_API_KEY nor OCTOPUS_API_KEY is set on the gateway"), (none.Failure, none.Error));
        Assert.Empty(octo.Calls);
        var noProject = new Octo();
        Assert.Equal("project 'orders-ui' not found in Spaces-1", (await Make(noProject).Promote(Promote, CancellationToken.None)).Error);
        var noRelease = Known(); noRelease.Answers.Remove("GET api/Spaces-1/projects/Projects-1/releases/1.0.5");
        var missing = await Make(noRelease).Promote(Promote, CancellationToken.None);
        Assert.Equal((FailureKind.Permanent, "release 1.0.5 of orders-ui is not in Octopus"), (missing.Failure, missing.Error));
        var lost = Known();
        var l = await Make(lost, may: () => false).Promote(Promote, CancellationToken.None);
        Assert.Equal(FailureKind.Transient, l.Failure);
        Assert.DoesNotContain(lost.Calls, c => c.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Promote_ANetworkFaultIsTransient()
    {
        var r = await new OctopusPromoter(new HttpClient(new Faulty()) { BaseAddress = new Uri("https://octopus.example/") },
            new OctopusPromoteSettings("Spaces-1", new Dictionary<string, string>(), () => "k")).Promote(Promote, CancellationToken.None);
        Assert.Equal(FailureKind.Transient, r.Failure);
    }

    sealed class Faulty : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new HttpRequestException("connection refused");
    }

    static readonly AnswerSignOff Answer = new("AnswerSignOff|orders|Interruptions-3", "orders", "Interruptions-3", "ServerTasks-77", true, "Signed off by the factory worker gateway: every gate of the release holds (docs/design/auto-promote.md).");

    static Octo Pending(bool isPending = true) =>
        new Octo().On(HttpMethod.Get, "api/Spaces-1/interruptions?regarding=ServerTasks-77&pendingOnly=true&take=20", 200,
            new JsonObject { ["Items"] = new JsonArray(new JsonObject { ["Id"] = "Interruptions-3", ["IsPending"] = isPending }, new JsonObject { ["Id"] = "Interruptions-4", ["IsPending"] = true }) });

    [Theory]
    [InlineData(true, "Proceed")]
    [InlineData(false, "Abort")]
    public async Task Answer_TakesResponsibilityByPut_ThenSubmitsTheResult(bool proceed, string result)
    {
        var octo = Pending()
            .On(HttpMethod.Put, "api/Spaces-1/interruptions/Interruptions-3/responsible", 200)
            .On(HttpMethod.Post, "api/Spaces-1/interruptions/Interruptions-3/submit", 200);
        var r = await Make(octo).Answer(Answer with { Proceed = proceed, Notes = "why" }, CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Equal(["GET", "PUT", "POST"], octo.Calls.Select(c => c.Method.Method).ToArray());   // only the interruption of the command is answered, never the other pending one
        var submit = JsonNode.Parse(octo.Calls[2].Body)!;
        Assert.Equal((result, "why", null), (submit["Result"]!.ToString(), submit["Notes"]!.ToString(), submit["Instructions"]?.ToString()));
    }

    [Fact]
    public async Task Answer_AnInterruptionThatIsNoLongerPending_IsAnsweredAlready()
    {
        var octo = Pending(isPending: false);
        Assert.True((await Make(octo).Answer(Answer, CancellationToken.None)).Ok);
        Assert.Single(octo.Calls);
        var gone = new Octo().On(HttpMethod.Get, "api/Spaces-1/interruptions?regarding=ServerTasks-77&pendingOnly=true&take=20", 200, new JsonObject { ["Items"] = new JsonArray() });
        Assert.True((await Make(gone).Answer(Answer, CancellationToken.None)).Ok);
    }

    [Fact]
    public async Task Answer_AMissingPermissionIsNamedAndPermanent_AnythingElseIsTried()
    {
        var octo = Pending().On(HttpMethod.Put, "api/Spaces-1/interruptions/Interruptions-3/responsible", 403, new JsonObject { ["ErrorMessage"] = "Missing permission: InterruptionViewSubmitResponsible" });
        var r = await Make(octo).Answer(Answer, CancellationToken.None);
        Assert.Equal((FailureKind.Permanent, "Octopus refused to take responsibility for the Sign-off of task ServerTasks-77 (403, missing permission InterruptionViewSubmitResponsible); the owner grants it to the gateway's Octopus account"), (r.Failure, r.Error));
        var octo2 = Pending().On(HttpMethod.Put, "api/Spaces-1/interruptions/Interruptions-3/responsible", 200).On(HttpMethod.Post, "api/Spaces-1/interruptions/Interruptions-3/submit", 503);
        Assert.Equal(FailureKind.Transient, (await Make(octo2).Answer(Answer, CancellationToken.None)).Failure);
        var lost = Pending();
        Assert.Equal(FailureKind.Transient, (await Make(lost, may: () => false).Answer(Answer, CancellationToken.None)).Failure);
        Assert.Single(lost.Calls);   // (the list was read, nothing was written)
    }

    [Fact]
    public async Task TheHandlers_RememberASettledCommand_SoAReplayCallsOctopusNoMore()
    {
        var octo = Known().On(HttpMethod.Post, "api/Spaces-1/deployments/create/untenanted/v1", 200, new JsonObject { ["DeploymentServerTasks"] = new JsonArray(new JsonObject { ["ServerTaskId"] = "ServerTasks-77" }) });
        var handler = new PromoteReleaseHandler(new InMemoryIdempotencyLedger(), Make(octo));
        Assert.True((await handler.Handle(Promote)).Ok);
        var calls = octo.Calls.Count;
        var again = await handler.Handle(Promote);
        Assert.True(again.Ok);
        Assert.Equal("ServerTasks-77", again.Execution);
        Assert.Equal(calls, octo.Calls.Count);
        // a transient failure is not remembered: it runs again
        var flaky = Known().On(HttpMethod.Post, "api/Spaces-1/deployments/create/untenanted/v1", 500);
        var h2 = new PromoteReleaseHandler(new InMemoryIdempotencyLedger(), Make(flaky));
        Assert.False((await h2.Handle(Promote)).Ok);
        var first = flaky.Calls.Count;
        Assert.False((await h2.Handle(Promote)).Ok);
        Assert.True(flaky.Calls.Count > first);
    }
}
