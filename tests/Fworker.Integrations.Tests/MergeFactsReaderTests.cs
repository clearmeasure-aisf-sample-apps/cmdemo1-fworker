using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Fworker.Contracts.Ids;
using Fworker.Integrations.Commands;
using Fworker.Integrations.GitHub;
using Xunit;

namespace Fworker.Integrations.Tests;

/// <summary>W5.0: the reads of the ItemMerged derivation, against a stub of GitHub.</summary>
public class MergeFactsReaderTests
{
    static readonly RepoId Repo = new("acme", "orders");

    sealed class Stub(Func<HttpRequestMessage, string, (HttpStatusCode, string)> answer) : HttpMessageHandler
    {
        public readonly List<string> Paths = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Paths.Add(request.RequestUri!.PathAndQuery);
            var (code, json) = answer(request, body);
            return new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    static HttpMergeFactsReader Reader(Stub s) => new(new HttpClient(s) { BaseAddress = new Uri("https://api.example/") }, new StaticCredentialSource([], new GitHubCredential("t", "x")), "factory-worker", "video");

    [Fact]
    public async Task ThePullRequest_IsReadByNumber_AndAMissingOneIsNull()
    {
        var stub = new Stub((r, _) => r.RequestUri!.AbsolutePath.EndsWith("/pulls/9")
            ? (HttpStatusCode.OK, """{"merged":true,"base":{"ref":"master"},"head":{"ref":"bot/7-x"},"title":"T (#9)","body":"Closes #7","merge_commit_sha":"ABCDEF0123456789ABCDEF0123456789ABCDEF01"}""")
            : (HttpStatusCode.NotFound, "{}"));
        var reader = Reader(stub);
        var pr = await reader.ReadPullRequest(Repo, 9);
        Assert.Equal((true, "master", "bot/7-x", "abcdef0123456789abcdef0123456789abcdef01"), (pr!.Merged, pr.BaseRef, pr.HeadRef, pr.MergeCommit));
        Assert.Null(await reader.ReadPullRequest(Repo, 10));
    }

    [Fact]
    public async Task TheMergedPullRequestOfAClosedIssue_IsTheNewestMergedOne()
    {
        var stub = new Stub((_, _) => (HttpStatusCode.OK, """
            {"data":{"repository":{"issue":{"closedByPullRequestsReferences":{"nodes":[
              {"number":4,"merged":true,"baseRefName":"master","headRefName":"a","title":"old","body":"","mergeCommit":{"oid":"1111111111111111111111111111111111111111"}},
              {"number":5,"merged":false,"baseRefName":"master","headRefName":"b","title":"closed unmerged","body":"","mergeCommit":null},
              {"number":9,"merged":true,"baseRefName":"master","headRefName":"bot/7-x","title":"new","body":"Closes #7","mergeCommit":{"oid":"2222222222222222222222222222222222222222"}}]}}}}}
            """));
        var pr = await Reader(stub).FindMergedPullRequest(new ItemId(Repo, 7));
        Assert.Equal((9, "2222222222222222222222222222222222222222"), (pr!.Number, pr.MergeCommit));
    }

