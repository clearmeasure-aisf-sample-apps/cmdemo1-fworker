using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fworker.Contracts.Catalog;
using Fworker.Contracts.Ids;
using Fworker.Contracts.Seams;
using Fworker.Domain;
using Fworker.Integrations.Commands;
using Fworker.Workflows.Delivery;
using Xunit;

namespace Gateway.Tests;

/// <summary>
/// W5.2 reads on a long commit range: an answer that cannot change is kept for good, so an evaluation that needs a read per commit finishes under the minute's cap,
/// a 'none' that depends on item state and a 'waiting' still expire, and the cap is still enforced. A fake HTTP handler and a fake clock; no network, no database.
/// </summary>
public sealed class DeliveryShadowReadsTests
{
    static readonly RepoId Home = RepoId.Parse("acme/work");
    static readonly string Deployed = new('d', 40), Released = new('e', 40);

    static string ShaOf(int i) => (i + 1).ToString("x40");

    // the kinds of commit of the long range, by index
    enum Kind { Direct, Settled, NotMerged, WaitsForState }
    static Kind KindOf(int i) => (i % 6) switch { 0 or 1 => Kind.Direct, 2 or 3 => Kind.Settled, 4 => Kind.NotMerged, _ => Kind.WaitsForState };
    static int PullOf(int i) => 1000 + i;
    static int ItemOf(int i) => 5000 + i;

    sealed class Handler : HttpMessageHandler
    {
        readonly object gate = new();
        public readonly List<(HttpMethod Method, string Path)> Seen = [];
        public Func<string, (HttpStatusCode, string)> Answer = _ => (HttpStatusCode.NotFound, "{}");
        public int Count(string contains) { lock (gate) return Seen.Count(s => s.Path.Contains(contains)); }
        public int Total { get { lock (gate) return Seen.Count; } }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (gate) Seen.Add((request.Method, request.RequestUri!.PathAndQuery));
            var (status, body) = Answer(request.RequestUri!.PathAndQuery);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    sealed class Facts(Func<int, MergedPullRequest?> pull) : IMergeFactsReader
    {
        public readonly Dictionary<int, int> Pulls = [];
        public Task<MergedPullRequest?> ReadPullRequest(RepoId repository, int number, CancellationToken ct = default) { lock (Pulls) Pulls[number] = Pulls.GetValueOrDefault(number) + 1; return Task.FromResult(pull(number)); }
        public Task<MergedPullRequest?> FindMergedPullRequest(ItemId item, CancellationToken ct = default) => Task.FromResult<MergedPullRequest?>(null);
        public Task<ItemMergeFacts?> ReadItem(ItemId item, CancellationToken ct = default) => Task.FromResult<ItemMergeFacts?>(null);
    }

    static JsonNode CommitJson(int i)
    {
        var subject = KindOf(i) == Kind.Direct ? "direct push" : $"Thing {i} (#{PullOf(i)})";
        return new JsonObject { ["sha"] = ShaOf(i), ["parents"] = new JsonArray(new JsonObject { ["sha"] = i == 0 ? Deployed : ShaOf(i - 1) }), ["commit"] = new JsonObject { ["message"] = subject + "\n\nbody" } };
    }

    static string CompareJson(int n, int total) =>
        new JsonObject { ["status"] = "ahead", ["total_commits"] = total, ["commits"] = new JsonArray(Enumerable.Range(0, n).Select(CommitJson).ToArray()) }.ToJsonString();

    static MergedPullRequest? PullFor(int number)
    {
        var i = number - 1000;
        return KindOf(i) switch
        {
            Kind.Settled or Kind.WaitsForState => new MergedPullRequest(Home, number, true, "master", $"me/{ItemOf(i)}-x", $"Thing {i} (#{number})", "", ShaOf(i)),
            Kind.NotMerged => new MergedPullRequest(Home, number, false, "master", $"me/{ItemOf(i)}-x", $"Thing {i} (#{number})", "", null),
            _ => null
        };
    }

