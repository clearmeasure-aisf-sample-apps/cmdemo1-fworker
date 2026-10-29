using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Fworker.Contracts.Commands;
using Fworker.Contracts.Ids;
using Fworker.Integrations.Commands;
using Fworker.Integrations.GitHub;
using Xunit;

namespace Fworker.Integrations.Tests;

/// <summary>W6.2: the direct merge (the strategy of Verbs.Merge.ps1) against a fake GitHub, checking what is sent as well as what is answered.</summary>
public class MergeTests
{
    static readonly RepoId Repo = new("acme", "orders");
    const string Pass = "1111111111111111111111111111111111111111";
    const string Update = "2222222222222222222222222222222222222222";
    const string BaseHead = "3333333333333333333333333333333333333333";
    const string MergeSha = "4444444444444444444444444444444444444444";

    sealed record Sent(string Method, string Path, string Body);

    // routes by "METHOD path"; a route holds a queue of answers and the last one repeats
    sealed class Fake : HttpMessageHandler
    {
        public readonly List<Sent> Requests = [];
        readonly Dictionary<string, Queue<(HttpStatusCode, string, (string, string)[])>> _routes = new();
        public Fake On(string route, HttpStatusCode code, string json, params (string Name, string Value)[] headers) { if (!_routes.TryGetValue(route, out var q)) _routes[route] = q = new(); q.Enqueue((code, json, headers)); return this; }
        public IEnumerable<Sent> To(string method, string pathEnd) => Requests.Where(r => r.Method == method && r.Path.EndsWith(pathEnd, StringComparison.Ordinal));
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            Requests.Add(new(request.Method.Method, path + request.RequestUri.Query, body));
            if (!_routes.TryGetValue($"{request.Method.Method} {path}", out var q) || q.Count == 0) return Respond(HttpStatusCode.NotFound, """{"message":"Not Found"}""");
            var (code, json, headers) = q.Count > 1 ? q.Dequeue() : q.Peek();
            var resp = Respond(code, json);
            foreach (var (name, value) in headers) resp.Headers.TryAddWithoutValidation(name, value);
            return resp;
        }
        static HttpResponseMessage Respond(HttpStatusCode code, string json) => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    static string Pr(string head = Pass, string mergeable = "true", string state = "open", bool merged = false, string mergeCommit = "null", bool draft = false, string title = "x") =>
        $$$"""{"state":"{{{state}}}","draft":{{{(draft ? "true" : "false")}}},"title":"{{{title}}}","merged":{{{(merged ? "true" : "false")}}},"merge_commit_sha":{{{mergeCommit}}},"mergeable":{{{mergeable}}},"mergeable_state":"clean","base":{"ref":"master"},"head":{"sha":"{{{head}}}","ref":"bot/7-x"}}""";

    const string PrPath = "repos/acme/orders/pulls/9";
    const string Merge = "PUT " + PrPath + "/merge";
    const string UpdateBranch = "PUT " + PrPath + "/update-branch";

    static (MergeDirect, List<TimeSpan>) Direct(Fake fake, MergeTiming? timing = null)
    {
        var pauses = new List<TimeSpan>();
        var factory = new GitHubClientFactory(new HttpClient(fake) { BaseAddress = new Uri("https://api.example/") },
            new StaticCredentialSource([], new GitHubCredential("acme-app", "x")), new GitHubBudget());
        return (new MergeDirect(factory, timing, (d, _) => { pauses.Add(d); return Task.CompletedTask; }), pauses);
    }

    static MergePullRequest Cmd(string? key = null, int updates = 0, Func<MergePullRequest, MergePullRequest>? with = null)
    {
        var c = new MergePullRequest(key ?? MergePullRequest.KeyFor(Repo, 9, Pass, updates), Repo, 9, Pass, UpdatesSoFar: updates);
        return with is null ? c : with(c);
    }

    static string UpdateCommit(string head = Update, string first = Pass, string second = BaseHead, bool verified = true, string committer = "GitHub") =>
        $$$"""{"sha":"{{{head}}}","commit":{"verification":{"verified":{{{(verified ? "true" : "false")}}},"reason":"valid"},"committer":{"name":"{{{committer}}}","email":"noreply@github.com"}},"parents":[{"sha":"{{{first}}}"},{"sha":"{{{second}}}"}]}""";