    [Fact]
    public async Task TheMergedPullRequestOfAClosedIssue_IsTheOneThatMergedLast_AndCarriesWhenItMerged()
    {
        // the list is not in merge order: the latest mergedAt wins, and a pull request without a time keeps the list's order
        var stub = new Stub((_, body) =>
        {
            Assert.Contains("mergedAt", body);
            return (HttpStatusCode.OK, """
                {"data":{"repository":{"issue":{"closedByPullRequestsReferences":{"nodes":[
                  {"number":9,"merged":true,"mergedAt":"2026-10-29T10:00:00Z","baseRefName":"master","headRefName":"a","title":"newer","body":"","mergeCommit":{"oid":"2222222222222222222222222222222222222222"}},
                  {"number":4,"merged":true,"mergedAt":"2026-10-01T10:00:00Z","baseRefName":"master","headRefName":"b","title":"older","body":"","mergeCommit":{"oid":"1111111111111111111111111111111111111111"}}]}}}}}
                """);
        });
        var pr = await Reader(stub).FindMergedPullRequest(new ItemId(Repo, 7));
        Assert.Equal((9, new DateTimeOffset(2026, 10, 29, 10, 0, 0, TimeSpan.Zero)), (pr!.Number, pr.MergedAt));
        var pulls = new Stub((_, _) => (HttpStatusCode.OK, """{"merged":true,"merged_at":"2026-10-29T10:00:00Z","base":{"ref":"master"},"head":{"ref":"x"},"title":"T","body":"","merge_commit_sha":"abc"}"""));
        Assert.Equal(new DateTimeOffset(2026, 10, 29, 10, 0, 0, TimeSpan.Zero), (await Reader(pulls).ReadPullRequest(Repo, 9))!.MergedAt);
    }

    [Fact]
    public async Task TheItem_CarriesLabelsBody_AndTheVerdictAndMergeOfItsStateComment_AndTheDemoVideoWhenLabelled()
    {
        var state = new JsonObject { ["verdict"] = new JsonObject { ["passed"] = true, ["pr"] = 9 }, ["merge"] = new JsonObject { ["pr"] = 9, ["by"] = "gateway" } };
        var comment = ItemStateComment.Marker("factory-worker") + "\nstate\n\n```json\n" + state.ToJsonString() + "\n```\n";
        var stub = new Stub((r, _) =>
        {
            var p = r.RequestUri!.AbsolutePath;
            if (p.EndsWith("/issues/7")) return (HttpStatusCode.OK, new JsonObject { ["body"] = "Depends on #5", ["labels"] = new JsonArray(new JsonObject { ["name"] = "Auto Merge" }, new JsonObject { ["name"] = "video" }) }.ToJsonString());
            if (p.EndsWith("/issues/7/comments")) return (HttpStatusCode.OK, new JsonArray(new JsonObject { ["body"] = "hello" }, new JsonObject { ["body"] = comment }).ToJsonString());
            if (p.EndsWith("/releases/tags/demo-7")) return (HttpStatusCode.OK, """{"assets":[{"name":"demo-7.mp4"}]}""");
            return (HttpStatusCode.NotFound, "{}");
        });
        var f = await Reader(stub).ReadItem(new ItemId(Repo, 7));
        Assert.True(f!.IsFactoryItem);
        Assert.Equal(["Auto Merge", "video"], f.Labels);
        Assert.Equal("Depends on #5", f.Body);
        Assert.Equal((true, 9, 9, "gateway", true), (f.VerdictPassed, f.VerdictPr, f.MergePr, f.MergeBy, f.DemoVideoPublished));
    }

    [Fact]
    public async Task AnIssueWithoutTheStateComment_IsNoFactoryItem_AndAPullRequestOrMissingIssueIsNull()
    {
        var stub = new Stub((r, _) =>
        {
            var p = r.RequestUri!.AbsolutePath;
            if (p.EndsWith("/issues/8")) return (HttpStatusCode.OK, """{"body":"","labels":[]}""");
            if (p.EndsWith("/issues/8/comments")) return (HttpStatusCode.OK, "[]");
            if (p.EndsWith("/issues/9")) return (HttpStatusCode.OK, """{"body":"","labels":[],"pull_request":{}}""");
            return (HttpStatusCode.NotFound, "{}");
        });
        var reader = Reader(stub);
        Assert.False((await reader.ReadItem(new ItemId(Repo, 8)))!.IsFactoryItem);
        Assert.Null(await reader.ReadItem(new ItemId(Repo, 9)));
        Assert.Null(await reader.ReadItem(new ItemId(Repo, 10)));
        Assert.DoesNotContain(stub.Paths, p => p.Contains("releases"));   // no video label, no release read
    }