    static (HttpTrainReads Reads, Handler Gh, Facts Facts) Reads(Func<DateTimeOffset> now, int commits = 300, int total = 300)
    {
        var snapshot = new CatalogSnapshot(CatalogSource.SystemJson, DateTimeOffset.UtcNow, "acme", Home, null, [new RepositoryEntry(Home, [new DeployableEntry("ui", Home, null, null, null, null, false)])], []);
        var policies = new CatalogDeliveryPolicies(() => snapshot, _ => null);
        var gh = new Handler { Answer = path => path.Contains("/compare/") ? (HttpStatusCode.OK, CompareJson(commits, total)) : Comments(path) };
        var facts = new Facts(PullFor);
        var reads = new HttpTrainReads(new HttpClient(gh) { BaseAddress = new Uri("https://api.example/") }, new HttpClient(new Handler()), "factory-worker", facts, new ItemGateConfig("Auto Merge", "video"), policies, null, now);
        return (reads, gh, facts);
    }

    // the comments of an item: the state comment naming the commit for a settled commit, none for a commit that waits for state
    static (HttpStatusCode, string) Comments(string path)
    {
        var m = Regex.Match(path, @"/issues/(\d+)/comments");
        if (!m.Success) return (HttpStatusCode.NotFound, "{}");
        var i = int.Parse(m.Groups[1].Value) - 5000;
        if (KindOf(i) != Kind.Settled) return (HttpStatusCode.OK, "[]");
        var state = ItemStateComment.Body("factory-worker", ItemOf(i), new JsonObject { ["merge"] = new JsonObject { ["sha"] = ShaOf(i), ["awaiting"] = "", ["done_at"] = "2026-10-28T09:00:00Z" } });
        return (HttpStatusCode.OK, new JsonArray(new JsonObject { ["body"] = state }).ToJsonString());
    }

    /// <summary>The train's evaluation of a release: the compare, then the commit of every first-parent step. A refused read ends it; the train asks again 15 s later (PR #505).</summary>
    static async Task<bool> Evaluate(HttpTrainReads reads, int commits)
    {
        try
        {
            var range = await reads.Compare(Home, Deployed, Released, default);
            foreach (var c in range!.Commits) await reads.LookupCommit(Home, c, default);
            return range.Commits.Count == commits;
        }
        catch (HttpRequestException) { return false; }
    }

    [Fact]
    public async Task ALongRange_FinishesUnderTheCap_AndNoAnswerThatCannotChangeIsReadTwice()
    {
        var now = DateTimeOffset.UtcNow;
        var (reads, gh, facts) = Reads(() => now);
        var start = now; var evaluations = 0;
        while (!await Evaluate(reads, 300))
        {
            evaluations++;
            now += TimeSpan.FromSeconds(15);
            Assert.True(now - start < TimeSpan.FromMinutes(8), $"not finished after {now - start} and {evaluations} evaluations");
        }
        // 1 compare + 100 settled commits + 50 commits waiting for state, one comment page each, 40 a minute: about four minutes of 15 s retries
        Assert.True(now - start <= TimeSpan.FromMinutes(5), $"took {now - start}");
        Assert.Equal(1, gh.Count("/compare/"));
        for (var i = 0; i < 300; i++)
            switch (KindOf(i))
            {
                case Kind.Settled: Assert.Equal(1, gh.Count($"/issues/{ItemOf(i)}/comments")); Assert.Equal(1, facts.Pulls[PullOf(i)]); break;
                case Kind.NotMerged: Assert.Equal(1, facts.Pulls[PullOf(i)]); Assert.Equal(0, gh.Count($"/issues/{ItemOf(i)}/comments")); break;
                case Kind.Direct: Assert.False(facts.Pulls.ContainsKey(PullOf(i))); break;
            }
        Assert.All(gh.Seen, s => Assert.Equal(HttpMethod.Get, s.Method));
        // answered from what was kept in the minutes after: nothing that cannot change is read again
        var seen = gh.Total;
        now += TimeSpan.FromSeconds(30);
        Assert.True(await Evaluate(reads, 300));
        Assert.Equal(seen, gh.Total);   // (the 50 state-dependent none are kept ten minutes too)
    }