    [Fact]
    public async Task Merge_HappyPath_SquashesWithThePassHead_AndNothingElse()
    {
        var fake = new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr()).On(Merge, HttpStatusCode.OK, $$"""{"sha":"{{MergeSha}}","merged":true,"message":"Pull Request successfully merged"}""");
        var (direct, _) = Direct(fake);
        var o = await direct.Run(Cmd());
        Assert.Equal(MergeStatus.Merged, o.Status); Assert.Equal(MergeSha, o.Sha);
        // the body is what Verbs.Merge.ps1 sends: @{ merge_method = 'squash'; sha = the PASS head }
        var sent = Assert.Single(fake.To("PUT", "/merge"));
        var body = JsonNode.Parse(sent.Body)!.AsObject();
        Assert.Equal(["merge_method", "sha"], body.Select(p => p.Key).ToArray());
        Assert.Equal(("squash", Pass), (body["merge_method"]!.ToString(), body["sha"]!.ToString()));
        Assert.Empty(fake.To("PUT", "/update-branch"));
    }

    [Fact]
    public async Task Merge_MergeableNull_IsReadAgainAtTheFixedInterval_ThenMerges()
    {
        var fake = new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr(mergeable: "null")).On("GET " + PrPath, HttpStatusCode.OK, Pr(mergeable: "null")).On("GET " + PrPath, HttpStatusCode.OK, Pr())
            .On(Merge, HttpStatusCode.OK, $$"""{"sha":"{{MergeSha}}","merged":true}""");
        var (direct, pauses) = Direct(fake);
        var o = await direct.Run(Cmd());
        Assert.Equal(MergeStatus.Merged, o.Status);
        Assert.Equal([TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4)], pauses);
        Assert.Equal(3, fake.To("GET", "/pulls/9").Count());
    }

    [Fact]
    public async Task Merge_MergeableStaysNull_IsBlocked_AndWaitsNoLongerThanTheGatewayForm12Seconds()
    {
        var fake = new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr(mergeable: "null"));
        var (direct, pauses) = Direct(fake);
        var o = await direct.Run(Cmd());
        Assert.Equal((MergeStatus.Blocked, "mergeability-unknown"), (o.Status, o.Reason));
        // the script under -Gateway: Math.Min(mergeabilityWaitSeconds, 12) at the fixed mergeabilityPollSeconds (4), not the 90 seconds of a session's own call
        Assert.Equal(TimeSpan.FromSeconds(12), pauses.Aggregate(TimeSpan.Zero, (a, b) => a + b));
        Assert.Equal([TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4)], pauses);
        Assert.Equal(TimeSpan.FromSeconds(12), MergeTiming.Default.MergeabilityWait);
        Assert.Empty(fake.To("PUT", "/merge"));
    }

    [Fact]
    public async Task Merge_NotMergeableOnGitHub_Fails_AndDoesNotCallMerge()
    {
        var fake = new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr(mergeable: "false"));
        var o = await Direct(fake).Item1.Run(Cmd());
        Assert.Equal((MergeStatus.Failed, "not-mergeable", FailureKind.Permanent), (o.Status, o.Reason, o.Kind));
        Assert.Empty(fake.To("PUT", "/merge"));
    }

    [Fact]
    public async Task Merge_BehindBranch_IsUpdatedWithTheExpectedHead_ThenBlockedWithTheVerifiedNewHead()
    {
        var fake = new Fake()
            .On("GET " + PrPath, HttpStatusCode.OK, Pr()).On("GET " + PrPath, HttpStatusCode.OK, Pr()).On("GET " + PrPath, HttpStatusCode.OK, Pr(head: Update))
            .On($"GET repos/acme/orders/compare/master...{Pass}", HttpStatusCode.OK, """{"ahead_by":1,"behind_by":3}""")
            .On(UpdateBranch, HttpStatusCode.Accepted, """{"message":"Updating pull request branch.","url":"https://x"}""")
            .On($"GET repos/acme/orders/commits/{Update}", HttpStatusCode.OK, UpdateCommit())
            .On($"GET repos/acme/orders/compare/master...{BaseHead}", HttpStatusCode.OK, """{"status":"behind"}""");
        var (direct, pauses) = Direct(fake);
        var o = await direct.Run(Cmd(with: c => c with { UpdateWhenBehind = true }));
        Assert.Equal((MergeStatus.Blocked, "branch-updated", Update), (o.Status, o.Reason, o.NewHead));
        var sent = Assert.Single(fake.To("PUT", "/update-branch"));
        Assert.Equal(Pass, JsonNode.Parse(sent.Body)!["expected_head_sha"]!.ToString());
        Assert.Single(pauses);
        Assert.Empty(fake.To("PUT", "/merge"));   // the checks of the new head decide the merge, in another call
    }

    [Fact]
    public async Task Merge_BehindBranch_UpdateDoneInTheMeantime_IsRecognizedFromTheNextCall_AndAForeignCommitIsNot()
    {
        var fake = new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr(head: Update))
            .On($"GET repos/acme/orders/commits/{Update}", HttpStatusCode.OK, UpdateCommit())
            .On($"GET repos/acme/orders/compare/master...{BaseHead}", HttpStatusCode.OK, """{"status":"identical"}""");
        var o = await Direct(fake).Item1.Run(Cmd(with: c => c with { UpdatedFrom = Pass }));
        Assert.Equal((MergeStatus.Blocked, "branch-updated", Update), (o.Status, o.Reason, o.NewHead));

        // a commit not made by GitHub (forged parents), or another one than update-branch named, is a head that moved
        var forged = new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr(head: Update)).On($"GET repos/acme/orders/commits/{Update}", HttpStatusCode.OK, UpdateCommit(committer: "Mallory"));
        Assert.Equal(MergeStatus.HeadMoved, (await Direct(forged).Item1.Run(Cmd(with: c => c with { UpdatedFrom = Pass }))).Status);
        var other = new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr(head: Update));
        Assert.Equal(MergeStatus.HeadMoved, (await Direct(other).Item1.Run(Cmd(with: c => c with { UpdatedFrom = Pass, UpdatedTo = "5555555555555555555555555555555555555555" }))).Status);
    }

    [Fact]
    public async Task Merge_HeadMoved_IsRefused_WithoutAMergeCall()
    {
        var fake = new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr(head: "9999999999999999999999999999999999999999"));
        var o = await Direct(fake).Item1.Run(Cmd());
        Assert.Equal((MergeStatus.HeadMoved, "stale-head", FailureKind.Permanent), (o.Status, o.Reason, o.Kind));
        Assert.Contains("9999999", o.Detail);
        Assert.Empty(fake.To("PUT", "/merge"));
    }

    [Fact]
    public async Task Merge_405NotMergeable_IsATransientRefusalWithGitHubsMessage_NotFrozenByTheLedger()
    {
        var fake = new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr()).On(Merge, HttpStatusCode.MethodNotAllowed, """{"message":"Pull Request is not mergeable"}""");
        var o = await Direct(fake).Item1.Run(Cmd());
        Assert.Equal((MergeStatus.Failed, "merge-refused", FailureKind.Transient), (o.Status, o.Reason, o.Kind));
        Assert.Contains("405", o.Detail); Assert.Contains("not mergeable", o.Detail);

        // a refusal that clears without a new head (an approval added, a rule changed) must not be cached by the key of the same head: the second call asks GitHub again and merges
        var fake2 = new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr()).On(Merge, HttpStatusCode.MethodNotAllowed, """{"message":"At least 1 approving review is required by reviewers with write access."}""")
            .On(Merge, HttpStatusCode.OK, $$"""{"sha":"{{MergeSha}}","merged":true}""");
        var h = new MergePullRequestHandler(new InMemoryIdempotencyLedger(), Direct(fake2).Item1);
        Assert.Equal("merge-refused", (await h.Process(Cmd())).Reason);
        Assert.True((await h.Process(Cmd())).Merged);
    }

    [Fact]
    public async Task Merge_405RequiredCheck_IsBlockedAsChecksPending_NotAFailure()
    {
        var fake = new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr()).On(Merge, HttpStatusCode.MethodNotAllowed, """{"message":"Required status check \"build\" is expected."}""");
        var o = await Direct(fake).Item1.Run(Cmd());
        Assert.Equal((MergeStatus.Blocked, "checks-pending", FailureKind.Transient), (o.Status, o.Reason, o.Kind));
    }

    [Fact]
    public async Task Merge_409HeadModified_StillOnThePassHead_UpdatesTheBranch_AndCountsAgainstTheLimit()
    {
        var fake = new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr()).On("GET " + PrPath, HttpStatusCode.OK, Pr()).On("GET " + PrPath, HttpStatusCode.OK, Pr()).On("GET " + PrPath, HttpStatusCode.OK, Pr(head: Update))
            .On(Merge, HttpStatusCode.Conflict, """{"message":"Head branch was modified. Review the changes and try again."}""")
            .On(UpdateBranch, HttpStatusCode.Accepted, """{"message":"Updating pull request branch."}""")
            .On($"GET repos/acme/orders/commits/{Update}", HttpStatusCode.OK, UpdateCommit())
            .On($"GET repos/acme/orders/compare/master...{BaseHead}", HttpStatusCode.OK, """{"status":"behind"}""");
        var o = await Direct(fake).Item1.Run(Cmd());
        Assert.Equal((MergeStatus.Blocked, "branch-updated", Update), (o.Status, o.Reason, o.NewHead));
        Assert.Single(fake.To("PUT", "/update-branch"));

        // the limit of updates of one verdict is reached: nothing more is tried
        var limit = new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr()).On(Merge, HttpStatusCode.MethodNotAllowed, """{"message":"Base branch was modified. Review the changes and try again. The branch is behind."}""");
        var l = await Direct(limit).Item1.Run(Cmd(updates: 4));
        Assert.Equal((MergeStatus.Failed, "merge-out-of-date"), (l.Status, l.Reason));
        Assert.Empty(limit.To("PUT", "/update-branch"));
    }

    [Fact]
    public async Task Merge_OutOfDate_WhenTheBranchMovedMeanwhile_IsHeadMoved_AndAnUpdateGitHubRefusesFails()
    {
        var moved = new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr()).On("GET " + PrPath, HttpStatusCode.OK, Pr(head: "9999999999999999999999999999999999999999"))
            .On(Merge, HttpStatusCode.Conflict, """{"message":"Head branch was modified."}""");
        Assert.Equal(MergeStatus.HeadMoved, (await Direct(moved).Item1.Run(Cmd())).Status);

        var refused = new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr()).On(Merge, HttpStatusCode.Conflict, """{"message":"Head branch was modified."}""")
            .On(UpdateBranch, HttpStatusCode.UnprocessableEntity, """{"message":"merge conflict between base and head"}""");
        var o = await Direct(refused).Item1.Run(Cmd());
        Assert.Equal((MergeStatus.Failed, "update-refused"), (o.Status, o.Reason));
        Assert.Contains("merge conflict", o.Detail);
    }

    [Fact]
    public async Task Merge_Queue_IsRespected_AsTheScriptDoes()
    {
        MergePullRequest Q(int busy, bool docs = false) => Cmd(with: c => c with { MergeQueueDepth = 2, QueueBusy = busy, DocsOnly = docs });
        string Ok = $$"""{"sha":"{{MergeSha}}","merged":true}""";
        var full = new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr()).On(Merge, HttpStatusCode.OK, Ok);
        var o = await Direct(full).Item1.Run(Q(2));
        Assert.Equal((MergeStatus.Blocked, "merge-queue"), (o.Status, o.Reason));
        Assert.Empty(full.To("PUT", "/merge"));

        Assert.Equal(MergeStatus.Merged, (await Direct(new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr()).On(Merge, HttpStatusCode.OK, Ok)).Item1.Run(Q(1))).Status);
        // a documentation-only change builds no release: it never waits in the queue
        Assert.Equal(MergeStatus.Merged, (await Direct(new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr()).On(Merge, HttpStatusCode.OK, Ok)).Item1.Run(Q(5, docs: true))).Status);
        // depth 0 is off
        Assert.Equal(MergeStatus.Merged, (await Direct(new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr()).On(Merge, HttpStatusCode.OK, Ok)).Item1.Run(Cmd(with: c => c with { QueueBusy = 9 }))).Status);
    }

    [Fact]
    public async Task Merge_AlreadyMerged_IsDone_ClosedIsRefused_MissingIsNotFound()
    {
        var merged = new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr(state: "closed", merged: true, mergeCommit: $"\"{MergeSha.ToUpperInvariant()}\""));
        var o = await Direct(merged).Item1.Run(Cmd(with: c => c with { MergeQueueDepth = 1, QueueBusy = 3 }));
        Assert.Equal((MergeStatus.Merged, "already-merged", MergeSha), (o.Status, o.Reason, o.Sha));
        Assert.Empty(merged.To("PUT", "/merge"));

        Assert.Equal("pr-not-open", (await Direct(new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr(state: "closed"))).Item1.Run(Cmd())).Reason);
        Assert.Equal("pr-not-found", (await Direct(new Fake()).Item1.Run(Cmd())).Reason);
    }

    [Fact]
    public async Task Merge_StackedPullRequest_UsesTheAsyncEndpoint()
    {
        var fake = new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr()).On("GET " + PrPath, HttpStatusCode.OK, Pr()).On("GET " + PrPath, HttpStatusCode.OK, Pr(state: "closed", merged: true, mergeCommit: $"\"{MergeSha}\""))
            .On(Merge, HttpStatusCode.Forbidden, """{"message":"This pull request must be merged through the asynchronous merge endpoint (stack)"}""")
            .On("PUT " + PrPath + "/merge-async", HttpStatusCode.OK, "{}");
        var o = await Direct(fake).Item1.Run(Cmd());
        Assert.Equal((MergeStatus.Merged, MergeSha), (o.Status, o.Sha));
        var body = JsonNode.Parse(Assert.Single(fake.To("PUT", "/merge-async")).Body)!;
        Assert.Equal(("squash", Pass, "direct_merge"), (body["merge_method"]!.ToString(), body["sha"]!.ToString(), body["merge_action"]!.ToString()));

        var pending = new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr()).On(Merge, HttpStatusCode.Forbidden, """{"message":"asynchronous"}""").On("PUT " + PrPath + "/merge-async", HttpStatusCode.Conflict, """{"message":"pending"}""");
        var blocked = await Direct(pending).Item1.Run(Cmd());
        Assert.Equal((MergeStatus.Blocked, "merge-async"), (blocked.Status, blocked.Reason));
    }

    [Fact]
    public async Task Merge_Forbidden_And_NoSha_AreFailures()
    {
        var forbidden = new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr()).On(Merge, HttpStatusCode.Forbidden, """{"message":"Resource not accessible by integration"}""");
        Assert.Equal("github-forbidden", (await Direct(forbidden).Item1.Run(Cmd())).Reason);
        var nosha = new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr()).On(Merge, HttpStatusCode.OK, """{"merged":true}""");
        Assert.Equal("merge-no-sha", (await Direct(nosha).Item1.Run(Cmd())).Reason);
        Assert.Equal("verdict-head-unknown", (await Direct(new Fake()).Item1.Run(Cmd(with: c => c with { PassHeadSha = " " }))).Reason);
    }

    [Fact]
    public async Task Handler_IdempotentRetryWithTheSameKey_MergesOnce_AndAnswersTheSame()
    {
        var fake = new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr()).On(Merge, HttpStatusCode.OK, $$"""{"sha":"{{MergeSha}}","merged":true}""");
        var h = new MergePullRequestHandler(new InMemoryIdempotencyLedger(), Direct(fake).Item1);
        var first = await h.Process(Cmd()); var again = await h.Process(Cmd());
        Assert.Equal(MergeSha, first.Sha); Assert.Equal(MergeSha, again.Sha); Assert.True(again.Merged);
        Assert.Single(fake.To("PUT", "/merge")); Assert.Single(fake.To("GET", "/pulls/9"));
        // another head (another verdict) is another key
        Assert.NotEqual(MergePullRequest.KeyFor(Repo, 9, Pass), MergePullRequest.KeyFor(Repo, 9, Update));
    }

    [Fact]
    public async Task Handler_Blocked_IsNotRecorded_SoTheSameKeyAsksAgain_ButHeadMovedIs()
    {
        var fake = new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr()).On(Merge, HttpStatusCode.MethodNotAllowed, """{"message":"Required status check is expected."}""")
            .On(Merge, HttpStatusCode.OK, $$"""{"sha":"{{MergeSha}}","merged":true}""");
        var h = new MergePullRequestHandler(new InMemoryIdempotencyLedger(), Direct(fake).Item1);
        Assert.Equal("checks-pending", (await h.Process(Cmd())).Reason);
        Assert.True((await h.Process(Cmd())).Merged);
        Assert.Equal(2, fake.To("PUT", "/merge").Count());

        var moved = new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr(head: Update));
        var hm = new MergePullRequestHandler(new InMemoryIdempotencyLedger(), Direct(moved).Item1);
        Assert.Equal(MergeStatus.HeadMoved, (await hm.Process(Cmd())).Status); Assert.Equal(MergeStatus.HeadMoved, (await hm.Process(Cmd())).Status);
        Assert.Single(moved.To("GET", "/pulls/9"));
    }

    [Fact]
    public async Task Handler_ServerError_IsTransient_AndBecomesNoIntegrationFailed()
    {
        var fake = new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr()).On(Merge, HttpStatusCode.BadGateway, """{"message":"Server Error"}""");
        var h = new MergePullRequestHandler(new InMemoryIdempotencyLedger(), Direct(fake).Item1);
        var o = await h.Process(Cmd());
        Assert.Equal((MergeStatus.Failed, FailureKind.Transient), (o.Status, o.Kind));
        Assert.Null(IntegrationCommandHandler<MergePullRequest>.FailureMessage(Cmd(), new CommandResult(false, o.Kind, o.Detail)));
        Assert.Equal(FailureKind.Permanent, (await h.Process(Cmd(key: " "))).Kind);
    }

    [Fact]
    public async Task Handler_RateLimit_IsRescheduledNotFailed()
    {
        var fake = new Fake().On("GET " + PrPath, HttpStatusCode.TooManyRequests, """{"message":"slow down"}""");
        var o = await new MergePullRequestHandler(new InMemoryIdempotencyLedger(), Direct(fake).Item1).Process(Cmd());
        Assert.Equal((MergeStatus.Failed, "rate-limited", FailureKind.RateLimited), (o.Status, o.Reason, o.Kind));
        // MergeDirect itself says how long to wait (Retry-After, the reset, or 30 seconds for a bare 429); a CommandResult has no field for it, so the caller of the handler reschedules by its own policy
        Assert.Equal(TimeSpan.FromSeconds(30), (await Direct(fake).Item1.Run(Cmd())).RetryAfter);
    }

    // ---- review findings of W6.2 -------------------------------------------------------------------------------------------------------

    static string OkMerge => $$"""{"sha":"{{MergeSha}}","merged":true}""";

    [Fact]
    public async Task Merge_403RateLimit_IsRescheduled_NotCachedAsForbidden_ButARealForbiddenIs()
    {
        var limited = new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr())
            .On(Merge, HttpStatusCode.Forbidden, """{"message":"You have exceeded a secondary rate limit."}""", ("Retry-After", "45"))
            .On(Merge, HttpStatusCode.OK, OkMerge);
        var h = new MergePullRequestHandler(new InMemoryIdempotencyLedger(), Direct(limited).Item1);
        var o = await h.Process(Cmd());
        Assert.Equal((MergeStatus.Failed, "rate-limited", FailureKind.RateLimited), (o.Status, o.Reason, o.Kind));
        Assert.Equal(TimeSpan.FromSeconds(45), o.RetryAfter);   // carried through the CommandResult of the handler
        Assert.True((await h.Process(Cmd())).Merged);   // not recorded: the same key merges once the limit is over

        // primary limit: 403 with remaining 0 and a reset in the future
        var reset = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds().ToString();
        var primary = new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr()).On(Merge, HttpStatusCode.Forbidden, """{"message":"API rate limit exceeded"}""", ("x-ratelimit-remaining", "0"), ("x-ratelimit-reset", reset));
        Assert.Equal(FailureKind.RateLimited, (await Direct(primary).Item1.Run(Cmd())).Kind);

        var forbidden = new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr()).On(Merge, HttpStatusCode.Forbidden, """{"message":"Resource not accessible by integration"}""");
        var f = await Direct(forbidden).Item1.Run(Cmd());
        Assert.Equal((MergeStatus.Failed, "github-forbidden", FailureKind.Permanent), (f.Status, f.Reason, f.Kind));
    }

    [Fact]
    public async Task Merge_UpdateBranchTransportOrServerError_IsTransient_NotACachedUpdateRefused()
    {
        MergePullRequest Behind() => Cmd(with: c => c with { UpdateWhenBehind = true });
        Fake Pre(HttpStatusCode code, params (string Name, string Value)[] headers) => new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr())
            .On($"GET repos/acme/orders/compare/master...{Pass}", HttpStatusCode.OK, """{"behind_by":2}""").On(UpdateBranch, code, """{"message":"boom"}""", headers);

        // a 502 on the update before the merge: the call throws, the handler classifies it as transient and records nothing; the merge is not tried "as it is"
        var f502 = Pre(HttpStatusCode.BadGateway);
        await Assert.ThrowsAsync<HttpRequestException>(() => Direct(f502).Item1.Run(Behind()));
        Assert.Empty(f502.To("PUT", "/merge"));
        var ledger = new InMemoryIdempotencyLedger();
        var o = await new MergePullRequestHandler(ledger, Direct(f502).Item1).Process(Behind());
        Assert.Equal((MergeStatus.Failed, FailureKind.Transient), (o.Status, o.Kind));
        Assert.Null(ledger.TryGet(Behind().IdempotencyKey));

        // a 429 is rescheduled with its Retry-After; nothing was updated, nothing is counted
        var limited = await Direct(Pre(HttpStatusCode.TooManyRequests, ("Retry-After", "20"))).Item1.Run(Behind());
        Assert.Equal((MergeStatus.Failed, FailureKind.RateLimited, TimeSpan.FromSeconds(20), 0), (limited.Status, limited.Kind, limited.RetryAfter!.Value, limited.UpdatesAdded));

        // the same two answers after a refused merge
        Fake After(HttpStatusCode code) => new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr()).On(Merge, HttpStatusCode.Conflict, """{"message":"Head branch was modified."}""").On(UpdateBranch, code, "{}");
        await Assert.ThrowsAsync<HttpRequestException>(() => Direct(After(HttpStatusCode.ServiceUnavailable)).Item1.Run(Cmd()));
        Assert.Equal("rate-limited", (await Direct(After(HttpStatusCode.TooManyRequests)).Item1.Run(Cmd())).Reason);

        // a 422 before the merge is GitHub's refusal: merged as it is, as the script does
        var f422 = Pre(HttpStatusCode.UnprocessableEntity).On(Merge, HttpStatusCode.OK, OkMerge);
        Assert.True((await Direct(f422).Item1.Run(Behind())).Merged);
    }

    [Fact]
    public async Task Merge_Draft_IsAWaitNotARefusal_AndTheItemIsChecked()
    {
        var draft = new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr(draft: true));
        var h = new MergePullRequestHandler(new InMemoryIdempotencyLedger(), Direct(draft).Item1);
        var o = await h.Process(Cmd());
        Assert.Equal((MergeStatus.Blocked, "pr-draft", FailureKind.Transient), (o.Status, o.Reason, o.Kind));
        Assert.Empty(draft.To("PUT", "/merge"));
        Assert.Equal("pr-draft", (await h.Process(Cmd())).Reason);   // asked again: not cached
        Assert.Equal(2, draft.To("GET", "/pulls/9").Count());

        // a draft that turned up between the read and the merge: GitHub's 422 says so
        var late = new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr()).On(Merge, HttpStatusCode.UnprocessableEntity, """{"message":"Pull request is still a draft"}""");
        var d = await Direct(late).Item1.Run(Cmd());
        Assert.Equal((MergeStatus.Blocked, "pr-draft"), (d.Status, d.Reason));

        // Test-PrBelongs: branch "bot/7-x" belongs to item 7, not to 8; the title can name the item; a copilot branch belongs by the recorded pull request
        var fake = new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr(title: "Fix the thing (#12)")).On(Merge, HttpStatusCode.OK, OkMerge);
        Assert.Equal("pr-mismatch", (await Direct(fake).Item1.Run(Cmd(with: c => c with { Item = 8 }))).Reason);
        Assert.Empty(fake.To("PUT", "/merge"));
        Assert.True((await Direct(fake).Item1.Run(Cmd(with: c => c with { Item = 7 }))).Merged);
        Assert.True((await Direct(fake).Item1.Run(Cmd(with: c => c with { Item = 12 }))).Merged);
        Assert.Equal("pr-mismatch", (await Direct(fake).Item1.Run(Cmd(with: c => c with { Item = 1 }))).Reason);   // #12 is not #1
        var copilot = Pr().Replace("bot/7-x", "copilot/fix-it");
        Assert.True((await Direct(new Fake().On("GET " + PrPath, HttpStatusCode.OK, copilot).On(Merge, HttpStatusCode.OK, OkMerge)).Item1.Run(Cmd(with: c => c with { Item = 5, RecordedPullRequest = 9 }))).Merged);
        Assert.Equal("pr-mismatch", (await Direct(new Fake().On("GET " + PrPath, HttpStatusCode.OK, copilot)).Item1.Run(Cmd(with: c => c with { Item = 5, RecordedPullRequest = 3 }))).Reason);
    }

    [Fact]
    public async Task Outcome_CarriesAlreadyMerged_Sha_RetryAfter_AndAnEmptyDetail_ThroughTheHandler()
    {
        var merged = new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr(state: "closed", merged: true, mergeCommit: $"\"{MergeSha}\""));
        var a = await new MergePullRequestHandler(new InMemoryIdempotencyLedger(), Direct(merged).Item1).Process(Cmd());
        Assert.Equal((MergeStatus.Merged, "already-merged", MergeSha), (a.Status, a.Reason, a.Sha));

        // the result of the ledger (a replay) answers the same
        var h = new MergePullRequestHandler(new InMemoryIdempotencyLedger(), Direct(merged).Item1);
        await h.Process(Cmd()); var replay = await h.Process(Cmd());
        Assert.Equal(("already-merged", MergeSha), (replay.Reason, replay.Sha));

        // an empty detail (a trimmed ": ") and a detail with ": " and "/" in it are not read wrong: Blocked stays Blocked, with every field
        foreach (var o in new[] { MergeOutcome.Blocked("checks-pending", "", "abc"), MergeOutcome.Blocked("checks-pending", "a: b/c: d"), MergeOutcome.HeadMoved(""),
                                  MergeOutcome.Failed("rate-limited", "x", FailureKind.RateLimited, TimeSpan.FromSeconds(7)) with { Updated = true, UpdatesAdded = 1 },
                                  MergeOutcome.MergedAs(MergeSha, "already-merged") })
            Assert.Equal(o, MergeOutcome.From(o.ToResult()));
        var plain = MergeOutcome.From(CommandResult.Failed(FailureKind.Transient, "SendAsync: boom"));
        Assert.Equal((MergeStatus.Failed, "command-failed", FailureKind.Transient), (plain.Status, plain.Reason, plain.Kind));
        // the plain result of the merge keeps the merge commit as Execution and a blocked update its new head
        Assert.Equal(MergeSha, MergeOutcome.MergedAs(MergeSha).ToResult().Execution);
        Assert.Equal("abc", MergeOutcome.Blocked("branch-updated", "", "abc").ToResult().Execution);
    }

    [Fact]
    public async Task Outcome_SaysWhetherThisCallUpdatedTheBranch_AndOnlyTheUpdateAfterARefusalIsCounted()
    {
        // before the merge: updated, not counted
        var pre = new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr()).On("GET " + PrPath, HttpStatusCode.OK, Pr()).On("GET " + PrPath, HttpStatusCode.OK, Pr(head: Update))
            .On($"GET repos/acme/orders/compare/master...{Pass}", HttpStatusCode.OK, """{"behind_by":3}""").On(UpdateBranch, HttpStatusCode.Accepted, "{}")
            .On($"GET repos/acme/orders/commits/{Update}", HttpStatusCode.OK, UpdateCommit()).On($"GET repos/acme/orders/compare/master...{BaseHead}", HttpStatusCode.OK, """{"status":"behind"}""");
        var a = await Direct(pre).Item1.Run(Cmd(with: c => c with { UpdateWhenBehind = true }));
        Assert.Equal(("branch-updated", true, 0), (a.Reason, a.Updated, a.UpdatesAdded));

        // after a refusal: updated and counted (also when GitHub refuses the update: the script counts before it asks)
        var post = new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr()).On("GET " + PrPath, HttpStatusCode.OK, Pr()).On("GET " + PrPath, HttpStatusCode.OK, Pr(head: Update))
            .On(Merge, HttpStatusCode.Conflict, """{"message":"Head branch was modified."}""").On(UpdateBranch, HttpStatusCode.Accepted, "{}")
            .On($"GET repos/acme/orders/commits/{Update}", HttpStatusCode.OK, UpdateCommit()).On($"GET repos/acme/orders/compare/master...{BaseHead}", HttpStatusCode.OK, """{"status":"behind"}""");
        var b = await Direct(post).Item1.Run(Cmd());
        Assert.Equal(("branch-updated", true, 1), (b.Reason, b.Updated, b.UpdatesAdded));
        var refused = new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr()).On(Merge, HttpStatusCode.Conflict, """{"message":"Head branch was modified."}""").On(UpdateBranch, HttpStatusCode.UnprocessableEntity, """{"message":"conflict"}""");
        var r = await Direct(refused).Item1.Run(Cmd());
        Assert.Equal(("update-refused", false, 1), (r.Reason, r.Updated, r.UpdatesAdded));

        // recognising the update of an earlier call is neither
        var seen = new Fake().On("GET " + PrPath, HttpStatusCode.OK, Pr(head: Update)).On($"GET repos/acme/orders/commits/{Update}", HttpStatusCode.OK, UpdateCommit())
            .On($"GET repos/acme/orders/compare/master...{BaseHead}", HttpStatusCode.OK, """{"status":"identical"}""");
        var c2 = await Direct(seen).Item1.Run(Cmd(with: c => c with { UpdatedFrom = Pass }));
        Assert.Equal(("branch-updated", false, 0), (c2.Reason, c2.Updated, c2.UpdatesAdded));
    }
}