    // ---- W9.10h: the reads of the saga's producers (IItemIssueReader) ----------------------------------------------------------------------------

    [Fact]
    public async Task TheIssue_CarriesItsStateAndLabels_WithoutReadingComments_AndAPullRequestOrAMissingIssueIsNull()
    {
        var stub = new Stub((r, _) =>
        {
            var p = r.RequestUri!.AbsolutePath;
            if (p.EndsWith("/issues/8")) return (HttpStatusCode.OK, """{"state":"closed","labels":[{"name":"factory worker"},{"name":"hold:owner"}]}""");
            if (p.EndsWith("/issues/9")) return (HttpStatusCode.OK, """{"state":"open","labels":[]}""");
            if (p.EndsWith("/issues/10")) return (HttpStatusCode.OK, """{"state":"open","labels":[],"pull_request":{}}""");
            return (HttpStatusCode.NotFound, "{}");
        });
        var reader = Reader(stub);
        var closed = await reader.ReadIssue(new ItemId(Repo, 8));
        Assert.True(closed!.Closed); Assert.Equal(["factory worker", "hold:owner"], closed.Labels);
        Assert.False((await reader.ReadIssue(new ItemId(Repo, 9)))!.Closed);
        Assert.Null(await reader.ReadIssue(new ItemId(Repo, 10)));
        Assert.Null(await reader.ReadIssue(new ItemId(Repo, 11)));
        Assert.DoesNotContain(stub.Paths, p => p.Contains("comments"));
    }

    [Fact]
    public async Task TheCheckRunsOfACommit_AreReadWithTheirStatusAndConclusion_AndAnUnknownCommitIsNull()
    {
        var stub = new Stub((r, _) => r.RequestUri!.AbsolutePath.EndsWith("/commits/abc/check-runs")
            ? (HttpStatusCode.OK, """{"total_count":2,"check_runs":[{"name":"Build result","status":"completed","conclusion":"success"},{"name":"Integration Build (SQL container)","status":"completed","conclusion":"skipped"},{"name":"Pending","status":"in_progress","conclusion":null}]}""")
            : (HttpStatusCode.NotFound, "{}"));
        var reader = Reader(stub);
        var checks = await reader.ReadCheckRuns(Repo, "abc");
        Assert.Equal([("Build result", "completed", "success"), ("Integration Build (SQL container)", "completed", "skipped"), ("Pending", "in_progress", null)], checks!.Select(c => (c.Name, c.Status, c.Conclusion)));
        Assert.Null(await reader.ReadCheckRuns(Repo, "def"));
    }

    [Fact]
    public async Task TheFilesOfAPullRequest_AreReadPageByPage_AndAMissingPullRequestIsNull()
    {
        var stub = new Stub((r, _) =>
        {
            var q = r.RequestUri!.Query;
            if (!r.RequestUri.AbsolutePath.EndsWith("/pulls/9/files")) return (HttpStatusCode.NotFound, "{}");
            return q.Contains("&page=1") ? (HttpStatusCode.OK, "[" + string.Join(",", Enumerable.Range(0, 100).Select(i => $"{{\"filename\":\"docs/f{i}.md\"}}")) + "]") : (HttpStatusCode.OK, """[{"filename":"src/last.cs"}]""");
        });
        var reader = Reader(stub);
        var files = await reader.ReadPullRequestFiles(Repo, 9);
        Assert.Equal((101, "docs/f0.md", "src/last.cs"), (files!.Count, files[0], files[^1]));
        Assert.Null(await reader.ReadPullRequestFiles(Repo, 10));
    }

    [Fact]
    public async Task AServerError_Throws_SoTheInboxRetries()
    {
        var stub = new Stub((_, _) => (HttpStatusCode.BadGateway, "{}"));
        await Assert.ThrowsAsync<MergeFactsException>(() => Reader(stub).ReadPullRequest(Repo, 9));
    }
}