    [Fact]
    public async Task ACompareOfTwoFixedCommits_IsKeptForGood_ButOneGitHubCannotDoIsNot()
    {
        var now = DateTimeOffset.UtcNow;
        var (reads, gh, _) = Reads(() => now);
        await reads.Compare(Home, Deployed, Released, default);
        now += TimeSpan.FromHours(3);
        await reads.Compare(Home, Deployed, Released, default);
        Assert.Equal(1, gh.Count("/compare/"));
        // a 404 or 422 may be a lag: kept the short time only
        gh.Answer = _ => (HttpStatusCode.UnprocessableEntity, "{}");
        var other = new string('9', 40);
        Assert.Null(await reads.Compare(Home, Deployed, other, default));
        Assert.Null(await reads.Compare(Home, Deployed, other, default));
        Assert.Equal(2, gh.Count("/compare/"));
        now += TimeSpan.FromMinutes(3);
        Assert.Null(await reads.Compare(Home, Deployed, other, default));
        Assert.Equal(3, gh.Count("/compare/"));
    }

    [Fact]
    public async Task ACompareThatGitHubTruncates_IsReportedAsItIs_NotPaged()
    {
        // v1 (Verbs.Promote.ps1) reads one compare page and waits when total_commits is greater than the listed commits; the train keeps that
        var (reads, gh, _) = Reads(() => DateTimeOffset.UtcNow, commits: 250, total: 345);
        var range = await reads.Compare(Home, Deployed, Released, default);
        Assert.Equal(250, range!.Commits.Count);
        Assert.Equal(345, range.TotalCommits);
        Assert.Equal(1, gh.Total);
    }

    [Fact]
    public async Task ANoneThatDependsOnItemState_ExpiresAfterTenMinutes_AndAFinalOneDoesNot()
    {
        var now = DateTimeOffset.UtcNow;
        var (reads, gh, facts) = Reads(() => now);
        var waits = new RangeCommit(ShaOf(5), [ShaOf(4)], "Thing 5 (#1005)");     // Kind.WaitsForState
        var notMerged = new RangeCommit(ShaOf(4), [ShaOf(3)], "Thing 4 (#1004)"); // Kind.NotMerged
        var direct = new RangeCommit(ShaOf(0), [Deployed], "direct push");
        foreach (var c in new[] { waits, notMerged, direct }) Assert.Equal(new CommitItemLookup(0, ""), await reads.LookupCommit(Home, c, default));
        now += TimeSpan.FromMinutes(9);
        foreach (var c in new[] { waits, notMerged, direct }) await reads.LookupCommit(Home, c, default);
        Assert.Equal(1, gh.Count($"/issues/{ItemOf(5)}/comments"));
        now += TimeSpan.FromMinutes(2);
        foreach (var c in new[] { waits, notMerged, direct }) await reads.LookupCommit(Home, c, default);
        Assert.Equal(2, gh.Count($"/issues/{ItemOf(5)}/comments"));   // read again after ten minutes
        Assert.Equal(2, facts.Pulls[PullOf(4)]);                        // the pull request that is not merged as the commit is read again after the ten minutes (it may have been a stale read)
        Assert.False(facts.Pulls.ContainsKey(PullOf(0)));
        // ... and when that second read, ten minutes after the first, says the same, it is final
        now += TimeSpan.FromMinutes(11);
        foreach (var c in new[] { waits, notMerged, direct }) await reads.LookupCommit(Home, c, default);
        now += TimeSpan.FromHours(3);
        foreach (var c in new[] { waits, notMerged, direct }) await reads.LookupCommit(Home, c, default);
        Assert.Equal(2, facts.Pulls[PullOf(4)]);
    }

