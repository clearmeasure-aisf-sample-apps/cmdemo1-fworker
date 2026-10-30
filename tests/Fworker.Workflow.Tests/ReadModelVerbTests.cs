using System.Text.Json.Nodes;
using Fworker.Workflows.ReadModels;
using Xunit;

namespace Fworker.Workflow.Tests;

/// <summary>
/// W9.5 (#357): the read models over a scripted GitHub, Octopus and state: the refusals of GitHub as the rule script maps them, which head a call may read, the limits of the detail, the count and the rerun of wait-for-checks and what each
/// verb leaves in the item's state. (The same verbs against the fake GitHub of the Tier 1 suites are Gateway.Tests' ReadModels*FakeTests.)
/// </summary>
public sealed class ReadModelVerbTests
{
    const string Sha = "1111111111111111111111111111111111111111";
    static readonly DateTime T0 = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

    sealed class Scripted : IGitHubApi
    {
        public readonly Dictionary<string, Func<GhCall, GhAnswer>> Routes = new(StringComparer.Ordinal);
        public readonly List<string> Calls = [];
        public Exception? Fail;

        public Scripted On(string route, int status, JsonNode? body = null, Dictionary<string, string>? headers = null) { Routes[route] = _ => new GhAnswer(status, body?.DeepClone(), headers ?? []); return this; }
        public Scripted On(string route, Func<GhCall, GhAnswer> answer) { Routes[route] = answer; return this; }

        public Task<GhAnswer> Send(GhCall call, CancellationToken ct)
        {
            if (Fail is not null) throw Fail;
            var key = $"{call.Method} {call.Path}";
            Calls.Add(key);
            if (Routes.TryGetValue(key, out var a)) return Task.FromResult(a(call));
            // a route may name the path without its query
            var bare = $"{call.Method} {call.Path.Split('?')[0]}";
            return Task.FromResult(Routes.TryGetValue(bare, out var b) ? b(call) : new GhAnswer(404, new JsonObject { ["message"] = "Not Found" }, new Dictionary<string, string>()));
        }
    }

    sealed class MemoryState : IItemStateStore
    {
        public readonly Dictionary<int, JsonObject> States = [];
        public int Saves; public Exception? ConflictOnSave;
        public JsonObject Of(int item) => States.TryGetValue(item, out var s) ? s : States[item] = new JsonObject { ["schema"] = 1, ["item"] = item };
        public Task<JsonObject> Read(int item, CancellationToken ct) => Task.FromResult((JsonObject)Of(item).DeepClone());
        public int Tries;
        public async Task<bool> Update(int item, Func<JsonObject, Task<bool>> mutate, CancellationToken ct, int tries = 1)
        {
            Tries = tries;
            var copy = (JsonObject)Of(item).DeepClone();
            if (!await mutate(copy)) return false;
            if (ConflictOnSave is not null) throw ConflictOnSave;
            States[item] = copy; Saves++;
            return true;
        }
    }

    sealed class CountingLock : IItemLock
    {
        public readonly List<int> Items = [];
        public Task<T> Use<T>(int item, Func<Task<T>> body, CancellationToken ct) { Items.Add(item); return body(); }
    }

    sealed class Octo : IOctopusApi
    {
        public readonly Dictionary<string, JsonNode?> Data = new(StringComparer.Ordinal);
        public readonly List<string> Calls = [];
        public Task<JsonNode?> Get(string path, CancellationToken ct) { Calls.Add(path); return Task.FromResult(Data.TryGetValue(path, out var d) ? d?.DeepClone() : null); }
    }

    sealed class Rig
    {
        public readonly Scripted Api = new();
        public readonly MemoryState State = new();
        public readonly CountingLock Lock = new();
        public readonly Octo Octopus = new();
        public readonly List<TimeSpan> Waits = [];
        public DateTime Now = T0;
        public ReadModelSettings Settings;
        public readonly Dictionary<int, int> Refusals = [];
        public ChecksReadModel Checks = null!; public InspectReadModel Inspect = null!; public ModelTierReadModel Tier = null!; public DeliveryReadModel Delivery = null!; public SecretsReadModel Secrets = null!; public ItemReads Reads = null!; public RuleGitHub Gh = null!;
        sealed class R(Dictionary<int, int> d) : IRefusalCounts { public int For(int item) => d.GetValueOrDefault(item); }

