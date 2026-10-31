using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Integrations.Commands;
using Fworker.Integrations.GitHub;
using Fworker.Integrations.Octopus;
using Xunit;

namespace Fworker.Integrations.Tests;

/// <summary>W9.9b: what the adoption reads from GitHub and Octopus, against stubs: the item as v1 left it, a pull request's state, the merged pull requests, the commits past a commit, and the Sign-offs Octopus shows.</summary>
public class AdoptionReadsTests
{
    static readonly RepoId Repo = new("acme", "orders");
    const string StateBody = "<!-- factory-state:factory-worker v1 -->\nstate\n\n```json\n{\"item\":7,\"rev\":4,\"claim\":{\"holder\":\"w1\"}}\n```\n";

    sealed class Stub(Func<HttpRequestMessage, (HttpStatusCode, string)> answer) : HttpMessageHandler
    {
        public readonly List<string> Paths = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Paths.Add(request.RequestUri!.PathAndQuery);
            var (code, json) = answer(request);
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    static HttpMergeFactsReader Reader(Stub s) => new(new HttpClient(s) { BaseAddress = new Uri("https://api.example/") }, new StaticCredentialSource([], new GitHubCredential("t", "x")), "factory-worker", "video");

    [Fact]
    public async Task TheItemAsV1LeftIt_HasItsIssueAndTheStateCommentItself()
    {
        var stub = new Stub(r => r.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/issues/7") => (HttpStatusCode.OK, """{"state":"open","body":"The body","created_at":"2026-10-20T10:00:00Z","labels":[{"name":"factory worker"},{"name":"Auto Merge"}]}"""),
            var p when p.EndsWith("/issues/7/comments") => (HttpStatusCode.OK, new JsonArray(new JsonObject { ["body"] = "hello" }, new JsonObject { ["body"] = StateBody }).ToJsonString()),
            _ => (HttpStatusCode.NotFound, "{}"),
        });
        var record = (await Reader(stub).ReadV1Record(new ItemId(Repo, 7)))!;
        Assert.Equal((true, "The body", new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero), false), (record.IssueOpen, record.Body, record.CreatedAt, record.DemoVideoPublished));
        Assert.Equal(["factory worker", "Auto Merge"], record.Labels);
        Assert.Equal((4, "w1"), ((int)record.State!["rev"]!, (string)record.State["claim"]!["holder"]!));
    }

    [Fact]
    public async Task AClosedIssueIsNotOpen_AnIssueWithoutAStateCommentHasNone_AndAMissingOneIsNull()
    {
        var stub = new Stub(r => r.RequestUri!.AbsolutePath.EndsWith("/issues/8") ? (HttpStatusCode.OK, """{"state":"closed","labels":[]}""") : r.RequestUri.AbsolutePath.EndsWith("/issues/8/comments") ? (HttpStatusCode.OK, "[]") : (HttpStatusCode.NotFound, "{}"));
        var closed = (await Reader(stub).ReadV1Record(new ItemId(Repo, 8)))!;
        Assert.Equal((false, null), (closed.IssueOpen, closed.State));
        Assert.Null(await Reader(stub).ReadV1Record(new ItemId(Repo, 9)));
        // a pull request is no item
        Assert.Null(await Reader(new Stub(_ => (HttpStatusCode.OK, """{"state":"open","pull_request":{},"labels":[]}"""))).ReadV1Record(new ItemId(Repo, 10)));
    }

    [Fact]
    public async Task APullRequestIsOpenClosedOrMerged_WithItsHead()
    {
        var reader = Reader(new Stub(r => r.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/pulls/1") => (HttpStatusCode.OK, """{"state":"open","merged":false,"head":{"sha":"AAAA"}}"""),
            var p when p.EndsWith("/pulls/2") => (HttpStatusCode.OK, """{"state":"closed","merged":false,"head":{"sha":"bbbb"}}"""),
            var p when p.EndsWith("/pulls/3") => (HttpStatusCode.OK, """{"state":"closed","merged":true,"head":{"sha":"cccc"},"merge_commit_sha":"DDDD"}"""),
            _ => (HttpStatusCode.NotFound, "{}"),
        }));
        Assert.Equal(("open", "aaaa", null), ((await reader.ReadPullRequestFacts(Repo, 1))!.State, (await reader.ReadPullRequestFacts(Repo, 1))!.Head, (await reader.ReadPullRequestFacts(Repo, 1))!.MergeCommit));
        Assert.Equal("closed", (await reader.ReadPullRequestFacts(Repo, 2))!.State);
        var merged = (await reader.ReadPullRequestFacts(Repo, 3))!;
        Assert.Equal(("merged", "dddd"), (merged.State, merged.MergeCommit));
        Assert.Null(await reader.ReadPullRequestFacts(Repo, 4));
    }

    [Fact]
    public async Task TheMergedPullRequests_AreTheNewestMergesOfTheBranch_NewestFirst_AndTheClosedUnmergedAreLeftOut()
    {
        var stub = new Stub(_ => (HttpStatusCode.OK, """
            [{"number":5,"merged_at":null,"base":{"ref":"master"},"head":{"ref":"a"},"title":"closed","merge_commit_sha":null},
             {"number":6,"merged_at":"2026-10-30T10:00:00Z","base":{"ref":"master"},"head":{"ref":"b"},"title":"older","body":"","merge_commit_sha":"1111111111111111111111111111111111111111"},
             {"number":7,"merged_at":"2026-10-30T12:00:00Z","base":{"ref":"master"},"head":{"ref":"c"},"title":"newer","body":"","merge_commit_sha":"2222222222222222222222222222222222222222"}]
            """));
        var pulls = await Reader(stub).ListMergedPulls(Repo, "master", 2);
        Assert.Equal([7, 6], pulls.Select(p => p.Number));
        Assert.Contains("base=master", stub.Paths[0]);
        Assert.Equal("2222222222222222222222222222222222222222", pulls[0].MergeCommit);
    }

    [Fact]
    public async Task TheCommitsPastACommit_AreTheComparesCommits_AndNullWhereGitHubCannotCompare()
    {
        var ok = Reader(new Stub(_ => (HttpStatusCode.OK, """{"commits":[{"sha":"AAAA"},{"sha":"bbbb"}]}""")));
        Assert.Equal(["aaaa", "bbbb"], (await ok.CommitsPast(Repo, "base", "master"))!.Order());
        Assert.Null(await Reader(new Stub(_ => (HttpStatusCode.NotFound, "{}"))).CommitsPast(Repo, "base", "master"));
    }

    // ---- Octopus ----------------------------------------------------------------------------------------------------------------------------------------------------------

    sealed class FakeOctopus : IOctopusReader
    {
        public readonly Dictionary<string, JsonNode?> Answers = [];
        public readonly List<string> Paths = [];
        public Task<JsonNode?> Get(string path, CancellationToken ct = default) { Paths.Add(path); return Task.FromResult(Answers.GetValueOrDefault(path)); }
        public void Set(string path, string json) => Answers[path] = JsonNode.Parse(json);
    }

    const string Sp = "api/Spaces-1";

    static FakeOctopus Server(string state = "Executing", bool pending = true)
    {
        var o = new FakeOctopus();
        o.Set($"{Sp}/projects/ui", """{"Id":"Projects-1"}""");
        o.Set($"{Sp}/environments?take=1000", """{"Items":[{"Id":"Environments-1","Name":"UAT"}]}""");
        o.Set($"{Sp}/deployments?projects=Projects-1&take=30", """{"Items":[{"Id":"Deployments-9","TaskId":"ServerTasks-9","ReleaseId":"Releases-5","EnvironmentId":"Environments-1"}]}""");
        o.Set($"{Sp}/tasks/ServerTasks-9", $$"""{"State":"{{state}}"}""");
        o.Set($"{Sp}/interruptions?regarding=ServerTasks-9&pendingOnly=true&take=20", pending ? """{"Items":[{"Id":"Interruptions-3","IsPending":true,"Created":"2026-10-31T08:00:00Z"}]}""" : """{"Items":[]}""");
        o.Set($"{Sp}/releases/Releases-5", """{"Version":"1.0.5"}""");
        return o;
    }

    [Fact]
    public async Task TheSignOffOfAnExecutingDeployment_IsReadWithItsEnvironmentAndTheReleasesVersion()
    {
        var found = Assert.Single(await new PendingSignOffReader(Server(), "Spaces-1", "ui").Read());
        Assert.Equal(("uat", "Interruptions-3", "Deployments-9", "ServerTasks-9", "1.0.5", new DateTimeOffset(2026, 10, 31, 8, 0, 0, TimeSpan.Zero)), (found.Environment, found.InterruptionId, found.DeploymentId, found.TaskId, found.Version, found.Created!.Value));
    }

    [Fact]
    public async Task ADeploymentThatIsNotExecuting_OrHasNoPendingInterruption_ShowsNoSignOff_AndNothingButGetsAreMade()
    {
        Assert.Empty(await new PendingSignOffReader(Server(state: "Success"), "Spaces-1", "ui").Read());
        var quiet = Server(pending: false);
        Assert.Empty(await new PendingSignOffReader(quiet, "Spaces-1", "ui").Read());
        Assert.DoesNotContain(quiet.Paths, p => p.Contains("releases/"));   // (the release is read only for a deployment that waits)
        Assert.Empty(await new PendingSignOffReader(new FakeOctopus(), "Spaces-1", "ui").Read());   // (no such project)
    }
}