    [Theory]
    [InlineData(false, "")]           // a read right after the merge: not merged yet
    [InlineData(true, "")]            // merged, but no merge commit in the read yet
    [InlineData(true, "different")]   // merged, a merge commit that is not this one yet
    public async Task APullRequestReadThatSaysNotMerged_IsNotFinalAtOnce_ItCanBeAStaleReadRightAfterTheMerge(bool merged, string mergeCommit)
    {
        var now = DateTimeOffset.UtcNow;
        var sha = ShaOf(2);
        var stale = true;
        var facts = new Facts(n => new MergedPullRequest(Home, n, stale ? merged : true, "master", $"me/{ItemOf(2)}-x", "Thing 2 (#1002)", "", stale ? (mergeCommit.Length == 0 ? null : mergeCommit) : sha));
        var snapshot = new CatalogSnapshot(CatalogSource.SystemJson, DateTimeOffset.UtcNow, "acme", Home, null, [new RepositoryEntry(Home, [new DeployableEntry("ui", Home, null, null, null, null, false)])], []);
        var gh = new Handler { Answer = Comments };
        var reads = new HttpTrainReads(new HttpClient(gh) { BaseAddress = new Uri("https://api.example/") }, new HttpClient(new Handler()), "factory-worker", facts, new ItemGateConfig("Auto Merge", "video"), new CatalogDeliveryPolicies(() => snapshot, _ => null), null, () => now);
        var commit = new RangeCommit(sha, [ShaOf(1)], "Thing 2 (#1002)");
        Assert.Equal(0, (await reads.LookupCommit(Home, commit, default)).Item);   // the stale read: no item for now
        stale = false;                                                             // GitHub caught up
        now += TimeSpan.FromMinutes(11);
        var again = await reads.LookupCommit(Home, commit, default);
        Assert.Equal(ItemOf(2), again.Item);                                       // not stored as 'no item' for good: the commit's item is found, so its merge gate is judged
        Assert.Equal(2, facts.Pulls[1002]);
    }

    [Fact]
    public async Task AWaitingAnswer_StillExpiresAfterAMinute()
    {
        var now = DateTimeOffset.UtcNow;
        var (reads, gh, _) = Reads(() => now);
        // the item's state names the commit but the merge is not settled
        gh.Answer = path => path.Contains("/comments")
            ? (HttpStatusCode.OK, new JsonArray(new JsonObject { ["body"] = ItemStateComment.Body("factory-worker", ItemOf(2), new JsonObject { ["merge"] = new JsonObject { ["sha"] = ShaOf(2), ["awaiting"] = "ci" } }) }).ToJsonString())
            : (HttpStatusCode.NotFound, "{}");
        var commit = new RangeCommit(ShaOf(2), [ShaOf(1)], "Thing 2 (#1002)");
        var first = await reads.LookupCommit(Home, commit, default);
        Assert.Equal(ItemOf(2), first.Item);
        Assert.NotEqual("", first.Gap);
        now += TimeSpan.FromSeconds(50);
        await reads.LookupCommit(Home, commit, default);
        Assert.Equal(1, gh.Total);
        now += TimeSpan.FromSeconds(15);
        await reads.LookupCommit(Home, commit, default);
        Assert.Equal(2, gh.Total);
    }

    [Fact]
    public async Task TheCap_IsStillTheMinutes40Reads()
    {
        var now = DateTimeOffset.UtcNow;
        var (reads, gh, _) = Reads(() => now);
        Assert.Equal(40, HttpTrainReads.MaxCallsPerMinute);
        // 41 commits that each need a state read: the 41st is refused until a minute has passed, and nothing was sent for it
        var waiting = Enumerable.Range(0, 300).Where(i => KindOf(i) == Kind.WaitsForState).Take(41).ToList();
        for (var k = 0; k < 40; k++) await reads.LookupCommit(Home, new RangeCommit(ShaOf(waiting[k]), [Deployed], $"x (#{PullOf(waiting[k])})"), default);
        var last = new RangeCommit(ShaOf(waiting[40]), [Deployed], $"x (#{PullOf(waiting[40])})");
        await Assert.ThrowsAsync<HttpRequestException>(() => reads.LookupCommit(Home, last, default));
        Assert.Equal(40, gh.Total);
        now += TimeSpan.FromSeconds(61);
        await reads.LookupCommit(Home, last, default);
        Assert.Equal(41, gh.Total);
    }
}