        public Rig(Action<JsonObject>? edit = null, Dictionary<string, string?>? env = null)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "config", "instance.json"))) dir = dir.Parent;
            var root = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "config", "instance.json")))!;
            edit?.Invoke(root);
            Settings = ReadModelSettings.From(root, k => env?.GetValueOrDefault(k));
            Gh = new RuleGitHub(Api, Settings, () => Now);
            Reads = new ItemReads(Gh, State);
            Checks = new ChecksReadModel(Reads, State, Lock, (t, _) => { Waits.Add(t); Now += t; return Task.CompletedTask; }, () => Path.Combine(Path.GetTempPath(), "rm-test-" + Guid.NewGuid().ToString("N")));
            Inspect = new InspectReadModel(Reads, Checks);
            Tier = new ModelTierReadModel(Reads, State, Lock, new R(Refusals));
            Delivery = new DeliveryReadModel(Reads, Octopus);
            Secrets = new SecretsReadModel(Reads, Octopus);
        }

        public const string Repo = "repos/clearmeasure-aisf-sample-apps/cmdemo1-workorders";
    }

    static JsonObject Pr(int number, string head, string branch, string title = "change", string baseRef = "master", string? mergeSha = null) =>
        new() { ["number"] = number, ["title"] = title, ["head"] = new JsonObject { ["sha"] = head, ["ref"] = branch }, ["base"] = new JsonObject { ["ref"] = baseRef }, ["merge_commit_sha"] = mergeSha };

    static JsonObject Runs(params (string Name, string Status, string? Conclusion)[] runs)
    {
        var a = new JsonArray(); var id = 100;
        foreach (var r in runs)
            a.Add(new JsonObject { ["id"] = id++, ["name"] = r.Name, ["status"] = r.Status, ["conclusion"] = r.Conclusion, ["started_at"] = "2026-01-01T10:00:00Z", ["completed_at"] = "2026-01-01T10:05:00Z", ["details_url"] = $"https://x/job/{id}", ["app"] = new JsonObject { ["slug"] = "github-actions" }, ["output"] = new JsonObject { ["title"] = null, ["summary"] = null, ["annotations_count"] = 0 } });
        return new JsonObject { ["total_count"] = a.Count, ["check_runs"] = a };
    }

    static VerbArgs Args(string json) => VerbArgs.Parse(json);

    // ---- Invoke-Gh and Invoke-GhOptional ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task GitHubRefusals_AreMappedAsInvokeGhMapsThem()
    {
        var rig = new Rig();
        rig.Api.On("GET x/ok", 200, new JsonObject { ["a"] = 1 });
        Assert.Equal(1, Ps.ToInt((await rig.Gh.Get("x/ok"))!["a"]));

        var nf = await Assert.ThrowsAsync<VerbRefusal>(() => rig.Gh.Get("x/missing"));
        Assert.Equal((12, "github-not-found", "GitHub 404 on GET x/missing"), (nf.Exit, nf.Code, nf.Message));
        Assert.Contains("Not Found", nf.Extra["detail"]!.ToString());

        rig.Api.On("GET x/limited", 403, new JsonObject { ["message"] = "API rate limit exceeded" }, new() { ["x-ratelimit-remaining"] = "0", ["x-ratelimit-reset"] = new DateTimeOffset(T0.AddSeconds(90)).ToUnixTimeSeconds().ToString() });
        var rl = await Assert.ThrowsAsync<VerbRefusal>(() => rig.Gh.Get("x/limited"));
        Assert.Equal((20, "github-rate-limited"), (rl.Exit, rl.Code));
        Assert.Equal("2026-10-29T12:01:30.0000000Z", rl.Extra["resetAt"]!.ToString());
        Assert.InRange(rl.Extra["retryAfterSeconds"]!.GetValue<int>(), 1, 90);   // (the wait is counted from the clock of the process, not the test's)

        rig.Api.On("GET x/retry", 429, new JsonObject { ["message"] = "slow down" }, new() { ["Retry-After"] = "17" });
        Assert.Equal(17, (await Assert.ThrowsAsync<VerbRefusal>(() => rig.Gh.Get("x/retry"))).Extra["retryAfterSeconds"]!.GetValue<int>());
        rig.Api.On("GET x/secondary", 403, new JsonObject { ["message"] = "You have exceeded a secondary rate limit" });
        Assert.Equal(("github-rate-limited", 60), ((await Assert.ThrowsAsync<VerbRefusal>(() => rig.Gh.Get("x/secondary"))) is var s ? (s.Code, s.Extra["retryAfterSeconds"]!.GetValue<int>()) : default));

        rig.Api.On("GET x/forbidden", 403, new JsonObject { ["message"] = "Resource not accessible by integration" });
        var fb = await Assert.ThrowsAsync<VerbRefusal>(() => rig.Gh.Get("x/forbidden"));
        Assert.Equal((30, "github-forbidden", "GitHub refused GET x/forbidden (403): Resource not accessible by integration"), (fb.Exit, fb.Code, fb.Message));
        Assert.Equal(403, fb.Extra["status"]!.GetValue<int>());

        rig.Api.On("GET x/boom", 500, new JsonObject { ["message"] = "boom" });
        var ge = await Assert.ThrowsAsync<VerbRefusal>(() => rig.Gh.Get("x/boom"));
        Assert.Equal((20, "github-error", "GitHub 500 on GET x/boom"), (ge.Exit, ge.Code, ge.Message));
        Assert.Equal((500, 30), (ge.Extra["status"]!.GetValue<int>(), ge.Extra["retryAfterSeconds"]!.GetValue<int>()));

        // an allowed status is an answer, not a refusal
        var allowed = await rig.Gh.Allow("GET", "x/missing", [404, 422]);
        Assert.True(allowed.Allowed && allowed.Status == 404);
        Assert.False((await rig.Gh.Allow("GET", "x/ok", [404])).Allowed);
        await Assert.ThrowsAsync<VerbRefusal>(() => rig.Gh.Allow("GET", "x/boom", [404]));
    }

    [Fact]
    public async Task GraphQl_RefusesAsInvokeGhGraphQlDoes()
    {
        var rig = new Rig();
        rig.Api.On("POST graphql", 200, new JsonObject { ["data"] = new JsonObject { ["x"] = 1 } });
        Assert.Equal(1, Ps.ToInt(Ps.Prop(await rig.Gh.GraphQL("q", new JsonObject()), "x")));
        rig.Api.On("POST graphql", 502);
        var http = await Assert.ThrowsAsync<VerbRefusal>(() => rig.Gh.GraphQL("q", new JsonObject()));
        Assert.Equal((20, "github-graphql-error", "GraphQL HTTP 502"), (http.Exit, http.Code, http.Message));
        rig.Api.On("POST graphql", 200, new JsonObject { ["errors"] = new JsonArray(new JsonObject { ["message"] = "bad field" }, new JsonObject { ["message"] = "worse" }) });
        var err = await Assert.ThrowsAsync<VerbRefusal>(() => rig.Gh.GraphQL("q", new JsonObject()));
        Assert.Equal(("github-graphql-error", "bad field; worse", 30), (err.Code, err.Message, err.Extra["retryAfterSeconds"]!.GetValue<int>()));
        rig.Api.On("POST graphql", 200, new JsonObject { ["errors"] = new JsonArray(new JsonObject { ["type"] = "RATE_LIMITED", ["message"] = "limit" }) }, new() { ["x-ratelimit-reset"] = new DateTimeOffset(T0.AddSeconds(30)).ToUnixTimeSeconds().ToString() });
        var rl = await Assert.ThrowsAsync<VerbRefusal>(() => rig.Gh.GraphQL("q", new JsonObject()));
        Assert.Equal(("github-rate-limited", "limit"), (rl.Code, rl.Message));
        Assert.NotNull(rl.Extra["resetAt"]);
    }

    [Theory]
    [InlineData(403, "logs", "logs-not-permitted", "Actions: read")]
    [InlineData(403, "annotations", "annotations-not-permitted", "Checks: read")]
    [InlineData(403, "baseline", "baseline-not-permitted", "Checks: read")]
    [InlineData(404, "artifacts", "artifacts-not-found", "Actions: read")]
    [InlineData(410, "artifacts", "artifacts-expired", "past the retention")]
    [InlineData(500, "logs", "github-error", "GitHub answered 500")]
    [InlineData(429, "logs", "github-rate-limited", "rate limit of the gateway")]
    public async Task AnOptionalRead_TurnsEveryRefusalIntoData(int status, string what, string code, string reasonHas)
    {
        var rig = new Rig();
        rig.Api.On("GET p", status, new JsonObject { ["message"] = "Resource not accessible" });
        var r = await rig.Gh.Optional("p", what, null);
        Assert.False(r.Ok);
        Assert.Equal(code, r.Code);
        Assert.Contains(reasonHas, r.Reason);
    }

    [Fact]
    public async Task AnOptionalRead_GivesTheBodyOf2xx_IsLimitedByTheDeadline_AndNeverThrowsForTheWire()
    {
        var rig = new Rig();
        rig.Api.On("GET p", c => { Assert.Equal((TimeSpan.FromSeconds(10), 1), (c.Timeout!.Value, c.Tries)); return new GhAnswer(200, new JsonObject { ["a"] = 1 }, new Dictionary<string, string>()); });
        Assert.True((await rig.Gh.Optional("p", "logs", null)).Ok);
        var calls = rig.Api.Calls.Count;
        var late = await rig.Gh.Optional("p", "logs", T0.AddMilliseconds(500));
        Assert.Equal(("detail-deadline", calls), (late.Code, rig.Api.Calls.Count));   // not even asked
        rig.Api.On("GET q", c => { Assert.Equal(TimeSpan.FromSeconds(4), c.Timeout!.Value); return new GhAnswer(200, null, new Dictionary<string, string>()); });
        Assert.True((await rig.Gh.Optional("q", "logs", T0.AddSeconds(4.9))).Ok);   // the time left, whole seconds, when it is less than ten
        rig.Api.Fail = new VerbRefusal(20, "github-unreachable", "down");
        var down = await rig.Gh.Optional("p", "logs", null);
        Assert.Equal(("github-unreachable", false), (down.Code, down.Ok));
        Assert.Contains("did not answer in 10 s", down.Reason);
        rig.Api.Fail = new InvalidOperationException("odd");
        Assert.Equal("github-unreachable", (await rig.Gh.Optional("p", "logs", null)).Code);
    }

    // ---- which head a call may read --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ACallThatNamesAnItemAndACommit_MayReadItOnlyWhenTheItemIsKnownByIt()
    {
        const string verdictSha = "2222222222222222222222222222222222222222", mergeSha = "3333333333333333333333333333333333333333", oldHead = "4444444444444444444444444444444444444444";
        var rig = new Rig();
        rig.Api.On($"GET {Rig.Repo}/pulls", 200, new JsonArray(Pr(5, Sha, "factory-worker/7-development")));
        rig.State.Of(7)["verdict"] = new JsonObject { ["head_sha"] = verdictSha };
        rig.State.Of(7)["merge"] = new JsonObject { ["sha"] = mergeSha };
        Assert.Equal((Sha, "5"), await Target(rig, """{"item":7,"sha":"1111111111111111111111111111111111111111"}"""));
        // the verdict's head and the merge commit are the item's too, and then there is no diff to speak of (the pull request's head is another)
        Assert.Equal((verdictSha, null), await Target(rig, $$"""{"item":7,"sha":"{{verdictSha}}"}"""));
        Assert.Equal((mergeSha.ToUpperInvariant(), null), await Target(rig, $$"""{"item":7,"sha":"{{mergeSha.ToUpperInvariant()}}"}"""));
        var other = await Assert.ThrowsAsync<VerbRefusal>(() => rig.Checks.ResolveTarget(Args("""{"item":7,"sha":"9999999999999999999999999999999999999999"}"""), default));
        Assert.Equal((10, "sha-not-own"), (other.Exit, other.Code));
        Assert.Equal("commit 9999999999999999999999999999999999999999 is not the head of the pull request of item #7, nor its recorded verdict or merge; a call that names an item reads the checks of that item only", other.Message);
        // no commit and no pull request: the item's open pull request; none open is refused
        Assert.Equal((Sha, "5"), await Target(rig, """{"item":7}""", read: 5));
        var quiet = new Rig(); quiet.Api.On($"GET {Rig.Repo}/pulls", 200, new JsonArray());
        var none = await Assert.ThrowsAsync<VerbRefusal>(() => quiet.Checks.ResolveTarget(Args("""{"item":7}"""), default));
        Assert.Equal((10, "no-open-pr", "no open pull request linked to this work item"), (none.Exit, none.Code, none.Message));
        var noItem = await Assert.ThrowsAsync<VerbRefusal>(() => rig.Checks.ResolveTarget(Args("{}"), default));
        Assert.Equal((2, "usage", "missing argument: item"), (noItem.Exit, noItem.Code, noItem.Message));
        // a call without an item reads any commit as it is, and any pull request
        Assert.Equal((oldHead, null), await Target(rig, $$"""{"sha":"{{oldHead}}"}"""));
        rig.Api.On($"GET {Rig.Repo}/pulls/5", 200, Pr(5, Sha, "factory-worker/7-development"));
        Assert.Equal((Sha, "5"), await Target(rig, """{"pr":5}"""));
        var pn = await Assert.ThrowsAsync<VerbRefusal>(() => rig.Checks.ResolveTarget(Args("""{"item":8,"pr":5}"""), default));
        Assert.Equal((10, "pr-not-own", "pull request #5 is not the pull request of item #8; a call that names an item reads the checks of that item's pull request only"), (pn.Exit, pn.Code, pn.Message));
    }

    [Fact]
    public async Task TheClosedPullRequestOfTheState_ServesAnItemWhoseBranchIsNoLongerOpen()
    {
        var rig = new Rig();
        rig.Api.On($"GET {Rig.Repo}/pulls", 200, new JsonArray());
        rig.State.Of(7)["pr"] = new JsonObject { ["number"] = 5 };
        rig.Api.On($"GET {Rig.Repo}/pulls/5", 200, Pr(5, Sha, "factory-worker/7-development", "x", "master", "5555555555555555555555555555555555555555"));
        Assert.Equal((Sha, "5"), await Target(rig, $$"""{"item":7,"sha":"{{Sha}}"}"""));
        // its merge commit is the item's as well, and then the diff of the pull request is not the diff of that commit
        Assert.Equal(("5555555555555555555555555555555555555555", null), await Target(rig, """{"item":7,"sha":"5555555555555555555555555555555555555555"}"""));
        // a pull request that is not the item's does not make a commit its
        rig.Api.On($"GET {Rig.Repo}/pulls/5", 200, Pr(5, Sha, "someone/else", "unrelated"));
        await Assert.ThrowsAsync<VerbRefusal>(() => rig.Checks.ResolveTarget(Args($$"""{"item":7,"sha":"{{Sha}}"}"""), default));
        // one that cannot be read is no reason to fail with another code
        rig.Api.Routes.Remove($"GET {Rig.Repo}/pulls/5");
        Assert.Equal("sha-not-own", (await Assert.ThrowsAsync<VerbRefusal>(() => rig.Checks.ResolveTarget(Args($$"""{"item":7,"sha":"{{Sha}}"}"""), default))).Code);
    }

    [Fact]
    public async Task ACopilotBranch_IsTheItemsWhenTheStateRecordsIt()
    {
        var rig = new Rig();
        rig.Api.On($"GET {Rig.Repo}/pulls", 200, new JsonArray(Pr(9, Sha, "copilot/fix-the-thing")));
        Assert.Equal("no-open-pr", (await Assert.ThrowsAsync<VerbRefusal>(() => rig.Checks.ResolveTarget(Args("""{"item":7}"""), default))).Code);
        rig.State.Of(7)["pr"] = new JsonObject { ["number"] = 9, ["branch"] = "COPILOT/fix-the-thing" };
        rig.Api.On($"GET {Rig.Repo}/pulls/9", 200, Pr(9, Sha, "copilot/fix-the-thing"));
        Assert.Equal((Sha, "9"), await Target(rig, """{"item":7}"""));
        Assert.Equal((Sha, "9"), await Target(rig, """{"item":7,"pr":9}"""));
    }

    static async Task<(string Sha, string? Pr)> Target(Rig rig, string args, int read = 0)
    {
        if (read > 0) rig.Api.On($"GET {Rig.Repo}/pulls/{read}", 200, Pr(read, Sha, "factory-worker/7-development"));
        var (sha, pr) = await rig.Checks.ResolveTarget(Args(args), default);
        return (sha, pr is null ? null : Ps.Str(pr, "number"));
    }

    // ---- pr-checks -------------------------------------------------------------------------------------------------------------------------------

    static Rig Red(Action<JsonObject>? runs = null, params (string, string, string?)[] checks)
    {
        var rig = new Rig();
        rig.Api.On($"GET {Rig.Repo}/pulls", 200, new JsonArray(Pr(5, Sha, "factory-worker/7-development")));
        rig.Api.On($"GET {Rig.Repo}/pulls/5", 200, Pr(5, Sha, "factory-worker/7-development"));
        var body = Runs(checks.Length > 0 ? checks : [("Build result", "completed", "failure"), ("secret-scan", "completed", "success")]);
        runs?.Invoke(body);
        rig.Api.On($"GET {Rig.Repo}/commits/{Sha}/check-runs", 200, body);
        return rig;
    }

    [Fact]
    public async Task ABadTail_AndAnUnknownCheck_AreRefusedAsTheCallsOwnMistake_NotHiddenInTheDetail()
    {
        var rig = Red();
        var tail = await Assert.ThrowsAsync<VerbRefusal>(() => rig.Checks.PrChecks(Args("""{"item":7,"tail":"lots"}"""), default));
        Assert.Equal((2, "usage", "tail must be a number of lines, from 1"), (tail.Exit, tail.Code, tail.Message));
        Assert.Equal(2, (await Assert.ThrowsAsync<VerbRefusal>(() => rig.Checks.PrChecks(Args("""{"item":7,"tail":0}"""), default))).Exit);
        var no = await Assert.ThrowsAsync<VerbRefusal>(() => rig.Checks.PrChecks(Args("""{"item":7,"check":"nope"}"""), default));
        Assert.Equal((12, "check-not-found", "this head has no check named 'nope'"), (no.Exit, no.Code, no.Message));
        Assert.Equal(["Build result", "secret-scan"], no.Extra["checks"]!.AsArray().Select(x => x!.ToString()));
        // a tail over 5000 is 5000
        rig.Api.On($"GET {Rig.Repo}/commits", 200, new JsonArray());
        var r = await rig.Checks.PrChecks(Args("""{"item":7,"tail":99999}"""), default);
        Assert.Equal(5000, Ps.ToInt(Ps.At(r, "limits", "tailLines")));
        // --full-log and --artifact with no check to choose are a usage error when several are red
        var many = Red(null, ("A", "completed", "failure"), ("B", "completed", "failure"));
        var fl = await Assert.ThrowsAsync<VerbRefusal>(() => many.Checks.PrChecks(Args("""{"item":7,"full_log":true}"""), default));
        Assert.Equal((2, "usage", "--full-log needs --check <name>: this head has 2 failed or cancelled checks"), (fl.Exit, fl.Code, fl.Message));
        // (--artifact with no check looks in every failed job's run; the jobs of this head cannot be read)
        var art = await Assert.ThrowsAsync<VerbRefusal>(() => many.Checks.PrChecks(Args("""{"item":7,"artifact":"x"}"""), default));
        Assert.Equal((10, "artifacts-not-found"), (art.Exit, art.Code));
        // a false flag is not set
        var ok = await many.Checks.PrChecks(Args("""{"item":7,"full_log":"False","brief":"false"}"""), default);
        Assert.Equal("pending", Ps.Str(ok, "status"));   // (the gates are absent from this head)
    }

    [Fact]
    public async Task OnlySixFailedChecksGetTheirDetail_TheOthersAreNamed_AndACancelledCheckGetsNoneAlone()
    {
        var checks = Enumerable.Range(1, 8).Select(i => ($"Job {i}", "completed", (string?)"failure")).Append(("Build result", "completed", "success")).Append(("secret-scan", "completed", "success")).ToArray();
        var rig = Red(null, checks);
        rig.Api.On($"GET {Rig.Repo}/commits", 200, new JsonArray());
        var r = await rig.Checks.PrChecks(Args("""{"item":7}"""), default);
        Assert.Equal(["Job 7", "Job 8"], Ps.List(r, "detailNotShownFor").Select(Ps.Text));   // in name order, after the sixth
        Assert.Equal(6, Ps.List(r, "checks").Count(c => Ps.Prop(c, "data") is not null));
        Assert.Equal(10, Ps.List(r, "checks").Count());
        // a head that is red only by a cancelled non-gate check is green; its cancelled check gets no detail, red or not
        var cancelled = Red(null, ("Build result", "completed", "success"), ("secret-scan", "completed", "success"), ("Other", "completed", "cancelled"));
        var rc = await cancelled.Checks.PrChecks(Args("""{"item":7}"""), default);
        Assert.Equal("success", Ps.Str(rc, "status"));
        Assert.Null(Ps.Prop(Ps.List(rc, "checks").First(c => Ps.Str(c, "name") == "Other"), "data"));
        Assert.Null(Ps.Prop(rc, "likelyPreexisting"));
        // next to a red gate it does
        var red = Red(null, ("Build result", "completed", "failure"), ("secret-scan", "completed", "success"), ("Other", "completed", "cancelled"));
        red.Api.On($"GET {Rig.Repo}/commits", 200, new JsonArray());
        var rr = await red.Checks.PrChecks(Args("""{"item":7}"""), default);
        Assert.NotNull(Ps.Prop(Ps.List(rr, "checks").First(c => Ps.Str(c, "name") == "Other"), "data"));
        Assert.Equal(["Build result"], Ps.List(rr, "failed_checks").Select(Ps.Text));
        Assert.Equal(["Other"], Ps.List(rr, "pending_checks").Select(Ps.Text));
    }

    [Fact]
    public async Task WhenTheTimeOfTheDetailIsUsedUp_TheRestIsNamed_AndAnExpiredArtifactIsRefused()
    {
        var rig = Red(null, ("A", "completed", "failure"), ("B", "completed", "failure"), ("Build result", "completed", "failure"), ("secret-scan", "completed", "success"));
        rig.Api.On($"GET {Rig.Repo}/commits", c => { rig.Now += TimeSpan.FromSeconds(70); return new GhAnswer(200, new JsonArray(), new Dictionary<string, string>()); });   // the baseline takes the whole minute
        var r = await rig.Checks.PrChecks(Args("""{"item":7}"""), default);
        Assert.Equal(["A", "B", "Build result"], Ps.List(r, "detailNotShownFor").Select(Ps.Text));
        Assert.Equal("failure", Ps.Str(r, "status"));
        Assert.Equal(0, Ps.List(r, "checks").Count(c => Ps.Prop(c, "data") is not null));
        // an artifact that has expired
        var art = Red(null, ("Build result", "completed", "failure"), ("secret-scan", "completed", "success"));
        art.Api.On($"GET {Rig.Repo}/actions/jobs/100", 200, new JsonObject { ["run_id"] = 9 });
        art.Api.On($"GET {Rig.Repo}/actions/runs/9/artifacts", 200, new JsonObject { ["artifacts"] = new JsonArray(new JsonObject { ["id"] = 1, ["name"] = "old", ["size_in_bytes"] = 10, ["expired"] = true }) });
        var ex = await Assert.ThrowsAsync<VerbRefusal>(() => art.Checks.PrChecks(Args("""{"item":7,"artifact":"old"}"""), default));
        Assert.Equal((10, "artifacts-expired", "artifact 'old' is past its retention period"), (ex.Exit, ex.Code, ex.Message));
        // a refusal of the run's artifacts is the refusal of the verb, with its code
        art.Api.On($"GET {Rig.Repo}/actions/runs/9/artifacts", 403, new JsonObject { ["message"] = "no" });
        var denied = await Assert.ThrowsAsync<VerbRefusal>(() => art.Checks.PrChecks(Args("""{"item":7,"artifact":"old"}"""), default));
        Assert.Equal((10, "artifacts-not-permitted"), (denied.Exit, denied.Code));
    }

    // ---- wait-for-checks -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WaitForChecks_CountsNothingForACallWithoutAnItem_AndTakesTheItemLockToCountOne()
    {
        var rig = Red();
        rig.Api.On($"GET {Rig.Repo}/commits", 200, new JsonArray());
        var anon = await Assert.ThrowsAsync<VerbRefusal>(() => rig.Checks.WaitForChecks(Args($$"""{"pr":5,"sha":"{{Sha}}"}"""), default));
        Assert.Equal((10, "checks-red"), (anon.Exit, anon.Code));
        Assert.Empty(rig.Lock.Items); Assert.Equal(0, rig.State.Saves);
        Assert.Equal("failure", Ps.Str(anon.Extra["checks"], "status"));
        Assert.Equal("checks failed: Build result. 'checks' holds, per failed check, its log excerpt or annotations (data.*) and its baseline on the base branch (likelyPreexisting).", anon.Message);
        var named = await Assert.ThrowsAsync<VerbRefusal>(() => rig.Checks.WaitForChecks(Args("""{"item":7}"""), default));
        Assert.Equal("checks-red", named.Code);
        Assert.Equal([7], rig.Lock.Items); Assert.Equal(1, rig.State.Saves);
        Assert.Equal(1, ChecksReadModel.RedCount(rig.State.Of(7)));
        await Assert.ThrowsAsync<VerbRefusal>(() => rig.Checks.WaitForChecks(Args("""{"item":7}"""), default));
        Assert.Equal(1, rig.State.Saves);   // the same head: nothing to save
        Assert.Equal(1, ChecksReadModel.RedCount(rig.State.Of(7)));
    }

    [Fact]
    public async Task WaitForChecks_ASavedStateThatMovedIsTheScriptsStateConflict()
    {
        var rig = Red();
        rig.Api.On($"GET {Rig.Repo}/commits", 200, new JsonArray());
        rig.State.ConflictOnSave = new VerbRefusal(11, "state-conflict", "the state of item 7 changed since it was read (revision 1, now 2); read it again and retry");
        var e = await Assert.ThrowsAsync<VerbRefusal>(() => rig.Checks.WaitForChecks(Args("""{"item":7}"""), default));
        Assert.Equal((11, "state-conflict"), (e.Exit, e.Code));
    }

    [Fact]
    public async Task WaitForChecks_TheRerunIsOfTheOnlyFailedCheckThatTheBaseBranchFailsToo_OncePerHead_AndOnlyWhenGitHubAllowsIt()
    {
        var rig = Red();
        rig.Api.On($"GET {Rig.Repo}/commits", 200, new JsonArray(new JsonObject { ["sha"] = "b1" }, new JsonObject { ["sha"] = "b2" }));
        foreach (var b in new[] { "b1", "b2" })
            rig.Api.On($"GET {Rig.Repo}/commits/{b}/check-runs", 200, Runs(("Build result", "completed", "failure")));
        rig.Api.On($"GET {Rig.Repo}/actions/jobs/100", 200, new JsonObject { ["run_id"] = 9, ["steps"] = new JsonArray() });
        rig.Api.On($"GET {Rig.Repo}/actions/jobs/100/logs", c => { if (c.OutFile is { } f) File.WriteAllText(f, "2026-01-01T10:00:20.0000000Z a line\n"); return new GhAnswer(200, null, new Dictionary<string, string>()); });
        rig.Api.On($"GET {Rig.Repo}/actions/runs/9/artifacts", 200, new JsonObject { ["artifacts"] = new JsonArray() });
        rig.Api.On($"POST {Rig.Repo}/actions/jobs/100/rerun", 201);
        var first = await Assert.ThrowsAsync<VerbRefusal>(() => rig.Checks.WaitForChecks(Args("""{"item":7}"""), default));
        Assert.Equal((21, "rerun-started"), (first.Exit, first.Code));
        Assert.Equal("the one failed check (Build result) fails on the base branch too (likelyPreexisting): the gateway reran its job once. Call wait-for-checks again.", first.Message);
        Assert.Equal(1, rig.Api.Calls.Count(c => c.StartsWith("POST ")));
        Assert.Equal([Sha], Ps.Items(rig.State.Of(7)["preexisting_reruns"]).Select(Ps.Text));
        Assert.Null(rig.State.Of(7)["checks_red"]);   // not counted
        // the same head again is red for good: counted, no second rerun
        var second = await Assert.ThrowsAsync<VerbRefusal>(() => rig.Checks.WaitForChecks(Args("""{"item":7}"""), default));
        Assert.Equal("checks-red", second.Code);
        Assert.Equal(1, rig.Api.Calls.Count(c => c.StartsWith("POST ")));
        Assert.Equal(1, ChecksReadModel.RedCount(rig.State.Of(7)));
        // GitHub refuses the rerun: the red stands and is counted
        var refused = Red();
        refused.Api.Routes.Clear();
        refused = Red(); refused.Api.Routes.Remove($"POST {Rig.Repo}/actions/jobs/100/rerun");
        refused.Api.On($"GET {Rig.Repo}/commits", 200, new JsonArray(new JsonObject { ["sha"] = "b1" }));
        refused.Api.On($"GET {Rig.Repo}/commits/b1/check-runs", 200, Runs(("Build result", "completed", "failure")));
        refused.Api.On($"GET {Rig.Repo}/actions/jobs/100", 200, new JsonObject { ["run_id"] = 9, ["steps"] = new JsonArray() });
        refused.Api.On($"GET {Rig.Repo}/actions/jobs/100/logs", 404);
        refused.Api.On($"GET {Rig.Repo}/actions/runs/9/artifacts", 200, new JsonObject { ["artifacts"] = new JsonArray() });
        refused.Api.On($"POST {Rig.Repo}/actions/jobs/100/rerun", 403, new JsonObject { ["message"] = "Must have admin rights" });
        Assert.Equal("checks-red", (await Assert.ThrowsAsync<VerbRefusal>(() => refused.Checks.WaitForChecks(Args("""{"item":7}"""), default))).Code);
        Assert.Equal(1, ChecksReadModel.RedCount(refused.State.Of(7)));
        Assert.Null(refused.State.Of(7)["preexisting_reruns"]);
        // an error GitHub answers that is none of the allowed ones is the verb's
        refused.Api.On($"POST {Rig.Repo}/actions/jobs/100/rerun", 500);
        Assert.Equal("github-error", (await Assert.ThrowsAsync<VerbRefusal>(() => refused.Checks.WaitForChecks(Args("""{"item":7}"""), default))).Code);
    }

    [Fact]
    public async Task WaitForChecks_WaitsTenSecondsBetweenPolls_AtMostOneHundred_AndTheRedDetailLeavesTheCallersFlagsOut()
    {
        var rig = Red(null, ("Build result", "in_progress", null), ("secret-scan", "completed", "success"));
        var e = await Assert.ThrowsAsync<VerbRefusal>(() => rig.Checks.WaitForChecks(Args("""{"item":7,"timeoutSeconds":5000}"""), default));
        Assert.Equal((21, "still-pending", "checks are still running; call again"), (e.Exit, e.Code, e.Message));
        Assert.Equal(10, rig.Waits.Count);   // a hundred seconds: ten waits of ten
        Assert.All(rig.Waits, w => Assert.Equal(TimeSpan.FromSeconds(10), w));
        Assert.Equal("pending", Ps.Str(e.Extra["checks"], "status"));
        var def = Red(null, ("Build result", "in_progress", null));
        await Assert.ThrowsAsync<VerbRefusal>(() => def.Checks.WaitForChecks(Args("""{"item":7}"""), default));
        Assert.Equal(6, def.Waits.Count);   // sixty seconds by default
        // the poll never reads a log or an artifact whatever the caller sent: a green head answers at once
        var green = Red(null, ("Build result", "completed", "success"), ("secret-scan", "completed", "success"));
        var ok = await green.Checks.WaitForChecks(Args("""{"item":7,"full_log":true,"artifact":"x","check":"nope","page":3,"entry":"e"}"""), default);
        Assert.Equal(("success", 0), (Ps.Str(ok, "status"), green.Waits.Count));
        Assert.Equal(Sha, Ps.Str(ok, "sha"));
    }

    // ---- model-tier ------------------------------------------------------------------------------------------------------------------------------

    static Rig OnTheBoard(int item = 7, string column = "Development", string[]? labels = null)
    {
        var rig = new Rig();
        rig.Api.On("POST graphql", c => new GhAnswer(200, new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["repository"] = new JsonObject
                {
                    ["issue"] = new JsonObject
                    {
                        ["id"] = "I_1", ["number"] = item, ["state"] = "OPEN", ["title"] = "Item", ["body"] = "body", ["url"] = "https://x", ["createdAt"] = "2026-10-20T00:00:00Z",
                        ["labels"] = new JsonObject { ["nodes"] = new JsonArray((labels ?? ["factory worker"]).Select(l => (JsonNode)new JsonObject { ["name"] = l }).ToArray()) },
                        ["projectItems"] = new JsonObject { ["nodes"] = new JsonArray(new JsonObject { ["id"] = "card1", ["project"] = new JsonObject { ["number"] = 679 }, ["fieldValueByName"] = new JsonObject { ["name"] = column } }) },
                    },
                },
            },
        }, new Dictionary<string, string>()));
        return rig;
    }

    [Fact]
    public async Task ModelTier_WritesTheStateOnlyWhenTheTierOrTheColumnChanged_UnderTheItemLock()
    {
        var rig = OnTheBoard();
        var a = await rig.Tier.ModelTier(Args("""{"item":7}"""), default);
        Assert.Equal((0, "Development"), (Ps.ToInt(a["tier"]), Ps.Str(a, "column")));
        Assert.Equal([7], rig.Lock.Items); Assert.Equal(1, rig.State.Saves);
        Assert.Equal(["column", "tier", "at", "signals"], ((JsonObject)rig.State.Of(7)["model_tier"]!).Select(kv => kv.Key));
        var again = await rig.Tier.ModelTier(Args("""{"item":7}"""), default);
        Assert.Equal(1, rig.State.Saves);   // unchanged: nothing written
        rig.Refusals[7] = 7;
        var up = await rig.Tier.ModelTier(Args("""{"item":7}"""), default);
        Assert.Equal((2, 2), (Ps.ToInt(up["tier"]), rig.State.Saves));
        Assert.Equal(["refusals=7"], Ps.List(up, "reasons").Select(Ps.Text));
        Assert.Equal(["launches", "findingsRounds", "demotions", "fixForwardRounds", "refusals"], ((JsonObject)up["signals"]!).Select(kv => kv.Key));
        // the column of the call wins over the card's, case aside, and a minimum tier of a column is a floor
        var floor = new Rig(r => r["escalation"]!["minTierByColumn"] = new JsonObject { ["Technical Design"] = 1 });
        floor.Api.Routes["POST graphql"] = rig.Api.Routes["POST graphql"];
        var fl = await floor.Tier.ModelTier(Args("""{"item":7,"column":"technical design"}"""), default);
        Assert.Equal((1, "technical design"), (Ps.ToInt(fl["tier"]), Ps.Str(fl, "column")));
        Assert.Equal([], Ps.List(fl, "reasons").Select(Ps.Text).ToArray());   // a floor is no signal
    }

    [Fact]
    public async Task ModelTier_RefusesAnItemOutsideTheEnvironmentScope_AndAMissingItem()
    {
        var rig = new Rig(r => r["envScope"] = new JsonArray("tdd"), new() { ["FACTORY_ENVIRONMENT"] = "tdd" });
        rig.Api.Routes["POST graphql"] = OnTheBoard(7, "Development", ["factory worker"]).Api.Routes["POST graphql"];
        var e = await Assert.ThrowsAsync<VerbRefusal>(() => rig.Tier.ModelTier(Args("""{"item":7}"""), default));
        Assert.Equal((10, "out-of-scope", "issue #7 is outside this gateway's scope: it does not carry exactly one env: label, 'env:tdd'"), (e.Exit, e.Code, e.Message));
        var missing = new Rig();
        missing.Api.On("POST graphql", 200, new JsonObject { ["data"] = new JsonObject { ["repository"] = new JsonObject { ["issue"] = null } } });
        var nf = await Assert.ThrowsAsync<VerbRefusal>(() => missing.Tier.ModelTier(Args("""{"item":7}"""), default));
        Assert.Equal((12, "item-not-found", "issue #7 not found"), (nf.Exit, nf.Code, nf.Message));
        Assert.Empty(missing.Lock.Items.Skip(1));
    }

    // ---- get-delivery ----------------------------------------------------------------------------------------------------------------------------

    static JsonObject Release(string id, string version, string? commit, string assembled, bool inNotes = true) =>
        new() { ["Id"] = id, ["Version"] = version, ["Assembled"] = assembled, ["ReleaseNotes"] = inNotes && commit is not null ? $"Build https://x/run/1 of {commit}" : "no commit here",
                ["BuildInformation"] = !inNotes && commit is not null ? new JsonArray(new JsonObject { ["VcsCommitNumber"] = commit.ToUpperInvariant() }) : null };

    static Rig Octopus(params JsonObject[] releases)
    {
        var rig = new Rig();
        rig.Octopus.Data["api/Spaces-355/projects/cmdemo1-ui"] = new JsonObject { ["Id"] = "Projects-1" };
        rig.Octopus.Data["api/Spaces-355/projects/Projects-1/releases?take=30"] = new JsonObject { ["Items"] = new JsonArray(releases.Select(r => (JsonNode)r).ToArray()) };
        rig.Octopus.Data["api/Spaces-355/environments?take=1000"] = new JsonObject { ["Items"] = new JsonArray(new JsonObject { ["Id"] = "E1", ["Name"] = "tdd" }, new JsonObject { ["Id"] = "E3", ["Name"] = "Prod" }) };
        return rig;
    }

    [Fact]
    public async Task Delivery_TheCandidateIsTheReleaseBuiltFromTheCommit_ItsNewestDeploymentPerEnvironmentIsReported()
    {
        var rig = Octopus(Release("R2", "2.0.2", new string('b', 40), "2026-10-28T11:00:00Z"), Release("R1", "2.0.1", Sha, "2026-10-28T10:00:00Z", inNotes: false));
        rig.Octopus.Data["api/Spaces-355/deployments?projects=Projects-1&take=30"] = new JsonObject
        {
            ["Items"] = new JsonArray(
                new JsonObject { ["Id"] = "D1", ["ReleaseId"] = "R1", ["EnvironmentId"] = "E1", ["TaskId"] = "T1", ["Created"] = "2026-10-28T10:05:00Z" },
                new JsonObject { ["Id"] = "D2", ["ReleaseId"] = "R1", ["EnvironmentId"] = "E1", ["TaskId"] = "T2", ["Created"] = "2026-10-28T10:30:00Z" },
                new JsonObject { ["Id"] = "D3", ["ReleaseId"] = "R1", ["EnvironmentId"] = "E3", ["TaskId"] = "T3", ["Created"] = "2026-10-28T10:40:00Z" },
                new JsonObject { ["Id"] = "D4", ["ReleaseId"] = "R2", ["EnvironmentId"] = "E1", ["TaskId"] = "T4", ["Created"] = "2026-10-28T11:30:00Z" },
                new JsonObject { ["Id"] = "D5", ["ReleaseId"] = "R1", ["EnvironmentId"] = "E9", ["TaskId"] = "T5", ["Created"] = "2026-10-28T10:50:00Z" }),
        };
        rig.Octopus.Data["api/Spaces-355/tasks/T2"] = new JsonObject { ["State"] = "Success", ["IsCompleted"] = true };
        rig.Octopus.Data["api/Spaces-355/tasks/T3"] = new JsonObject { ["State"] = "Executing", ["IsCompleted"] = false };
        var d = await rig.Delivery.GetDelivery(Args($$"""{"commit":"{{Sha}}"}"""), default);
        Assert.Equal(("2.0.1", "R1", Sha, Sha), (Ps.Str(d["candidate"], "version"), Ps.Str(d["candidate"], "release_id"), Ps.Str(d["candidate"], "commit"), Ps.Str(d, "commit")));   // the commit of the build information, lower case
        var deps = (JsonObject)d["deployments"]!;
        Assert.Equal(["Prod", "tdd"], deps.Select(kv => kv.Key).Order());   // an environment Octopus does not know is left out
        Assert.Equal(("Success", true, "D2"), (Ps.Str(deps["tdd"], "state"), Ps.Bool(deps["tdd"]!["completed"]), Ps.Str(deps["tdd"], "deployment")));   // the newer of the two
        Assert.Equal(("Executing", false), (Ps.Str(deps["Prod"], "state"), Ps.Bool(deps["Prod"]!["completed"])));
        Assert.False(Ps.Bool(d["prod_success"]));
        Assert.DoesNotContain("api/Spaces-355/tasks/T1", rig.Octopus.Calls);
        // the production deployment finished: prod_success, the environment compared without regard to case
        rig.Octopus.Data["api/Spaces-355/tasks/T3"] = new JsonObject { ["State"] = "Success", ["IsCompleted"] = true };
        Assert.True(Ps.Bool((await rig.Delivery.GetDelivery(Args($$"""{"commit":"{{Sha}}"}"""), default))["prod_success"]));
    }

    [Fact]
    public async Task Delivery_TheNewestReleaseIsTheCandidateWhenItContainsTheCommit_ElseThereIsNone()
    {
        var newest = new string('c', 40);
        var rig = Octopus(Release("R1", "2.0.1", new string('b', 40), "2026-10-28T10:00:00Z"), Release("R2", "2.0.2", newest, "2026-10-28T11:00:00Z"));
        rig.Octopus.Data["api/Spaces-355/deployments?projects=Projects-1&take=30"] = new JsonObject { ["Items"] = new JsonArray() };
        rig.Api.On($"GET {Rig.Repo}/compare/{Sha}...{newest}", 200, new JsonObject { ["status"] = "ahead" });
        var ahead = await rig.Delivery.Delivery(Sha, default);
        Assert.Equal("R2", Ps.Str(ahead["candidate"], "release_id"));
        Assert.Empty((JsonObject)ahead["deployments"]!);
        rig.Api.On($"GET {Rig.Repo}/compare/{Sha}...{newest}", 200, new JsonObject { ["status"] = "behind" });
        Assert.Null((await rig.Delivery.Delivery(Sha, default))["candidate"]);
        rig.Api.On($"GET {Rig.Repo}/compare/{Sha}...{newest}", 422, new JsonObject { ["message"] = "no merge base" });
        Assert.Null((await rig.Delivery.Delivery(Sha, default))["candidate"]);
        rig.Api.On($"GET {Rig.Repo}/compare/{Sha}...{newest}", 500);
        Assert.Equal("github-error", (await Assert.ThrowsAsync<VerbRefusal>(() => rig.Delivery.Delivery(Sha, default))).Code);
        // a release that names no commit cannot be compared
        var blind = Octopus(Release("R1", "2.0.1", null, "2026-10-28T10:00:00Z"));
        Assert.Null((await blind.Delivery.Delivery(Sha, default))["candidate"]);
        Assert.Equal(0, blind.Api.Calls.Count);
        // no releases at all
        var empty = Octopus();
        var d = await empty.Delivery.GetDelivery(Args($$"""{"commit":"{{Sha}}"}"""), default);
        Assert.Equal(("get-delivery-shape", true), ("get-delivery-shape", d["candidate"] is null && d["deployments"] is JsonObject { Count: 0 } && Ps.Str(d, "commit") == Sha));
    }

    [Fact]
    public async Task Delivery_AMissingProjectIsExit12_AndAnItemWithoutAMergeIsExit10()
    {
        var rig = new Rig();
        var e = await Assert.ThrowsAsync<VerbRefusal>(() => rig.Delivery.Delivery(Sha, default));
        Assert.Equal((12, "octopus-project-not-found", "project 'cmdemo1-ui' not found in Spaces-355"), (e.Exit, e.Code, e.Message));
        var nm = await Assert.ThrowsAsync<VerbRefusal>(() => rig.Delivery.GetDelivery(Args("""{"item":7}"""), default));
        Assert.Equal((10, "not-merged", "the item has no merge commit yet"), (nm.Exit, nm.Code, nm.Message));
        var noArg = await Assert.ThrowsAsync<VerbRefusal>(() => rig.Delivery.GetDelivery(Args("{}"), default));
        Assert.Equal((2, "usage"), (noArg.Exit, noArg.Code));
    }

    // ---- check-secrets ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CheckSecrets_EveryProbeIsNamed_AFailureIsAnEntryAndNeverStopsTheOthers()
    {
        var rig = new Rig(null, new() { ["FACTORY_WEBHOOK_SECRET"] = new string('s', 31), ["OCTOPUS_API_KEY"] = "API-1", ["FACTORY_AUTHOR_APP"] = "1", ["FACTORY_APP_AUTHOR_SLUG"] = "fw-author" });
        rig.Api.On($"GET {Rig.Repo}", 200, new JsonObject { ["permissions"] = new JsonObject { ["push"] = false, ["admin"] = false } });
        rig.Api.On("POST graphql", 200, new JsonObject { ["data"] = new JsonObject { ["organization"] = new JsonObject { ["projectV2"] = new JsonObject { ["id"] = "P", ["fields"] = new JsonObject { ["nodes"] = new JsonArray(new JsonObject(), new JsonObject { ["id"] = "F", ["name"] = "Status", ["options"] = new JsonArray(new JsonObject { ["id"] = "1", ["name"] = "Technical Design" }, new JsonObject { ["id"] = "2", ["name"] = "Development" }) }) } } } } });
        rig.Api.On($"GET {Rig.Repo}/labels", 200, new JsonArray(new JsonObject { ["name"] = "Factory Worker" }));
        var e = await Assert.ThrowsAsync<VerbRefusal>(() => rig.Secrets.CheckSecrets(default));
        Assert.Equal((30, "secrets-check-failed", "5 check(s) failed"), (e.Exit, e.Code, e.Message));
        var checks = e.Extra["checks"]!.AsArray().Select(c => (Name: Ps.Str(c, "name"), Ok: Ps.Bool(c!["ok"]), Detail: Ps.Str(c, "detail"))).ToList();
        Assert.Equal(
            [("github-token", true, "authenticated as fw-author[bot]"), ("repo-access", false, "push=False admin=False"), ("project-read", true, "Status options: 2"), ("board-columns", false, "missing: Functional Testing, Release Queue, Done"),
             ("worker-label", true, "label 'factory worker' exists"), ("webhook-secret", false, "set, 31 chars"), ("octopus-key", false, "authenticated"), ("octopus-project", false, "project cmdemo1-ui")], checks);
        Assert.DoesNotContain("API-1", e.Extra.ToJsonString());
        // every probe failing is told in its own words
        var down = new Rig();
        down.Api.Fail = new VerbRefusal(20, "github-unreachable", "down");
        var all = await Assert.ThrowsAsync<VerbRefusal>(() => down.Secrets.CheckSecrets(default));
        Assert.Equal(
            ["token missing or rejected", "repository not readable", "project not readable", "unreadable", "labels unreadable", "not set", "not set"],
            all.Extra["checks"]!.AsArray().Select(c => Ps.Str(c, "detail")));
        Assert.Equal("7 check(s) failed", all.Message);
    }
}
